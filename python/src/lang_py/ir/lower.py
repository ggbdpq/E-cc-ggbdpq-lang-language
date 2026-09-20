"""Lower：HIR → Core IR（lower.go 语义等价）。

机械降级——局部槽位化、迭代协议归类、元数据剥离。
前置条件：HIR 构建成功（semantic/types 无诊断）。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from ..hir import nodes as H
from ..hir.nodes import typ as hir_typ
from ..semantic.symbols import Symbol
from ..typesys.model import UNKNOWN, Type, TK
from . import nodes as I
from .nodes import IterKind, MatchKind


def _hir_kind_to_int_match(kind: int) -> MatchKind:
    return MatchKind(kind)


@dataclass
class _Scope:
    vars: dict[str, I.Local]
    parent: Optional["_Scope"] = None

    def lookup(self, name: str) -> Optional[I.Local]:
        cur: Optional[_Scope] = self
        while cur is not None:
            if name in cur.vars:
                return cur.vars[name]
            cur = cur.parent
        return None


@dataclass
class _FnCtx:
    scopes: _Scope
    locals: list[I.Local]
    in_fn: bool


class Lowerer:
    def __init__(self) -> None:
        self.out_structs: list[I.StructDecl] = []
        self.out_enums: list[I.EnumDecl] = []
        self.out_globals: list[I.Let] = []
        self.out_fns: list[I.Fn] = []
        self.out_main: Optional[I.Fn] = None
        self.scopes = _Scope({})
        self.locals: list[I.Local] = []
        self.in_fn = False

    def define(self, v: I.Local) -> None:
        self.scopes.vars[v.name] = v
        if self.in_fn:
            self.locals.append(v)

    def push(self) -> None:
        self.scopes = _Scope({}, self.scopes)

    def pop(self) -> None:
        self.scopes = self.scopes.parent

    def local(self, hn: Optional[H.NodeMeta], name: str, mutable: bool, t: Optional[Type]) -> I.Local:
        sym = hn.sym if hn is not None else None
        return I.Local(name=name, mutable=mutable, typ=t if t is not None else UNKNOWN, sym=sym)

    # ———— 程序 ————
    def lower_prog(self, p: H.Prog) -> I.Prog:
        for s in p.structs:
            sd = I.StructDecl(name=s.name, type_params=list(s.type_params),
                              fields=[I.FieldDecl(name=f.name, typ=f.typ) for f in s.fields])
            self.out_structs.append(sd)
        for e in p.enums:
            ed = I.EnumDecl(name=e.name, type_params=list(e.type_params),
                            cases=[I.Case(name=c.name, payloads=list(c.payloads)) for c in e.cases])
            self.out_enums.append(ed)
        for g in p.globals:
            self.out_globals.append(self.lower_let(g))
        for f in p.fns:
            fn = self.lower_fn(f)
            if f is p.main:
                self.out_main = fn
            self.out_fns.append(fn)
        return I.Prog(structs=self.out_structs, enums=self.out_enums, globals=self.out_globals,
                      fns=self.out_fns, main=self.out_main)

    def lower_fn(self, hf: H.Fn) -> I.Fn:
        saved = (self.scopes, self.locals, self.in_fn)
        self.scopes = _Scope({})
        self.locals = []
        self.in_fn = True
        # 参数/接收者局部不带 fn 节点的符号：后端按名引用，避免误把参数当 fn 值绑定
        recv = None
        if hf.recv is not None:
            recv = self.local(None, hf.recv.name, False, hf.recv.typ)
            self.scopes.vars[recv.name] = recv
        params = []
        for p in hf.params:
            pl = self.local(None, p.name, False, p.typ)
            self.scopes.vars[p.name] = pl
            params.append(pl)
        body = self.lower_stmts(hf.body)
        fn = I.Fn(name=hf.name, sym=hf.sym, recv=recv, type_params=list(hf.type_params),
                  params=params, ret=hf.ret, locals=self.locals, body=body,
                  extern=hf.extern, extern_host=hf.extern_host)
        self.scopes, self.locals, self.in_fn = saved
        return fn

    def lower_let(self, hl: H.Let) -> I.Let:
        init = self.lower_expr(hl.init)
        name = hl.name
        t = hl.meta.typ
        if t is None or t.kind == TK.UNKNOWN:
            t = hir_typ(hl.init)
        v = self.local(hl.meta, name, hl.mutable, t)
        if name != "_":
            self.define(v)
        else:
            v = I.Local(name="_", mutable=v.mutable, typ=v.typ, sym=v.sym)
        return I.Let(local=v, init=init)

    def lower_stmts(self, list_: list[H.Stmt]) -> list[I.Stmt]:
        return [self.lower_stmt(s) for s in list_]

    def lower_stmt(self, s: H.Stmt) -> I.Stmt:
        if isinstance(s, H.Let):
            return self.lower_let(s)
        if isinstance(s, H.Assign):
            return I.Assign(target=self.lower_expr(s.target), op=_hir_op(s.op), val=self.lower_expr(s.rhs))
        if isinstance(s, H.Eval):
            return I.Eval(x=self.lower_expr(s.x))
        if isinstance(s, H.Return):
            return I.Return(val=self.lower_expr(s.val) if s.val is not None else None)
        if isinstance(s, H.If):
            return I.If(cond=self.lower_expr(s.cond),
                        then=self.lower_block(s.then),
                        els=self.lower_block(s.els))
        if isinstance(s, H.While):
            return I.While(cond=self.lower_expr(s.cond), body=self.lower_block(s.body))
        if isinstance(s, H.ForIn):
            self.push()
            v = self.local(s.meta, s.var, False, s.var_type)
            if s.var != "_":
                self.scopes.vars[s.var] = v
            fn = I.ForIn(var=v, kind=_iter_kind(H.typ(s.iter)),
                         iter=self.lower_expr(s.iter), body=self.lower_stmts(s.body))
            self.pop()
            return fn
        if isinstance(s, H.Break):
            return I.Break()
        if isinstance(s, H.Continue):
            return I.Continue()
        if isinstance(s, H.Spawn):
            return I.Spawn(fn=s.fn, args=[self.lower_expr(a) for a in s.args])
        if isinstance(s, H.FnStmt):
            saved = (self.scopes, self.locals, self.in_fn)
            fn = self.lower_fn(s.fn)
            self.scopes, self.locals, self.in_fn = saved
            return I.FnDef(fn=fn)
        raise AssertionError(f"未知 HIR 语句 {s!r}")

    def lower_block(self, list_: list[H.Stmt]) -> list[I.Stmt]:
        self.push()
        out = self.lower_stmts(list_)
        self.pop()
        return out

    # ———— 表达式 ————
    def lower_expr(self, e: H.Expr) -> I.Expr:
        t = hir_typ(e)
        if isinstance(e, H.Lit):
            if e.kind == H.LitKind.INT:
                return I.Const(kind=I.ConstKind.INT, typ=t, i64=e.i64)
            if e.kind == H.LitKind.FLOAT:
                return I.Const(kind=I.ConstKind.FLOAT, typ=t, f64=e.f64)
            if e.kind == H.LitKind.STR:
                return I.Const(kind=I.ConstKind.STR, typ=t, s=e.s)
            return I.Const(kind=I.ConstKind.BOOL, typ=t, b=e.b)
        if isinstance(e, H.LocalRef):
            v = self.scopes.lookup(e.name)
            if v is not None:
                return I.LocalGet(local=v, typ=t)
            # 全局（跨 fn 引用）：降级为按名槽位
            return I.LocalGet(local=I.Local(name=e.name, mutable=False, typ=t, sym=e.meta.sym), typ=t)
        if isinstance(e, H.FnRef):
            return I.FnGet(name=e.name, sym=e.meta.sym, typ=t)
        if isinstance(e, H.BuiltinRef):
            return I.BuiltinGet(name=e.name, typ=t)
        if isinstance(e, H.Ctor):
            return I.Ctor(sym=e.ctor_sym, args=[self.lower_expr(a) for a in e.args], typ=t)
        if isinstance(e, H.Call):
            if e.fn is not None:
                n = I.Call(typ=t, fn=e.fn)
            elif e.builtin:
                n = I.Call(typ=t, builtin=e.builtin)
            else:
                n = I.Call(typ=t, callee=self.lower_expr(e.callee))
            n.args.extend(self.lower_expr(a) for a in e.args)
            return n
        if isinstance(e, H.MethodCall):
            if e.method is not None:
                n = I.Call(typ=t, method=e.method, recv=self.lower_expr(e.recv))
            else:
                # 字段函数值调用：callee = 字段读取
                n = I.Call(typ=t, callee=self.lower_expr(
                    H.Field(meta=e.meta, obj=e.recv, name=e.field)))
            n.args.extend(self.lower_expr(a) for a in e.args)
            return n
        if isinstance(e, H.Unary):
            return I.Unary(op=_hir_op(e.op), x=self.lower_expr(e.x), typ=t)
        if isinstance(e, H.Binary):
            return I.Binary(op=_hir_op(e.op), l=self.lower_expr(e.l), r=self.lower_expr(e.r), typ=t)
        if isinstance(e, H.AndThen):
            return I.Binary(op=I.OP_ANDAND, l=self.lower_expr(e.l), r=self.lower_expr(e.r), typ=t)
        if isinstance(e, H.OrElse):
            return I.Binary(op=I.OP_OROR, l=self.lower_expr(e.l), r=self.lower_expr(e.r), typ=t)
        if isinstance(e, H.Range):
            return I.Range(lo=self.lower_expr(e.lo), hi=self.lower_expr(e.hi), typ=t)
        if isinstance(e, H.Index):
            return I.Index(obj=self.lower_expr(e.obj), idx=self.lower_expr(e.idx), typ=t)
        if isinstance(e, H.Slice):
            return I.Slice(obj=self.lower_expr(e.obj), lo=self.lower_expr(e.lo),
                           hi=self.lower_expr(e.hi), typ=t)
        if isinstance(e, H.Field):
            return I.Field(obj=self.lower_expr(e.obj), name=e.name, typ=t)
        if isinstance(e, H.Try):
            return I.Try(x=self.lower_expr(e.x), typ=t)
        if isinstance(e, H.StructLit):
            n = I.StructLit(struct=e.meta.sym, typ=t, fields=[])
            for f in e.fields:
                n.fields.append(I.FieldValue(name=f.name, val=self.lower_expr(f.val)))
            return n
        if isinstance(e, H.MapLit):
            n = I.MapLit(typ=t, keys=[], vals=[])
            for k, v in zip(e.keys, e.vals):
                n.keys.append(self.lower_expr(k))
                n.vals.append(self.lower_expr(v))
            return n
        if isinstance(e, H.SliceLit):
            return I.SliceLit(elems=[self.lower_expr(el) for el in e.elems], typ=t)
        if isinstance(e, H.Match):
            n = I.Match(scrut=self.lower_expr(e.scrut), kind=MatchKind(e.kind),
                        arms=[], typ=t)
            for arm in e.arms:
                self.push()
                ir_arm = I.Arm(pat=self.lower_pat(arm.pat), body=self.lower_expr(arm.body))
                self.pop()
                n.arms.append(ir_arm)
            return n
        if isinstance(e, H.FnLit):
            saved = (self.scopes, self.locals, self.in_fn)
            # 闭包作用域继承外层链（VALUE-CAPTURE-004 按引用捕获）：
            # 捕获变量解析到外层的同一 Local 槽位，捕获关系显式保留在 IR 中（v0.9 语义收口）
            self.push()
            self.locals = []
            self.in_fn = True
            params = []
            for p in e.params:
                pl = self.local(e.meta, p.name, False, p.typ)
                self.scopes.vars[p.name] = pl
                params.append(pl)
            body = self.lower_stmts(e.body)
            fn_locals = self.locals
            self.scopes, self.locals, self.in_fn = saved
            return I.FnLit(params=params, ret=e.ret, locals=fn_locals, body=body, typ=t)
        raise AssertionError(f"未知 HIR 表达式 {e!r}")

    def lower_pat(self, p: H.Pat) -> I.Pat:
        if isinstance(p, H.WildPat):
            return I.WildPat()
        if isinstance(p, H.BindPat):
            v = self.local(None, p.name, False, p.typ)
            if p.name != "_":
                self.scopes.vars[p.name] = v
            return I.BindPat(name=p.name, typ=p.typ, local=v)
        if isinstance(p, H.LitPat):
            return I.LitPat(val=self.lower_expr(p.val))
        if isinstance(p, H.CtorPat):
            return I.CtorPat(sym=p.ctor, subs=[self.lower_pat(sp) for sp in p.subs])
        return I.WildPat()


_KIND_TO_IR_OP: dict = {}


def _hir_op(k) -> str:
    """syntax.Kind → IR 运算符文本（记号显示名即文本）。"""
    from ..syntax.tokens import kind_name
    text = kind_name(k) if not isinstance(k, str) else k
    return text


def _iter_kind(t: Optional[Type]) -> IterKind:
    if t is None:
        return IterKind.UNKNOWN
    if t.kind == TK.SLICE:
        return IterKind.SLICE
    if t.kind == TK.MAP:
        return IterKind.MAP
    if t.kind == TK.STRING:
        return IterKind.STRING
    if t.kind == TK.RANGE:
        return IterKind.RANGE
    return IterKind.UNKNOWN


def lower(p: H.Prog) -> I.Prog:
    """降级整个程序。"""
    return Lowerer().lower_prog(p)
