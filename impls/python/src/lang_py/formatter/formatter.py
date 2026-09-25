"""lang-go 格式化器（v0.5 Safe Editing；format.go 语义等价）。

渲染为行缓冲；注释按 trivia 流插回（Lossless 层）：
- 行首注释独立行输出，保留注释间空行分组（最多一空行）；
- 行尾注释追加到语句渲染输出的最后一行（两空格间隔）；
- 表达式内注释在语句边界冲刷（位置后移，文本零丢失）；
- 块尾注释原位保留在 `}` 之前。

硬契约：100% 注释保留、幂等 format(format(x)) == format(x)、语义等价、确定性输出。
"""

from __future__ import annotations

from typing import Optional

from ..errors import ParseError
from ..syntax import ast_nodes as A
from ..syntax.comments import Comment, scan_comments
from ..syntax.parser import parse
from ..syntax.tokens import K, kind_name

_INDENT = "    "


class FormatError(Exception):
    pass


class _CommentTracker:
    def __init__(self, src: str) -> None:
        self.all = scan_comments(src)
        self.pos = 0

    def peek(self) -> Optional[Comment]:
        if self.pos < len(self.all):
            return self.all[self.pos]
        return None


class _Formatter:
    def __init__(self, src: str) -> None:
        self.lines: list[str] = []
        self.cm = _CommentTracker(src)
        self.last_src_line = 0

    # ———— 注释流 ————
    def flush_leading(self, start: int, indent: int) -> None:
        while True:
            c = self.cm.peek()
            if c is None or c.span.start.offset >= start:
                return
            self.cm.pos += 1
            if c.line - self.last_src_line >= 2:
                self.lines.append("")
            self.lines.append(_INDENT * indent + c.text)
            self.last_src_line = c.line

    def flush_trailing(self, end_offset: int, end_line: int) -> None:
        while True:
            c = self.cm.peek()
            if c is None or c.line != end_line or c.span.start.offset < end_offset:
                return
            self.cm.pos += 1
            if not self.lines:
                self.lines.append("")
            self.lines[-1] += "  " + c.text
            self.last_src_line = c.line

    def flush_remaining(self, indent: int) -> None:
        while True:
            c = self.cm.peek()
            if c is None:
                return
            self.cm.pos += 1
            if c.line - self.last_src_line >= 2:
                self.lines.append("")
            self.lines.append(_INDENT * indent + c.text)
            self.last_src_line = c.line

    def w(self, indent: int, s: str) -> None:
        parts = s.split("\n")
        for i, p in enumerate(parts):
            if i == 0:
                self.lines.append(_INDENT * indent + p)
            else:
                self.lines.append(p)

    # ———— 语句 ————
    def stmts(self, list_: list[A.Stmt], indent: int) -> None:
        for s in list_:
            sp = s.span
            self.flush_leading(sp.start.offset, indent)
            self.last_src_line = sp.end.line
            self.stmt(s, indent)
            self.flush_trailing(sp.end.offset, sp.end.line)
        self.flush_remaining(indent)

    def stmt(self, s: A.Stmt, indent: int) -> None:
        if isinstance(s, A.LetStmt):
            kw = "var" if s.mutable else "let"
            anno = ""
            if s.typ is not None:
                anno = ": " + type_str_repr(s.typ)
            self.w(indent, f"{kw} {s.name}{anno} = {self.expr(s.init, 0, indent)}")
        elif isinstance(s, A.AssignStmt):
            op = kind_name(s.op)
            self.w(indent, f"{self.expr(s.target, 0, indent)} {op} {self.expr(s.val, 0, indent)}")
        elif isinstance(s, A.ExprStmt):
            self.w(indent, self.expr(s.x, 0, indent))
        elif isinstance(s, A.ReturnStmt):
            if s.val is None:
                self.w(indent, "return")
            else:
                self.w(indent, "return " + self.expr(s.val, 0, indent))
        elif isinstance(s, A.IfStmt):
            self.if_chain(s, indent)
        elif isinstance(s, A.WhileStmt):
            self.w(indent, f"while {self.expr(s.cond, 0, indent)} {{")
            self.stmts(s.body, indent + 1)
            self.w(indent, "}")
        elif isinstance(s, A.ForInStmt):
            self.w(indent, f"for {s.var} in {self.expr(s.iter, 0, indent)} {{")
            self.stmts(s.body, indent + 1)
            self.w(indent, "}")
        elif isinstance(s, A.BreakStmt):
            self.w(indent, "break")
        elif isinstance(s, A.ContinueStmt):
            self.w(indent, "continue")
        elif isinstance(s, A.GoStmt):
            self.w(indent, "go " + self.expr(s.call, 0, indent))
        elif isinstance(s, A.FnDecl):
            self.fn_decl(s, indent)
        elif isinstance(s, A.StructDecl):
            generic = generic_params(s.type_params)
            self.w(indent, f"struct {s.name}{generic} {{")
            for fd in s.fields:
                self.w(indent + 1, f"{fd.name}: {type_str_repr(fd.typ)}")
            self.w(indent, "}")
        elif isinstance(s, A.EnumDecl):
            generic = generic_params(s.type_params)
            self.w(indent, f"enum {s.name}{generic} {{")
            for c in s.cases:
                if not c.payloads:
                    self.w(indent + 1, c.name)
                else:
                    ps = ", ".join(type_str_repr(p) for p in c.payloads)
                    self.w(indent + 1, f"{c.name}({ps})")
            self.w(indent, "}")
        elif isinstance(s, A.ImportStmt):
            self.w(indent, f'import "{s.path}"')
        elif isinstance(s, A.ExternDecl):
            self.w(indent, f'extern "{s.host}" {{')
            for fn in s.fns:
                self.w(indent + 1, fn_sig(fn))
            self.w(indent, "}")
        else:
            self.w(indent, "/* 格式化器不支持该语句 */")

    def if_chain(self, st: A.IfStmt, indent: int) -> None:
        self.w(indent, f"if {self.expr(st.cond, 0, indent)} {{")
        self.stmts(st.then, indent + 1)
        self.else_tail(st, indent)

    def else_tail(self, st: A.IfStmt, indent: int) -> None:
        if st.els is not None and len(st.els) == 1 and isinstance(st.els[0], A.IfStmt):
            nested = st.els[0]
            self.w(indent, f"}} else if {self.expr(nested.cond, 0, indent)} {{")
            self.stmts(nested.then, indent + 1)
            self.else_tail(nested, indent)
            return
        if st.els:
            self.w(indent, "} else {")
            self.stmts(st.els, indent + 1)
        self.w(indent, "}")

    def fn_decl(self, st: A.FnDecl, indent: int) -> None:
        if st.recv is not None:
            # 接收者无冒号（Go 风格）
            recv = f"({st.recv.name} {type_str_repr(st.recv.typ)})"
            ps = []
            for p in st.params:
                if p.typ is None:
                    ps.append(p.name)
                else:
                    ps.append(f"{p.name}: {type_str_repr(p.typ)}")
            sig = f"fn {recv}.{st.name}({', '.join(ps)})"
            if st.ret is not None:
                sig += f": {type_str_repr(st.ret)}"
            self.w(indent, sig + " {")
        else:
            self.w(indent, fn_sig(st) + " {")
        self.stmts(st.body, indent + 1)
        self.w(indent, "}")

    # ———— 表达式（优先级感知加括号）————
    def expr(self, e: A.Expr, min_prec: int, indent: int) -> str:
        s = self.expr_raw(e, indent)
        if expr_prec(e) < min_prec:
            return f"({s})"
        return s

    def expr_raw(self, e: A.Expr, indent: int) -> str:
        if isinstance(e, A.IntLit):
            return str(e.val)
        if isinstance(e, A.FloatLit):
            from ..interp.values import format_g
            s = format_g(e.val)
            # 保住 float 身份：整数值的浮点字面量补 ".0"（否则重解析变 int）
            if not any(ch in s for ch in ".eE"):
                s += ".0"
            return s
        if isinstance(e, A.StrLit):
            return quote_str(e.val)
        if isinstance(e, A.BoolLit):
            return "true" if e.val else "false"
        if isinstance(e, A.Ident):
            return e.name
        if isinstance(e, A.Unary):
            op = {K.MINUS: "-", K.NOT: "!", K.AMP: "&"}[e.op]
            return op + self.expr(e.x, 8, indent)
        if isinstance(e, A.Binary):
            p = _PREC[e.op]
            return f"{self.expr(e.l, p, indent)} {kind_name(e.op)} {self.expr(e.r, p + 1, indent)}"
        if isinstance(e, A.RangeEx):
            return f"{self.expr(e.lo, 5, indent)}..{self.expr(e.hi, 6, indent)}"
        if isinstance(e, A.CallEx):
            as_ = [self.expr(a, 0, indent) for a in e.args]
            return f"{self.expr(e.callee, 9, indent)}({', '.join(as_)})"
        if isinstance(e, A.IndexEx):
            return f"{self.expr(e.obj, 9, indent)}[{self.expr(e.idx, 0, indent)}]"
        if isinstance(e, A.SliceEx):
            return (f"{self.expr(e.obj, 9, indent)}[{self.expr(e.lo, 0, indent)}:"
                    f"{self.expr(e.hi, 0, indent)}]")
        if isinstance(e, A.FieldEx):
            return f"{self.expr(e.obj, 9, indent)}.{e.name}"
        if isinstance(e, A.TryEx):
            return f"{self.expr(e.x, 9, indent)}?"
        if isinstance(e, A.StructLit):
            fs = [f"{fl.name}: {self.expr(fl.val, 0, indent)}" for fl in e.fields]
            return f"{e.type_name}{{{', '.join(fs)}}}"
        if isinstance(e, A.MapLit):
            kvs = [f"{self.expr(it.key, 0, indent)}: {self.expr(it.val, 0, indent)}"
                   for it in e.items]
            return "{" + ", ".join(kvs) + "}"
        if isinstance(e, A.ArrLit):
            is_ = [self.expr(el, 0, indent) for el in e.items]
            return "[" + ", ".join(is_) + "]"
        if isinstance(e, A.MatchEx):
            b = ["match " + self.expr(e.scrut, 0, indent) + " {"]
            for arm in e.arms:
                b.append(_INDENT * (indent + 1) + f"{self.pat(arm.pat)} => "
                         f"{self.expr(arm.body, 0, indent + 1)}")
            b.append(_INDENT * indent + "}")
            return "\n".join(b)
        if isinstance(e, A.FnLit):
            ps = []
            for p in e.params:
                if p.typ is None:
                    ps.append(p.name)
                else:
                    ps.append(f"{p.name}: {type_str_repr(p.typ)}")
            sig = f"fn({', '.join(ps)})"
            if e.ret is not None:
                sig += f": {type_str_repr(e.ret)}"
            f2 = _Formatter.__new__(_Formatter)
            f2.lines = []
            f2.cm = self.cm
            f2.last_src_line = self.last_src_line
            f2.w(0, sig + " {")
            f2.stmts(e.body, indent + 1)
            f2.w(indent, "}")
            return "\n".join(f2.lines)
        return "?"

    def pat(self, p: A.Pat) -> str:
        if isinstance(p, A.WildPat):
            return "_"
        if isinstance(p, A.BindPat):
            return p.name
        if isinstance(p, A.LitPat):
            return self.expr(p.val, 0, 0)
        if isinstance(p, A.CasePat):
            if not p.subs:
                return p.name
            return f"{p.name}({', '.join(self.pat(s) for s in p.subs)})"
        return "?"


