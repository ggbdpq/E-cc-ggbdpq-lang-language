"""Typed HIR / Core IR → 契约 JSON 节点树（dump.go 语义等价）。

HIR 使用编译器 NodeID（先序稳定）与 Span；IR 节点无内部 ID，
由契约层按先序编号，且不含 span（span 见 dump.hir，文档化于 agent-protocol）。
"""

from __future__ import annotations

from ..hir import nodes as H
from ..ir import nodes as I
from ..typesys.model import type_str


def _sym_str(s) -> str:
    return s.name if s is not None else ""


def _hir_node(n: H.NodeMeta, kind: str) -> dict:
    from .model import span_json
    return {"nodeId": n.id, "kind": kind, "span": span_json(n.span), "type": type_str(n.typ)}


def _stmt_node(n: H.NodeMeta, kind: str) -> dict:
    from .model import span_json
    return {"nodeId": n.id, "kind": kind, "span": span_json(n.span)}


class _HirBuilder:
    def fn(self, f: H.Fn) -> dict:
        n = _hir_node(f.meta, "fn")
        n["name"] = f.sym.name if f.sym is not None else f.name
        if f.recv is not None:
            recv = _hir_node(f.meta, "recv")
            recv["name"] = f.recv.name
            recv["type"] = type_str(f.recv.typ)
            n.setdefault("children", []).append(recv)
        for p in f.params:
            pn = _hir_node(f.meta, "param")
            pn["name"] = p.name
            pn["type"] = type_str(p.typ)
            n.setdefault("children", []).append(pn)
        n.setdefault("children", []).extend(self.stmts(f.body))
        return n

    def stmts(self, list_: list[H.Stmt]) -> list[dict]:
        return [self.stmt(st) for st in list_]

    def stmt(self, st: H.Stmt) -> dict:
        if isinstance(st, H.Let):
            n = _hir_node(st.meta, "var" if st.mutable else "let")
            n["name"] = st.name
            n["children"] = [self.expr(st.init)]
            return n
        if isinstance(st, H.Assign):
            n = _hir_node(st.meta, "assign")
            from ..syntax.tokens import kind_name
            n["name"] = kind_name(st.op)
            n["children"] = [self.expr(st.target), self.expr(st.rhs)]
            return n
        if isinstance(st, H.Eval):
            n = _hir_node(st.meta, "eval")
            n["children"] = [self.expr(st.x)]
            return n
        if isinstance(st, H.Return):
            n = _hir_node(st.meta, "return")
            if st.val is not None:
                n["children"] = [self.expr(st.val)]
            return n
        if isinstance(st, H.If):
            n = _hir_node(st.meta, "if")
            n["children"] = [self.expr(st.cond)]
            n["children"].extend(self.stmts(st.then))
            if st.els:
                els = _stmt_node(st.meta, "else")
                els["children"] = self.stmts(st.els)
                n["children"].append(els)
            return n
        if isinstance(st, H.While):
            n = _hir_node(st.meta, "while")
            n["children"] = [self.expr(st.cond)]
            n["children"].extend(self.stmts(st.body))
            return n
        if isinstance(st, H.ForIn):
            n = _hir_node(st.meta, "forin")
            n["name"] = st.var
            n["type"] = type_str(st.var_type)
            n["children"] = [self.expr(st.iter)]
            n["children"].extend(self.stmts(st.body))
            return n
        if isinstance(st, H.Break):
            return _hir_node(st.meta, "break")
        if isinstance(st, H.Continue):
            return _hir_node(st.meta, "continue")
        if isinstance(st, H.Spawn):
            n = _hir_node(st.meta, "spawn")
            n["name"] = _sym_str(st.fn)
            n["children"] = [self.expr(a) for a in st.args]
            return n
        if isinstance(st, H.FnStmt):
            return self.fn(st.fn)
        return {"kind": "unknown"}

    def expr(self, e: H.Expr) -> dict:
        from ..interp.values import format_g
        if isinstance(e, H.Lit):
            if e.kind == H.LitKind.INT:
                n = _hir_node(e.meta, "int_lit")
                n["value"] = str(e.i64)
                return n
            if e.kind == H.LitKind.FLOAT:
                n = _hir_node(e.meta, "float_lit")
                n["value"] = format_g(e.f64)
                return n
            if e.kind == H.LitKind.STR:
                n = _hir_node(e.meta, "str_lit")
                n["value"] = e.s
                return n
            n = _hir_node(e.meta, "bool_lit")
            n["value"] = "true" if e.b else "false"
            return n
        if isinstance(e, H.LocalRef):
            n = _hir_node(e.meta, "local")
            n["name"] = e.name
            return n
        if isinstance(e, H.FnRef):
            n = _hir_node(e.meta, "fnref")
            n["name"] = e.name
            return n
        if isinstance(e, H.BuiltinRef):
            n = _hir_node(e.meta, "builtin_ref")
            n["name"] = e.name
            return n
        if isinstance(e, H.Ctor):
            n = _hir_node(e.meta, "ctor")
            n["name"] = _sym_str(e.ctor_sym)
            n["children"] = [self.expr(a) for a in e.args]
            return n
        if isinstance(e, H.Call):
            n = _hir_node(e.meta, "call")
            if e.fn is not None:
                n["name"] = e.fn.name
            elif e.builtin:
                n["kind"] = "builtin_call"
                n["name"] = e.builtin
            else:
                n["kind"] = "call_value"
                n["children"] = [self.expr(e.callee)]
            n.setdefault("children", []).extend(self.expr(a) for a in e.args)
            return n
        if isinstance(e, H.MethodCall):
            n = _hir_node(e.meta, "method")
            if e.method is not None:
                n["name"] = e.method.name
                n["children"] = [self.expr(e.recv)]
            else:
                n["kind"] = "call_field"
                n["name"] = e.field
                n["children"] = [self.expr(e.recv)]
            n["children"].extend(self.expr(a) for a in e.args)
            return n
        if isinstance(e, H.Unary):
            n = _hir_node(e.meta, "unary")
            from ..syntax.tokens import kind_name
            n["name"] = kind_name(e.op)
            n["children"] = [self.expr(e.x)]
            return n
        if isinstance(e, H.Binary):
            n = _hir_node(e.meta, "binary")
            from ..syntax.tokens import kind_name
            n["name"] = kind_name(e.op)
            n["children"] = [self.expr(e.l), self.expr(e.r)]
            return n
        if isinstance(e, H.AndThen):
            n = _hir_node(e.meta, "and")
            n["children"] = [self.expr(e.l), self.expr(e.r)]
            return n
        if isinstance(e, H.OrElse):
            n = _hir_node(e.meta, "or")
            n["children"] = [self.expr(e.l), self.expr(e.r)]
            return n
        if isinstance(e, H.Range):
            n = _hir_node(e.meta, "range")
            n["children"] = [self.expr(e.lo), self.expr(e.hi)]
            return n
        if isinstance(e, H.Index):
            n = _hir_node(e.meta, "index")
            n["children"] = [self.expr(e.obj), self.expr(e.idx)]
            return n
        if isinstance(e, H.Slice):
            n = _hir_node(e.meta, "slice")
            n["children"] = [self.expr(e.obj), self.expr(e.lo), self.expr(e.hi)]
            return n
        if isinstance(e, H.Field):
            n = _hir_node(e.meta, "field_access")
            n["name"] = e.name
            n["children"] = [self.expr(e.obj)]
            return n
        if isinstance(e, H.Try):
            n = _hir_node(e.meta, "try")
            n["children"] = [self.expr(e.x)]
            return n
        if isinstance(e, H.StructLit):
            n = _hir_node(e.meta, "struct_lit")
            n["name"] = _sym_str(e.meta.sym)
            for f in e.fields:
                fn = {"nodeId": e.meta.id, "kind": "field_init", "name": f.name}
                fn["children"] = [self.expr(f.val)]
                n.setdefault("children", []).append(fn)
            return n
        if isinstance(e, H.MapLit):
            n = _hir_node(e.meta, "map_lit")
            for k, v in zip(e.keys, e.vals):
                item = {"nodeId": e.meta.id, "kind": "item"}
                item["children"] = [self.expr(k), self.expr(v)]
                n.setdefault("children", []).append(item)
            return n
        if isinstance(e, H.SliceLit):
            n = _hir_node(e.meta, "arr_lit")
            n["children"] = [self.expr(el) for el in e.elems]
            return n
        if isinstance(e, H.Match):
            kind = {1: "enum", 2: "result", 3: "option"}.get(e.kind, "literal")
            n = _hir_node(e.meta, "match")
            n["name"] = kind
            n["children"] = [self.expr(e.scrut)]
            for arm in e.arms:
                an = {"nodeId": e.meta.id, "kind": "arm"}
                an["children"] = [self.pat(arm.pat), self.expr(arm.body)]
                n["children"].append(an)
            return n
        if isinstance(e, H.FnLit):
            n = _hir_node(e.meta, "fnlit")
            for p in e.params:
                pn = _hir_node(e.meta, "param")
                pn["name"] = p.name
                pn["type"] = type_str(p.typ)
                n.setdefault("children", []).append(pn)
            n.setdefault("children", []).extend(self.stmts(e.body))
            return n
        return {"kind": "unknown"}

    def pat(self, p: H.Pat) -> dict:
        if isinstance(p, H.WildPat):
            return {"kind": "pattern_wild"}
        if isinstance(p, H.BindPat):
            return {"kind": "pattern_bind", "name": p.name, "type": type_str(p.typ)}
        if isinstance(p, H.LitPat):
            return {"kind": "pattern_lit", "children": [self.expr(p.val)]}
        if isinstance(p, H.CtorPat):
            n = {"kind": "pattern_ctor", "name": _sym_str(p.ctor)}
            n["children"] = [self.pat(sp) for sp in p.subs]
            return n
        return {"kind": "pattern_wild"}


