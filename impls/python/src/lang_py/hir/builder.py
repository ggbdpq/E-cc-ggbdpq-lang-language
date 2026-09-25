"""HIR 构建（build.go 语义等价）：AST + resolver 产物 + checker 产物 → Typed HIR。

构建前提：semantic/types 两阶段均无诊断（调用方负责）。
"""

from __future__ import annotations

from typing import Optional

from ..semantic.symbols import KCTOR, KENUM, KFN, KSTRUCT, ResolveResult, Symbol
from ..syntax import ast_nodes as A
from ..syntax.span import Position, Span
from ..syntax.tokens import K
from ..typesys.anno import resolve_anno
from ..typesys.model import BOOL, FLOAT, INT, RANGE, STRING, UNKNOWN, Type, TK
from . import nodes as H
from .nodes import NodeMeta


def _struct_of(t: Optional[Type]) -> Optional[Symbol]:
    while t is not None and t.kind == TK.REF:
        t = t.elem
    if t is not None and t.kind == TK.STRUCT:
        return t.sym
    return None


class Builder:
    def __init__(self, res: ResolveResult, ty) -> None:
        self.res = res
        self.ty = ty  # typesys.checker.CheckResult
        self.next_id = 0
        self.ctors: dict[str, list[Symbol]] = {}
        self.out_structs: list[H.StructDecl] = []
        self.out_enums: list[H.EnumDecl] = []
        self.out_globals: list[H.Let] = []
        self.out_fns: list[H.Fn] = []
        self.out_main: Optional[H.Fn] = None

    def node(self, sp: Span, line: int, sym: Optional[Symbol], t: Optional[Type]) -> NodeMeta:
        self.next_id += 1
        return NodeMeta(id=self.next_id, line=line, span=sp, sym=sym, typ=t if t is not None else UNKNOWN)

    def anno(self, t: Optional[A.TypeEx], tparams: list[str]) -> Type:
        if t is None:
            return UNKNOWN
        return resolve_anno(self.res, t, tparams)

    # ———— 查表 ————
    def expr_type(self, e: A.Expr) -> Type:
        return self.ty.expr_ty.get(id(e), UNKNOWN)

    def pat_type(self, p: A.Pat) -> Type:
        return self.ty.pat_ty.get(id(p), UNKNOWN)

    def sym_type(self, s: Optional[Symbol]) -> Type:
        if s is None:
            return UNKNOWN
        return self.ty.sym_ty.get(s.id, UNKNOWN)

    def type_sym_by_name(self, name: str) -> Optional[Symbol]:
        s = self.res.builtin.get(name)
        if s is not None and s.kind == KSTRUCT:
            return s
        for s in self.res.symbols:
            if s.kind in (KSTRUCT, KENUM) and s.name == name:
                return s
        return None

    # ———— 顶层收集 ————
    def collect_decls(self, prog: list[A.Stmt]) -> None:
        for s in prog:
            if isinstance(s, A.StructDecl):
                self.out_structs.append(self.struct_decl(s))
            elif isinstance(s, A.EnumDecl):
                self.out_enums.append(self.enum_decl(s))
        for s in prog:
            if isinstance(s, A.LetStmt):
                self.out_globals.append(self.let(s))
        for s in prog:
            if isinstance(s, A.FnDecl) and s.recv is None:
                fn = self.fn_decl(s, self.res.decl_syms.get(id(s)))
                if s.name == "main" and self.out_main is None:
                    self.out_main = fn
                self.out_fns.append(fn)
            elif isinstance(s, A.ExternDecl):
                for f in s.fns:
                    sym = self.extern_sym(f.name)
                    ret = self.anno(f.ret, f.type_params) if f.ret is not None else None
                    meta = self.node(f.span, f.line, sym, ret)
                    params = [H.Param(name=p.name, typ=self.anno(p.typ, f.type_params)) for p in f.params]
                    self.out_fns.append(H.Fn(meta=meta, name=f.name, sym=sym, recv=None,
                                             type_params=list(f.type_params), params=params,
                                             ret=ret, body=[], extern=True, extern_host=s.host))
        for s in prog:
            if isinstance(s, A.FnDecl) and s.recv is not None:
                st = self.recv_struct_sym(s)
                if st is not None:
                    self.out_fns.append(self.fn_decl(s, st.methods.get(s.name)))

    def extern_sym(self, name: str) -> Optional[Symbol]:
        for s in self.res.symbols:
            if s.kind == KFN and s.extern and s.name == name:
                return s
        return None

    def recv_struct_sym(self, d: A.FnDecl) -> Optional[Symbol]:
        assert d.recv is not None
        nt = d.recv.typ
        if not isinstance(nt, A.NamedType):
            return None
        s = self.res.builtin.get(nt.name)
        if s is not None and s.kind == KSTRUCT:
            return s
        return self.type_sym_by_name(nt.name)

    def struct_decl(self, d: A.StructDecl) -> H.StructDecl:
        sym = self.type_sym_by_name(d.name)
        sd = H.StructDecl(meta=self.node(d.span, d.line, sym, None),
                          name=d.name, type_params=list(d.type_params), fields=[])
        for f in d.fields:
            sd.fields.append(H.FieldDecl(name=f.name, typ=self.anno(f.typ, d.type_params)))
        return sd

    def enum_decl(self, d: A.EnumDecl) -> H.EnumDecl:
        sym = self.type_sym_by_name(d.name)
        ed = H.EnumDecl(meta=self.node(d.span, d.line, sym, None),
                        name=d.name, type_params=list(d.type_params), cases=[])
        for c in d.cases:
            csym = None
            for s in self.ctors.get(d.name, []):
                if s.name == c.name:
                    csym = s
            payloads = [self.anno(pt, d.type_params) for pt in c.payloads]
            ed.cases.append(H.Case(sym=csym, name=c.name, payloads=payloads))
        return ed

    def let(self, d: A.LetStmt) -> H.Let:
        sym = self.res.decl_syms.get(id(d))
        return H.Let(meta=self.node(d.span, d.line, sym, self.sym_type(sym)),
                     name=d.name, mutable=d.mutable, init=self.expr(d.init))

    def fn_decl(self, d: A.FnDecl, sym: Optional[Symbol]) -> H.Fn:
        ret = self.anno(d.ret, d.type_params) if d.ret is not None else None
        recv = None
        if d.recv is not None:
            recv = H.Param(name=d.recv.name, typ=self.anno(d.recv.typ, []))
        params = [H.Param(name=p.name, typ=self.anno(p.typ, d.type_params)) for p in d.params]
        body = [self.stmt(s) for s in d.body]
        return H.Fn(meta=self.node(d.span, d.line, sym, ret),
                    name=d.name, sym=sym, recv=recv, type_params=list(d.type_params),
                    params=params, ret=ret, body=body)

    # ———— 语句 ————
    def stmts(self, list_: list[A.Stmt]) -> list[H.Stmt]:
        return [self.stmt(s) for s in list_]

    def stmt(self, s: A.Stmt) -> H.Stmt:
        if isinstance(s, A.LetStmt):
            return self.let(s)
        if isinstance(s, A.AssignStmt):
            return H.Assign(meta=self.node(s.span, s.line, None, None),
                            target=self.assign_target(s.target), op=s.op, rhs=self.expr(s.val))
        if isinstance(s, A.ExprStmt):
            return H.Eval(meta=self.node(s.span, s.line, None, None), x=self.expr(s.x))
        if isinstance(s, A.ReturnStmt):
            return H.Return(meta=self.node(s.span, s.line, None, None),
                            val=self.expr(s.val) if s.val is not None else None)
        if isinstance(s, A.IfStmt):
            return H.If(meta=self.node(s.span, s.line, None, None),
                        cond=self.expr(s.cond),
                        then=self.stmts(s.then),
                        els=self.stmts(s.els) if s.els is not None else [])
        if isinstance(s, A.WhileStmt):
            return H.While(meta=self.node(s.span, s.line, None, None),
                           cond=self.expr(s.cond), body=self.stmts(s.body))
        if isinstance(s, A.ForInStmt):
            sym = self.res.decl_syms.get(id(s))
            return H.ForIn(meta=self.node(s.span, s.line, sym, None),
                           var=s.var, var_type=self.sym_type(sym),
                           iter=self.expr(s.iter), body=self.stmts(s.body))
        if isinstance(s, A.BreakStmt):
            return H.Break(meta=self.node(s.span, s.line, None, None))
        if isinstance(s, A.ContinueStmt):
            return H.Continue(meta=self.node(s.span, s.line, None, None))
        if isinstance(s, A.GoStmt):
            fn = None
            if isinstance(s.call.callee, A.Ident):
                fn = self.res.refs.get(id(s.call.callee))
            return H.Spawn(meta=self.node(s.span, s.line, None, None),
                           fn=fn, args=[self.expr(a) for a in s.call.args])
        if isinstance(s, A.FnDecl):
            return H.FnStmt(meta=self.node(s.span, s.line, None, None),
                            fn=self.fn_decl(s, self.res.decl_syms.get(id(s))))
        raise AssertionError(f"未知语句 {s!r}")

    def assign_target(self, t: A.Expr) -> H.Expr:
        if isinstance(t, A.Ident):
            sym = self.res.refs.get(id(t))
            return H.LocalRef(meta=self.node(t.span, t.line, sym, self.sym_type(sym)), name=t.name)
        if isinstance(t, A.FieldEx):
            return H.Field(meta=self.node(t.span, t.line, None, None),
                           obj=self.expr(t.obj), name=t.name)
        if isinstance(t, A.IndexEx):
            return H.Index(meta=self.node(t.span, t.line, None, None),
                           obj=self.expr(t.obj), idx=self.expr(t.idx))
        raise AssertionError("赋值目标不合法")

    # ———— 表达式 ————
    def expr(self, e: A.Expr) -> H.Expr:
        if isinstance(e, A.IntLit):
            return H.Lit(meta=self.node(e.span, e.line, None, INT), kind=H.LitKind.INT, i64=e.val)
        if isinstance(e, A.FloatLit):
            return H.Lit(meta=self.node(e.span, e.line, None, FLOAT), kind=H.LitKind.FLOAT, f64=e.val)
        if isinstance(e, A.StrLit):
            return H.Lit(meta=self.node(e.span, e.line, None, STRING), kind=H.LitKind.STR, s=e.val)
        if isinstance(e, A.BoolLit):
            return H.Lit(meta=self.node(e.span, e.line, None, BOOL), kind=H.LitKind.BOOL, b=e.val)
        if isinstance(e, A.Ident):
            return self.ident(e)
        if isinstance(e, A.Unary):
            return H.Unary(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                           op=e.op, x=self.expr(e.x))
        if isinstance(e, A.Binary):
            if e.op == K.AND:
                return H.AndThen(meta=self.node(e.span, e.line, None, BOOL),
                                 l=self.expr(e.l), r=self.expr(e.r))
            if e.op == K.OR:
                return H.OrElse(meta=self.node(e.span, e.line, None, BOOL),
                                l=self.expr(e.l), r=self.expr(e.r))
            return H.Binary(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                            op=e.op, l=self.expr(e.l), r=self.expr(e.r))
        if isinstance(e, A.RangeEx):
            return H.Range(meta=self.node(e.span, e.line, None, RANGE),
                           lo=self.expr(e.lo), hi=self.expr(e.hi))
        if isinstance(e, A.CallEx):
            return self.call(e)
        if isinstance(e, A.IndexEx):
            return H.Index(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                           obj=self.expr(e.obj), idx=self.expr(e.idx))
        if isinstance(e, A.SliceEx):
            return H.Slice(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                           obj=self.expr(e.obj), lo=self.expr(e.lo), hi=self.expr(e.hi))
        if isinstance(e, A.FieldEx):
            return H.Field(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                           obj=self.expr(e.obj), name=e.name)
        if isinstance(e, A.TryEx):
            return H.Try(meta=self.node(e.span, e.line, None, self.expr_type(e)), x=self.expr(e.x))
        if isinstance(e, A.StructLit):
            fields = [H.FieldValue(name=f.name, val=self.expr(f.val)) for f in e.fields]
            return H.StructLit(meta=self.node(e.span, e.line, self.res.refs.get(id(e)), self.expr_type(e)),
                               fields=fields)
        if isinstance(e, A.MapLit):
            return H.MapLit(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                            keys=[self.expr(it.key) for it in e.items],
                            vals=[self.expr(it.val) for it in e.items])
        if isinstance(e, A.ArrLit):
            return H.SliceLit(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                              elems=[self.expr(el) for el in e.items])
        if isinstance(e, A.MatchEx):
            return self.match_(e)
        if isinstance(e, A.FnLit):
            params = [H.Param(name=p.name, typ=self.anno(p.typ, e.type_params)) for p in e.params]
            return H.FnLit(meta=self.node(e.span, e.line, None, self.expr_type(e)),
                           params=params,
                           ret=self.anno(e.ret, e.type_params),
                           body=self.stmts(e.body))
        raise AssertionError(f"未知表达式 {e!r}")

    def ident(self, x: A.Ident) -> H.Expr:
        t = self.expr_type(x)
        sym = self.res.refs.get(id(x))
        if sym is not None:
            if sym.kind == KFN:
                return H.FnRef(meta=self.node(x.span, x.line, sym, t), name=x.name)
            if sym.kind == KCTOR:
                return H.Ctor(meta=self.node(x.span, x.line, sym, t), ctor_sym=sym, args=[])
            return H.LocalRef(meta=self.node(x.span, x.line, sym, t), name=x.name)
        if x.name in self.res.builtin:
            return H.BuiltinRef(meta=self.node(x.span, x.line, None, t), name=x.name)
        # 未定义名（checker 已报，构建不该到这里）→ LocalRef 占位
        return H.LocalRef(meta=self.node(x.span, x.line, None, t), name=x.name)

    def call(self, x: A.CallEx) -> H.Expr:
        t = self.expr_type(x)
        if isinstance(x.callee, A.Ident):
            sym = self.res.refs.get(id(x.callee))
            if sym is not None and sym.kind == KCTOR:
                return H.Ctor(meta=self.node(x.span, x.line, sym, t), ctor_sym=sym,
                              args=[self.expr(a) for a in x.args])
            if sym is None and x.callee.name in self.res.builtin:
                return H.Call(meta=self.node(x.span, x.line, None, t), builtin=x.callee.name,
                              args=[self.expr(a) for a in x.args])
            if sym is not None and sym.kind == KFN:
                return H.Call(meta=self.node(x.span, x.line, sym, t), fn=sym,
                              args=[self.expr(a) for a in x.args])
        if isinstance(x.callee, A.FieldEx):
            fe = x.callee
            recv = self.expr(fe.obj)
            st = _struct_of(H.typ(recv))
            if st is not None and fe.name in st.methods:
                return H.MethodCall(meta=self.node(x.span, x.line, st.methods[fe.name], t),
                                    recv=recv, method=st.methods[fe.name],
                                    args=[self.expr(a) for a in x.args],
                                    name_span=_method_name_span(fe))
            if isinstance(fe.obj, A.Ident) and fe.obj.name == "http" and fe.name == "get" \
                    and "http" in self.res.builtin:
                return H.Call(meta=self.node(x.span, x.line, None, t), builtin="http.get",
                              args=[self.expr(a) for a in x.args])
            return H.MethodCall(meta=self.node(x.span, x.line, None, t),
                                recv=recv, field=fe.name,
                                args=[self.expr(a) for a in x.args])
        return H.Call(meta=self.node(x.span, x.line, None, t),
                      callee=self.expr(x.callee), args=[self.expr(a) for a in x.args])

    def match_(self, x: A.MatchEx) -> H.Expr:
        kind = self.ty.match_kind.get(id(x))
        kind_val = kind.value if kind is not None else 0
        n = H.Match(meta=self.node(x.span, x.line, None, self.expr_type(x)),
                    scrut=self.expr(x.scrut), kind=kind_val, arms=[])
        for arm in x.arms:
            n.arms.append(H.Arm(line=arm.line, pat=self.pat(arm.pat), body=self.expr(arm.body)))
        return n

    def pat(self, p: A.Pat) -> H.Pat:
        if isinstance(p, A.WildPat):
            return H.WildPat(line=p.line)
        if isinstance(p, A.BindPat):
            return H.BindPat(line=p.line, name=p.name, typ=self.pat_type(p))
        if isinstance(p, A.LitPat):
            return H.LitPat(line=p.line, val=self.expr(p.val))
        if isinstance(p, A.CasePat):
            subs = [self.pat(sp) for sp in p.subs]
            return H.CtorPat(line=p.line, ctor=self.res.pat_refs.get(id(p)), subs=subs)
        return H.WildPat(line=0)


def _method_name_span(fe: A.FieldEx) -> Span:
    sp = fe.span
    n = len(fe.name)
    return Span(start=Position(sp.end.offset - n, sp.end.line, sp.end.col - n), end=sp.end,
                file=sp.file)


def build(prog: list[A.Stmt], res: ResolveResult, ty) -> H.Prog:
    """构建 HIR。"""
    b = Builder(res, ty)
    for s in res.symbols:
        if s.kind == KCTOR and not s.builtin_mark:
            b.ctors.setdefault(s.struct_name, []).append(s)
    b.collect_decls(prog)
    return H.Prog(structs=b.out_structs, enums=b.out_enums, globals=b.out_globals,
                  fns=b.out_fns, main=b.out_main)