_PREC: dict[K, int] = {
    K.OR: 1, K.AND: 2,
    K.EQ: 3, K.NEQ: 3,
    K.LT: 4, K.LE: 4, K.GT: 4, K.GE: 4,
    K.DOT2: 5,
    K.PLUS: 6, K.MINUS: 6, K.BIT_OR: 6, K.XOR: 6,
    K.STAR: 7, K.SLASH: 7, K.PERCENT: 7, K.AMP: 7, K.SHL: 7, K.SHR: 7,
}


def _prec_of(k: K) -> int:
    return _PREC.get(k, 0)


def expr_prec(e: A.Expr) -> int:
    if isinstance(e, A.Binary):
        return _prec_of(e.op)
    if isinstance(e, A.Unary):
        return 8
    return 9


def generic_params(tps: list[str]) -> str:
    if not tps:
        return ""
    return "[" + ", ".join(tps) + "]"


def fn_sig(d: A.FnDecl) -> str:
    ps = []
    for p in d.params:
        if p.typ is None:
            ps.append(p.name)
        else:
            ps.append(f"{p.name}: {type_str_repr(p.typ)}")
    sig = f"fn {d.name}{generic_params(d.type_params)}({', '.join(ps)})"
    if d.ret is not None:
        sig += f": {type_str_repr(d.ret)}"
    return sig


