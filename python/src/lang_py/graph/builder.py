"""语义关系图构建与查询（graph.go + build.go + query.go 语义等价）。

真值源：semantic 符号表 + types 产物 + HIR 调用/方法边 + IR 效果推导 + load 文件依赖。
纯内存图；节点键 = SymbolID；查询为 BFS（访问集防环）。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

from ..hir import nodes as H
from ..ir import nodes as I
from ..semantic.symbols import (KCTOR, KENUM, KFN, KMETHOD, KPARAM, KSTRUCT,
                                KVAR, ResolveResult, SymKind)
from ..syntax import ast_nodes as A
from ..syntax.span import Span
from ..typesys.model import type_str
from .effects import derive_effects

EDGE_CALL = "call"
EDGE_REFERENCE = "references"
EDGE_WRITE = "writes"
EDGE_SPAWN = "spawn"
EDGE_TYPE = "type"
EDGE_PATTERN = "pattern"
EDGE_DEPENDS_ON = "depends_on"
EDGE_IMPORTS = "imports"


def _kind_str(k: SymKind) -> str:
    return {
        KFN: "function", KMETHOD: "method", KVAR: "variable", KPARAM: "parameter",
        KSTRUCT: "struct", KENUM: "enum", KCTOR: "constructor",
    }.get(k, "unknown")


@dataclass(eq=False)
class Node:
    id: int
    kind: str
    name: str
    module: str = ""
    span: Span = field(default_factory=Span)
    type: str = ""
    effects: list[str] = field(default_factory=list)
    fields: list[str] = field(default_factory=list)
    extern: bool = False
    immutable: bool = False


@dataclass(eq=False)
class Edge:
    from_id: int
    to_id: int
    kind: str
    span: Optional[Span] = None
    file: str = ""


@dataclass(eq=False)
class ImpactItem:
    from_id: int
    kind: str = ""
    span: Optional[Span] = None
    transitive: bool = False


@dataclass(eq=False)
class Impact:
    symbol: Optional[Node]
    direct: list[ImpactItem] = field(default_factory=list)
    transitive: list[ImpactItem] = field(default_factory=list)
    effect_watch: list[int] = field(default_factory=list)
    modules: list[str] = field(default_factory=list)


@dataclass(eq=False)
class FileDep:
    module: str
    imports: list[str] = field(default_factory=list)
    depends_on: list[str] = field(default_factory=list)


class Graph:
    def __init__(self) -> None:
        self.nodes: dict[int, Node] = {}
        self.order: list[int] = []
        self.edges: list[Edge] = []
        self._in: dict[int, list[int]] = {}
        self._out: dict[int, list[int]] = {}
        self.file_imports: dict[str, list[str]] = {}
        self.file_depends_on: dict[str, list[str]] = {}
        self.files: list[str] = []
        self._dep_seen: set[str] = set()

    def in_edges(self, node_id: int) -> list[Edge]:
        return [self.edges[i] for i in self._in.get(node_id, [])]

    def out_edges(self, node_id: int) -> list[Edge]:
        return [self.edges[i] for i in self._out.get(node_id, [])]

    def add_edge(self, e: Edge) -> None:
        self.edges.append(e)
        idx = len(self.edges) - 1
        self._in.setdefault(e.to_id, []).append(idx)
        self._out.setdefault(e.from_id, []).append(idx)
        if e.from_id > 0 and e.to_id > 0:
            frm = self._node_module(e.from_id)
            to = self._node_module(e.to_id)
            if frm and to and frm != to:
                key = frm + "\x00" + to
                if key not in self._dep_seen:
                    self._dep_seen.add(key)
                    self.file_depends_on.setdefault(frm, []).append(to)

    def _node_module(self, node_id: int) -> str:
        n = self.nodes.get(node_id)
        return n.module if n is not None else ""

    # ———— 查询 ————
    def callers(self, node_id: int, depth: int) -> list[ImpactItem]:
        return self._call_bfs(node_id, True, depth)

    def callees(self, node_id: int, depth: int) -> list[ImpactItem]:
        return self._call_bfs(node_id, False, depth)

    def _call_bfs(self, node_id: int, reverse: bool, depth: int) -> list[ImpactItem]:
        visited = {node_id: True}
        level = [node_id]
        out: list[ImpactItem] = []
        d = 0
        while level and (depth < 0 or d < depth):
            next_level: list[int] = []
            for cur in level:
                edges = self.in_edges(cur) if reverse else self.out_edges(cur)
                for e in edges:
                    if e.kind != EDGE_CALL:
                        continue
                    other = e.from_id if reverse else e.to_id
                    if other in visited:
                        continue
                    visited[other] = True
                    next_level.append(other)
                    out.append(ImpactItem(from_id=other, transitive=(d + 1 > 1)))
            level = next_level
            d += 1
        out.sort(key=lambda it: it.from_id)
        return out

    def impact(self, node_id: int, depth: int) -> Impact:
        imp = Impact(symbol=self.nodes.get(node_id))
        seen: set[int] = {node_id}
        # 直接影响：全部入边
        for e in self.in_edges(node_id):
            if e.from_id == 0:
                continue
            imp.direct.append(ImpactItem(from_id=e.from_id, kind=e.kind, span=e.span))
            if e.kind == EDGE_CALL and e.from_id not in seen:
                seen.add(e.from_id)
                imp.effect_watch.append(e.from_id)
        # 传递影响：沿调用边反向逐层（直接调用方为第一层种子）
        level = [e.from_id for e in self.in_edges(node_id) if e.kind == EDGE_CALL and e.from_id > 0]
        d = 1
        while level and (depth < 0 or d < depth):
            next_level: list[int] = []
            for cur in level:
                for e in self.in_edges(cur):
                    if e.from_id == 0 or e.from_id in seen:
                        continue
                    seen.add(e.from_id)
                    next_level.append(e.from_id)
                    imp.effect_watch.append(e.from_id)
                    it = ImpactItem(from_id=e.from_id, kind=e.kind, transitive=True)
                    if not any(x.from_id == it.from_id and x.kind == it.kind for x in imp.transitive):
                        imp.transitive.append(it)
            level = next_level
            d += 1
        mods = set()
        for it in imp.direct + imp.transitive:
            n = self.nodes.get(it.from_id)
            if n is not None and n.module:
                mods.add(n.module)
        imp.modules = sorted(mods)
        imp.direct.sort(key=lambda it: it.from_id)
        imp.transitive.sort(key=lambda it: it.from_id)
        imp.effect_watch.sort()
        return imp

    def file_dep(self, module: str) -> Optional[FileDep]:
        if not self.files:
            imports = self.file_imports.get(module)
            if imports is not None:
                return FileDep(module=module, imports=list(imports))
            return None
        if module in self.file_imports or module in self.files:
            imports = self.file_imports.get(module, [])
            dep = FileDep(module=module, imports=list(imports))
            seen: set[str] = set()
            for d in self.file_depends_on.get(module, []):
                if d not in seen:
                    seen.add(d)
                    dep.depends_on.append(d)
            return dep
        return None


def build_graph(prog: list[A.Stmt], res: ResolveResult, ty, h: H.Prog,
                irp: Optional[I.Prog], files) -> Graph:
    """从全前端产物构建语义图。files 为 loader.FileInfo 序列。"""
    g = Graph()
    for f in files:
        g.files.append(f.path)
        g.file_imports[f.path] = list(f.imports)

    # 1) 节点（用户符号）
    for sym in res.symbols:
        if sym.builtin_mark:
            continue
        n = Node(id=sym.id, kind=_kind_str(sym.kind), name=sym.name,
                 extern=sym.extern, immutable=not sym.mutable)
        if sym.decl is not None and getattr(sym.decl, "span", None) is not None:
            n.span = sym.decl.span
            n.module = n.span.file
        if ty is not None:
            t = ty.sym_ty.get(sym.id)
            if t is None:
                t = ty.fn_ty.get(sym.id)
            if t is not None:
                n.type = type_str(t)
        if sym.kind == KSTRUCT:
            n.fields.extend(sym.fields)
        g.nodes[n.id] = n
        g.order.append(n.id)

    # 2) 效果（IR 推导）附着到函数/方法节点
    if irp is not None:
        for e in derive_effects(irp):
            n = g.nodes.get(e.symbol_id)
            if n is not None:
                n.effects = e.effects

    # 3) call / spawn / method 边（HIR）
    if h is not None:
        for gl in h.globals:
            _collect_hir_stmt(gl, 0, g)
        for f in h.fns:
            owner = f.sym.id if f.sym is not None else 0
            _collect_hir_stmts(f.body, owner, g)

    # 4) reference / write / type 边 + 文件级 depends_on
    _collect_refs(prog, res, g)
    return g


def _collect_hir_stmts(list_: list[H.Stmt], from_id: int, g: Graph) -> None:
    for st in list_:
        _collect_hir_stmt(st, from_id, g)


def _collect_hir_stmt(st: H.Stmt, from_id: int, g: Graph) -> None:
    if isinstance(st, H.Let):
        _collect_hir_expr(st.init, from_id, g)
    elif isinstance(st, H.Assign):
        _collect_hir_expr(st.target, from_id, g)
        _collect_hir_expr(st.rhs, from_id, g)
    elif isinstance(st, H.Eval):
        _collect_hir_expr(st.x, from_id, g)
    elif isinstance(st, H.Return):
        if st.val is not None:
            _collect_hir_expr(st.val, from_id, g)
    elif isinstance(st, H.If):
        _collect_hir_expr(st.cond, from_id, g)
        _collect_hir_stmts(st.then, from_id, g)
        _collect_hir_stmts(st.els, from_id, g)
    elif isinstance(st, H.While):
        _collect_hir_expr(st.cond, from_id, g)
        _collect_hir_stmts(st.body, from_id, g)
    elif isinstance(st, H.ForIn):
        _collect_hir_expr(st.iter, from_id, g)
        _collect_hir_stmts(st.body, from_id, g)
    elif isinstance(st, H.Spawn):
        if st.fn is not None:
            g.add_edge(Edge(from_id=from_id, to_id=st.fn.id, kind=EDGE_SPAWN))
        for a in st.args:
            _collect_hir_expr(a, from_id, g)
    elif isinstance(st, H.FnStmt):
        _collect_hir_stmts(st.fn.body, from_id, g)


def _collect_hir_expr(e: H.Expr, from_id: int, g: Graph) -> None:
    if isinstance(e, H.Ctor):
        for a in e.args:
            _collect_hir_expr(a, from_id, g)
    elif isinstance(e, H.Call):
        if e.fn is not None:
            g.add_edge(Edge(from_id=from_id, to_id=e.fn.id, kind=EDGE_CALL, span=e.meta.span))
        if e.callee is not None:
            _collect_hir_expr(e.callee, from_id, g)
        for a in e.args:
            _collect_hir_expr(a, from_id, g)
    elif isinstance(e, H.MethodCall):
        if e.method is not None:
            g.add_edge(Edge(from_id=from_id, to_id=e.method.id, kind=EDGE_CALL, span=e.meta.span))
        _collect_hir_expr(e.recv, from_id, g)
        for a in e.args:
            _collect_hir_expr(a, from_id, g)
    elif isinstance(e, H.Unary):
        _collect_hir_expr(e.x, from_id, g)
    elif isinstance(e, H.Binary):
        _collect_hir_expr(e.l, from_id, g)
        _collect_hir_expr(e.r, from_id, g)
    elif isinstance(e, (H.AndThen, H.OrElse)):
        _collect_hir_expr(e.l, from_id, g)
        _collect_hir_expr(e.r, from_id, g)
    elif isinstance(e, H.Range):
        _collect_hir_expr(e.lo, from_id, g)
        _collect_hir_expr(e.hi, from_id, g)
    elif isinstance(e, H.Index):
        _collect_hir_expr(e.obj, from_id, g)
        _collect_hir_expr(e.idx, from_id, g)
    elif isinstance(e, H.Slice):
        _collect_hir_expr(e.obj, from_id, g)
        _collect_hir_expr(e.lo, from_id, g)
        _collect_hir_expr(e.hi, from_id, g)
    elif isinstance(e, H.Field):
        _collect_hir_expr(e.obj, from_id, g)
    elif isinstance(e, H.Try):
        _collect_hir_expr(e.x, from_id, g)
    elif isinstance(e, H.StructLit):
        for f in e.fields:
            _collect_hir_expr(f.val, from_id, g)
    elif isinstance(e, H.MapLit):
        for k in e.keys:
            _collect_hir_expr(k, from_id, g)
        for v in e.vals:
            _collect_hir_expr(v, from_id, g)
    elif isinstance(e, H.SliceLit):
        for el in e.elems:
            _collect_hir_expr(el, from_id, g)
    elif isinstance(e, H.Match):
        _collect_hir_expr(e.scrut, from_id, g)
        for arm in e.arms:
            _collect_hir_expr(arm.body, from_id, g)
    elif isinstance(e, H.FnLit):
        _collect_hir_stmts(e.body, from_id, g)


class _RefCtx:
    def __init__(self, g: Graph, res: ResolveResult) -> None:
        self.g = g
        self.res = res
        self.fn_id = 0
        self.file = ""
        self.write_set: set[int] = set()
        self.calls_set: set[int] = set()

    def stmt(self, s: A.Stmt) -> None:
        if getattr(s, "span", None) is not None and s.span.file:
            self.file = s.span.file
        if isinstance(s, A.LetStmt):
            self.expr(s.init)
        elif isinstance(s, A.AssignStmt):
            self.expr(s.target)
            self.expr(s.val)
        elif isinstance(s, A.ExprStmt):
            self.expr(s.x)
        elif isinstance(s, A.ReturnStmt):
            if s.val is not None:
                self.expr(s.val)
        elif isinstance(s, A.IfStmt):
            self.expr(s.cond)
            self.block(s.then)
            if s.els is not None:
                self.block(s.els)
        elif isinstance(s, A.WhileStmt):
            self.expr(s.cond)
            self.block(s.body)
        elif isinstance(s, A.ForInStmt):
            self.expr(s.iter)
            self.block(s.body)
        elif isinstance(s, A.GoStmt):
            self.expr(s.call)
        elif isinstance(s, A.FnDecl):
            saved = self.fn_id
            sym = self.res.decl_syms.get(id(s))
            if sym is not None:
                self.fn_id = sym.id
            for st in s.body:
                self.stmt(st)
            self.fn_id = saved
        # ExternDecl/StructDecl/EnumDecl/ImportStmt: 无引用面

    def block(self, list_: list[A.Stmt]) -> None:
        for s in list_:
            self.stmt(s)

    def expr(self, e: A.Expr) -> None:
        from ..syntax.walk import walk_expr

        def on(x: A.Expr) -> None:
            if isinstance(x, A.Ident):
                sym = self.res.refs.get(id(x))
                if sym is None:
                    return
                # 直接调用的 fn 名：call 边由 HIR 收集，此处跳过
                if id(x) in self.calls_set and sym.kind == KFN:
                    return
                kind = EDGE_WRITE if id(x) in self.write_set else EDGE_REFERENCE
                self.g.add_edge(Edge(from_id=self.fn_id, to_id=sym.id, kind=kind, span=x.span))
            elif isinstance(x, A.StructLit):
                sym = self.res.refs.get(id(x))
                if sym is not None:
                    self.g.add_edge(Edge(from_id=self.fn_id, to_id=sym.id, kind=EDGE_TYPE, span=x.span))

        walk_expr(e, None, on, None)


def _collect_refs(prog: list[A.Stmt], res: ResolveResult, g: Graph) -> None:
    from ..syntax.walk import walk_stmt

    c = _RefCtx(g, res)
    # 预扫：赋值目标集合（write）与调用位置集合（call）
    def scan_stmt(s: A.Stmt) -> None:
        if isinstance(s, A.AssignStmt) and isinstance(s.target, A.Ident):
            c.write_set.add(id(s.target))

    def scan_expr(e: A.Expr) -> None:
        if isinstance(e, A.CallEx) and isinstance(e.callee, A.Ident):
            c.calls_set.add(id(e.callee))

    for st in prog:
        walk_stmt(st, scan_stmt, scan_expr, None)
    for st in prog:
        c.stmt(st)
