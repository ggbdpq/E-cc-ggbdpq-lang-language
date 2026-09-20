"""词法器（LEX-001..008）。

行为对齐 Go 参考：换行即语句结束，续行只看上一记号类别；十六进制整数字面量
的记号文本统一为十进制；字符串转义仅 \\n \\t \\r \\" \\\\ 五种；单独 `_` 为通配记号。
错误文本与 Go 参考逐条对应（含行摘录）。
"""

from __future__ import annotations

from ..errors import LexError
from .tokens import K, KEYWORDS, Token, is_cont

_HEX = set("0123456789abcdefABCDEF")


def _is_ident_start(r: str) -> bool:
    return r == "_" or r.isalpha()


def _is_digit(r: str) -> bool:
    return "0" <= r <= "9"


class _Lexer:
    def __init__(self, src: str) -> None:
        self.runes = src
        self.i = 0
        self.line = 1
        self.last_kind: K | None = None
        self.toks: list[Token] = []

    # ———— 错误（带当前行摘录；输出末尾报错时摘录前一行）————
    def err(self, msg: str) -> LexError:
        line = self.line
        if line > 1 and self.i >= len(self.runes):
            line -= 1
        text = ""
        if line >= 1:
            start = 0
            cur = 1
            for i, ch in enumerate(self.runes):
                if cur >= line:
                    break
                if ch == "\n":
                    cur += 1
                    start = i + 1
            end = self.runes.find("\n", start)
            if end < 0:
                end = len(self.runes)
            text = self.runes[start:end].strip()
        return LexError(msg, self.line, text)

    def peek(self, n: int) -> str:
        j = self.i + n
        return self.runes[j] if j < len(self.runes) else "\0"

    def emit(self, k: K, lit: str, start: int) -> None:
        t = Token(k, lit, self.line, start, self.i)
        self.toks.append(t)
        self.last_kind = k

    def emit_newline(self) -> None:
        if self.last_kind is None or is_cont(self.last_kind) or self.last_kind == K.NEWLINE:
            return
        self.toks.append(Token(K.NEWLINE, "\n", self.line - 1, self.i - 1, self.i))
        self.last_kind = K.NEWLINE

    # ———— 各记号类 ————
    def number(self, start: int) -> None:
        if self.runes[start] == "0" and self.i + 1 < len(self.runes) and self.peek(1) in "xX":
            self.i += 2
            digits = self.i
            while self.i < len(self.runes) and self.runes[self.i] in _HEX:
                self.i += 1
            if self.i == digits:
                raise self.err("十六进制字面量缺少数字（如 0xFF）")
            text = self.runes[start : self.i]
            try:
                val = int(text, 16)
            except ValueError:
                raise self.err("十六进制字面量超出 int 范围: " + text) from None
            if not 0 <= val < 2**63:
                raise self.err("十六进制字面量超出 int 范围: " + text)
            self.emit(K.INT, str(val), start)
            return
        while self.i < len(self.runes) and _is_digit(self.runes[self.i]):
            self.i += 1
        is_float = False
        # "1..3" 里第一个 '.' 不属于数字
        if self.i < len(self.runes) and self.runes[self.i] == "." and _is_digit(self.peek(1)):
            is_float = True
            self.i += 1
            while self.i < len(self.runes) and _is_digit(self.runes[self.i]):
                self.i += 1
        lit = self.runes[start : self.i]
        self.emit(K.FLOAT if is_float else K.INT, lit, start)

    def string_lit(self, start: int) -> None:
        self.i += 1
        parts: list[str] = []
        while True:
            if self.i >= len(self.runes) or self.runes[self.i] == "\n":
                raise self.err("字符串未闭合")
            r = self.runes[self.i]
            if r == '"':
                self.i += 1
                break
            if r == "\\":
                self.i += 1
                if self.i >= len(self.runes):
                    raise self.err("字符串未闭合")
                esc = self.runes[self.i]
                if esc == "n":
                    parts.append("\n")
                elif esc == "t":
                    parts.append("\t")
                elif esc == "r":
                    parts.append("\r")
                elif esc == '"':
                    parts.append('"')
                elif esc == "\\":
                    parts.append("\\")
                else:
                    raise self.err(f"不支持的转义 \\{esc}")
                self.i += 1
                continue
            parts.append(r)
            self.i += 1
        self.emit(K.STR, "".join(parts), start)

    def ident(self, start: int) -> None:
        while self.i < len(self.runes) and (
            _is_ident_start(self.runes[self.i]) or _is_digit(self.runes[self.i])
        ):
            self.i += 1
        name = self.runes[start : self.i]
        if name == "_":
            self.emit(K.UNDERSCORE, name, start)
            return
        k = KEYWORDS.get(name)
        if k is not None:
            self.emit(k, name, start)
        else:
            self.emit(K.IDENT, name, start)

    def op(self, start: int) -> None:
        r = self.runes[self.i]
        nxt = self.peek(1)

        def two(k: K, lit: str) -> bool:
            if nxt == lit[1]:
                self.i += 2
                self.emit(k, lit, start)
                return True
            return False

        if r == "&":
            if not two(K.AND, "&&"):
                self.i += 1
                self.emit(K.AMP, "&", start)
        elif r == "|":
            if not two(K.OR, "||"):
                self.i += 1
                self.emit(K.BIT_OR, "|", start)
        elif r == "^":
            self.i += 1
            self.emit(K.XOR, "^", start)
        elif r == "+":
            if not two(K.PLUS_ASSIGN, "+="):
                self.i += 1
                self.emit(K.PLUS, "+", start)
        elif r == "-":
            if not two(K.MINUS_ASSIGN, "-="):
                self.i += 1
                self.emit(K.MINUS, "-", start)
        elif r == "*":
            if not two(K.STAR_ASSIGN, "*="):
                self.i += 1
                self.emit(K.STAR, "*", start)
        elif r == "/":
            if not two(K.SLASH_ASSIGN, "/="):
                self.i += 1
                self.emit(K.SLASH, "/", start)
        elif r == "=":
            if not two(K.EQ, "==") and not two(K.ARROW, "=>"):
                self.i += 1
                self.emit(K.ASSIGN, "=", start)
        elif r == "!":
            if not two(K.NEQ, "!="):
                self.i += 1
                self.emit(K.NOT, "!", start)
        elif r == "<":
            if not two(K.SHL, "<<") and not two(K.LE, "<="):
                self.i += 1
                self.emit(K.LT, "<", start)
        elif r == ">":
            if not two(K.SHR, ">>") and not two(K.GE, ">="):
                self.i += 1
                self.emit(K.GT, ">", start)
        elif r == ".":
            if not two(K.DOT2, ".."):
                self.i += 1
                self.emit(K.DOT, ".", start)
        elif r in "%(){}[],:?":
            simple = {
                "%": K.PERCENT, "(": K.LPAREN, ")": K.RPAREN, "{": K.LBRACE,
                "}": K.RBRACE, "[": K.LBRACKET, "]": K.RBRACKET, ",": K.COMMA,
                ":": K.COLON, "?": K.QUESTION,
            }
            self.i += 1
            self.emit(simple[r], r, start)
        else:
            raise self.err(f"非法字符 {r}")


def lex(src: str) -> list[Token]:
    """源码 → 记号流（末尾追加 EOF）。"""
    lx = _Lexer(src)
    while lx.i < len(lx.runes):
        start = lx.i
        r = lx.runes[lx.i]
        if r in " \t\r":
            lx.i += 1
        elif r == "\n":
            lx.i += 1
            lx.line += 1
            lx.emit_newline()
        elif r == "/" and lx.peek(1) == "/":
            while lx.i < len(lx.runes) and lx.runes[lx.i] != "\n":
                lx.i += 1
        elif _is_digit(r):
            lx.number(start)
        elif r == '"':
            lx.string_lit(start)
        elif _is_ident_start(r):
            lx.ident(start)
        else:
            lx.op(start)
    lx.toks.append(Token(K.EOF, "", lx.line, lx.i, lx.i))
    return lx.toks
