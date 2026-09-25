"""Typed HIR 节点（v0.3 Semantic Core 第三层）。

AST → 已解析、已标注类型的语义树。每个表达式/语句节点携带 meta
（NodeID 编译内稳定、源码 span、ResolvedSymbol、ResolvedType）。
HIR 消除的语法糖：裸构造器引用 → Ctor(0 参)、obj.m(args) → MethodCall、
go <调用> → Spawn、&&/|| → AndThen/OrElse。节点为 frozen dataclass。
"""

from __future__ import annotations

from dataclasses import dataclass
from dataclasses import field as dfield

field = dfield  # noqa: A001 —— 模块级别名（MethodCall.field 属性仅在类体内遮蔽）
from enum import Enum
from typing import Optional, Union

from ..semantic.symbols import Symbol
from ..syntax.span import Span
from ..syntax.tokens import K
from ..typesys.model import UNKNOWN, Type


@dataclass(frozen=True, eq=False)
class NodeMeta:
    id: int
    line: int
    span: Span
    sym: Optional[Symbol] = None
    typ: Optional[Type] = None


_EMPTY_META = NodeMeta(0, 0, Span())


def meta_of(e: "Expr") -> NodeMeta:
    return getattr(e, "meta", _EMPTY_META)


def typ(e: Optional["Expr"]) -> Type:
    if e is None:
        return UNKNOWN
    t = meta_of(e).typ
    return t if t is not None else UNKNOWN


# ———— 程序 ————

@dataclass(frozen=True, eq=False)
class Param:
    name: str
    typ: Optional[Type]


@dataclass(frozen=True, eq=False)
class FieldDecl:
    name: str
    typ: Optional[Type]


@dataclass(frozen=True, eq=False)
class StructDecl:
    meta: NodeMeta
    name: str
    type_params: list[str]
    fields: list[FieldDecl]


@dataclass(frozen=True, eq=False)
class Case:
    sym: Optional[Symbol]
    name: str
    payloads: list[Optional[Type]]


@dataclass(frozen=True, eq=False)
class EnumDecl:
    meta: NodeMeta
    name: str
    type_params: list[str]
    cases: list[Case]


@dataclass(frozen=True, eq=False)
class Fn:
    meta: NodeMeta
    name: str
    sym: Optional[Symbol]
    recv: Optional[Param]
    type_params: list[str]
    params: list[Param]
    ret: Optional[Type]
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
    meta: NodeMeta
    name: str
    mutable: bool
    init: "Expr"


@dataclass(frozen=True, eq=False)
class Assign:
    meta: NodeMeta
    target: "Expr"
    op: K  # ASSIGN 或复合运算符
    rhs: "Expr"


@dataclass(frozen=True, eq=False)
class Eval:
    meta: NodeMeta
    x: "Expr"


@dataclass(frozen=True, eq=False)
class Return:
    meta: NodeMeta
    val: Optional["Expr"]


@dataclass(frozen=True, eq=False)
class If:
    meta: NodeMeta
    cond: "Expr"
    then: list["Stmt"]
    els: list["Stmt"]


@dataclass(frozen=True, eq=False)
class While:
    meta: NodeMeta
    cond: "Expr"
    body: list["Stmt"]


@dataclass(frozen=True, eq=False)
class ForIn:
    meta: NodeMeta
    var: str
    var_type: Optional[Type]
    iter: "Expr"
    body: list["Stmt"]


@dataclass(frozen=True, eq=False)
class Break:
    meta: NodeMeta


@dataclass(frozen=True, eq=False)
class Continue:
    meta: NodeMeta


@dataclass(frozen=True, eq=False)
class Spawn:
    meta: NodeMeta
    fn: Optional[Symbol]
    args: list["Expr"]


@dataclass(frozen=True, eq=False)
class FnStmt:
    meta: NodeMeta
    fn: Fn


Stmt = Union[Let, Assign, Eval, Return, If, While, ForIn, Break, Continue, Spawn, FnStmt]


# ———— 表达式 ————

class LitKind(Enum):
    INT = 0
    FLOAT = 1
    STR = 2
    BOOL = 3


@dataclass(frozen=True, eq=False)
class Lit:
    meta: NodeMeta
    kind: LitKind
    i64: int = 0
    f64: float = 0.0
    s: str = ""
    b: bool = False


