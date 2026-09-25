"""syntax 子包：tokens/lexer/parser/ast/walk/comments。"""

from .parser import parse
from .lexer import lex

__all__ = ["parse", "lex"]
