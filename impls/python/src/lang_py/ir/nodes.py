"""Core IR 节点（v0.3 Semantic Core 第四层）。

HIR 降级而成的最小后端友好节点集：去元数据（保留类型）、局部槽位化、
迭代协议显式化。不做 SSA；解释器与 →Go/→JS 后端优先消费本层。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional, Union

from ..semantic.symbols import Symbol
from ..typesys.model import UNKNOWN, Type

# ———— 运算符（宿主无关字符串形态） ————

OP_ASSIGN = "="
OP_PLUS = "+"
OP_MINUS = "-"
OP_STAR = "*"
OP_SLASH = "/"
OP_PERCENT = "%"
OP_PLUS_A = "+="
OP_MINUS_A = "-="
OP_STAR_A = "*="
OP_SLASH_A = "/="
OP_EQ = "=="
OP_NEQ = "!="
OP_LT = "<"
OP_LE = "<="
OP_GT = ">"
OP_GE = ">="
OP_AND = "&"
OP_BIT_OR = "|"
OP_XOR = "^"
OP_SHL = "<<"
OP_SHR = ">>"
OP_ANDAND = "&&"
OP_OROR = "||"
OP_NEG = "-"
OP_NOT = "!"
OP_REF = "&"


# ———— 程序 ————

@dataclass(frozen=True, eq=False)
class FieldDecl:
    name: str
    typ: Optional[Type]


@dataclass(frozen=True, eq=False)
class StructDecl:
    name: str
    type_params: list[str]
    fields: list[FieldDecl]


@dataclass(frozen=True, eq=False)
class Case:
    name: str
    payloads: list[Optional[Type]]


@dataclass(frozen=True, eq=False)
class EnumDecl:
    name: str
    type_params: list[str]
    cases: list[Case]


@dataclass(frozen=True, eq=False)
class Local:
    """具名局部槽位（参数/绑定/for 变量/臂绑定）。以身份为键（等价 Go 指针）。"""

    name: str
    mutable: bool
    typ: Optional[Type] = None
    sym: Optional[Symbol] = None


@dataclass(frozen=True, eq=False)
class Fn:
    name: str
    sym: Optional[Symbol]
    recv: Optional[Local]
    type_params: list[str]
    params: list[Local]
    ret: Optional[Type]
    locals: list[Local]
    body: list["Stmt"]
    extern: bool = False
    extern_host: str = ""


@dataclass(frozen=True, eq=False)
class Prog:
    structs: list[StructDecl] = field(default_factory=list)
    enums: list[EnumDecl] = field(default_factory=list)
    globals: list["Let"] = field(default_factory=list)
    fns: list[Fn] = field(default_factory=list)
    main: Optional[Fn] = None


# ———— 语句 ————

@dataclass(frozen=True, eq=False)
class Let:
    local: Local
    init: "Expr"


@dataclass(frozen=True, eq=False)
class Assign:
    target: "Expr"
    op: str
    val: "Expr"


@dataclass(frozen=True, eq=False)
class Eval:
    x: "Expr"


@dataclass(frozen=True, eq=False)
class Return:
    val: Optional["Expr"]


@dataclass(frozen=True, eq=False)
class If:
    cond: "Expr"
    then: list["Stmt"]
    els: list["Stmt"]


@dataclass(frozen=True, eq=False)
class While:
    cond: "Expr"
    body: list["Stmt"]


class IterKind(Enum):
    UNKNOWN = 0
    SLICE = 1
    MAP = 2
    STRING = 3
    RANGE = 4


@dataclass(frozen=True, eq=False)
class ForIn:
    var: Local
    kind: IterKind
    iter: "Expr"
    body: list["Stmt"]


@dataclass(frozen=True, eq=False)
class Break:
    pass


@dataclass(frozen=True, eq=False)
class Continue:
    pass


@dataclass(frozen=True, eq=False)
class Spawn:
    fn: Optional[Symbol]
    args: list["Expr"]


@dataclass(frozen=True, eq=False)
class FnDef:
    fn: Fn


Stmt = Union[Let, Assign, Eval, Return, If, While, ForIn, Break, Continue, Spawn, FnDef]


# ———— 表达式 ————

class ConstKind(Enum):
    INT = 0
    FLOAT = 1
    STR = 2
    BOOL = 3


@dataclass(frozen=True, eq=False)
class Const:
    kind: ConstKind
    typ: Type = UNKNOWN
    i64: int = 0
    f64: float = 0.0
    s: str = ""
    b: bool = False


@dataclass(frozen=True, eq=False)
class LocalGet:
    local: Local
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class FnGet:
    name: str
    sym: Optional[Symbol]
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class BuiltinGet:
    name: str
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Ctor:
    sym: Symbol
    args: list["Expr"]
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Call:
    """四形态：用户 fn（fn）/内建（builtin）/方法（method+recv）/函数值（callee）。"""

    typ: Type = UNKNOWN
    fn: Optional[Symbol] = None
    builtin: str = ""
    method: Optional[Symbol] = None
    recv: Optional["Expr"] = None
    callee: Optional["Expr"] = None
    args: list["Expr"] = field(default_factory=list)


@dataclass(frozen=True, eq=False)
class Unary:
    op: str
    x: "Expr"
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Binary:
    op: str
    l: "Expr"
    r: "Expr"
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Range:
    lo: "Expr"
    hi: "Expr"
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Index:
    obj: "Expr"
    idx: "Expr"
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Slice:
    obj: "Expr"
    lo: "Expr"
    hi: "Expr"
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Field:
    obj: "Expr"
    name: str
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class Try:
    x: "Expr"
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class FieldValue:
    name: str
    val: "Expr"


@dataclass(frozen=True, eq=False)
class StructLit:
    struct: Optional[Symbol]
    fields: list[FieldValue]
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class MapLit:
    keys: list["Expr"]
    vals: list["Expr"]
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class SliceLit:
    elems: list["Expr"]
    typ: Type = UNKNOWN


class MatchKind(Enum):
    LITERAL = 0
    ENUM = 1
    RESULT = 2
    OPTION = 3


@dataclass(frozen=True, eq=False)
class Arm:
    pat: "Pat"
    body: "Expr"


@dataclass(frozen=True, eq=False)
class WildPat:
    pass


@dataclass(frozen=True, eq=False)
class BindPat:
    name: str
    typ: Optional[Type]
    local: Optional[Local] = None


@dataclass(frozen=True, eq=False)
class LitPat:
    val: "Expr"


@dataclass(frozen=True, eq=False)
class CtorPat:
    sym: Optional[Symbol]
    subs: list["Pat"]


Pat = Union[WildPat, BindPat, LitPat, CtorPat]


@dataclass(frozen=True, eq=False)
class Match:
    scrut: "Expr"
    kind: MatchKind
    arms: list[Arm]
    typ: Type = UNKNOWN


@dataclass(frozen=True, eq=False)
class FnLit:
    params: list[Local]
    ret: Optional[Type]
    locals: list[Local]
    body: list["Stmt"]
    typ: Type = UNKNOWN


Expr = Union[
    Const, LocalGet, FnGet, BuiltinGet, Ctor, Call, Unary, Binary, Range,
    Index, Slice, Field, Try, StructLit, MapLit, SliceLit, Match, FnLit,
]
