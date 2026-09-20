"""语法分析器（PARSE-001..006）。

Pratt 表达式解析，优先级表与 Go 参考一致（PARSE-004）。控制头禁裸 struct 字面量
用 noStruct 计数器实现；REPL 依赖 incomplete（EOF 处报错）判定续行。
错误文本、行摘录规则与 Go 参考逐条对应。
"""

from __future__ import annotations

from ..errors import ParseError
from . import ast_nodes as A
from .lexer import lex
from .span import Position, Span
from .tokens import K, Token, kind_name

# 优先级（低 → 高）；0 表示不参与二元运算
_PREC: dict[K, int] = {
    K.OR: 1,
    K.AND: 2,
    K.EQ: 3, K.NEQ: 3,
    K.LT: 4, K.LE: 4, K.GT: 4, K.GE: 4,
    K.DOT2: 5,
    K.PLUS: 6, K.MINUS: 6, K.BIT_OR: 6, K.XOR: 6,  # 对齐 Go：+ - | ^ 同级
    K.STAR: 7, K.SLASH: 7, K.PERCENT: 7, K.AMP: 7, K.SHL: 7, K.SHR: 7,
}


def _line_starts(src: str) -> list[int]:
    starts = [0]
    n = 0
    for ch in src:
        if ch == "\n":
            starts.append(n + 1)
        n += 1
    return starts


def _line_snippet(lines: list[str], line: int) -> str:
    if line < 1:
        line = 1
    if line > len(lines):
        line = len(lines)
    if line < 1:
        return ""
    t = lines[line - 1].strip()
    if t:
        return t
    if line > 1:
        return lines[line - 2].strip()
    return ""


def _first_byte_is_upper(name: str) -> bool:
    """与 Go 的 unicode.IsUpper(rune(lit[0]))（字节转 rune）同口径：
    ASCII 大写，或 UTF-8 首字节落在 Latin-1 大写字母区间 0xC0..0xDE。"""
    b = name.encode("utf-8")[0]
    return (0x41 <= b <= 0x5A) or (0xC0 <= b <= 0xDE)


