"""语义级重构（v0.7 P0；rename/organize-imports/safe-remove 语义等价）。

基于 SymbolId + 语义引用，禁止 Regex Rename。编辑位两类：
1. 定义位：声明节点内「名字 token」的精确区间（token 级扫描定位）；
2. 引用位：resolver 引用表中指向目标符号的 Ident 节点 Span；方法调用点为 HIR NameSp。
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field

from ..edit.edit import TextEdit
from ..errors import LangError, ParseError
from ..semantic.symbols import KCTOR, KENUM, KFN, KMETHOD, KPARAM, KSTRUCT, KVAR, ResolveResult, Symbol, SymKind
from ..syntax import ast_nodes as A
from ..syntax.comments import scan_comments
from ..syntax.lexer import lex
from ..syntax.span import Position, Span
from ..syntax.tokens import K, Token
from ..syntax.walk import walk_expr, walk_stmt, walk_stmts
from ..hir import nodes as H


@dataclass
class Sources:
    """声明所在文件的源码供给：多文件包按 Sp.File 取对应源码；单文件（File 空）用 Default。"""

    default: str
    by_file: dict[str, str] = field(default_factory=dict)

    def source(self, file: str) -> str:
        if not file:
            return self.default
        return self.by_file.get(file, self.default)


_KIND_NAME = {
    KFN: "function", KMETHOD: "method", KVAR: "variable", KPARAM: "parameter",
    KSTRUCT: "struct", KENUM: "enum", KCTOR: "constructor",
}


def kind_name_en(k: SymKind) -> str:
    return _KIND_NAME.get(k, "unknown")


# ———— token 扫描 ————

@dataclass
class _SpanTok:
    kind: K
    abs_start: int
    abs_end: int
    abs_line: int
    abs_col: int


def _span_tokens(sp: Span, src: str) -> list[_SpanTok]:
    """声明区间的词法切分（坐标换算为绝对行列）。"""
    runes = list(src)
    lo, hi = sp.start.offset, sp.end.offset
    if lo < 0 or hi > len(runes) or lo >= hi:
        return []
    segment = "".join(runes[lo:hi])
    line_starts = [lo]
    for i in range(lo, hi):
        if runes[i] == "\n":
            line_starts.append(i + 1)
    from ..errors import LexError
    try:
        toks = lex(segment)
    except LexError:
        return []
    out: list[_SpanTok] = []
    for t in toks:
        if t.kind in (K.EOF, K.NEWLINE):
            continue
        line_idx = t.line - 1
        if line_idx < 0 or line_idx >= len(line_starts):
            continue
        off = lo + t.start
        out.append(_SpanTok(kind=t.kind, abs_start=lo + t.start, abs_end=lo + t.end,
                            abs_line=sp.start.line + (t.line - 1),
                            abs_col=off - line_starts[line_idx] + 1))
    return out


def _span_tok_abs(sp: Span, t: _SpanTok) -> Span:
    return Span(start=Position(t.abs_start, t.abs_line, t.abs_col),
                end=Position(t.abs_end, t.abs_line, t.abs_col + (t.abs_end - t.abs_start)),
                file=sp.file)


def _scan_name_after_fn(sp: Span, srcs: Sources, has_recv: bool) -> Span:
    toks = _span_tokens(sp, srcs.source(sp.file))
    i = 0
    while i < len(toks) and toks[i].kind != K.KW_FN:
        i += 1
    if i >= len(toks):
        raise LangError("未找到 fn")
    i += 1  # 越过 fn
    if has_recv:
        depth = 1
        i += 1
        while i < len(toks) and depth > 0:
            if toks[i].kind == K.LPAREN:
                depth += 1
            elif toks[i].kind == K.RPAREN:
                depth -= 1
            if depth > 0:
                i += 1
        i += 1
        while i < len(toks) and toks[i].kind == K.DOT:
            i += 1
    if i >= len(toks) or toks[i].kind != K.IDENT:
        raise LangError("未找到名字 token")
    return _span_tok_abs(sp, toks[i])


def _first_ident_span(sp: Span, srcs: Sources) -> Span:
    toks = _span_tokens(sp, srcs.source(sp.file))
    for t in toks:
        if t.kind == K.IDENT:
            return _span_tok_abs(sp, t)
    raise LangError("未找到标识符")


def _param_ident_span(sp: Span, srcs: Sources, group: int, seg: int) -> Span:
    toks = _span_tokens(sp, srcs.source(sp.file))
    gi = 0
    i = 0
    while i < len(toks):
        if toks[i].kind != K.LPAREN:
            i += 1
            continue
        if gi == group:
            return _seg_ident(sp, toks, i, seg)
        depth = 1
        i += 1
        while i < len(toks) and depth > 0:
            if toks[i].kind in (K.LPAREN, K.LBRACKET):
                depth += 1
            elif toks[i].kind in (K.RPAREN, K.RBRACKET):
                depth -= 1
            if depth > 0:
                i += 1
        gi += 1
    raise LangError("未找到参数组")


def _seg_ident(sp: Span, toks: list[_SpanTok], lp: int, seg: int) -> Span:
    depth = 1
    i = lp + 1
    seg_idx = 0
    start = i
    while i < len(toks) and depth > 0:
        if toks[i].kind in (K.LPAREN, K.LBRACKET):
            depth += 1
        elif toks[i].kind in (K.RPAREN, K.RBRACKET):
            depth -= 1
        if depth == 0:
            break
        if toks[i].kind == K.COMMA and depth == 1:
            if seg_idx == seg:
                return _first_ident_in_seg(sp, toks[start:i])
            seg_idx += 1
            start = i + 1
        i += 1
    if seg_idx == seg:
        return _first_ident_in_seg(sp, toks[start:i])
    raise LangError(f"参数段 {seg} 不存在")


def _first_ident_in_seg(sp: Span, toks: list[_SpanTok]) -> Span:
    for t in toks:
        if t.kind == K.IDENT:
            return _span_tok_abs(sp, t)
    raise LangError("参数段无标识符")


# ———— 声明定位 ————

def _collect_fn_decls(prog: list[A.Stmt], res: ResolveResult):
    out = []
    seen: set[int] = set()

    def walk_s(s: A.Stmt) -> None:
        if id(s) in seen:
            return
        seen.add(id(s))
        if isinstance(s, A.FnDecl):
            sym = res.decl_syms.get(id(s))
            if sym is not None:
                out.append((s, sym))
        walk_stmt(s, walk_s, None, None)

    for st in prog:
        walk_s(st)
    return out


def _find_let_stmt(prog: list[A.Stmt], res: ResolveResult, target: Symbol) -> A.LetStmt | None:
    found = []

    def walk_s(s: A.Stmt) -> None:
        if isinstance(s, A.LetStmt) and not found:
            if res.decl_syms.get(id(s)) is target:
                found.append(s)

    walk_stmts(prog, walk_s, None, None)
    return found[0] if found else None


def _param_owner(res: ResolveResult, target: Symbol):
    for fn_id, params in res.fn_params.items():
        for i, p in enumerate(params):
            if p.id == target.id:
                fn = next((s for s in res.symbols if s.id == fn_id), None)
                if fn is None:
                    return None, 0, False
                return fn, i, True
    return None, 0, False


def _find_fn_decl_by_sym(prog: list[A.Stmt], res: ResolveResult, owner: Symbol) -> A.FnDecl | None:
    for d, sym in _collect_fn_decls(prog, res):
        if sym is owner:
            return d
    if isinstance(owner.decl, A.FnDecl):
        return owner.decl
    return None


def decl_name_span(prog: list[A.Stmt], srcs: Sources, res: ResolveResult, target: Symbol) -> Span:
    if target.kind == KFN:
        if not isinstance(target.decl, A.FnDecl):
            raise LangError("未找到函数声明节点")
        return _scan_name_after_fn(target.decl.span, srcs, False)
    if target.kind == KMETHOD:
        if not isinstance(target.decl, A.FnDecl):
            raise LangError("未找到方法声明节点")
        return _scan_name_after_fn(target.decl.span, srcs, True)
    if target.kind == KVAR:
        st = _find_let_stmt(prog, res, target)
        if st is None:
            raise LangError("未找到绑定声明")
        return _first_ident_span(st.span, srcs)
    if target.kind == KPARAM:
        owner_sym, idx, ok = _param_owner(res, target)
        if not ok:
            raise LangError("未找到参数所属函数")
        d = _find_fn_decl_by_sym(prog, res, owner_sym)
        if d is None:
            raise LangError("未找到函数声明节点")
        return _param_ident_span(d.span, srcs, 1, idx)
    raise LangError("不支持的定义定位")


# ———— rename ————

RESERVED_NAMES = [
    "let", "var", "fn", "return", "if", "else", "while", "for", "in",
    "match", "struct", "enum", "true", "false", "break", "continue",
    "go", "chan", "import", "extern", "interface",
    "println", "print", "len", "str", "int", "float", "keys", "values",
    "has", "push", "assert", "err", "bytes", "char", "jsonParse",
    "jsonStringify", "isNone", "makeChan", "send", "recv",
    "Some", "None", "Ok", "Err", "http",
]


def resolve_target(res: ResolveResult, arg: str) -> Symbol:
    """解析 --symbol 参数（ID 或名字）为唯一符号。"""
    if not arg:
        raise LangError("缺少 --symbol 参数")
    if arg.isdigit():
        n = int(arg)
        for s in res.symbols:
            if not s.builtin_mark and s.id == n:
                return s
        raise LangError(f"未知符号 ID {arg}")
    hits = [s for s in res.symbols if not s.builtin_mark and s.name == arg]
    if len(hits) == 1:
        return hits[0]
    if not hits:
        raise LangError(f"未知符号 {arg}")
    raise LangError(f"符号名 {arg} 对应多个符号，请用 --symbol <id>")


def validate_new_name(name: str) -> None:
    if not name:
        raise LangError("新名字为空")
    for i, r in enumerate(name):
        ok = r == "_" or ("a" <= r <= "z") or ("A" <= r <= "Z") or (i > 0 and r.isdigit())
        if not ok:
            raise LangError(f"新名字含非法字符 {r!r}")
    if name in RESERVED_NAMES:
        raise LangError(f"新名字 {name} 与关键字/保留字/内建名冲突")


def rename(prog: list[A.Stmt], srcs: Sources, res: ResolveResult, h, target: Symbol,
           new_name: str) -> list[TextEdit]:
    """语义重命名：fn/method/variable/parameter；struct/enum/ctor 显式拒绝（列 P1）。"""
    validate_new_name(new_name)
    if target.kind not in (KFN, KMETHOD, KVAR, KPARAM):
        if target.kind in (KSTRUCT, KENUM, KCTOR):
            raise LangError(f"暂不支持对 {kind_name_en(target.kind)} 的 rename（列 P1）")
        raise LangError("该符号类型不支持 rename")
    if target.name == "_":
        raise LangError("占位符 _ 不可 rename")
    # 冲突检测（保守）：其他同名用户符号存在即拒绝
    for s in res.symbols:
        if s.builtin_mark or s.id == target.id:
            continue
        if s.name == new_name:
            raise LangError(f"新名字 {new_name} 与已有符号冲突（{s.name} #{s.id}）")

    def_sp = decl_name_span(prog, srcs, res, target)
    edits = [TextEdit(file=def_sp.file, start_offset=def_sp.start.offset,
                      end_offset=def_sp.end.offset, replacement=new_name)]

    # 引用位：Ident（read/write/call 位置的直接标识符引用）+ 赋值目标
    def on_stmt(s: A.Stmt) -> None:
        if isinstance(s, A.AssignStmt) and isinstance(s.target, A.Ident):
            sym = res.refs.get(id(s.target))
            if sym is not None and sym.id == target.id:
                edits.append(TextEdit(file=s.target.span.file,
                                      start_offset=s.target.span.start.offset,
                                      end_offset=s.target.span.end.offset,
                                      replacement=new_name))

    def on_expr(e: A.Expr) -> None:
        if isinstance(e, A.Ident):
            sym = res.refs.get(id(e))
            if sym is not None and sym.id == target.id:
                edits.append(TextEdit(file=e.span.file,
                                      start_offset=e.span.start.offset,
                                      end_offset=e.span.end.offset,
                                      replacement=new_name))

    walk_stmts(prog, on_stmt, on_expr, None)

    # 方法调用点：HIR MethodCall（method == target）
    if target.kind == KMETHOD and h is not None:
        for st in h.globals:
            _collect_hir_method_refs(st, target, new_name, edits)
        for f in h.fns:
            for st in f.body:
                _collect_hir_method_refs(st, target, new_name, edits)

    return _dedupe_edits(edits)


def _collect_hir_method_refs(st: H.Stmt, target: Symbol, new_name: str, edits: list[TextEdit]) -> None:
    """收集 HIR 语句中指向 target 的方法调用编辑。"""

    def on_edit(e: H.Expr) -> None:
        if isinstance(e, H.MethodCall):
            if e.method is not None and e.method.id == target.id:
                edits.append(TextEdit(file=e.name_span.file,
                                      start_offset=e.name_span.start.offset,
                                      end_offset=e.name_span.end.offset,
                                      replacement=new_name))
            _collect_hir_edit(e.recv, target, new_name, edits)
            for a in e.args:
                _collect_hir_edit(a, target, new_name, edits)
        elif isinstance(e, H.Call):
            if e.callee is not None:
                _collect_hir_edit(e.callee, target, new_name, edits)
            for a in e.args:
                _collect_hir_edit(a, target, new_name, edits)

    if isinstance(st, H.Let):
        _collect_hir_edit(st.init, target, new_name, edits)
    elif isinstance(st, H.Assign):
        _collect_hir_edit(st.target, target, new_name, edits)
        _collect_hir_edit(st.rhs, target, new_name, edits)
    elif isinstance(st, H.Eval):
        _collect_hir_edit(st.x, target, new_name, edits)
    elif isinstance(st, H.Return):
        if st.val is not None:
            _collect_hir_edit(st.val, target, new_name, edits)
    elif isinstance(st, H.If):
        _collect_hir_edit(st.cond, target, new_name, edits)
        for s2 in st.then:
            _collect_hir_method_refs(s2, target, new_name, edits)
        for s2 in st.els:
            _collect_hir_method_refs(s2, target, new_name, edits)
    elif isinstance(st, H.While):
        _collect_hir_edit(st.cond, target, new_name, edits)
        for s2 in st.body:
            _collect_hir_method_refs(s2, target, new_name, edits)
    elif isinstance(st, H.ForIn):
        _collect_hir_edit(st.iter, target, new_name, edits)
        for s2 in st.body:
            _collect_hir_method_refs(s2, target, new_name, edits)
    elif isinstance(st, H.Spawn):
        for a in st.args:
            _collect_hir_edit(a, target, new_name, edits)
    elif isinstance(st, H.FnStmt):
        for s2 in st.fn.body:
            _collect_hir_method_refs(s2, target, new_name, edits)


def _collect_hir_edit(e: H.Expr, target: Symbol, new_name: str, edits: list[TextEdit]) -> None:
    """递归收集 HIR 表达式中的方法调用编辑。"""
    if isinstance(e, (H.Lit, H.LocalRef, H.FnRef, H.BuiltinRef)):
        return
    if isinstance(e, H.MethodCall):
        if e.method is not None and e.method.id == target.id:
            edits.append(TextEdit(file=e.name_span.file,
                                  start_offset=e.name_span.start.offset,
                                  end_offset=e.name_span.end.offset,
                                  replacement=new_name))
        _collect_hir_edit(e.recv, target, new_name, edits)
        for a in e.args:
            _collect_hir_edit(a, target, new_name, edits)
    elif isinstance(e, H.Call):
        if e.callee is not None:
            _collect_hir_edit(e.callee, target, new_name, edits)
        for a in e.args:
            _collect_hir_edit(a, target, new_name, edits)
    elif isinstance(e, H.Ctor):
        for a in e.args:
            _collect_hir_edit(a, target, new_name, edits)
    elif isinstance(e, H.Unary):
        _collect_hir_edit(e.x, target, new_name, edits)
    elif isinstance(e, H.Binary):
        _collect_hir_edit(e.l, target, new_name, edits)
        _collect_hir_edit(e.r, target, new_name, edits)
    elif isinstance(e, (H.AndThen, H.OrElse)):
        _collect_hir_edit(e.l, target, new_name, edits)
        _collect_hir_edit(e.r, target, new_name, edits)
    elif isinstance(e, H.Range):
        _collect_hir_edit(e.lo, target, new_name, edits)
        _collect_hir_edit(e.hi, target, new_name, edits)
    elif isinstance(e, H.Index):
        _collect_hir_edit(e.obj, target, new_name, edits)
        _collect_hir_edit(e.idx, target, new_name, edits)
    elif isinstance(e, H.Slice):
        _collect_hir_edit(e.obj, target, new_name, edits)
        _collect_hir_edit(e.lo, target, new_name, edits)
        _collect_hir_edit(e.hi, target, new_name, edits)
    elif isinstance(e, H.Field):
        _collect_hir_edit(e.obj, target, new_name, edits)
    elif isinstance(e, H.Try):
        _collect_hir_edit(e.x, target, new_name, edits)
    elif isinstance(e, H.StructLit):
        for f in e.fields:
            _collect_hir_edit(f.val, target, new_name, edits)
    elif isinstance(e, H.MapLit):
        for k in e.keys:
            _collect_hir_edit(k, target, new_name, edits)
        for v in e.vals:
            _collect_hir_edit(v, target, new_name, edits)
    elif isinstance(e, H.SliceLit):
        for el in e.elems:
            _collect_hir_edit(el, target, new_name, edits)
    elif isinstance(e, H.Match):
        _collect_hir_edit(e.scrut, target, new_name, edits)
        for arm in e.arms:
            _collect_hir_edit(arm.body, target, new_name, edits)
    elif isinstance(e, H.FnLit):
        for st in e.body:
            _collect_hir_method_refs(st, target, new_name, edits)


def _dedupe_edits(edits: list[TextEdit]) -> list[TextEdit]:
    out: list[TextEdit] = []
    for e in edits:
        if not any(x.start_offset == e.start_offset and x.end_offset == e.end_offset
                   and x.file == e.file for x in out):
            out.append(e)
    out.sort(key=lambda e: e.start_offset, reverse=True)
    return out


# ———— safe remove ————

@dataclass
class RemoveCheck:
    removable: bool
    reason: str
    ref_count: int
    ref_sites: list[str]


def safe_remove_check(prog: list[A.Stmt], res: ResolveResult, target: Symbol) -> RemoveCheck:
    if target.kind not in (KFN, KVAR):
        raise LangError(
            f"safe remove 仅支持顶层 fn 与全局变量（目标 kind={kind_name_en(target.kind)}）")
    if target.extern:
        raise LangError("extern 声明不支持 safe remove")
    if not isinstance(target.decl, (A.FnDecl, A.LetStmt)):
        raise LangError("未找到声明节点")

    count: list[int] = [0]
    sites: list[str] = []

    for st in prog:
        _collect_remove_refs(st, res, target, count, sites)
    ref_count = count[0]
    return RemoveCheck(removable=ref_count == 0, reason=f"{ref_count} 处引用",
                       ref_count=ref_count, ref_sites=sites)


def _count_ident(e: A.Expr, res: ResolveResult, target: Symbol, count, sites) -> None:
    def on(x: A.Expr) -> None:
        if isinstance(x, A.Ident):
            sym = res.refs.get(id(x))
            if sym is not None and sym.id == target.id:
                count[0] += 1
                sp = x.span
                if sp.file:
                    sites.append(f"{sp.file}:{sp.start.line}:{sp.start.col}")
                else:
                    sites.append(f"{sp.start.line}:{sp.start.col}")
    walk_expr(e, None, on, None)


def _collect_remove_refs(st: A.Stmt, res: ResolveResult, target: Symbol, count, sites) -> None:
    if isinstance(st, A.LetStmt):
        _count_ident(st.init, res, target, count, sites)
    elif isinstance(st, A.AssignStmt):
        _count_ident(st.target, res, target, count, sites)
        _count_ident(st.val, res, target, count, sites)
    elif isinstance(st, A.ExprStmt):
        _count_ident(st.x, res, target, count, sites)
    elif isinstance(st, A.ReturnStmt):
        if st.val is not None:
            _count_ident(st.val, res, target, count, sites)
    elif isinstance(st, A.IfStmt):
        _count_ident(st.cond, res, target, count, sites)
        for s in st.then:
            _collect_remove_refs(s, res, target, count, sites)
        if st.els is not None:
            for s in st.els:
                _collect_remove_refs(s, res, target, count, sites)
    elif isinstance(st, A.WhileStmt):
        _count_ident(st.cond, res, target, count, sites)
        for s in st.body:
            _collect_remove_refs(s, res, target, count, sites)
    elif isinstance(st, A.ForInStmt):
        _count_ident(st.iter, res, target, count, sites)
        for s in st.body:
            _collect_remove_refs(s, res, target, count, sites)
    elif isinstance(st, A.FnDecl):
        for s in st.body:
            _collect_remove_refs(s, res, target, count, sites)
    elif isinstance(st, A.GoStmt):
        for a in st.call.args:
            _count_ident(a, res, target, count, sites)


def safe_remove_span(prog: list[A.Stmt], res: ResolveResult, target: Symbol) -> Span:
    if target.kind == KFN:
        if not isinstance(target.decl, A.FnDecl):
            raise LangError("未找到函数声明节点")
        return target.decl.span
    if target.kind == KVAR:
        st = _find_let_stmt(prog, res, target)
        if st is None:
            raise LangError("未找到绑定声明")
        return st.span
    raise LangError("不支持的定义定位")


def safe_remove_edits(prog: list[A.Stmt], srcs: Sources, res: ResolveResult,
                      target: Symbol) -> list[TextEdit]:
    """可删除时给出删除编辑：区间吞掉声明后的单个换行，避免残留空行。"""
    sp = safe_remove_span(prog, res, target)
    end = sp.end.offset
    src = srcs.source(sp.file)
    runes = list(src)
    if end < len(runes) and runes[end] == "\n":
        end += 1
    return [TextEdit(file=sp.file, start_offset=sp.start.offset, end_offset=end, replacement="")]


# ———— organize imports ————

def organize_imports(src: str) -> tuple[str, bool]:
    """同文件 import 行去重 + 按路径排序 + 删除未使用 import。注释随行保留。"""
    changed = False
    lines = src.split("\n")

    import_idx = [i for i, l in enumerate(lines) if l.strip().startswith("import ")]
    if not import_idx:
        return src, False

    imps: list[dict] = []
    for i in import_idx:
        l = lines[i]
        t = l.strip()
        rest = t[len("import "):].strip()
        path = comment = ""
        q = rest.find('"')
        if q >= 0:
            rest = rest[q + 1:]
            e = rest.find('"')
            if e < 0:
                continue
            path = rest[:e]
            tail = rest[e + 1:].strip()
            if tail.startswith("//"):
                comment = tail
        if not path:
            continue
        imp = {"path": path, "comment": comment, "line_idx": i, "used": False}
        base = path.rsplit("/", 1)[-1]
        for j, other in enumerate(lines):
            if j == i or other.strip().startswith("import "):
                continue
            if base in other:
                imp["used"] = True
                break
        imps.append(imp)

    kept: list[dict] = []
    removed_lines: list[int] = []
    seen: set[str] = set()
    for im in imps:
        if not im["used"]:
            removed_lines.append(im["line_idx"])
            changed = True
            continue
        if im["path"] in seen:
            removed_lines.append(im["line_idx"])
            changed = True
            continue
        seen.add(im["path"])
        kept.append(im)
    if not changed and len(kept) == len(imps):
        paths = [k["path"] for k in kept]
        if paths == sorted(paths):
            return src, False
        changed = True

    removed_set = set(removed_lines)
    import_idx_set = set(import_idx)
    out = []
    for i, l in enumerate(lines):
        if i in import_idx_set and i not in removed_set:
            continue  # import 行由排序块统一输出
        if i in removed_set:
            continue
        out.append(l)

    # 找到第一个声明的原位置（在其前插入排序后的 import 行）
    insert_at = -1
    for i, l in enumerate(out):
        t = l.strip()
        if t.startswith("//"):
            continue
        if t.startswith("fn ") or t.startswith("struct "):
            insert_at = i
            break
    if insert_at < 0:
        insert_at = len(out)
    sorted_imps = sorted(kept, key=lambda im: im["path"])
    import_lines = []
    for im in sorted_imps:
        l = f'import "{im["path"]}"'
        if im["comment"]:
            l += " // " + im["comment"].removeprefix("// ")
        import_lines.append(l)
    res = out[:insert_at] + import_lines + out[insert_at:]
    return "\n".join(res), changed
