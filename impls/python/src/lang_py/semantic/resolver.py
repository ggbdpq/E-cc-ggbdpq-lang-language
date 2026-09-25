"""名字解析（v0.3 Semantic Core 第一层）。

四遍结构（与 Go 参考一致）：
1) 收集 struct/enum（含预载构造器）；2) 收集 fn/extern/method 符号；
3) 顶层绑定依序解析（前向引用即 E2002，与解释器顺序执行、→Go var 初始化一致）；
4) 函数体（对全局可见性与声明顺序无关）。
诊断码：E2001..E2010（specs/name-resolution.md）。
"""

from __future__ import annotations

from ..errors import Diagnostic
from ..syntax import ast_nodes as A
from .symbols import (KCTOR, KENUM, KFN, KMETHOD, KPARAM, KSTRUCT, KVAR, NODE_REGISTRY,
                      ResolveResult, Symbol, SymKind, kind_name)

# 内建函数名单（与 interp/gen/genjs 三方现行实现一致的唯一清单）
BUILTIN_NAMES = [
    "println", "print", "len", "str", "int", "float", "keys", "values",
    "has", "push", "assert", "err", "bytes", "char",
    "jsonParse", "jsonStringify", "isNone", "makeChan", "send", "recv",
]

# 预载 struct（interp prelude 的 error/HttpResp）
PRELUDE_STRUCT_NAMES = ["error", "HttpResp"]

E2001, E2002, E2003, E2004, E2005, E2006, E2007, E2008, E2010 = (
    "E2001", "E2002", "E2003", "E2004", "E2005", "E2006", "E2007", "E2008", "E2010",
)