def type_str_repr(t) -> str:
    """类型注解的规范文本。"""
    if isinstance(t, A.NamedType):
        if not t.args:
            return t.name
        return f"{t.name}[{', '.join(type_str_repr(a) for a in t.args)}]"
    if isinstance(t, A.ArrType):
        return f"[{type_str_repr(t.elem)}]"
    if isinstance(t, A.MapType):
        return f"map[{type_str_repr(t.key)}]{type_str_repr(t.val)}"
    if isinstance(t, A.PtrType):
        return f"&{type_str_repr(t.elem)}"
    if isinstance(t, A.FnType):
        ps = [type_str_repr(p) for p in t.params]
        sig = f"fn({', '.join(ps)})"
        if t.ret is not None:
            sig += f": {type_str_repr(t.ret)}"
        return sig
    return "?"


def quote_str(s: str) -> str:
    """lang-go 转义规则输出字符串字面量（仅 \\n \\t \\r \\" \\\\ 五种）。"""
    out = ['"']
    for r in s:
        if r == "\n":
            out.append("\\n")
        elif r == "\t":
            out.append("\\t")
        elif r == "\r":
            out.append("\\r")
        elif r == '"':
            out.append('\\"')
        elif r == "\\":
            out.append("\\\\")
        else:
            out.append(r)
    out.append('"')
    return "".join(out)


def format_src(src: str) -> str:
    """把源码格式化为规范形态。解析失败抛 ParseError。"""
    prog = parse(src)
    f = _Formatter(src)
    f.stmts(prog, 0)
    f.flush_remaining(0)
    if not f.lines:
        return ""
    return "\n".join(f.lines) + "\n"
