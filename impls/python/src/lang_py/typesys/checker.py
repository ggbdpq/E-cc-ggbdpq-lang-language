"""语义类型检查器（v0.3 Semantic Core 第二层；checker.go + infer.go 语义等价物）。

保守性铁律：任一侧为 Any/Unknown 时不报错。诊断码 E3001..E3014 / E4001..E4003。
遍历结构与 resolver 同构；引用绑定复用 resolver 产物（以节点身份为键）。
本检查器收编全部前置约束（参数注解、? 位置、Result return 形态、match 分类与穷尽性），
后端不再重复实现。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional

from ..errors import Diagnostic
from ..semantic.symbols import KCTOR, KENUM, KFN, KSTRUCT, ResolveResult, Symbol
from ..syntax import ast_nodes as A
from ..syntax.tokens import K
from .diagnostics import (E3001, E3002, E3003, E3004, E3005, E3006, E3007,
                          E3008, E3009, E3010, E3011, E3012, E3013, E3014,
                          E4001, E4002, E4003, join_names, op_name)
from .model import (ANY, BOOL, ERROR_T, FLOAT, INT, INVALID, RANGE, STRING,
                    UNKNOWN, UNIT, Type, TK, assignable, chan_of, enum_of,
                    fn_of, is_concrete, is_numeric, map_of, option_of,
                    ref_of, result_err, result_of, result_ok, slice_of,
                    struct_of, type_arg_of, type_equal, type_str)

_ASSIGNABLE_OPS = {K.PLUS_ASSIGN: "+=", K.MINUS_ASSIGN: "-=", K.STAR_ASSIGN: "*=", K.SLASH_ASSIGN: "/="}


class MatchKind(Enum):
    LITERAL = 0
    ENUM = 1
    RESULT = 2
    OPTION = 3


@dataclass(eq=False)
class CheckResult:
    expr_ty: dict[int, Type] = field(default_factory=dict)   # id(Expr) → 类型
    pat_ty: dict[int, Type] = field(default_factory=dict)    # id(BindPat) → 捕获类型
    sym_ty: dict[int, Type] = field(default_factory=dict)    # Symbol.id → 类型
    fn_ty: dict[int, Type] = field(default_factory=dict)     # fn/method Symbol.id → 函数类型
    match_kind: dict[int, MatchKind] = field(default_factory=dict)  # id(MatchEx) → 分类
    diags: list[Diagnostic] = field(default_factory=list)

    def has_errors(self) -> bool:
        return bool(self.diags)


def _deref(t: Type) -> Type:
    while t.kind == TK.REF:
        t = t.elem
    return t


class Checker:
    def __init__(self, res: ResolveResult) -> None:
        self.res = res
        self.out = CheckResult()
        self.scopes: list[dict[str, Type]] = [{}]  # 绑定类型作用域栈
        self.fn_ret: Optional[Type] = None  # 当前函数返回注解（None = 无注解）
        self.tps: set[str] = set()  # 当前函数泛型参数名集
        self.in_fn = False

    def errf(self, line: int, code: str, msg: str) -> None:
        self.out.diags.append(Diagnostic(code, msg, line))

    # ———— 程序遍历：与 resolver 同构 ————
    def walk(self, prog: list[A.Stmt]) -> None:
        self.push_scope()
        # 1. 顶层绑定依序（前向引用已被 resolver 拦）
        for s in prog:
            if isinstance(s, A.LetStmt):
                self.let_stmt(s)
        # 2. fn 与方法体
        for s in prog:
            if isinstance(s, A.FnDecl) and s.recv is None:
                self.fn_body(s, self.res.decl_syms.get(id(s)), None)
        for s in prog:
            if isinstance(s, A.FnDecl) and s.recv is not None:
                self.fn_body(s, self._method_sym(s), self._method_struct_type(s))
        # 3. 顶层游离语句（脚本模式；只做类型走查）
        for s in prog:
            if isinstance(s, (A.LetStmt, A.FnDecl, A.StructDecl, A.EnumDecl, A.ExternDecl, A.ImportStmt)):
                continue
            self.stmt(s)
        self.pop_scope()

    def _method_sym(self, d: A.FnDecl) -> Optional[Symbol]:
        assert d.recv is not None
        nt = d.recv.typ
        if not isinstance(nt, A.NamedType):
            return None
        st = self._lookup_type_sym(nt.name)
        if st is not None:
            return st.methods.get(d.name)
        return None

    def _method_struct_type(self, d: A.FnDecl) -> Type:
        assert d.recv is not None
        nt = d.recv.typ
        if not isinstance(nt, A.NamedType):
            return UNKNOWN
        st = self._lookup_type_sym(nt.name)
        if st is not None:
            return struct_of(st)
        return UNKNOWN

    def fn_body(self, d: A.FnDecl, sym: Optional[Symbol], recv_ty: Optional[Type]) -> None:
        saved = (self.fn_ret, self.in_fn, self.scopes, self.tps)
        self.scopes = [{}]
        self.tps = set(d.type_params)
        sig = self.fn_signature(d)
        self.fn_ret = sig.ret if d.ret is not None else None
        self.in_fn = True
        if d.recv is not None:
            self.scopes[0][d.recv.name] = recv_ty if recv_ty is not None else UNKNOWN
        self._declare_params(d, sym, recv_ty)
        if sym is not None:
            self.out.fn_ty[sym.id] = sig
        for s in d.body:
            self.stmt(s)
        self.fn_ret, self.in_fn, self.scopes, self.tps = saved

    def _declare_params(self, d: A.FnDecl, sym: Optional[Symbol], recv_ty: Optional[Type]) -> None:
        psyms = self.res.fn_params.get(sym.id, []) if sym is not None else []
        pi = 0
        if d.recv is not None:
            if pi < len(psyms):
                self.out.sym_ty[psyms[pi].id] = recv_ty if recv_ty is not None else UNKNOWN
                pi += 1
        for p in d.params:
            if p.typ is None:
                self.errf(d.line, E3007, f"参数 {p.name} 缺少类型注解")
                pt = UNKNOWN
            else:
                pt = self.resolve_type_ex(p.typ, set(d.type_params))
            self.scopes[0][p.name] = pt
            if p.name != "_" and pi < len(psyms):
                self.out.sym_ty[psyms[pi].id] = pt
                pi += 1

    def fn_signature(self, d: A.FnDecl) -> Type:
        tps = set(d.type_params)
        ps: list[Type] = []
        for p in d.params:
            if p.typ is None:
                ps.append(UNKNOWN)
                continue
            ps.append(self.resolve_type_ex(p.typ, tps))
        ret = UNKNOWN
        if d.ret is not None:
            ret = self.resolve_type_ex(d.ret, tps)
        return fn_of(ps, ret)

    def let_stmt(self, d: A.LetStmt) -> None:
        sym = self.res.decl_syms.get(id(d))
        if isinstance(d.init, A.TryEx):
            ty = self.infer_try(d.init)
        else:
            ty = self.infer(d.init)
        if d.typ is not None:
            anno = self.resolve_type_ex(d.typ, set())
            if not assignable(anno, ty):
                self.errf(d.line, E3001, f"不能把 {type_str(ty)} 绑定为注解类型 {type_str(anno)}")
            ty = anno
        if sym is not None:
            self.out.sym_ty[sym.id] = ty
        self.define_var(d.name, ty)

    # ———— 语句 ————
    def stmt(self, s: A.Stmt) -> None:
        if isinstance(s, A.LetStmt):
            self.let_stmt(s)
        elif isinstance(s, A.AssignStmt):
            self.assign(s)
        elif isinstance(s, A.ExprStmt):
            if isinstance(s.x, A.TryEx):
                # ADR-0005 扩展：表达式语句位置的顶层 ? 合法，走与 let/return/赋值相同的传播检查
                self.infer_try(s.x)
            else:
                self.infer(s.x)
        elif isinstance(s, A.ReturnStmt):
            self.return_stmt(s)
        elif isinstance(s, A.IfStmt):
            self.require_bool(s.cond, "if 条件")
            self.block(s.then)
            if s.els is not None:
                self.block(s.els)
        elif isinstance(s, A.WhileStmt):
            self.require_bool(s.cond, "while 条件")
            self.block(s.body)
        elif isinstance(s, A.ForInStmt):
            ty = self.infer(s.iter)
            if ty.kind == TK.SLICE:
                elem = ty.elem
            elif ty.kind == TK.MAP:
                elem = ty.key
            elif ty.kind == TK.STRING:
                elem = STRING
            elif ty.kind == TK.RANGE:
                elem = INT
            elif not is_concrete(ty):
                elem = UNKNOWN
            else:
                self.errf(s.line, E3010, f"{type_str(ty)} 不可迭代")
                elem = UNKNOWN
            sym = self.res.decl_syms.get(id(s))
            if sym is not None:
                self.out.sym_ty[sym.id] = elem
            self.push_scope()
            self.define_var(s.var, elem)
            for b in s.body:
                self.stmt(b)
            self.pop_scope()
        elif isinstance(s, A.GoStmt):
            self.infer_call(s.call)
        elif isinstance(s, A.FnDecl):
            if s.recv is None:
                self.fn_body(s, self.res.decl_syms.get(id(s)), None)
        elif isinstance(s, (A.StructDecl, A.EnumDecl, A.BreakStmt, A.ContinueStmt,
                            A.ImportStmt, A.ExternDecl)):
            pass  # 位置合法性由 resolver 管；extern 无类型面

    def block(self, list_: list[A.Stmt]) -> None:
        self.push_scope()
        for s in list_:
            self.stmt(s)
        self.pop_scope()

    def assign(self, st: A.AssignStmt) -> None:
        if isinstance(st.val, A.TryEx):
            # ADR-0005：赋值右值位置的顶层 ? 合法，走与 let/return 相同的传播检查
            val_ty = self.infer_try(st.val)
        else:
            val_ty = self.infer(st.val)
        if isinstance(st.target, A.Ident):
            tgt_ty = self.lookup_var(st.target.name)
            if st.op != K.ASSIGN:
                self.check_compound(st, tgt_ty, val_ty)
                return
            if not assignable(tgt_ty, val_ty):
                self.errf(st.line, E3001, f"不能把 {type_str(val_ty)} 赋给 {type_str(tgt_ty)}")
        elif isinstance(st.target, A.FieldEx):
            obj_ty = self.infer(st.target.obj)
            f_ty = self.field_type(obj_ty, st.target.name, st.target.line)
            if st.op != K.ASSIGN:
                self.check_compound(st, f_ty, val_ty)
                return
            if not assignable(f_ty, val_ty):
                self.errf(st.line, E3001,
                          f"不能把 {type_str(val_ty)} 赋给字段 {st.target.name}（{type_str(f_ty)}）")
        elif isinstance(st.target, A.IndexEx):
            obj_ty = self.infer(st.target.obj)
            idx_ty = self.infer(st.target.idx)
            self.check_index_key(obj_ty, idx_ty, st.target.line)
            elem_ty = self.index_elem_ty(obj_ty, st.target.line)
            if st.op != K.ASSIGN:
                self.check_compound(st, elem_ty, val_ty)
                return
            if not assignable(elem_ty, val_ty):
                self.errf(st.line, E3001,
                          f"不能把 {type_str(val_ty)} 赋给容器元素（{type_str(elem_ty)}）")

    def check_compound(self, st: A.AssignStmt, tgt_ty: Type, val_ty: Type) -> None:
        if not is_concrete(tgt_ty) or not is_concrete(val_ty):
            return
        opn = op_name(_ASSIGNABLE_OPS.get(st.op, "="))
        if tgt_ty.kind == TK.SLICE:
            if st.op == K.PLUS_ASSIGN and val_ty.kind == TK.SLICE:
                return
            self.errf(st.line, E3001, f"数组 {opn} 需要数组右值，得到 {type_str(val_ty)}")
            return
        if tgt_ty.kind == val_ty.kind and (is_numeric(tgt_ty) or tgt_ty.kind == TK.STRING):
            return
        self.errf(st.line, E3002,
                  f"{type_str(tgt_ty)} 与 {type_str(val_ty)} 不可做 {opn}（无隐式转换）")

    def return_stmt(self, st: A.ReturnStmt) -> None:
        if not self.in_fn:
            return  # resolver 已报 E2008
        if st.val is None:
            if self.fn_ret is not None and self.fn_ret.kind == TK.RESULT:
                self.errf(st.line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
            elif self.fn_ret is not None and is_concrete(self.fn_ret):
                self.errf(st.line, E3001, f"注解为 {type_str(self.fn_ret)} 的函数不能裸 return")
            return
        if isinstance(st.val, A.TryEx):
            self.infer_try(st.val)
            return
        if self.fn_ret is not None and self.fn_ret.kind == TK.RESULT:
            self.infer_result_return(st, self.fn_ret)
            return
        val_ty = self.infer(st.val)
        if self.fn_ret is not None and is_concrete(self.fn_ret) and not assignable(self.fn_ret, val_ty):
            self.errf(st.line, E3001, f"返回 {type_str(val_ty)} 与注解 {type_str(self.fn_ret)} 不匹配")

    def infer_result_return(self, st: A.ReturnStmt, ret: Type) -> None:
        call = st.val
        if not isinstance(call, A.CallEx):
            self.errf(st.line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
            self.infer(call)
            return
        id_ok = isinstance(call.callee, A.Ident) and call.callee.name in ("Ok", "Err") and len(call.args) == 1
        if not id_ok:
            self.errf(st.line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
            self.infer(call)
            return
        name = call.callee.name
        arg_ty = self.infer(call.args[0])
        if name == "Ok":
            if not assignable(result_ok(ret), arg_ty):
                self.errf(st.line, E3001, f"Ok({type_str(arg_ty)}) 与返回类型 {type_str(ret)} 不匹配")
        else:
            if not assignable(result_err(ret), arg_ty):
                self.errf(st.line, E3001, f"Err({type_str(arg_ty)}) 与返回类型 {type_str(ret)} 不匹配")
        self.out.expr_ty[id(st.val)] = ret

    # ———— 作用域与类型查名 ————
    def push_scope(self) -> None:
        self.scopes.append({})

    def pop_scope(self) -> None:
        self.scopes.pop()

    def define_var(self, name: str, t: Type) -> None:
        """绑定类型写入当前（最内层）块作用域。

        NAME-SCOPE-001：块内 let/循环变量/match 臂绑定不得写入函数/全局层。
        """
        self.scopes[-1][name] = t

    def lookup_var(self, name: str) -> Type:
        for scope in reversed(self.scopes):
            if name in scope:
                t = scope[name]
                if t is not None:
                    return t
        return UNKNOWN

    def _lookup_type_sym(self, name: str) -> Optional[Symbol]:
        s = self.res.builtin.get(name)
        if s is not None and s.kind == KSTRUCT:
            return s
        for s in self.res.symbols:
            if s.kind in (KSTRUCT, KENUM) and s.name == name:
                return s
        return None

    _PRIM_TYPES = {"int": INT, "float": FLOAT, "string": STRING, "bool": BOOL,
                   "error": ERROR_T, "any": ANY}

    def resolve_type_ex(self, t: A.TypeEx, tps: set[str]) -> Type:
        """源码注解 → 语义类型（含 E3001/E3009 注解期诊断）。"""
        if isinstance(t, A.NamedType):
            name = t.name
            if name in self._PRIM_TYPES:
                return self._PRIM_TYPES[name]
            if name == "chan":
                elem = UNKNOWN
                if len(t.args) == 1:
                    elem = self.resolve_type_ex(t.args[0], tps)
                return chan_of(elem)
            if name == "Option":
                if len(t.args) == 1:
                    return option_of(self.resolve_type_ex(t.args[0], tps))
                self.errf(0, E3001, "Option 需要一个类型参数")
                return INVALID
            if name == "Result":
                if len(t.args) != 2:
                    self.errf(0, E3001, "Result 需要两个类型参数")
                    return INVALID
                en = t.args[1]
                if not (isinstance(en, A.NamedType) and en.name == "error"):
                    self.errf(0, E3009, "Result 第二类型参数须为 error")
                    # 仍返回 Result 形：避免下游把 Result 函数误判为普通函数（E3006 连锁）
                    return result_of(self.resolve_type_ex(t.args[0], tps), ERROR_T)
                return result_of(self.resolve_type_ex(t.args[0], tps), ERROR_T)
            if name in tps:
                return type_arg_of(name)  # 泛型擦除：保留参数名供 →Go 生成
            sym = self._lookup_type_sym(name)
            if sym is not None:
                return enum_of(sym) if sym.kind == KENUM else struct_of(sym)
            return UNKNOWN  # 未知注解不阻塞（保守）
        if isinstance(t, A.ArrType):
            return slice_of(self.resolve_type_ex(t.elem, tps))
        if isinstance(t, A.MapType):
            return map_of(self.resolve_type_ex(t.key, tps), self.resolve_type_ex(t.val, tps))
        if isinstance(t, A.PtrType):
            return ref_of(self.resolve_type_ex(t.elem, tps))
        if isinstance(t, A.FnType):
            ps = [self.resolve_type_ex(p, tps) for p in t.params]
            ret = UNKNOWN
            if t.ret is not None:
                ret = self.resolve_type_ex(t.ret, tps)
            return fn_of(ps, ret)
        return UNKNOWN

    # ———— 推断主入口 ————
    def infer(self, e: A.Expr) -> Type:
        key = id(e)
        t = self.out.expr_ty.get(key)
        if t is not None:
            return t
        t = self.compute(e)
        self.out.expr_ty[key] = t
        return t

    def compute(self, e: A.Expr) -> Type:
        if isinstance(e, A.IntLit):
            return INT
        if isinstance(e, A.FloatLit):
            return FLOAT
        if isinstance(e, A.StrLit):
            return STRING
        if isinstance(e, A.BoolLit):
            return BOOL
        if isinstance(e, A.Ident):
            return self.ident_type(e)
        if isinstance(e, A.Unary):
            return self.infer_unary(e)
        if isinstance(e, A.Binary):
            return self.infer_binary(e)
        if isinstance(e, A.RangeEx):
            lo, hi = self.infer(e.lo), self.infer(e.hi)
            if (is_concrete(lo) and lo.kind != TK.INT) or (is_concrete(hi) and hi.kind != TK.INT):
                self.errf(e.line, E3001, f"区间两端须为 int，得到 {type_str(lo)}..{type_str(hi)}")
                return INVALID
            return RANGE
        if isinstance(e, A.CallEx):
            return self.infer_call(e)
        if isinstance(e, A.IndexEx):
            obj = self.infer(e.obj)
            idx = self.infer(e.idx)
            self.check_index_key(obj, idx, e.line)
            return self.index_elem_ty(obj, e.line)
        if isinstance(e, A.SliceEx):
            obj = _deref(self.infer(e.obj))
            lo, hi = self.infer(e.lo), self.infer(e.hi)
            for t in (lo, hi):
                if is_concrete(t) and t.kind != TK.INT:
                    self.errf(e.line, E3013, f"切片端点须为 int，得到 {type_str(t)}")
            if obj.kind == TK.SLICE:
                return obj
            if obj.kind == TK.STRING:
                return STRING
            if obj.kind in (TK.ANY, TK.UNKNOWN, TK.INVALID):
                return UNKNOWN
            self.errf(e.line, E3001, f"{type_str(obj)} 不可切片")
            return INVALID
        if isinstance(e, A.FieldEx):
            return self.field_type(self.infer(e.obj), e.name, e.line)
        if isinstance(e, A.StructLit):
            return self.infer_struct_lit(e)
        if isinstance(e, A.MapLit):
            return self.infer_map_lit(e)
        if isinstance(e, A.ArrLit):
            return self.infer_arr_lit(e)
        if isinstance(e, A.MatchEx):
            return self.infer_match(e)
        if isinstance(e, A.FnLit):
            return self.infer_fn_lit(e)
        if isinstance(e, A.TryEx):
            # 非语句顶层位置（let/return/赋值/表达式语句已在语句层处理）
            self.errf(e.line, E3005, "? 只能出现在 let/return/赋值/表达式语句的顶层")
            return self.infer_try_operand(e)
        return UNKNOWN

    def ident_type(self, x: A.Ident) -> Type:
        t = self.lookup_var(x.name)
        if t is not None and t.kind != TK.INVALID and self._lookup_scopes_has(x.name):
            return t
        sym = self.res.refs.get(id(x))
        if sym is not None:
            if sym.kind == KFN:
                t = self.out.fn_ty.get(sym.id)
                if t is not None:
                    return t
                if isinstance(sym.decl, A.FnDecl):
                    t = self.fn_signature(sym.decl)
                    self.out.fn_ty[sym.id] = t
                    return t
                return UNKNOWN
            if sym.kind == KCTOR:
                return self.ctor_value_type(sym, x.line)
            t = self.out.sym_ty.get(sym.id)
            if t is not None:
                return t
            return UNKNOWN
        return UNKNOWN  # 内建裸引用 / 未定义名（已报）→ 保守

    def _lookup_scopes_has(self, name: str) -> bool:
        return any(name in scope for scope in self.scopes)

    def ctor_value_type(self, sym: Symbol, line: int) -> Type:
        """裸构造器引用（零参才合法，如 None）。"""
        if sym.arity != 0:
            self.errf(line, E3003, f"构造器 {sym.name} 须直接调用")
            return INVALID
        return self.ctor_result_type(sym, None)

    def ctor_result_type(self, sym: Symbol, args: Optional[list[Type]]) -> Type:
        if sym.struct_name == "Option":
            elem = UNKNOWN
            if args and len(args) == 1:
                elem = args[0]
            return option_of(elem)
        if sym.struct_name == "Result":
            ok = UNKNOWN
            if args and len(args) == 1:
                ok = args[0]
            return result_of(ok, ERROR_T)
        en = self._lookup_type_sym(sym.struct_name)
        if en is not None:
            return enum_of(en)
        return UNKNOWN

    def infer_unary(self, x: A.Unary) -> Type:
        t = self.infer(x.x)
        if x.op == K.MINUS:
            if not is_concrete(t):
                return t
            if is_numeric(t):
                return t
            self.errf(x.line, E3011, f"- 不能用于 {type_str(t)}")
            return INVALID
        if x.op == K.NOT:
            if is_concrete(t) and t.kind != TK.BOOL:
                self.errf(x.line, E3011, f"! 不能用于 {type_str(t)}（须为 bool）")
                return INVALID
            return BOOL
        if x.op == K.AMP:
            if not isinstance(x.x, A.Ident):
                self.errf(x.line, E3012, "v0 只能对变量取引用")
                return INVALID
            return ref_of(t)
        return UNKNOWN

    def infer_binary(self, x: A.Binary) -> Type:
        if x.op in (K.AND, K.OR):
            self.require_bool(x.l, "逻辑运算")
            self.require_bool(x.r, "逻辑运算")
            return BOOL
        l, r = self.infer(x.l), self.infer(x.r)

        if x.op in (K.EQ, K.NEQ):
            if is_concrete(l) and is_concrete(r):
                same_scalar = l.kind == r.kind and (is_numeric(l) or l.kind in (TK.STRING, TK.BOOL))
                same_enum = l.kind == TK.ENUM and r.kind == TK.ENUM and l.sym is r.sym
                if not same_scalar and not same_enum:
                    self.errf(x.line, E3001, f"{type_str(l)} 与 {type_str(r)} 不可用 == 比较")
                    return INVALID
            return BOOL
        if x.op in (K.LT, K.LE, K.GT, K.GE):
            if is_concrete(l) and is_concrete(r):
                ok = l.kind == r.kind and (is_numeric(l) or l.kind == TK.STRING)
                if not ok:
                    self.errf(x.line, E3001, f"{type_str(l)} 与 {type_str(r)} 不可比较大小（无隐式转换）")
                    return INVALID
            return BOOL
        if x.op in (K.AMP, K.BIT_OR, K.XOR, K.SHL, K.SHR):
            if is_concrete(l) and is_concrete(r):
                if l.kind != TK.INT or r.kind != TK.INT:
                    self.errf(x.line, E3001, f"{type_str(l)} 与 {type_str(r)} 不可位运算（须为 int）")
                    return INVALID
                return INT
            return ANY

        if x.op == K.PLUS:
            if is_concrete(l) and l.kind == TK.STRING and is_concrete(r) and r.kind == TK.STRING:
                return STRING
            return self.arith_type(x, l, r)
        if x.op in (K.MINUS, K.STAR, K.SLASH):
            return self.arith_type(x, l, r)
        if x.op == K.PERCENT:
            if is_concrete(l) and l.kind == TK.FLOAT:
                self.errf(x.line, E3001, "% 不能用于 float")
                return INVALID
            return self.arith_type(x, l, r)
        return UNKNOWN

    def arith_type(self, x: A.Binary, l: Type, r: Type) -> Type:
        if not is_concrete(l) or not is_concrete(r):
            return ANY
        if l.kind == r.kind and is_numeric(l):
            return l
        self.errf(x.line, E3002,
                  f"{type_str(l)} 与 {type_str(r)} 不可做 {op_name(_BINARY_OP_NAMES.get(x.op, '?'))}（无隐式转换）")
        return INVALID

    def require_bool(self, e: A.Expr, what: str) -> None:
        t = self.infer(e)
        if is_concrete(t) and t.kind != TK.BOOL:
            self.errf(e.line, E3011, f"{what}须为 bool，得到 {type_str(t)}")

    # ———— 调用 ————
    def infer_call(self, x: A.CallEx) -> Type:
        if isinstance(x.callee, A.Ident):
            sym = self.res.refs.get(id(x.callee))
            if sym is not None and sym.kind == KCTOR:
                args = [self.infer(a) for a in x.args]
                if len(args) != sym.arity:
                    self.errf(x.line, E3003, f"{x.callee.name} 需要 {sym.arity} 个参数，得到 {len(args)} 个")
                    return INVALID
                if sym.name in ("Ok", "Err"):
                    # Ok/Err 只能出现在 Result 函数的 return（return_stmt 已处理该路径）
                    self.errf(x.line, E3006, "Ok/Err 只能出现在 Result 函数的 return")
                return self.ctor_result_type(sym, args)
            if sym is None:
                fn = BUILTIN_SIGS.get(x.callee.name)
                if fn is not None and x.callee.name in self.res.builtin:
                    args = [self.infer(a) for a in x.args]
                    return fn(self, args)
            if sym is not None and sym.kind == KFN:
                if isinstance(sym.decl, A.FnDecl):
                    self.check_args_vs_decl(sym.decl, x.args, x.line)
                t = self.out.fn_ty.get(sym.id)
                if t is not None:
                    return t.ret if t.ret is not None else UNKNOWN
                return UNKNOWN

        if isinstance(x.callee, A.FieldEx):
            return self.infer_method_call(x.callee, x)

        callee_ty = self.infer(x.callee)
        args = [self.infer(a) for a in x.args]
        if callee_ty.kind == TK.FN:
            self.check_args_vs_fn(callee_ty, args, x.line)
            return callee_ty.ret if callee_ty.ret is not None else UNKNOWN
        return UNKNOWN

    def infer_method_call(self, fe: A.FieldEx, x: A.CallEx) -> Type:
        obj_ty = _deref(self.infer(fe.obj))
        if isinstance(fe.obj, A.Ident) and obj_ty.kind == TK.UNKNOWN:
            # http.get：预载服务值
            if fe.obj.name == "http" and "http" in self.res.builtin and fe.name == "get":
                for a in x.args:
                    self.infer(a)
                if len(x.args) != 2:
                    self.errf(x.line, E3003, f"http.get 需要 2 个参数（url, headers），得到 {len(x.args)} 个")
                resp = self._lookup_type_sym("HttpResp")
                if resp is not None:
                    return result_of(struct_of(resp), ERROR_T)
                return result_of(UNKNOWN, ERROR_T)
        if obj_ty.kind == TK.STRUCT and obj_ty.sym is not None:
            m = obj_ty.sym.methods.get(fe.name)
            if m is not None:
                if isinstance(m.decl, A.FnDecl):
                    self.check_args_vs_decl(m.decl, x.args, x.line)
                    if m.decl.ret is not None:
                        return self.resolve_type_ex(m.decl.ret, set(m.decl.type_params))
                    return UNKNOWN
                return UNKNOWN
            # 字段持有的函数值
            f_ty = self.field_type(obj_ty, fe.name, fe.line)
            args = [self.infer(a) for a in x.args]
            if f_ty.kind == TK.FN:
                self.check_args_vs_fn(f_ty, args, x.line)
                return f_ty.ret if f_ty.ret is not None else UNKNOWN
            if not is_concrete(f_ty) or f_ty.kind == TK.INVALID:
                return UNKNOWN
            self.errf(x.line, E3004, f"{fe.name} 不是方法也不是函数值字段")
            return INVALID
        if not is_concrete(obj_ty):
            for a in x.args:
                self.infer(a)
            return UNKNOWN
        self.errf(x.line, E3004, f"{type_str(obj_ty)} 没有方法 {fe.name}")
        return INVALID

    def check_args_vs_decl(self, d: A.FnDecl, args: list[A.Expr], line: int) -> None:
        if len(args) != len(d.params):
            self.errf(line, E3003, f"{d.name} 需要 {len(d.params)} 个参数，得到 {len(args)} 个")
            for a in args:
                self.infer(a)
            return
        tps = set(d.type_params)
        for i, a in enumerate(args):
            at = self.infer(a)
            if d.params[i].typ is None:
                continue
            pt = self.resolve_type_ex(d.params[i].typ, tps)
            if not assignable(pt, at):
                self.errf(a.line, E3001, f"参数 {d.params[i].name} 需要 {type_str(pt)}，得到 {type_str(at)}")

    def check_args_vs_fn(self, fn_ty: Type, args: list[Type], line: int) -> None:
        if len(args) != len(fn_ty.params):
            self.errf(line, E3003, f"需要 {len(fn_ty.params)} 个参数，得到 {len(args)} 个")
            return
        for i, (tp, ap) in enumerate(zip(fn_ty.params, args)):
            if not assignable(tp, ap):
                self.errf(line, E3001, f"第 {i + 1} 个参数需要 {type_str(tp)}，得到 {type_str(ap)}")

    # ———— 字面量与容器 ————
    def infer_struct_lit(self, x: A.StructLit) -> Type:
        sym = self.res.refs.get(id(x))
        if sym is None:
            for f in x.fields:
                self.infer(f.val)
            return UNKNOWN
        if len(x.fields) != len(sym.fields):
            self.errf(x.line, E3001, f"{sym.name} 需要 {len(sym.fields)} 个字段，得到 {len(x.fields)} 个")
        for i, f in enumerate(x.fields):
            ft = self.infer(f.val)
            if i < len(sym.fields):
                if f.name != sym.fields[i]:
                    self.errf(x.line, E3001, f"{sym.name} 第 {i + 1} 个字段应为 {sym.fields[i]}，得到 {f.name}")
                if i < len(sym.field_types) and sym.field_types[i] is not None:
                    anno = self.resolve_type_ex(sym.field_types[i], set(sym.type_params))
                    if not assignable(anno, ft):
                        self.errf(f.val.line, E3001, f"字段 {f.name} 需要 {type_str(anno)}，得到 {type_str(ft)}")
        return struct_of(sym)

    def infer_map_lit(self, x: A.MapLit) -> Type:
        key_t: Optional[Type] = None
        val_t: Optional[Type] = None

        def mixed(cur: Optional[Type], nxt: Type) -> Optional[Type]:
            if cur is None:
                return nxt
            if type_equal(cur, nxt):
                return cur
            if not is_concrete(nxt):
                return cur
            if not is_concrete(cur):
                return nxt
            return UNKNOWN  # 混型键/值：interp 合法，动态处理

        for item in x.items:
            key_t = mixed(key_t, self.infer(item.key))
            val_t = mixed(val_t, self.infer(item.val))
        if key_t is None:
            key_t = UNKNOWN
        if val_t is None:
            val_t = UNKNOWN
        if is_concrete(key_t) and key_t.kind not in (TK.INT, TK.STRING, TK.BOOL):
            self.errf(x.line, E3014, f"映射键须为 int/string/bool，得到 {type_str(key_t)}")
            return map_of(UNKNOWN, val_t)
        return map_of(key_t, val_t)

    def infer_arr_lit(self, x: A.ArrLit) -> Type:
        elem: Optional[Type] = None
        for el in x.items:
            t = self.infer(el)
            if elem is None:
                elem = t
                continue
            if type_equal(elem, t):
                continue
            if not is_concrete(t):
                continue
            if not is_concrete(elem):
                elem = t
                continue
            elem = UNKNOWN  # 混型数组：interp 合法
        if elem is None:
            elem = UNKNOWN
        return slice_of(elem)

    # ———— match ————
    def infer_match(self, x: A.MatchEx) -> Type:
        scrut = self.infer(x.scrut)
        kind, ok = self.classify_match(x)
        self.out.match_kind[id(x)] = kind
        if not ok:
            return UNKNOWN  # 分类已报错（混用/字面量臂），不再叠加穷尽诊断

        if kind == MatchKind.ENUM:
            self.check_enum_exhaustive(x)
        elif kind in (MatchKind.RESULT, MatchKind.OPTION):
            self.check_algebraic_exhaustive(x)

        body: Optional[Type] = None
        for arm in x.arms:
            bind_types = self.arm_bind_types(arm.pat, kind, scrut)
            self.push_scope()
            # 臂绑定写臂级作用域（NAME-SCOPE-001）：臂内可见，臂结束销毁，不残留外层
            for name, t in bind_types.items():
                self.define_var(name, t)
            bt = self.infer(arm.body)
            self.pop_scope()
            if body is None:
                body = bt
            else:
                if type_equal(body, bt):
                    pass
                elif assignable(bt, body):
                    pass  # 臂体可归入已定类型 → 保持（如 [] 归入 [Ref]）
                elif assignable(body, bt):
                    body = bt
                else:
                    if is_concrete(body) and is_concrete(bt):
                        self.errf(arm.line, E3001, f"match 臂类型不一致：{type_str(body)} 与 {type_str(bt)}")
                    elif not is_concrete(body):
                        body = bt
        if body is None:
            body = UNKNOWN
        return body

    def arm_bind_types(self, p: A.Pat, kind: MatchKind, scrut: Type) -> dict[str, Type]:
        out: dict[str, Type] = {}

        def walk(p: A.Pat, t: Optional[Type]) -> None:
            if isinstance(p, A.BindPat):
                out[p.name] = t if t is not None else UNKNOWN
                self.out.pat_ty[id(p)] = out[p.name]
            elif isinstance(p, A.CasePat):
                sym = self.res.pat_refs.get(id(p))
                if sym is None:
                    return
                for i, sp in enumerate(p.subs):
                    if sym.struct_name == "Result":
                        sub_t = result_ok(scrut) if sym.name == "Ok" else ERROR_T
                    elif sym.struct_name == "Option":
                        sub_t = scrut.elem if scrut.elem is not None else UNKNOWN
                    else:
                        en = self._lookup_type_sym(sym.struct_name)
                        if en is not None and i < len(p.subs):
                            sub_t = self.case_payload_type(en, sym.case_index, i)
                        else:
                            sub_t = None
                    if sub_t is None:
                        sub_t = UNKNOWN
                    walk(sp, sub_t)

        walk(p, UNKNOWN)
        return out

    def case_payload_type(self, enum_sym: Symbol, case_idx: int, payload_idx: int) -> Type:
        d = enum_sym.decl
        if not isinstance(d, A.EnumDecl) or case_idx >= len(d.cases) or payload_idx >= len(d.cases[case_idx].payloads):
            return UNKNOWN
        pt = d.cases[case_idx].payloads[payload_idx]
        if pt is None:
            return UNKNOWN
        return self.resolve_type_ex(pt, set(enum_sym.type_params))

    def classify_match(self, x: A.MatchEx) -> tuple[MatchKind, bool]:
        """match 分类（唯一判定点）。第二返回值 False 表示分类阶段已报错。"""
        has_case = has_lit = False
        result_ctor = option_ctor = False
        enum_name = ""
        mixed_enum = False
        for arm in x.arms:
            p = arm.pat
            if isinstance(p, A.LitPat):
                has_lit = True
            elif isinstance(p, A.CasePat):
                has_case = True
                sym = self.res.pat_refs.get(id(p))
                if sym is None:
                    continue
                if sym.struct_name == "Result":
                    result_ctor = True
                elif sym.struct_name == "Option":
                    option_ctor = True
                else:
                    if enum_name == "":
                        enum_name = sym.struct_name
                    elif enum_name != sym.struct_name:
                        mixed_enum = True
        if result_ctor and option_ctor:
            self.errf(x.line, E4003, "Result 与 Option 的模式混用")
            return MatchKind.LITERAL, False
        if result_ctor:
            if has_lit:
                self.errf(x.line, E4002, "代数类型的 match 不能混入字面量模式")
                return MatchKind.RESULT, False
            return MatchKind.RESULT, True
        if option_ctor:
            if has_lit:
                self.errf(x.line, E4002, "代数类型的 match 不能混入字面量模式")
                return MatchKind.OPTION, False
            return MatchKind.OPTION, True
        if has_case:
            if has_lit:
                self.errf(x.line, E4002, "enum 的 match 不能混入字面量模式")
                return MatchKind.ENUM, False
            if mixed_enum:
                self.errf(x.line, E4003, "match 混入了不同 enum 的构造器")
                return MatchKind.ENUM, False
            return MatchKind.ENUM, True
        return MatchKind.LITERAL, True

    def check_enum_exhaustive(self, x: A.MatchEx) -> None:
        covered: set[str] = set()
        has_wild = False
        enum_sym: Optional[Symbol] = None
        for arm in x.arms:
            p = arm.pat
            if isinstance(p, A.CasePat):
                sym = self.res.pat_refs.get(id(p))
                if sym is None:
                    continue
                if enum_sym is None:
                    en = self._lookup_type_sym(sym.struct_name)
                    if en is not None:
                        enum_sym = en
                covered.add(p.name)
            elif isinstance(p, (A.WildPat, A.BindPat)):
                has_wild = True
        if has_wild or enum_sym is None:
            return
        d = enum_sym.decl
        if not isinstance(d, A.EnumDecl):
            return
        missing = [cs.name for cs in d.cases if cs.name not in covered]
        if missing:
            self.errf(x.line, E4001, "match 不穷尽，缺少: " + join_names(missing))

    def check_algebraic_exhaustive(self, x: A.MatchEx) -> None:
        pos = neg = False
        for arm in x.arms:
            p = arm.pat
            if isinstance(p, A.CasePat):
                if p.name in ("Ok", "Some"):
                    pos = True
                elif p.name in ("Err", "None"):
                    neg = True
            elif isinstance(p, A.WildPat):
                pos = neg = True
        if not pos or not neg:
            self.errf(x.line, E4001, "match 不穷尽：Result/Option 须覆盖两臂")

    # ———— ? 传播 ————
    def infer_try(self, t: A.TryEx) -> Type:
        if not self.in_fn or self.fn_ret is None or self.fn_ret.kind != TK.RESULT:
            self.errf(t.line, E3005, "带 ? 的函数须注解为 Result[T, error]")
        return self.infer_try_operand(t)

    def infer_try_operand(self, t: A.TryEx) -> Type:
        op = self.infer(t.x)
        if op.kind == TK.RESULT:
            return result_ok(op)
        if op.kind in (TK.ANY, TK.UNKNOWN, TK.INVALID):
            return UNKNOWN
        if op.kind == TK.OPTION:
            self.errf(t.line, E3008, f"? 的操作数须为 Result，得到 {type_str(op)}（Option 不可用 ?）")
            return INVALID
        self.errf(t.line, E3008, f"? 的操作数须为 Result，得到 {type_str(op)}")
        return INVALID

    # ———— 字段与索引 ————
    def field_type(self, obj_ty: Type, name: str, line: int) -> Type:
        obj_ty = _deref(obj_ty)
        if obj_ty.kind == TK.STRUCT and obj_ty.sym is not None:
            for i, f in enumerate(obj_ty.sym.fields):
                if f == name:
                    if i < len(obj_ty.sym.field_types) and obj_ty.sym.field_types[i] is not None:
                        return self.resolve_type_ex(obj_ty.sym.field_types[i], set(obj_ty.sym.type_params))
                    return UNKNOWN
            self.errf(line, E3004, f"{obj_ty.sym.name} 没有字段 {name}")
            return INVALID
        if obj_ty.kind == TK.ERROR:
            if name == "msg":
                return STRING
            self.errf(line, E3004, "error 只有 msg 字段")
            return INVALID
        if not is_concrete(obj_ty):
            return UNKNOWN
        self.errf(line, E3004, f"{type_str(obj_ty)} 没有字段 {name}")
        return INVALID

    def index_elem_ty(self, obj_ty: Type, line: int) -> Type:
        obj_ty = _deref(obj_ty)
        if obj_ty.kind == TK.SLICE:
            return obj_ty.elem if obj_ty.elem is not None else UNKNOWN
        if obj_ty.kind == TK.MAP:
            return obj_ty.elem if obj_ty.elem is not None else UNKNOWN
        if obj_ty.kind == TK.STRING:
            return STRING
        if obj_ty.kind in (TK.ANY, TK.UNKNOWN, TK.INVALID):
            return UNKNOWN
        self.errf(line, E3001, f"{type_str(obj_ty)} 不可索引")
        return INVALID

    def check_index_key(self, obj_ty: Type, idx_ty: Type, line: int) -> None:
        if not is_concrete(idx_ty):
            return
        obj_ty = _deref(obj_ty)
        if obj_ty.kind in (TK.SLICE, TK.STRING):
            if idx_ty.kind != TK.INT:
                self.errf(line, E3013, f"索引须为 int，得到 {type_str(idx_ty)}")
        elif obj_ty.kind == TK.MAP:
            if idx_ty.kind not in (TK.INT, TK.STRING, TK.BOOL):
                self.errf(line, E3014, f"映射键须为 int/string/bool，得到 {type_str(idx_ty)}")

    # ———— 函数字面量 ————
    def infer_fn_lit(self, x: A.FnLit) -> Type:
        saved = (self.fn_ret, self.in_fn, self.scopes)
        self.scopes = [{}]
        self.fn_ret = self.resolve_type_ex(x.ret, set(x.type_params)) if x.ret is not None else None
        self.in_fn = True
        ps: list[Type] = []
        for p in x.params:
            if p.typ is None:
                self.errf(x.line, E3007, f"函数字面量参数 {p.name} 缺少类型注解")
                ps.append(UNKNOWN)
                self.scopes[0][p.name] = UNKNOWN
                continue
            pt = self.resolve_type_ex(p.typ, set(x.type_params))
            ps.append(pt)
            self.scopes[0][p.name] = pt
        for s in x.body:
            self.stmt(s)
        self.fn_ret, self.in_fn, self.scopes = saved
        ret = UNKNOWN
        if x.ret is not None:
            ret = self.resolve_type_ex(x.ret, set(x.type_params))
        return fn_of(ps, ret)


_BINARY_OP_NAMES = {
    K.PLUS: "+", K.MINUS: "-", K.STAR: "*", K.SLASH: "/", K.PERCENT: "%",
}


def _builtin_recv(c: "Checker", a: list[Type]) -> Type:
    if len(a) == 1 and a[0].kind == TK.CHAN:
        return result_of(a[0].elem if a[0].elem is not None else UNKNOWN, ERROR_T)
    return result_of(UNKNOWN, ERROR_T)


def _builtin_push(c: "Checker", a: list[Type]) -> Type:
    if len(a) > 0:
        return a[0]
    return UNKNOWN


# 内建函数签名（参数不逐位检查；与 Go builtinSigs 一致）
BUILTIN_SIGS = {
    "println": lambda c, a: UNIT,
    "print": lambda c, a: UNIT,
    "len": lambda c, a: INT,
    "str": lambda c, a: STRING,
    "int": lambda c, a: INT,
    "float": lambda c, a: FLOAT,
    "keys": lambda c, a: slice_of(UNKNOWN),
    "values": lambda c, a: slice_of(UNKNOWN),
    "has": lambda c, a: BOOL,
    "push": _builtin_push,
    "assert": lambda c, a: UNIT,
    "err": lambda c, a: ERROR_T,
    "bytes": lambda c, a: slice_of(INT),
    "char": lambda c, a: STRING,
    "jsonParse": lambda c, a: ANY,
    "jsonStringify": lambda c, a: STRING,
    "isNone": lambda c, a: BOOL,
    "makeChan": lambda c, a: chan_of(UNKNOWN),
    "send": lambda c, a: UNIT,
    "recv": _builtin_recv,
}


def check(prog: list[A.Stmt], res: ResolveResult) -> CheckResult:
    """运行类型检查（res 需先通过 semantic.resolve）。"""
    c = Checker(res)
    c.walk(prog)
    return c.out
