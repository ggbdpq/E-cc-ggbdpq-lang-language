"""AST → 契约 JSON 节点树（dumpast.go 语义等价）。

nodeId 为契约层按先序分配的稳定编号（同一源码同一结果）。
"""

from __future__ import annotations

from typing import Optional

from ..semantic.symbols import ResolveResult
from ..syntax import ast_nodes as A
from ..syntax.tokens import kind_name
from .model import span_json

_ASSIGN_OP_NAMES = {"=": "=", "+=": "+=", "-=": "-=", "*=": "*=", "/=": "/="}


class _AstBuilder:
    def __init__(self, res: Optional[ResolveResult], ty) -> None:
        self.id = 0
        self.ty = ty
        self.res = res
        self.enable = ty is not None

    def next(self) -> int:
        self.id += 1
        return self.id

    def node(self, sp):
        return {"nodeId": self.next(), "span": span_json(sp)}

    def typ(self, e: A.Expr) -> str:
        if not self.enable or self.ty is None:
            return ""
        t = self.ty.expr_ty.get(id(e))
        from ..typesys.model import type_str
        return type_str(t) if t is not None else ""

    def _sym_type(self, st) -> str:
        if not self.enable:
            return ""
        sym = self.res.decl_syms.get(id(st)) if self.res is not None else None
        if sym is not None:
            t = self.ty.sym_ty.get(sym.id)
            if t is not None:
                from ..typesys.model import type_str
                return type_str(t)
        return ""

    def stmts(self, list_: list[A.Stmt]) -> list[dict]:
        return [self.stmt(st) for st in list_]

    def stmt(self, st: A.Stmt) -> dict:
        if isinstance(st, A.LetStmt):
            kind = "var" if st.mutable else "let"
            n = self.node(st.span)
            n["kind"] = kind
            n["name"] = st.name
            n["type"] = self._sym_type(st)
            n["children"] = [self.expr(st.init)]
            return n
        if isinstance(st, A.AssignStmt):
            n = self.node(st.span)
            n["kind"] = "assign"
            n["name"] = kind_name(st.op)
            n["children"] = [self.expr(st.target), self.expr(st.val)]
            return n
        if isinstance(st, A.ExprStmt):
            n = self.node(st.span)
            n["kind"] = "eval"
            n["children"] = [self.expr(st.x)]
            return n
        if isinstance(st, A.ReturnStmt):
            n = self.node(st.span)
            n["kind"] = "return"
            if st.val is not None:
                n["children"] = [self.expr(st.val)]
            return n
        if isinstance(st, A.IfStmt):
            n = self.node(st.span)
            n["kind"] = "if"
            n["children"] = [self.expr(st.cond)]
            n["children"].extend(self.stmts(st.then))
            if st.els:
                els = {"nodeId": self.next(), "kind": "else"}
                els["children"] = self.stmts(st.els)
                n["children"].append(els)
            return n
        if isinstance(st, A.WhileStmt):
            n = self.node(st.span)
            n["kind"] = "while"
            n["children"] = [self.expr(st.cond)]
            n["children"].extend(self.stmts(st.body))
            return n
        if isinstance(st, A.ForInStmt):
            n = self.node(st.span)
            n["kind"] = "forin"
            n["name"] = st.var
            n["type"] = self._sym_type(st)
            n["children"] = [self.expr(st.iter)]
            n["children"].extend(self.stmts(st.body))
            return n
        if isinstance(st, A.BreakStmt):
            n = self.node(st.span)
            n["kind"] = "break"
            return n
        if isinstance(st, A.ContinueStmt):
            n = self.node(st.span)
            n["kind"] = "continue"
            return n
        if isinstance(st, A.FnDecl):
            n = self.node(st.span)
            n["kind"] = "fn"
            if st.recv is not None:
                n["name"] = f"({st.recv.name}) {st.name}"
            else:
                n["name"] = st.name
            for p in st.params:
                n["children"] = n.get("children", []) + [self.param(p)]
            n["children"] = n.get("children", []) + self.stmts(st.body)
            return n
        if isinstance(st, A.StructDecl):
            n = self.node(st.span)
            n["kind"] = "struct"
            n["name"] = st.name
            for f in st.fields:
                fn = self.node(st.span)
                fn["kind"] = "field"
                fn["name"] = f.name
                fn["type"] = self.anno_type(f.typ)
                n.setdefault("children", []).append(fn)
            return n
        if isinstance(st, A.EnumDecl):
            n = self.node(st.span)
            n["kind"] = "enum"
            n["name"] = st.name
            for c in st.cases:
                cn = self.node(c.span)
                cn["kind"] = "case"
                cn["name"] = c.name
                for i, pt in enumerate(c.payloads):
                    pn = self.node(c.span)
                    pn["kind"] = "payload"
                    pn["name"] = str(i)
                    pn["type"] = self.anno_type(pt)
                    cn.setdefault("children", []).append(pn)
                n.setdefault("children", []).append(cn)
            return n
        if isinstance(st, A.ExternDecl):
            n = self.node(st.span)
            n["kind"] = "extern"
            n["name"] = st.host
            for fn in st.fns:
                fn_n = self.node(fn.span)
                fn_n["kind"] = "fn"
                fn_n["name"] = fn.name
                for p in fn.params:
                    fn_n.setdefault("children", []).append(self.param(p))
                n.setdefault("children", []).append(fn_n)
            return n
        if isinstance(st, A.GoStmt):
            n = self.node(st.span)
            n["kind"] = "spawn"
            n["children"] = [self.expr(st.call)]
            return n
        if isinstance(st, A.ImportStmt):
            n = self.node(st.span)
            n["kind"] = "import"
            n["name"] = st.path
            return n
        return {"nodeId": self.next(), "kind": "unknown"}

    def param(self, p: A.Param) -> dict:
        return {"nodeId": self.next(), "kind": "param", "name": p.name,
                "type": self.anno_type(p.typ)}

    def anno_type(self, t) -> str:
        if t is None:
            return ""
        return "annotated"

    def expr(self, e: A.Expr) -> dict:
        from ..interp.values import format_g
        if isinstance(e, A.IntLit):
            n = self.node(e.span)
            n.update(kind="int_lit", value=str(e.val), type=self.typ(e))
            return n
        if isinstance(e, A.FloatLit):
            n = self.node(e.span)
            n.update(kind="float_lit", value=format_g(e.val), type=self.typ(e))
            return n
        if isinstance(e, A.StrLit):
            n = self.node(e.span)
            n.update(kind="str_lit", value=e.val, type=self.typ(e))
            return n
        if isinstance(e, A.BoolLit):
            n = self.node(e.span)
            n.update(kind="bool_lit", value="true" if e.val else "false", type=self.typ(e))
            return n
        if isinstance(e, A.Ident):
            n = self.node(e.span)
            n.update(kind="ident", name=e.name, type=self.typ(e))
            return n
        if isinstance(e, A.Unary):
            n = self.node(e.span)
            n.update(kind="unary", name=kind_name(e.op), type=self.typ(e))
            n["children"] = [self.expr(e.x)]
            return n
        if isinstance(e, A.Binary):
            n = self.node(e.span)
            n.update(kind="binary", name=kind_name(e.op), type=self.typ(e))
            n["children"] = [self.expr(e.l), self.expr(e.r)]
            return n
        if isinstance(e, A.RangeEx):
            n = self.node(e.span)
            n.update(kind="range", type=self.typ(e))
            n["children"] = [self.expr(e.lo), self.expr(e.hi)]
            return n
        if isinstance(e, A.CallEx):
            n = self.node(e.span)
            n.update(kind="call", type=self.typ(e))
            n["children"] = [self.expr(e.callee)]
            n["children"].extend(self.expr(a) for a in e.args)
            return n
        if isinstance(e, A.IndexEx):
            n = self.node(e.span)
            n.update(kind="index", type=self.typ(e))
            n["children"] = [self.expr(e.obj), self.expr(e.idx)]
            return n
        if isinstance(e, A.SliceEx):
            n = self.node(e.span)
            n.update(kind="slice", type=self.typ(e))
            n["children"] = [self.expr(e.obj), self.expr(e.lo), self.expr(e.hi)]
            return n
        if isinstance(e, A.FieldEx):
            n = self.node(e.span)
            n.update(kind="field_access", name=e.name, type=self.typ(e))
            n["children"] = [self.expr(e.obj)]
            return n
        if isinstance(e, A.TryEx):
            n = self.node(e.span)
            n.update(kind="try", type=self.typ(e))
            n["children"] = [self.expr(e.x)]
            return n
        if isinstance(e, A.StructLit):
            n = self.node(e.span)
            n.update(kind="struct_lit", name=e.type_name, type=self.typ(e))
            for f in e.fields:
                fn = {"nodeId": self.next(), "kind": "field_init", "name": f.name}
                fn["children"] = [self.expr(f.val)]
                n.setdefault("children", []).append(fn)
            return n
        if isinstance(e, A.MapLit):
            n = self.node(e.span)
            n.update(kind="map_lit", type=self.typ(e))
            for it in e.items:
                kn = self.expr(it.key)
                vn = self.expr(it.val)
                item = {"nodeId": self.next(), "kind": "item"}
                item["children"] = [kn, vn]
                n.setdefault("children", []).append(item)
            return n
        if isinstance(e, A.ArrLit):
            n = self.node(e.span)
            n.update(kind="arr_lit", type=self.typ(e))
            n["children"] = [self.expr(el) for el in e.items]
            return n
        if isinstance(e, A.MatchEx):
            n = self.node(e.span)
            n.update(kind="match", type=self.typ(e))
            n["children"] = [self.expr(e.scrut)]
            for arm in e.arms:
                an = {"nodeId": self.next(), "kind": "arm"}
                an["children"] = [self.pat(arm.pat), self.expr(arm.body)]
                n["children"].append(an)
            return n
        if isinstance(e, A.FnLit):
            n = self.node(e.span)
            n.update(kind="fnlit", type=self.typ(e))
            for p in e.params:
                n.setdefault("children", []).append(self.param(p))
            n.setdefault("children", []).extend(self.stmts(e.body))
            return n
        return {"nodeId": self.next(), "kind": "unknown"}

    def pat(self, p: A.Pat) -> dict:
        if isinstance(p, A.WildPat):
            n = self.node(p.span)
            n["kind"] = "pattern_wild"
            return n
        if isinstance(p, A.BindPat):
            n = self.node(p.span)
            n.update(kind="pattern_bind", name=p.name)
            return n
        if isinstance(p, A.LitPat):
            n = self.node(p.span)
            n["kind"] = "pattern_lit"
            n["children"] = [self.expr(p.val)]
            return n
        if isinstance(p, A.CasePat):
            n = self.node(p.span)
            n.update(kind="pattern_ctor", name=p.name)
            n["children"] = [self.pat(sp) for sp in p.subs]
            return n
        return {"nodeId": self.next(), "kind": "pattern_wild"}


def ast_json(prog: list[A.Stmt], res: Optional[ResolveResult], ty) -> list[dict]:
    """整棵 AST → 契约节点（ty/res 可为 None：仅 dump 语法树时不标类型）。"""
    b = _AstBuilder(res, ty)
    return b.stmts(prog)


def ast_node(e: A.Expr, res: Optional[ResolveResult], ty) -> dict:
    """单个表达式 → 契约节点（inspect type 用；编号从该子树独立分配）。"""
    b = _AstBuilder(res, ty)
    return b.expr(e)