class Parser:
    def __init__(self, toks: list[Token], src: str) -> None:
        self.toks = toks
        self.i = 0
        self.no_struct = 0
        self.lines = src.split("\n")
        self.line_start = _line_starts(src)
        self.prev_end = 0
        self.prev_line = 1

    # ———— 基础 ————
    def cur(self) -> Token:
        return self.toks[self.i]

    def next(self) -> Token:
        t = self.toks[self.i]
        self.prev_end = t.end
        self.prev_line = t.line
        if self.i < len(self.toks) - 1:
            self.i += 1
        return t

    def pos_at(self, offset: int, line: int) -> Position:
        if line < 1:
            line = 1
        if line > len(self.line_start):
            line = len(self.line_start)
        col = offset - self.line_start[line - 1] + 1
        if col < 1:
            col = 1
        return Position(offset, line, col)

    def span_of(self, start_tok: Token) -> Span:
        return Span(
            start=self.pos_at(start_tok.start, start_tok.line),
            end=self.pos_at(self.prev_end, self.prev_line),
        )

    def at(self, k: K) -> bool:
        return self.toks[self.i].kind == k

    def skip_nl(self) -> None:
        while self.at(K.NEWLINE):
            self.i += 1

    def accept(self, k: K) -> bool:
        if self.at(k):
            t = self.toks[self.i]
            self.prev_end = t.end
            self.prev_line = t.line
            self.i += 1
            return True
        return False

    def err_at(self, t: Token, msg: str) -> ParseError:
        return ParseError(msg, t.line, incomplete=(t.kind == K.EOF),
                          snippet=_line_snippet(self.lines, t.line))

    def expect(self, k: K) -> Token:
        if self.at(k):
            return self.next()
        t = self.cur()
        raise self.err_at(t, f"期望 {kind_name(k)}，得到 {kind_name(t.kind)}")

    def expect_name(self) -> str:
        if self.at(K.IDENT):
            return self.next().lit
        if self.at(K.UNDERSCORE):
            self.next()
            return "_"
        t = self.cur()
        raise self.err_at(t, f"期望标识符，得到 {kind_name(t.kind)}")

    # ———— 顶层 ————
    _RESERVED: dict[K, str] = {K.KW_CHAN: "chan", K.KW_INTERFACE: "interface"}

    def parse_top(self) -> A.Stmt:
        t = self.cur()
        reserved = self._RESERVED.get(t.kind)
        if reserved is not None:
            raise self.err_at(t, f"保留字 {reserved} 尚未启用（规划于 M3+）")
        if t.kind == K.KW_IMPORT:
            self.next()
            path = self.expect(K.STR)
            return A.ImportStmt(path.lit, t.line, self.span_of(t))
        if t.kind == K.KW_EXTERN:
            return self.parse_extern()
        return self.parse_stmt()

    def parse_extern(self) -> A.Stmt:
        t = self.next()  # extern
        if not self.at(K.STR):
            raise self.err_at(t, 'extern 后须是宿主名字符串，如 extern "go"')
        host = self.next()
        self.expect(K.LBRACE)
        decl = A.ExternDecl(host.lit, [], t.line, Span())
        while True:
            self.skip_nl()
            if self.accept(K.RBRACE):
                decl.span = self.span_of(t)
                return decl
            if self.at(K.EOF):
                raise self.err_at(self.cur(), "extern 块未闭合")
            fn_tok = self.expect(K.KW_FN)
            name = self.expect_name()
            tps = self.parse_type_params()
            params = self.parse_params()
            ret = None
            if self.accept(K.COLON):
                ret = self.parse_type()
            decl.fns.append(A.FnDecl(name, None, tps, params, ret, [], fn_tok.line, self.span_of(fn_tok)))

    # ———— 语句 ————
    def parse_stmt(self) -> A.Stmt:
        t = self.cur()
        if t.kind in (K.KW_LET, K.KW_VAR):
            self.next()
            name = self.expect_name()
            typ = None
            if self.accept(K.COLON):
                typ = self.parse_type()
            self.expect(K.ASSIGN)
            init = self.parse_expr(0)
            return A.LetStmt(name, t.kind == K.KW_VAR, typ, init, t.line, self.span_of(t))

        if t.kind == K.KW_RETURN:
            self.next()
            val = None
            if not self.at(K.NEWLINE) and not self.at(K.RBRACE) and not self.at(K.EOF):
                val = self.parse_expr(0)
            return A.ReturnStmt(val, t.line, self.span_of(t))

        if t.kind == K.KW_IF:
            return self.parse_if()

        if t.kind == K.KW_WHILE:
            self.next()
            self.no_struct += 1
            try:
                cond = self.parse_expr(0)
            finally:
                self.no_struct -= 1
            body = self.parse_block()
            return A.WhileStmt(cond, body, t.line, self.span_of(t))

        if t.kind == K.KW_FOR:
            self.next()
            name = self.expect_name()
            self.expect(K.KW_IN)
            self.no_struct += 1
            try:
                it = self.parse_expr(0)
            finally:
                self.no_struct -= 1
            body = self.parse_block()
            return A.ForInStmt(name, it, body, t.line, self.span_of(t))

        if t.kind == K.KW_GO:
            self.next()
            callee = self.parse_expr(0)
            if not isinstance(callee, A.CallEx):
                raise self.err_at(t, "go 的目标须是函数调用")
            return A.GoStmt(callee, t.line, self.span_of(t))

        if t.kind in (K.KW_BREAK, K.KW_CONTINUE):
            self.next()
            cls = A.BreakStmt if t.kind == K.KW_BREAK else A.ContinueStmt
            return cls(t.line, self.span_of(t))

        if t.kind == K.KW_FN:
            return self.parse_fn_decl()
        if t.kind == K.KW_STRUCT:
            return self.parse_struct()
        if t.kind == K.KW_ENUM:
            return self.parse_enum()

        # 表达式 / 赋值 / 复合赋值
        e = self.parse_expr(0)
        if self.accept(K.ASSIGN):
            self._check_target(e)
            v = self.parse_expr(0)
            return A.AssignStmt(e, v, K.ASSIGN, t.line, self.span_of(t))
        for op in (K.PLUS_ASSIGN, K.MINUS_ASSIGN, K.STAR_ASSIGN, K.SLASH_ASSIGN):
            if self.accept(op):
                self._check_target(e)
                v = self.parse_expr(0)
                return A.AssignStmt(e, v, op, t.line, self.span_of(t))
        return A.ExprStmt(e, t.line, self.span_of(t))

    @staticmethod
    def _check_target(e: A.Expr) -> None:
        if isinstance(e, A.Ident):
            return
        if isinstance(e, A.FieldEx):
            Parser._check_target(e.obj)
            return
        if isinstance(e, A.IndexEx):
            Parser._check_target(e.obj)
            return
        raise ParseError("赋值目标不合法", e.line)

    def parse_if(self) -> A.Stmt:
        t = self.next()  # if
        self.no_struct += 1
        try:
            cond = self.parse_expr(0)
        finally:
            self.no_struct -= 1
        then = self.parse_block()
        st = A.IfStmt(cond, then, None, t.line, self.span_of(t))
        self.skip_nl()
        if self.accept(K.KW_ELSE):
            if self.at(K.KW_IF):
                st.els = [self.parse_if()]
            else:
                st.els = self.parse_block()
        st.span = self.span_of(t)
        return st

    def parse_block(self) -> list[A.Stmt]:
        self.expect(K.LBRACE)
        stmts: list[A.Stmt] = []
        while True:
            self.skip_nl()
            if self.accept(K.RBRACE):
                return stmts
            if self.at(K.EOF):
                raise self.err_at(self.cur(), "块未闭合，缺少 }")
            stmts.append(self.parse_stmt())

    # ———— 声明 ————
    def parse_fn_decl(self) -> A.Stmt:
        t = self.next()  # fn
        recv: A.Param | None = None
        if self.accept(K.LPAREN):
            rname = self.expect_name()
            if self.at(K.AMP):
                raise self.err_at(self.cur(), "v0 暂不支持指针接收者")
            rtyp = self.parse_type()
            self.expect(K.RPAREN)
            self.expect(K.DOT)
            recv = A.Param(rname, rtyp)
        name = self.expect_name()
        return self.parse_func_rest(t, recv, name)

    def parse_func_rest(self, start_tok: Token, recv: A.Param | None, name: str) -> A.FnDecl:
        tps = self.parse_type_params()
        params = self.parse_params()
        ret = None
        if self.accept(K.COLON):
            ret = self.parse_type()
        body = self.parse_block()
        return A.FnDecl(name, recv, tps, params, ret, body, start_tok.line, self.span_of(start_tok))

    def parse_type_params(self) -> list[str]:
        if not self.accept(K.LBRACKET):
            return []
        tps: list[str] = []
        while True:
            tps.append(self.expect_name())
            if not self.accept(K.COMMA):
                break
        self.expect(K.RBRACKET)
        return tps

    def parse_params(self) -> list[A.Param]:
        self.expect(K.LPAREN)
        params: list[A.Param] = []
        while not self.at(K.RPAREN):
            name = self.expect_name()
            typ = None
            if self.accept(K.COLON):
                typ = self.parse_type()
            params.append(A.Param(name, typ))
            if not self.accept(K.COMMA):
                break
        self.expect(K.RPAREN)
        return params

    def parse_struct(self) -> A.Stmt:
        t = self.next()  # struct
        name = self.expect_name()
        tps = self.parse_type_params()
        self.expect(K.LBRACE)
        fields: list[A.Param] = []
        while True:
            self.skip_nl()
            if self.accept(K.RBRACE):
                break
            if self.at(K.EOF):
                raise self.err_at(self.cur(), "struct 未闭合")
            fname = self.expect_name()
            self.expect(K.COLON)
            typ = self.parse_type()
            fields.append(A.Param(fname, typ))
            self.skip_nl()
            self.accept(K.COMMA)
        return A.StructDecl(name, tps, fields, t.line, self.span_of(t))

    def parse_enum(self) -> A.Stmt:
        t = self.next()  # enum
        name = self.expect_name()
        tps = self.parse_type_params()
        self.expect(K.LBRACE)
        cases: list[A.EnumCase] = []
        while True:
            self.skip_nl()
            if self.accept(K.RBRACE):
                break
            if self.at(K.EOF):
                raise self.err_at(self.cur(), "enum 未闭合")
            cn = self.expect_name()
            case = A.EnumCase(cn, [], self.cur().line)
            if self.accept(K.LPAREN):
                while not self.at(K.RPAREN):
                    case.payloads.append(self.parse_type())
                    if not self.accept(K.COMMA):
                        break
                self.expect(K.RPAREN)
            cases.append(case)
            self.skip_nl()
            self.accept(K.COMMA)
        return A.EnumDecl(name, tps, cases, t.line, self.span_of(t))

    # ———— 类型注解 ————
    def parse_type(self) -> A.TypeEx:
        t = self.cur()
        if t.kind == K.KW_CHAN:
            self.next()
            if not self.accept(K.LBRACKET):
                raise self.err_at(t, "chan 需要 elem 类型参数，如 chan[int]")
            elem = self.parse_type()
            self.expect(K.RBRACKET)
            return A.NamedType("chan", [elem])
        if t.kind == K.AMP:
            self.next()
            return A.PtrType(self.parse_type())
        if t.kind == K.KW_FN:
            self.next()
            self.expect(K.LPAREN)
            ft = A.FnType([])
            while not self.at(K.RPAREN):
                ft.params.append(self.parse_type())
                if not self.accept(K.COMMA):
                    break
            self.expect(K.RPAREN)
            if self.accept(K.COLON):
                ft.ret = self.parse_type()
            return ft
        if t.kind == K.LBRACKET:
            self.next()
            elem = self.parse_type()
            self.expect(K.RBRACKET)
            return A.ArrType(elem)
        if t.kind == K.IDENT:
            self.next()
            if t.lit == "map" and self.at(K.LBRACKET):
                self.next()
                key = self.parse_type()
                self.expect(K.RBRACKET)
                val = self.parse_type()
                return A.MapType(key, val)
            nt = A.NamedType(t.lit)
            if self.accept(K.LBRACKET):
                while not self.at(K.RBRACKET):
                    nt.args.append(self.parse_type())
                    if not self.accept(K.COMMA):
                        break
                self.expect(K.RBRACKET)
            return nt
        raise self.err_at(t, f"期望类型，得到 {kind_name(t.kind)}")

    # ———— 表达式（Pratt）————
    def parse_expr(self, min_prec: int) -> A.Expr:
        left = self.parse_unary()
        while True:
            t = self.cur()
            prec = _PREC.get(t.kind, 0)
            if prec == 0 or prec < min_prec:
                return left
            self.next()
            right = self.parse_expr(prec + 1)
            if t.kind == K.DOT2:
                left = A.RangeEx(left, right, t.line, self.span_of(t))
            else:
                left = A.Binary(t.kind, left, right, t.line, self.span_of(t))

    def parse_unary(self) -> A.Expr:
        t = self.cur()
        if t.kind in (K.MINUS, K.NOT, K.AMP):
            self.next()
            x = self.parse_unary()
            return A.Unary(t.kind, x, t.line, self.span_of(t))
        return self.parse_postfix()

    def parse_postfix(self) -> A.Expr:
        x = self.parse_primary()
        while True:
            t = self.cur()
            if t.kind == K.LPAREN:  # 调用
                self.next()
                args: list[A.Expr] = []
                while True:
                    self.skip_nl()
                    if self.at(K.RPAREN):
                        break
                    args.append(self.parse_expr(0))
                    self.skip_nl()
                    if not self.accept(K.COMMA):
                        break
                self.skip_nl()
                self.expect(K.RPAREN)
                x = A.CallEx(x, args, t.line, self.span_of(t))
            elif t.kind == K.LBRACKET:  # 索引或切片
                self.next()
                self.skip_nl()
                lo = self.parse_expr(0)
                if self.accept(K.COLON):
                    if self.at(K.RBRACKET):
                        raise self.err_at(self.cur(), "切片需要上界（v0 不支持开区间切片）")
                    hi = self.parse_expr(0)
                    self.skip_nl()
                    self.expect(K.RBRACKET)
                    x = A.SliceEx(x, lo, hi, t.line, self.span_of(t))
                else:
                    self.skip_nl()
                    self.expect(K.RBRACKET)
                    x = A.IndexEx(x, lo, t.line, self.span_of(t))
            elif t.kind == K.DOT:
                self.next()
                name = self.expect(K.IDENT)
                x = A.FieldEx(x, name.lit, t.line, self.span_of(t))
            elif t.kind == K.QUESTION:
                self.next()
                x = A.TryEx(x, t.line, self.span_of(t))
            else:
                return x

    def parse_primary(self) -> A.Expr:
        t = self.cur()
        if t.kind == K.INT:
            self.next()
            try:
                v = int(t.lit, 10)
            except ValueError:
                raise self.err_at(t, f"整数溢出: {t.lit}") from None
            if not 0 <= v < 2**63:
                raise self.err_at(t, f"整数溢出: {t.lit}")
            return A.IntLit(v, t.line, self.span_of(t))
        if t.kind == K.FLOAT:
            self.next()
            return A.FloatLit(float(t.lit), t.line, self.span_of(t))
        if t.kind == K.STR:
            self.next()
            return A.StrLit(t.lit, t.line, self.span_of(t))
        if t.kind in (K.KW_TRUE, K.KW_FALSE):
            self.next()
            return A.BoolLit(t.kind == K.KW_TRUE, t.line, self.span_of(t))
        if t.kind == K.IDENT:
            self.next()
            if self.at(K.LBRACE) and self.no_struct == 0:
                return self.parse_struct_lit(t)
            return A.Ident(t.lit, t.line, self.span_of(t))
        if t.kind == K.LPAREN:
            self.next()
            saved = self.no_struct
            self.no_struct = 0
            try:
                x = self.parse_expr(0)
            finally:
                self.no_struct = saved
            self.skip_nl()
            self.expect(K.RPAREN)
            return x
        if t.kind == K.LBRACKET:  # 数组字面量
            self.next()
            items: list[A.Expr] = []
            while True:
                self.skip_nl()
                if self.at(K.RBRACKET):
                    break
                items.append(self.parse_expr(0))
                self.skip_nl()
                if not self.accept(K.COMMA):
                    break
            self.skip_nl()
            self.expect(K.RBRACKET)
            return A.ArrLit(items, t.line, self.span_of(t))
        if t.kind == K.LBRACE:  # 映射字面量
            self.next()
            items: list[A.MapItem] = []
            while True:
                self.skip_nl()
                if self.accept(K.RBRACE):
                    break
                if self.at(K.EOF):
                    raise self.err_at(self.cur(), "映射字面量未闭合")
                key = self.parse_expr(0)
                self.expect(K.COLON)
                val = self.parse_expr(0)
                items.append(A.MapItem(key, val))
                self.skip_nl()
                self.accept(K.COMMA)
            return A.MapLit(items, t.line, self.span_of(t))
        if t.kind == K.KW_FN:  # 匿名函数
            self.next()
            fn = self.parse_func_rest(t, None, "")
            return A.FnLit(fn.type_params, fn.params, fn.ret, fn.body, t.line, self.span_of(t))
        if t.kind == K.KW_MATCH:
            return self.parse_match()
        if t.kind == K.EOF:
            raise self.err_at(t, "表达式意外结束")
        raise self.err_at(t, f"意外的记号 {kind_name(t.kind)}")

    def parse_struct_lit(self, start_tok: Token) -> A.Expr:
        name, line = start_tok.lit, start_tok.line
        self.next()  # {
        fields: list[A.FieldLit] = []
        while True:
            self.skip_nl()
            if self.accept(K.RBRACE):
                break
            if self.at(K.EOF):
                raise self.err_at(self.cur(), "struct 字面量未闭合")
            fname = self.expect(K.IDENT)
            self.expect(K.COLON)
            val = self.parse_expr(0)
            fields.append(A.FieldLit(fname.lit, val))
            self.skip_nl()
            self.accept(K.COMMA)
        return A.StructLit(name, fields, line, self.span_of(start_tok))

    def parse_match(self) -> A.Expr:
        t = self.next()  # match
        self.no_struct += 1
        try:
            scrut = self.parse_expr(0)
        finally:
            self.no_struct -= 1
        self.expect(K.LBRACE)
        arms: list[A.Arm] = []
        while True:
            self.skip_nl()
            if self.accept(K.RBRACE):
                break
            if self.at(K.EOF):
                raise self.err_at(self.cur(), "match 未闭合")
            arm_line = self.cur().line
            pat = self.parse_pattern()
            self.expect(K.ARROW)
            body = self.parse_expr(0)
            arms.append(A.Arm(pat, body, arm_line))
            if self.at(K.NEWLINE) or self.at(K.RBRACE):
                continue
            raise self.err_at(self.cur(), "match 分支之间需要换行")
        return A.MatchEx(scrut, arms, t.line, self.span_of(t))

    def parse_pattern(self) -> A.Pat:
        t = self.cur()
        if t.kind == K.UNDERSCORE:
            self.next()
            return A.WildPat(t.line, self.span_of(t))
        if t.kind in (K.INT, K.FLOAT, K.STR, K.KW_TRUE, K.KW_FALSE):
            e = self.parse_primary()
            return A.LitPat(e, t.line, self.span_of(t))
        if t.kind == K.IDENT:
            self.next()
            if self.accept(K.LPAREN):
                subs: list[A.Pat] = []
                while not self.at(K.RPAREN):
                    subs.append(self.parse_pattern())
                    if not self.accept(K.COMMA):
                        break
                self.expect(K.RPAREN)
                return A.CasePat(t.lit, subs, t.line, self.span_of(t))
            if _first_byte_is_upper(t.lit):
                return A.CasePat(t.lit, [], t.line, self.span_of(t))
            return A.BindPat(t.lit, t.line, self.span_of(t))
        raise self.err_at(t, f"非法模式，得到 {kind_name(t.kind)}")


def parse(src: str) -> list[A.Stmt]:
    """解析整个源文件/REPL 片段，返回语句序列。"""
    toks = lex(src)
    p = Parser(toks, src)
    prog: list[A.Stmt] = []
    while True:
        p.skip_nl()
        if p.at(K.EOF):
            return prog
        prog.append(p.parse_top())
