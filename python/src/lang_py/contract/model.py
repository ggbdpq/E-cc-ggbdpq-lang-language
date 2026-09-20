"""Agent Contract 契约层（v0.4+；contract 包语义等价）。

编译器内部模型 → 稳定 JSON 协议的唯一转换边界：
- 本模块不依赖解释器/后端（它们不是契约数据源）；
- 输出必须 deterministic（数组显式排序，禁用宿主容器迭代序）；
- schemaVersion "1" 内字段只加不改不删（specs/agent-protocol.md）。

JSON 构造用纯 dict（json.dumps 输出），字段序按协议定义排列。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional

from .. import TOOL_VERSION, SCHEMA_VERSION
from ..errors import Diagnostic as LangDiagnostic
from ..semantic.symbols import KCTOR, KENUM, KFN, KMETHOD, KPARAM, KSTRUCT, KVAR, ResolveResult, SymKind
from ..syntax import ast_nodes as A
from ..syntax.span import Span
from ..typesys.model import type_str
from .rules import rule_of

# Kind 输出类别
KIND_INSPECT_SYMBOLS = "inspect.symbols"
KIND_INSPECT_REFS = "inspect.refs"
KIND_INSPECT_TYPE = "inspect.type"
KIND_INSPECT_DEPENDENCIES = "inspect.dependencies"
KIND_INSPECT_EFFECTS = "inspect.effects"
KIND_INSPECT_CALLERS = "inspect.callers"
KIND_INSPECT_CALLEES = "inspect.callees"
KIND_INSPECT_IMPACT = "inspect.impact"
KIND_INSPECT_DEPENDENCY = "inspect.dependency"
KIND_DUMP_AST = "dump.ast"
KIND_DUMP_HIR = "dump.hir"
KIND_DUMP_IR = "dump.ir"
KIND_CHECK = "check"


def span_json(sp: Optional[Span]) -> Optional[dict]:
    """syntax.Span → 契约 Span；未盖章节点不输出。"""
    if sp is None:
        return None
    if not sp.file and sp.start.offset == 0 and sp.end.offset == 0:
        return None
    return {
        "file": sp.file,
        "start": {"offset": sp.start.offset, "line": sp.start.line, "column": sp.start.col},
        "end": {"offset": sp.end.offset, "line": sp.end.line, "column": sp.end.col},
    }


def line_span(line: int) -> dict:
    """行级定位（诊断 primarySpan：line 正确，column 恒 1，offset 未填）。"""
    if line < 1:
        line = 0
    return {
        "file": "",
        "start": {"offset": 0, "line": line, "column": 1},
        "end": {"offset": 0, "line": line, "column": 1},
    }


def diag_json(d: LangDiagnostic) -> dict:
    return {
        "code": d.code,
        "ruleId": rule_of(d.code),
        "severity": "error",
        "message": d.message,
        "primarySpan": line_span(d.line),
        "relatedSpans": [],
        "help": "",
    }


def envelope(kind: str, **fields: Any) -> dict:
    out: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "toolVersion": TOOL_VERSION,
        "kind": kind,
    }
    out.update(fields)
    return out


def error_envelope(kind: str, msg: str) -> dict:
    return envelope(kind, error=msg)


def check_json(diags: list[LangDiagnostic]) -> dict:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "toolVersion": TOOL_VERSION,
        "kind": KIND_CHECK,
        "success": len(diags) == 0,
        "diagnostics": [diag_json(d) for d in diags],
    }


# ———— inspect symbols ————

_SYM_KIND_STR = {
    KFN: "function", KMETHOD: "method", KVAR: "variable", KPARAM: "parameter",
    KSTRUCT: "struct", KENUM: "enum", KCTOR: "constructor",
}


def _is_top_level(sym) -> bool:
    return sym.kind in (KFN, KMETHOD, KSTRUCT, KENUM, KCTOR)


def symbols_json(prog: list[A.Stmt], res: ResolveResult, ty) -> list[dict]:
    """符号清单（ID 序 = 声明/绑定序；预载符号不输出）。"""
    from ..semantic.symbols import NODE_REGISTRY
    stmt_span: dict[int, Span] = {}
    for st, sym in res.decl_syms.items():
        node = NODE_REGISTRY.get(st)
        if node is not None and getattr(node, "span", None) is not None:
            stmt_span[sym.id] = node.span

    def type_of(sym) -> str:
        t = ty.sym_ty.get(sym.id)
        if t is None:
            t = ty.fn_ty.get(sym.id)
        return type_str(t) if t is not None else ""

    def def_span(sym) -> Optional[dict]:
        if sym.decl is not None and getattr(sym.decl, "span", None) is not None:
            return span_json(sym.decl.span)
        sp = stmt_span.get(sym.id)
        if sp is not None:
            return span_json(sp)
        return None

    out = []
    for sym in res.symbols:
        if sym.builtin_mark:
            continue
        visibility = "local"
        if sym.line > 0 and not sym.mutable and _is_top_level(sym):
            visibility = "public"
        module = ""
        sp = def_span(sym)
        if sp is not None:
            module = sp["file"]
        out.append({
            "symbolId": sym.id,
            "name": sym.name,
            "kind": _SYM_KIND_STR.get(sym.kind, "unknown"),
            "definition": sp,
            "type": type_of(sym),
            "visibility": visibility,
            "module": module,
        })
    out.sort(key=lambda s: s["symbolId"])
    return out


def symbols_envelope(prog: list[A.Stmt], res: ResolveResult, ty) -> dict:
    return envelope(KIND_INSPECT_SYMBOLS, symbols=symbols_json(prog, res, ty))


# ———— inspect refs ————

def references_json(prog: list[A.Stmt], res: ResolveResult) -> list[dict]:
    """引用清单：read/write/call/pattern/type。"""
    from ..syntax.walk import walk_stmts, walk_stmt

    writes: set[int] = set()
    calls: set[int] = set()

    def pass_a_stmt(st: A.Stmt) -> None:
        if isinstance(st, A.AssignStmt) and isinstance(st.target, A.Ident):
            writes.add(id(st.target))

    def pass_a_expr(e: A.Expr) -> None:
        if isinstance(e, A.CallEx) and isinstance(e.callee, A.Ident):
            calls.add(id(e.callee))

    walk_stmts(prog, pass_a_stmt, pass_a_expr, None)

    out: list[dict] = []

    def add(sp: Span, sym, kind: str) -> None:
        if sym is None:
            return
        out.append({"symbolId": sym.id, "span": span_json(sp), "referenceKind": kind})

    def on_stmt(st: A.Stmt) -> None:
        pass

    def on_expr(e: A.Expr) -> None:
        if isinstance(e, A.Ident):
            sym = res.refs.get(id(e))
            if sym is not None:
                if id(e) in writes:
                    kind = "write"
                elif id(e) in calls:
                    kind = "call"
                else:
                    kind = "read"
                add(e.span, sym, kind)
        elif isinstance(e, A.StructLit):
            sym = res.refs.get(id(e))
            if sym is not None:
                add(e.span, sym, "type")

    def on_pat(p: A.Pat) -> None:
        if isinstance(p, A.CasePat):
            sym = res.pat_refs.get(id(p))
            if sym is not None:
                add(p.span, sym, "pattern")

    walk_stmts(prog, on_stmt, on_expr, on_pat)

    def sort_key(r):
        sp = r["span"]
        if sp is None:
            return (r["symbolId"], 1, "", 0)
        return (r["symbolId"], 0, sp["file"], sp["start"]["offset"])

    out.sort(key=sort_key)
    return out


def references_envelope(prog: list[A.Stmt], res: ResolveResult, symbol_id: int = -1) -> dict:
    refs = references_json(prog, res)
    if symbol_id >= 0:
        refs = [r for r in refs if r["symbolId"] == symbol_id]
    return envelope(KIND_INSPECT_REFS, references=refs)


# ———— inspect type ————

def _contains_pos(s: Span, line: int, col: int) -> bool:
    if line < s.start.line or line > s.end.line:
        return False
    if line == s.start.line and col < s.start.col:
        return False
    if line == s.end.line and col > s.end.col:
        return False
    return True


def _span_size(s: Span) -> int:
    dl = (s.end.line - s.start.line) * 100000
    return dl + s.end.col - s.start.col


def type_at(prog: list[A.Stmt], res: ResolveResult, ty, file: str, line: int, col: int):
    """行列定位最内层表达式节点 → 契约 Node（dumpast.ast_node）；未命中 None。"""
    from .dumpast import ast_node

    from ..syntax.walk import walk_stmts

    best = None
    found = False
    best_size = 1 << 60

    def consider(e: A.Expr) -> None:
        nonlocal best, found, best_size
        s = getattr(e, "span", None)
        if s is None:
            return
        if file and s.file != file:
            return
        if not _contains_pos(s, line, col):
            return
        size = _span_size(s)
        if not found or size < best_size:
            best = ast_node(e, res, ty)
            found = True
            best_size = size

    walk_stmts(prog, None, consider, None)
    return best


def type_envelope(prog: list[A.Stmt], res: ResolveResult, ty, file: str,
                  line: int, col: int) -> tuple[dict, bool]:
    n = type_at(prog, res, ty, file, line, col)
    if n is None:
        return error_envelope(KIND_INSPECT_TYPE, "指定位置没有表达式节点"), False
    return envelope(KIND_INSPECT_TYPE, nodes=[n]), True


# ———— inspect dependencies ————

def dependencies_envelope(files) -> dict:
    deps = [{"module": f.path, "imports": list(f.imports)} for f in files]
    return envelope(KIND_INSPECT_DEPENDENCIES, dependencies=deps)