@dataclass(frozen=True, eq=False)
class LocalRef:
    meta: NodeMeta
    name: str


@dataclass(frozen=True, eq=False)
class FnRef:
    meta: NodeMeta
    name: str


@dataclass(frozen=True, eq=False)
class BuiltinRef:
    meta: NodeMeta
    name: str


@dataclass(frozen=True, eq=False)
class Ctor:
    meta: NodeMeta
    ctor_sym: Symbol
    args: list["Expr"]


@dataclass(frozen=True, eq=False)
class Call:
    meta: NodeMeta
    fn: Optional[Symbol] = None
    builtin: str = ""
    callee: Optional["Expr"] = None
    args: list["Expr"] = field(default_factory=list)


@dataclass(frozen=True, eq=False)
class MethodCall:
    meta: NodeMeta
    recv: "Expr"
    method: Optional[Symbol] = None
    args: list["Expr"] = field(default_factory=list)
    field: str = ""  # 字段函数值调用时的字段名（方法调用为空）
    name_span: Span = dfield(default_factory=Span)  # 方法名 token 区间（rename 用）


@dataclass(frozen=True, eq=False)
class Unary:
    meta: NodeMeta
    op: K
    x: "Expr"


@dataclass(frozen=True, eq=False)
class Binary:
    meta: NodeMeta
    op: K
    l: "Expr"
    r: "Expr"


@dataclass(frozen=True, eq=False)
class AndThen:
    meta: NodeMeta
    l: "Expr"
    r: "Expr"


@dataclass(frozen=True, eq=False)
class OrElse:
    meta: NodeMeta
    l: "Expr"
    r: "Expr"


@dataclass(frozen=True, eq=False)
class Range:
    meta: NodeMeta
    lo: "Expr"
    hi: "Expr"


@dataclass(frozen=True, eq=False)
class Index:
    meta: NodeMeta
    obj: "Expr"
    idx: "Expr"


@dataclass(frozen=True, eq=False)
class Slice:
    meta: NodeMeta
    obj: "Expr"
    lo: "Expr"
    hi: "Expr"


@dataclass(frozen=True, eq=False)
class Field:
    meta: NodeMeta
    obj: "Expr"
    name: str


@dataclass(frozen=True, eq=False)
class Try:
    meta: NodeMeta
    x: "Expr"


@dataclass(frozen=True, eq=False)
class FieldValue:
    name: str
    val: "Expr"


@dataclass(frozen=True, eq=False)
class StructLit:
    meta: NodeMeta
    fields: list[FieldValue]


@dataclass(frozen=True, eq=False)
class MapLit:
    meta: NodeMeta
    keys: list["Expr"]
    vals: list["Expr"]


@dataclass(frozen=True, eq=False)
class SliceLit:
    meta: NodeMeta
    elems: list["Expr"]


@dataclass(frozen=True, eq=False)
class Arm:
    line: int
    pat: "Pat"
    body: "Expr"


@dataclass(frozen=True, eq=False)
class Match:
    meta: NodeMeta
    scrut: "Expr"
    kind: int  # MatchKind 值（checker 结论；int 避免 typesys↔hir 环导入）
    arms: list[Arm]


@dataclass(frozen=True, eq=False)
class WildPat:
    line: int


@dataclass(frozen=True, eq=False)
class BindPat:
    line: int
    name: str
    typ: Optional[Type] = None


@dataclass(frozen=True, eq=False)
class LitPat:
    line: int
    val: "Expr"


@dataclass(frozen=True, eq=False)
class CtorPat:
    line: int
    ctor: Optional[Symbol]
    subs: list["Pat"]


Pat = Union[WildPat, BindPat, LitPat, CtorPat]


@dataclass(frozen=True, eq=False)
class FnLit:
    meta: NodeMeta
    params: list[Param]
    ret: Optional[Type]
    body: list["Stmt"]


Expr = Union[
    Lit, LocalRef, FnRef, BuiltinRef, Ctor, Call, MethodCall, Unary, Binary,
    AndThen, OrElse, Range, Index, Slice, Field, Try, StructLit, MapLit,
    SliceLit, Match, FnLit,
]
