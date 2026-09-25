"""AST 节点（与 Go 参考的语法事实一一对应；形状对齐 specs/grammar.md）。

设计约定：
- 节点为 dataclass(eq=False)：身份即对象身份（等价 Go 指针语义），解析器/检查器
  以 id(节点) 为键回填引用与类型。
- 节点不可变性按约定维护；唯一合法的变更是装载器解析后盖 Span.file 章
  （specs/source-span.md），这与 Go 参考一致。
- 每个节点带 line（Go 的 Pos()）与 span（列级区间，供契约层输出）。
- 类型注解节点（TypeEx）不带 span（Go 同）：NamedType/ArrType/MapType/PtrType/FnType。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional, Union

from .span import Position, Span
from .tokens import K


# ———— 类型注解 ————

@dataclass(eq=False)
class NamedType:
    name: str
    args: list["TypeEx"] = field(default_factory=list)


@dataclass(eq=False)
class ArrType:
    elem: "TypeEx"


@dataclass(eq=False)
class MapType:
    key: "TypeEx"
    val: "TypeEx"


@dataclass(eq=False)
class PtrType:
    elem: "TypeEx"


@dataclass(eq=False)
class FnType:
    params: list["TypeEx"]
    ret: Optional["TypeEx"] = None


TypeEx = Union[NamedType, ArrType, MapType, PtrType, FnType]


# ———— 表达式 ————

@dataclass(eq=False)
class IntLit:
    val: int
    line: int
    span: Span


@dataclass(eq=False)
class FloatLit:
    val: float
    line: int
    span: Span


@dataclass(eq=False)
class StrLit:
    val: str
    line: int
    span: Span


@dataclass(eq=False)
class BoolLit:
    val: bool
    line: int
    span: Span


@dataclass(eq=False)
class Ident:
    name: str
    line: int
    span: Span


@dataclass(eq=False)
class Unary:
    op: K
    x: "Expr"
    line: int
    span: Span


@dataclass(eq=False)
class Binary:
    op: K
    l: "Expr"
    r: "Expr"
    line: int
    span: Span


@dataclass(eq=False)
class RangeEx:
    lo: "Expr"
    hi: "Expr"
    line: int
    span: Span


@dataclass(eq=False)
class CallEx:
    callee: "Expr"
    args: list["Expr"]
    line: int
    span: Span


@dataclass(eq=False)
class IndexEx:
    obj: "Expr"
    idx: "Expr"
    line: int
    span: Span


@dataclass(eq=False)
class SliceEx:
    obj: "Expr"
    lo: "Expr"
    hi: "Expr"
    line: int
    span: Span


@dataclass(eq=False)
class FieldEx:
    obj: "Expr"
    name: str
    line: int
    span: Span


@dataclass(eq=False)
class TryEx:
    x: "Expr"
    line: int
    span: Span


@dataclass(eq=False)
class FieldLit:
    name: str
    val: "Expr"


@dataclass(eq=False)
class StructLit:
    type_name: str
    fields: list[FieldLit]
    line: int
    span: Span


@dataclass(eq=False)
class MapItem:
    key: "Expr"
    val: "Expr"


@dataclass(eq=False)
class MapLit:
    items: list[MapItem]
    line: int
    span: Span


@dataclass(eq=False)
class ArrLit:
    items: list["Expr"]
    line: int
    span: Span


@dataclass(eq=False)
class Arm:
    pat: "Pat"
    body: "Expr"
    line: int
    span: Span = field(default_factory=Span)


@dataclass(eq=False)
class MatchEx:
    scrut: "Expr"
    arms: list[Arm]
    line: int
    span: Span


@dataclass(eq=False)
class Param:
    name: str
    typ: Optional[TypeEx]


@dataclass(eq=False)
class FnLit:
    type_params: list[str]
    params: list[Param]
    ret: Optional[TypeEx]
    body: list["Stmt"]
    line: int
    span: Span


Expr = Union[
    IntLit, FloatLit, StrLit, BoolLit, Ident, Unary, Binary, RangeEx, CallEx,
    IndexEx, SliceEx, FieldEx, TryEx, StructLit, MapLit, ArrLit, MatchEx, FnLit,
]


# ———— 模式 ————

@dataclass(eq=False)
class WildPat:
    line: int
    span: Span


@dataclass(eq=False)
class BindPat:
    name: str
    line: int
    span: Span


@dataclass(eq=False)
class LitPat:
    val: Expr
    line: int
    span: Span


@dataclass(eq=False)
class CasePat:
    name: str
    subs: list["Pat"]
    line: int
    span: Span


Pat = Union[WildPat, BindPat, LitPat, CasePat]


# ———— 语句 ————

@dataclass(eq=False)
class LetStmt:
    name: str
    mutable: bool
    typ: Optional[TypeEx]
    init: Expr
    line: int
    span: Span


@dataclass(eq=False)
class AssignStmt:
    target: Expr
    val: Expr
    op: K  # ASSIGN 或复合赋值
    line: int
    span: Span


@dataclass(eq=False)
class ExprStmt:
    x: Expr
    line: int
    span: Span


@dataclass(eq=False)
class ReturnStmt:
    val: Optional[Expr]
    line: int
    span: Span


@dataclass(eq=False)
class IfStmt:
    cond: Expr
    then: list["Stmt"]
    els: Optional[list["Stmt"]]
    line: int
    span: Span


@dataclass(eq=False)
class WhileStmt:
    cond: Expr
    body: list["Stmt"]
    line: int
    span: Span


@dataclass(eq=False)
class ForInStmt:
    var: str
    iter: Expr
    body: list["Stmt"]
    line: int
    span: Span


@dataclass(eq=False)
class BreakStmt:
    line: int
    span: Span


@dataclass(eq=False)
class ContinueStmt:
    line: int
    span: Span


@dataclass(eq=False)
class FnDecl:
    name: str
    recv: Optional[Param]
    type_params: list[str]
    params: list[Param]
    ret: Optional[TypeEx]
    body: list["Stmt"]
    line: int
    span: Span


@dataclass(eq=False)
class StructDecl:
    name: str
    type_params: list[str]
    fields: list[Param]
    line: int
    span: Span


@dataclass(eq=False)
class EnumCase:
    name: str
    payloads: list[TypeEx]
    line: int
    span: Span = field(default_factory=Span)


@dataclass(eq=False)
class EnumDecl:
    name: str
    type_params: list[str]
    cases: list[EnumCase]
    line: int
    span: Span


@dataclass(eq=False)
class GoStmt:
    call: CallEx
    line: int
    span: Span


@dataclass(eq=False)
class ImportStmt:
    path: str
    line: int
    span: Span


@dataclass(eq=False)
class ExternDecl:
    host: str
    fns: list[FnDecl]
    line: int
    span: Span


Stmt = Union[
    LetStmt, AssignStmt, ExprStmt, ReturnStmt, IfStmt, WhileStmt, ForInStmt,
    BreakStmt, ContinueStmt, FnDecl, StructDecl, EnumDecl, GoStmt, ImportStmt, ExternDecl,
]