class Resolver:
    def __init__(self) -> None:
        self.res = ResolveResult()
        self.next_id = 0
        self.scopes: list[dict[str, Symbol]] = [{}]  # 值命名空间
        self.type_scopes: list[dict[str, Symbol]] = [{}]  # 类型命名空间
        self.ctor_scope: dict[str, Symbol] = {}  # 构造器全局命名空间（含预载）
        self.loop_depth = 0
        self.fn_depth = 0

    # ———— 基础 ————
    def new_sym(self, s: Symbol) -> Symbol:
        self.next_id += 1
        s.id = self.next_id
        self.res.symbols.append(s)
        return s

    def errf(self, line: int, code: str, msg: str) -> None:
        self.res.diags.append(Diagnostic(code, msg, line))

    def push_scope(self) -> None:
        self.scopes.append({})
        self.type_scopes.append({})

    def pop_scope(self) -> None:
        self.scopes.pop()
        self.type_scopes.pop()

    def define(self, s: Symbol) -> None:
        top = self.scopes[-1]
        if s.name in top:
            self.errf(s.line, E2001, f"{kind_name(s.kind)} {s.name} 在此作用域重复定义")
            return
        top[s.name] = s

    def define_ctor(self, s: Symbol) -> None:
        old = self.ctor_scope.get(s.name)
        if old is not None:
            owner = "内建" if old.builtin_mark else old.struct_name
            self.errf(s.line, E2003, f"构造器 {s.name} 重名（已被 {owner} 使用）")
            return
        self.ctor_scope[s.name] = s

    def lookup(self, name: str) -> Symbol | None:
        for scope in reversed(self.scopes):
            if name in scope:
                return scope[name]
        return None

    def lookup_ctor(self, name: str) -> Symbol | None:
        return self.ctor_scope.get(name)

    def lookup_type(self, name: str) -> Symbol | None:
        for scope in reversed(self.type_scopes):
            if name in scope:
                return scope[name]
        return None

    def lookup_top(self, name: str) -> Symbol | None:
        return self.scopes[0].get(name)

    # ———— 预载 ————
    def prelude(self) -> None:
        for n in BUILTIN_NAMES:
            self.res.builtin[n] = self.new_sym(Symbol(kind=KFN, name=n, builtin_mark=True))
        for n in PRELUDE_STRUCT_NAMES:
            self.res.builtin[n] = self.new_sym(Symbol(kind=KSTRUCT, name=n, builtin_mark=True))
        self.res.builtin["http"] = self.new_sym(Symbol(kind=KVAR, name="http", builtin_mark=True))
        for name, enum, idx, arity in [
            ("Some", "Option", 0, 1),
            ("None", "Option", 1, 0),
            ("Ok", "Result", 0, 1),
            ("Err", "Result", 1, 1),
        ]:
            self.ctor_scope[name] = self.new_sym(Symbol(
                kind=KCTOR, name=name, struct_name=enum, case_index=idx,
                arity=arity, payloads=[None] * arity, builtin_mark=True,
            ))

    # ———— 第一遍：收集 struct/enum ————
    def collect_types(self, prog: list[A.Stmt]) -> None:
        for s in prog:
            if isinstance(s, A.StructDecl):
                if s.name in self.type_scopes[0]:
                    self.errf(s.line, E2001, f"struct {s.name} 重复定义")
                    continue
                sym = self.new_sym(Symbol(kind=KSTRUCT, name=s.name, line=s.line,
                                          type_params=s.type_params, decl=s))
                for f in s.fields:
                    sym.fields.append(f.name)
                    sym.field_types.append(f.typ)
                self.type_scopes[0][s.name] = sym
            elif isinstance(s, A.EnumDecl):
                if s.name in self.type_scopes[0]:
                    self.errf(s.line, E2001, f"enum {s.name} 重复定义")
                    continue
                sym = self.new_sym(Symbol(kind=KENUM, name=s.name, line=s.line,
                                          type_params=s.type_params, decl=s))
                self.type_scopes[0][s.name] = sym
                for i, c in enumerate(s.cases):
                    if c.name in self.res.builtin:
                        self.errf(c.line, E2006, f"case {c.name} 与内建名冲突")
                        continue
                    cs = self.new_sym(Symbol(
                        kind=KCTOR, name=c.name, line=c.line,
                        struct_name=s.name, case_index=i,
                        arity=len(c.payloads), payloads=c.payloads,
                    ))
                    self.define_ctor(cs)

    # ———— 第二遍：收集 fn/extern/method 符号 ————
    def collect_fns(self, prog: list[A.Stmt]) -> None:
        for s in prog:
            if isinstance(s, A.FnDecl):
                if s.recv is not None:
                    self.collect_method(s)
                    continue
                old = self.lookup_top(s.name)
                if old is not None:
                    self.errf(s.line, E2001, f"fn {s.name} 重复定义（先前定义于第 {old.line} 行）")
                    continue
                self.scopes[0][s.name] = self.fn_symbol(s)
            elif isinstance(s, A.ExternDecl):
                for fn in s.fns:
                    old = self.lookup_top(fn.name)
                    if old is not None:
                        self.errf(s.line, E2001, f"fn {fn.name} 重复定义（先前定义于第 {old.line} 行）")
                        continue
                    sym = self.fn_symbol(fn)
                    sym.extern = True
                    self.scopes[0][fn.name] = sym

    def fn_symbol(self, d: A.FnDecl) -> Symbol:
        sym = self.new_sym(Symbol(
            kind=KFN, name=d.name, line=d.line, decl=d,
            type_ex=d.ret, type_params=d.type_params, arity=len(d.params),
        ))
        for p in d.params:
            sym.params.append(p.typ)
        return sym

    def collect_method(self, d: A.FnDecl) -> None:
        assert d.recv is not None
        nt = d.recv.typ
        if not isinstance(nt, A.NamedType):
            self.errf(d.line, E2004, "方法接收者须为具名 struct 类型")
            return
        st = self.type_scopes[0].get(nt.name)
        if st is None or st.kind != KSTRUCT:
            self.errf(d.line, E2004, f"为未知 struct {nt.name} 定义方法")
            return
        if d.name in st.methods:
            self.errf(d.line, E2001, f"方法 {d.name} 在 struct {nt.name} 上重复定义")
            return
        sym = self.new_sym(Symbol(
            kind=KMETHOD, name=d.name, line=d.line, recv_name=nt.name,
            type_ex=d.ret, arity=len(d.params),
        ))
        for p in d.params:
            sym.params.append(p.typ)
        st.methods[d.name] = sym

    # ———— 第三遍：顶层绑定依序 ————
    def bind_globals(self, prog: list[A.Stmt]) -> None:
        for s in prog:
            if isinstance(s, A.LetStmt):
                self.expr(s.init)
                if s.name == "_":
                    continue
                old = self.lookup_top(s.name)
                if old is not None:
                    self.errf(s.line, E2001,
                              f"绑定 {s.name} 在此作用域重复定义（先前定义于第 {old.line} 行）")
                    continue
                sym = self.new_sym(Symbol(kind=KVAR, name=s.name, line=s.line,
                                          mutable=s.mutable, type_ex=s.typ))
                self.scopes[0][s.name] = sym
                self.res.decl_syms[id(s)] = sym
                NODE_REGISTRY[id(s)] = s
            elif isinstance(s, (A.FnDecl, A.StructDecl, A.EnumDecl, A.ExternDecl, A.ImportStmt)):
                continue
            else:
                self.stmt(s)

    # ———— 第四遍：函数体 ————
    def bind_fns(self, prog: list[A.Stmt]) -> None:
        for s in prog:
            if isinstance(s, A.FnDecl) and s.recv is None:
                sym = self.scopes[0].get(s.name)
                if sym is not None:
                    self.res.decl_syms[id(s)] = sym
                    NODE_REGISTRY[id(s)] = s
                self.fn_scope(s, sym)
        for s in prog:
            if not isinstance(s, A.FnDecl) or s.recv is None:
                continue
            nt = s.recv.typ
            if isinstance(nt, A.NamedType):
                st = self.type_scopes[0].get(nt.name)
                if st is not None and st.kind == KSTRUCT:
                    sym = st.methods.get(s.name)
                    if sym is not None:
                        sym.decl = s
                        self.fn_scope(s, sym)

    # ———— 语句 ————
    def stmt(self, s: A.Stmt) -> None:
        if isinstance(s, A.LetStmt):
            self.expr(s.init)
            if s.name == "_":
                return
            sym = self.new_sym(Symbol(kind=KVAR, name=s.name, line=s.line,
                                      mutable=s.mutable, type_ex=s.typ))
            self.define(sym)
            self.res.decl_syms[id(s)] = sym
            NODE_REGISTRY[id(s)] = s
        elif isinstance(s, A.AssignStmt):
            if isinstance(s.target, A.Ident):
                sym = self.lookup(s.target.name)
                if sym is None:
                    self.errf(s.target.line, E2002, f"未定义变量 {s.target.name}")
                elif not sym.mutable:
                    self.errf(s.line, E2005, f"{s.target.name} 由 let 绑定，不可重新赋值")
                self.res.refs[id(s.target)] = sym
            else:
                self.assign_target(s.target)
            self.expr(s.val)
        elif isinstance(s, A.ExprStmt):
            self.expr(s.x)
        elif isinstance(s, A.ReturnStmt):
            if self.fn_depth == 0:
                self.errf(s.line, E2008, "return 出现在函数之外")
            if s.val is not None:
                self.expr(s.val)
        elif isinstance(s, A.IfStmt):
            self.expr(s.cond)
            self.block(s.then)
            if s.els is not None:
                self.block(s.els)
        elif isinstance(s, A.WhileStmt):
            self.expr(s.cond)
            self.loop_depth += 1
            self.block(s.body)
            self.loop_depth -= 1
        elif isinstance(s, A.ForInStmt):
            self.expr(s.iter)
            self.push_scope()
            if s.var != "_":
                sym = self.new_sym(Symbol(kind=KVAR, name=s.var, line=s.line))
                self.define(sym)
                self.res.decl_syms[id(s)] = sym
                NODE_REGISTRY[id(s)] = s
            self.loop_depth += 1
            for b in s.body:
                self.stmt(b)
            self.loop_depth -= 1
            self.pop_scope()
        elif isinstance(s, A.BreakStmt):
            if self.loop_depth == 0:
                self.errf(s.line, E2007, "break 出现在循环之外")
        elif isinstance(s, A.ContinueStmt):
            if self.loop_depth == 0:
                self.errf(s.line, E2007, "continue 出现在循环之外")
        elif isinstance(s, A.FnDecl):
            if s.recv is not None:
                return  # 方法体在 bind_fns 统一走
            sym = self.fn_symbol(s)
            self.define(sym)
            self.res.decl_syms[id(s)] = sym
            NODE_REGISTRY[id(s)] = s
            self.fn_scope(s, sym)
        elif isinstance(s, A.StructDecl):
            self.errf(s.line, E2010, f"嵌套类型声明不支持（struct {s.name} 须在顶层声明）")
            top = self.type_scopes[-1]
            if s.name not in top:
                sym = self.new_sym(Symbol(kind=KSTRUCT, name=s.name, line=s.line,
                                          type_params=s.type_params))
                for f in s.fields:
                    sym.fields.append(f.name)
                    sym.field_types.append(f.typ)
                top[s.name] = sym
        elif isinstance(s, A.EnumDecl):
            self.errf(s.line, E2010, f"嵌套类型声明不支持（enum {s.name} 须在顶层声明）")
            top = self.type_scopes[-1]
            if s.name not in top:
                self.new_sym(Symbol(kind=KENUM, name=s.name, line=s.line,
                                    type_params=s.type_params))
        elif isinstance(s, A.ExternDecl):
            for fn in s.fns:
                if self.lookup(fn.name) is None:
                    sym = self.fn_symbol(fn)
                    sym.extern = True
                    self.define(sym)
        elif isinstance(s, A.GoStmt):
            callee = s.call.callee
            if not isinstance(callee, A.Ident):
                self.errf(s.line, E2002, "go 的目标须是具名函数调用")
            else:
                sym = self.lookup(callee.name)
                if sym is None or sym.kind != KFN:
                    self.errf(callee.line, E2002, f"go 的目标须是顶层函数：{callee.name}")
                else:
                    self.res.refs[id(callee)] = sym
            for a in s.call.args:
                self.expr(a)
        elif isinstance(s, A.ImportStmt):
            pass  # 装载器已展开

    def block(self, stmts: list[A.Stmt]) -> None:
        self.push_scope()
        for s in stmts:
            self.stmt(s)
        self.pop_scope()

    def fn_scope(self, d: A.FnDecl, sym: Symbol | None) -> None:
        self.push_scope()
        psyms: list[Symbol] = []
        if d.recv is not None:
            ps = self.new_sym(Symbol(kind=KPARAM, name=d.recv.name, line=d.line, type_ex=d.recv.typ))
            self.define(ps)
            psyms.append(ps)
        for p in d.params:
            if p.name == "_":
                continue
            ps = self.new_sym(Symbol(kind=KPARAM, name=p.name, line=d.line, type_ex=p.typ))
            self.define(ps)
            psyms.append(ps)
        if sym is not None:
            self.res.fn_params[sym.id] = psyms
        self.fn_depth += 1
        for s in d.body:
            self.stmt(s)
        self.fn_depth -= 1
        self.pop_scope()

    def assign_target(self, t: A.Expr) -> None:
        if isinstance(t, A.FieldEx):
            self.expr(t.obj)
        elif isinstance(t, A.IndexEx):
            self.expr(t.obj)
            self.expr(t.idx)
        else:
            self.expr(t)

    # ———— 表达式 ————
    def expr(self, e: A.Expr) -> None:
        if isinstance(e, A.Ident):
            sym = self.lookup(e.name)
            if sym is not None:
                self.res.refs[id(e)] = sym
                return
            sym = self.lookup_ctor(e.name)
            if sym is not None:
                self.res.refs[id(e)] = sym
                return
            if e.name in self.res.builtin:
                return  # 裸内建引用是否合法由 checker 判定
            self.errf(e.line, E2002, f"未定义 {e.name}")
        elif isinstance(e, A.Unary):
            self.expr(e.x)
        elif isinstance(e, A.Binary):
            self.expr(e.l)
            self.expr(e.r)
        elif isinstance(e, A.RangeEx):
            self.expr(e.lo)
            self.expr(e.hi)
        elif isinstance(e, A.CallEx):
            self.expr(e.callee)
            for a in e.args:
                self.expr(a)
        elif isinstance(e, A.IndexEx):
            self.expr(e.obj)
            self.expr(e.idx)
        elif isinstance(e, A.SliceEx):
            self.expr(e.obj)
            self.expr(e.lo)
            self.expr(e.hi)
        elif isinstance(e, A.FieldEx):
            self.expr(e.obj)
        elif isinstance(e, A.TryEx):
            self.expr(e.x)
        elif isinstance(e, A.StructLit):
            sym = self.lookup_type(e.type_name)
            if sym is not None:
                self.res.refs[id(e)] = sym
            else:
                self.errf(e.line, E2002, f"未知 struct {e.type_name}")
            for f in e.fields:
                self.expr(f.val)
        elif isinstance(e, A.MapLit):
            for item in e.items:
                self.expr(item.key)
                self.expr(item.val)
        elif isinstance(e, A.ArrLit):
            for el in e.items:
                self.expr(el)
        elif isinstance(e, A.MatchEx):
            self.expr(e.scrut)
            for arm in e.arms:
                self.push_scope()
                self.pat(arm.pat)
                self.expr(arm.body)
                self.pop_scope()
        elif isinstance(e, A.FnLit):
            self.push_scope()
            for p in e.params:
                if p.name != "_":
                    self.define(self.new_sym(Symbol(kind=KPARAM, name=p.name, line=e.line, type_ex=p.typ)))
            self.fn_depth += 1
            for s in e.body:
                self.stmt(s)
            self.fn_depth -= 1
            self.pop_scope()

    def pat(self, p: A.Pat) -> None:
        if isinstance(p, A.WildPat):
            return
        if isinstance(p, A.BindPat):
            sym = self.new_sym(Symbol(kind=KVAR, name=p.name, line=p.line))
            self.define(sym)
            self.res.pat_refs[id(p)] = sym
        elif isinstance(p, A.LitPat):
            self.expr(p.val)
        elif isinstance(p, A.CasePat):
            sym = self.lookup_ctor(p.name)
            if sym is None:
                self.errf(p.line, E2002, f"未知构造器 {p.name}")
            else:
                self.res.pat_refs[id(p)] = sym
            for sp in p.subs:
                self.pat(sp)


def resolve(prog: list[A.Stmt]) -> ResolveResult:
    """解析整个程序（装载器合并后的语句序列）。有诊断时仍返回产物，调用方以 has_errors 判定。"""
    r = Resolver()
    r.prelude()
    r.collect_types(prog)
    r.collect_fns(prog)
    r.bind_globals(prog)
    r.bind_fns(prog)
    r.pop_scope()
    return r.res
