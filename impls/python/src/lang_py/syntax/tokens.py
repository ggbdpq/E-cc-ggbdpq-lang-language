"""记号类别与记号（LEX-001..004）。

Kind 用 Enum（int 值稳定），kind_name 提供与 Go 参考一致的记号显示名（诊断文本用）。
Token 的 lit：字符串字面量为解码后的值；十六进制整数统一为十进制文本。
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum, auto


class K(Enum):
    EOF = auto()
    NEWLINE = auto()
    IDENT = auto()
    INT = auto()
    FLOAT = auto()
    STR = auto()
    UNDERSCORE = auto()

    KW_LET = auto()
    KW_VAR = auto()
    KW_FN = auto()
    KW_RETURN = auto()
    KW_IF = auto()
    KW_ELSE = auto()
    KW_WHILE = auto()
    KW_FOR = auto()
    KW_IN = auto()
    KW_MATCH = auto()
    KW_STRUCT = auto()
    KW_ENUM = auto()
    KW_TRUE = auto()
    KW_FALSE = auto()
    KW_BREAK = auto()
    KW_CONTINUE = auto()

    # 保留字
    KW_GO = auto()
    KW_CHAN = auto()
    KW_IMPORT = auto()
    KW_EXTERN = auto()
    KW_INTERFACE = auto()

    PLUS = auto()
    MINUS = auto()
    STAR = auto()
    SLASH = auto()
    PERCENT = auto()
    ASSIGN = auto()  # =
    EQ = auto()  # ==
    NEQ = auto()  # !=
    LT = auto()
    LE = auto()
    GT = auto()
    GE = auto()
    AND = auto()  # &&
    OR = auto()  # ||
    NOT = auto()  # !
    AMP = auto()  # &
    BIT_OR = auto()
    XOR = auto()
    SHL = auto()
    SHR = auto()
    PLUS_ASSIGN = auto()
    MINUS_ASSIGN = auto()
    STAR_ASSIGN = auto()
    SLASH_ASSIGN = auto()
    QUESTION = auto()
    DOT = auto()
    DOT2 = auto()  # ..
    COMMA = auto()
    COLON = auto()
    ARROW = auto()  # =>
    LPAREN = auto()
    RPAREN = auto()
    LBRACE = auto()
    RBRACE = auto()
    LBRACKET = auto()
    RBRACKET = auto()


KEYWORDS: dict[str, K] = {
    "let": K.KW_LET, "var": K.KW_VAR, "fn": K.KW_FN, "return": K.KW_RETURN,
    "if": K.KW_IF, "else": K.KW_ELSE, "while": K.KW_WHILE, "for": K.KW_FOR,
    "in": K.KW_IN, "match": K.KW_MATCH, "struct": K.KW_STRUCT, "enum": K.KW_ENUM,
    "true": K.KW_TRUE, "false": K.KW_FALSE, "break": K.KW_BREAK, "continue": K.KW_CONTINUE,
    "go": K.KW_GO, "chan": K.KW_CHAN, "import": K.KW_IMPORT,
    "extern": K.KW_EXTERN, "interface": K.KW_INTERFACE,
}

_KIND_NAMES: dict[K, str] = {
    K.EOF: "文件尾", K.NEWLINE: "换行", K.IDENT: "标识符",
    K.INT: "整数", K.FLOAT: "浮点数", K.STR: "字符串", K.UNDERSCORE: "_",
    K.KW_LET: "let", K.KW_VAR: "var", K.KW_FN: "fn", K.KW_RETURN: "return",
    K.KW_IF: "if", K.KW_ELSE: "else", K.KW_WHILE: "while", K.KW_FOR: "for",
    K.KW_IN: "in", K.KW_MATCH: "match", K.KW_STRUCT: "struct", K.KW_ENUM: "enum",
    K.KW_TRUE: "true", K.KW_FALSE: "false", K.KW_BREAK: "break", K.KW_CONTINUE: "continue",
    K.KW_GO: "go", K.KW_CHAN: "chan", K.KW_IMPORT: "import",
    K.KW_EXTERN: "extern", K.KW_INTERFACE: "interface",
    K.PLUS: "+", K.MINUS: "-", K.STAR: "*", K.SLASH: "/", K.PERCENT: "%",
    K.ASSIGN: "=", K.EQ: "==", K.NEQ: "!=", K.LT: "<", K.LE: "<=",
    K.GT: ">", K.GE: ">=", K.AND: "&&", K.OR: "||", K.NOT: "!", K.AMP: "&",
    K.BIT_OR: "|", K.XOR: "^", K.SHL: "<<", K.SHR: ">>",
    K.PLUS_ASSIGN: "+=", K.MINUS_ASSIGN: "-=", K.STAR_ASSIGN: "*=", K.SLASH_ASSIGN: "/=",
    K.QUESTION: "?", K.DOT: ".", K.DOT2: "..", K.COMMA: ",", K.COLON: ":",
    K.ARROW: "=>", K.LPAREN: "(", K.RPAREN: ")", K.LBRACE: "{", K.RBRACE: "}",
    K.LBRACKET: "[", K.RBRACKET: "]",
}


def kind_name(k: K) -> str:
    return _KIND_NAMES.get(k, f"Kind({k.value})")


def is_cont(k: K) -> bool:
    """该类记号之后换行视为续行（LEX-008）。"""
    return k in {
        K.PLUS, K.MINUS, K.STAR, K.SLASH, K.PERCENT,
        K.ASSIGN, K.EQ, K.NEQ, K.LT, K.LE, K.GT, K.GE,
        K.AND, K.OR, K.COMMA, K.COLON, K.ARROW, K.DOT, K.DOT2,
        K.LPAREN, K.LBRACKET, K.LBRACE,
    }


@dataclass(frozen=True, slots=True)
class Token:
    kind: K
    lit: str
    line: int
    start: int  # rune 偏移（0 基）
    end: int

    def display(self) -> str:
        return kind_name(self.kind)