def hir_json(p: H.Prog) -> list[dict]:
    """整个 HIR 程序。"""
    b = _HirBuilder()
    out: list[dict] = []
    for s in p.structs:
        n = _stmt_node(s.meta, "struct")
        n["name"] = s.name
        for f in s.fields:
            n.setdefault("children", []).append(
                {"nodeId": s.meta.id, "kind": "field", "name": f.name, "type": type_str(f.typ)})
        out.append(n)
    for e in p.enums:
        n = _stmt_node(e.meta, "enum")
        n["name"] = e.name
        for c in e.cases:
            n.setdefault("children", []).append(
                {"nodeId": e.meta.id, "kind": "case", "name": c.name})
        out.append(n)
    for g in p.globals:
        out.append(b.stmt(g))
    for f in p.fns:
        out.append(b.fn(f))
    return out


class _IrBuilder:
    def __init__(self) -> None:
        self.id = 0
        self.out: list[dict] = []

    def next(self) -> int:
        self.id += 1
        return self.id

    def fn(self, f: I.Fn) -> dict:
        n = {"nodeId": self.next(), "kind": "fn", "name": f.name, "type": type_str(f.ret)}
        if f.recv is not None:
            n.setdefault("children", []).append(
                {"nodeId": self.next(), "kind": "recv", "name": f.recv.name,
                 "type": type_str(f.recv.typ)})
        for p in f.params:
            n.setdefault("children", []).append(
                {"nodeId": self.next(), "kind": "param", "name": p.name, "type": type_str(p.typ)})
        n.setdefault("children", []).extend(self.stmts(f.body))
        return n

    def stmts(self, list_: list[I.Stmt]) -> list[dict]:
        return [self.stmt(st) for st in list_]

    def stmt(self, st: I.Stmt) -> dict:
        if isinstance(st, I.Let):
            n = {"nodeId": self.next(), "kind": "var" if st.local.mutable else "let",
                 "name": st.local.name, "type": type_str(st.local.typ)}
            n["children"] = [self.expr(st.init)]
            return n
        if isinstance(st, I.Assign):
            n = {"nodeId": self.next(), "kind": "assign", "name": st.op}
            n["children"] = [self.expr(st.target), self.expr(st.val)]
            return n
        if isinstance(st, I.Eval):
            n = {"nodeId": self.next(), "kind": "eval"}
            n["children"] = [self.expr(st.x)]
            return n
        if isinstance(st, I.Return):
            n = {"nodeId": self.next(), "kind": "return"}
            if st.val is not None:
                n["children"] = [self.expr(st.val)]
            return n
        if isinstance(st, I.If):
            n = {"nodeId": self.next(), "kind": "if"}
            n["children"] = [self.expr(st.cond)]
            n["children"].extend(self.stmts(st.then))
            if st.els:
                els = {"nodeId": self.next(), "kind": "else"}
                els["children"] = self.stmts(st.els)
                n["children"].append(els)
            return n
        if isinstance(st, I.While):
            n = {"nodeId": self.next(), "kind": "while"}
            n["children"] = [self.expr(st.cond)]
            n["children"].extend(self.stmts(st.body))
            return n
        if isinstance(st, I.ForIn):
            n = {"nodeId": self.next(), "kind": "forin", "name": st.var.name,
                 "type": type_str(st.var.typ)}
            n["children"] = [self.expr(st.iter)]
            n["children"].extend(self.stmts(st.body))
            return n
        if isinstance(st, I.Break):
            return {"nodeId": self.next(), "kind": "break"}
        if isinstance(st, I.Continue):
            return {"nodeId": self.next(), "kind": "continue"}
        if isinstance(st, I.Spawn):
            n = {"nodeId": self.next(), "kind": "spawn",
                 "name": st.fn.name if st.fn is not None else ""}
            n["children"] = [self.expr(a) for a in st.args]
            return n
        if isinstance(st, I.FnDef):
            return self.fn(st.fn)
        return {"nodeId": self.next(), "kind": "unknown"}

    def expr(self, e: I.Expr) -> dict:
        from ..interp.values import format_g
        if isinstance(e, I.Const):
            n = {"nodeId": self.next(), "kind": "const", "type": type_str(e.typ)}
            if e.kind == I.ConstKind.INT:
                n["value"] = str(e.i64)
            elif e.kind == I.ConstKind.FLOAT:
                n["value"] = format_g(e.f64)
            elif e.kind == I.ConstKind.STR:
                n["value"] = e.s
            else:
                n["value"] = "true" if e.b else "false"
            return n
        if isinstance(e, I.LocalGet):
            return {"nodeId": self.next(), "kind": "local", "name": e.local.name,
                    "type": type_str(e.typ)}
        if isinstance(e, I.FnGet):
            return {"nodeId": self.next(), "kind": "fnref", "name": e.name, "type": type_str(e.typ)}
        if isinstance(e, I.BuiltinGet):
            return {"nodeId": self.next(), "kind": "builtin_ref", "name": e.name}
        if isinstance(e, I.Ctor):
            n = {"nodeId": self.next(), "kind": "ctor", "name": _sym_str(e.sym),
                 "type": type_str(e.typ)}
            n["children"] = [self.expr(a) for a in e.args]
            return n
        if isinstance(e, I.Call):
            n = {"nodeId": self.next(), "kind": "call", "type": type_str(e.typ)}
            if e.fn is not None:
                n["name"] = e.fn.name
            elif e.builtin:
                n["kind"] = "builtin_call"
                n["name"] = e.builtin
            elif e.method is not None:
                n["kind"] = "method"
                n["name"] = e.method.name
                n["children"] = [self.expr(e.recv)]
            else:
                n["kind"] = "call_value"
                n["children"] = [self.expr(e.callee)]
            n.setdefault("children", []).extend(self.expr(a) for a in e.args)
            return n
        if isinstance(e, I.Unary):
            n = {"nodeId": self.next(), "kind": "unary", "name": e.op, "type": type_str(e.typ)}
            n["children"] = [self.expr(e.x)]
            return n
        if isinstance(e, I.Binary):
            n = {"nodeId": self.next(), "kind": "binary", "name": e.op, "type": type_str(e.typ)}
            n["children"] = [self.expr(e.l), self.expr(e.r)]
            return n
        if isinstance(e, I.Range):
            n = {"nodeId": self.next(), "kind": "range", "type": type_str(e.typ)}
            n["children"] = [self.expr(e.lo), self.expr(e.hi)]
            return n
        if isinstance(e, I.Index):
            n = {"nodeId": self.next(), "kind": "index", "type": type_str(e.typ)}
            n["children"] = [self.expr(e.obj), self.expr(e.idx)]
            return n
        if isinstance(e, I.Slice):
            n = {"nodeId": self.next(), "kind": "slice", "type": type_str(e.typ)}
            n["children"] = [self.expr(e.obj), self.expr(e.lo), self.expr(e.hi)]
            return n
        if isinstance(e, I.Field):
            n = {"nodeId": self.next(), "kind": "field_access", "name": e.name,
                 "type": type_str(e.typ)}
            n["children"] = [self.expr(e.obj)]
            return n
        if isinstance(e, I.Try):
            n = {"nodeId": self.next(), "kind": "try", "type": type_str(e.typ)}
            n["children"] = [self.expr(e.x)]
            return n
        if isinstance(e, I.StructLit):
            n = {"nodeId": self.next(), "kind": "struct_lit",
                 "name": _sym_str(e.struct), "type": type_str(e.typ)}
            for f in e.fields:
                fn = {"nodeId": self.next(), "kind": "field_init", "name": f.name}
                fn["children"] = [self.expr(f.val)]
                n.setdefault("children", []).append(fn)
            return n
        if isinstance(e, I.MapLit):
            n = {"nodeId": self.next(), "kind": "map_lit", "type": type_str(e.typ)}
            for k, v in zip(e.keys, e.vals):
                item = {"nodeId": self.next(), "kind": "item"}
                item["children"] = [self.expr(k), self.expr(v)]
                n.setdefault("children", []).append(item)
            return n
        if isinstance(e, I.SliceLit):
            n = {"nodeId": self.next(), "kind": "arr_lit", "type": type_str(e.typ)}
            n["children"] = [self.expr(el) for el in e.elems]
            return n
        if isinstance(e, I.Match):
            kind = {1: "enum", 2: "result", 3: "option"}.get(e.kind.value, "literal")
            n = {"nodeId": self.next(), "kind": "match", "name": kind, "type": type_str(e.typ)}
            n["children"] = [self.expr(e.scrut)]
            for arm in e.arms:
                an = {"nodeId": self.next(), "kind": "arm"}
                an["children"] = [self.pat(arm.pat), self.expr(arm.body)]
                n["children"].append(an)
            return n
        if isinstance(e, I.FnLit):
            n = {"nodeId": self.next(), "kind": "fnlit", "type": type_str(e.typ)}
            for p in e.params:
                n.setdefault("children", []).append(
                    {"nodeId": self.next(), "kind": "param", "name": p.name,
                     "type": type_str(p.typ)})
            n.setdefault("children", []).extend(self.stmts(e.body))
            return n
        return {"nodeId": self.next(), "kind": "unknown"}

    def pat(self, p: I.Pat) -> dict:
        if isinstance(p, I.WildPat):
            return {"nodeId": self.next(), "kind": "pattern_wild"}
        if isinstance(p, I.BindPat):
            return {"nodeId": self.next(), "kind": "pattern_bind", "name": p.name,
                    "type": type_str(p.typ)}
        if isinstance(p, I.LitPat):
            n = {"nodeId": self.next(), "kind": "pattern_lit"}
            n["children"] = [self.expr(p.val)]
            return n
        if isinstance(p, I.CtorPat):
            n = {"nodeId": self.next(), "kind": "pattern_ctor", "name": _sym_str(p.sym)}
            n["children"] = [self.pat(sp) for sp in p.subs]
            return n
        return {"nodeId": self.next(), "kind": "pattern_wild"}


def ir_json(p: I.Prog) -> list[dict]:
    """整个 IR 程序。"""
    b = _IrBuilder()
    for s in p.structs:
        n = {"nodeId": b.next(), "kind": "struct", "name": s.name}
        for f in s.fields:
            n.setdefault("children", []).append(
                {"nodeId": b.next(), "kind": "field", "name": f.name, "type": type_str(f.typ)})
        b.out.append(n)
    for e in p.enums:
        n = {"nodeId": b.next(), "kind": "enum", "name": e.name}
        for c in e.cases:
            n.setdefault("children", []).append(
                {"nodeId": b.next(), "kind": "case", "name": c.name})
        b.out.append(n)
    for g in p.globals:
        n = {"nodeId": b.next(), "kind": "var" if g.local.mutable else "let",
             "name": g.local.name, "type": type_str(g.local.typ)}
        n["children"] = [b.expr(g.init)]
        b.out.append(n)
    for f in p.fns:
        b.out.append(b.fn(f))
    return b.out
