"""静态符号与解析产物（NAME-*）。

SymbolID 按声明/绑定顺序编号（本次编译内稳定）。符号对象以身份比较
（等价 Go 指针），字段由 resolver/checker/HIR builder 依序填充。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum, auto
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from ..syntax import ast_nodes as A
    from ..errors import Diagnostic


class SymKind(Enum):
    FN = auto()
    METHOD = auto()
    VAR = auto()  # let/var 绑定（全局与局部）
    PARAM = auto()  # 函数参数与方法接收者
    STRUCT = auto()
    ENUM = auto()
    CTOR = auto()  # enum case 构造器（含预载 Some/None/Ok/Err）


KFN, KMETHOD, KVAR, KPARAM, KSTRUCT, KENUM, KCTOR = SymKind


def kind_name(k: SymKind) -> str:
    return {
        SymKind.FN: "fn",
        SymKind.METHOD: "方法",
        SymKind.VAR: "绑定",
        SymKind.PARAM: "参数",
        SymKind.STRUCT: "struct",
        SymKind.ENUM: "enum",
        SymKind.CTOR: "构造器",
    }[k]


@dataclass(eq=False)
class Symbol:
    id: int = 0
    kind: SymKind = KFN
    name: str = ""
    line: int = 0
    mutable: bool = False

    # 声明形态
    type_ex: Optional["A.TypeEx"] = None  # 声明注解原文（fn 返回值 / let 注解）
    params: list[Optional["A.TypeEx"]] = field(default_factory=list)  # 参数注解（可含 None）
    recv_name: str = ""  # METHOD：接收者 struct 名
    arity: int = 0
    type_params: list[str] = field(default_factory=list)

    # CTOR
    struct_name: str = ""  # 所属 enum 名
    case_index: int = 0
    payloads: list[Optional["A.TypeEx"]] = field(default_factory=list)

    # STRUCT
    fields: list[str] = field(default_factory=list)
    field_types: list[Optional["A.TypeEx"]] = field(default_factory=list)
    methods: dict[str, "Symbol"] = field(default_factory=dict)

    extern: bool = False  # FN：extern 声明（无函数体）

    builtin_mark: bool = False  # 预载符号（内建/预载 struct/http/Some 等）
    decl: object = None  # 源 AST 声明节点（checker/HIR 复用；预载符号为 None）

    def is_ctor(self) -> bool:
        return self.kind == KCTOR


# 编译内节点身份注册表（decl_syms 以 id 为键；inspect 反查 Span 用）。
# resolver 写入 decl_syms 时同步登记；单次编译内有效。
NODE_REGISTRY: dict[int, object] = {}


@dataclass(eq=False)
class ResolveResult:
    """解析产物。refs/pat_refs/decl_syms 以 AST 节点身份为键，仅本次编译内有效。"""

    symbols: list[Symbol] = field(default_factory=list)
    refs: dict[int, Symbol] = field(default_factory=dict)  # id(Ident/StructLit/GoStmt callee Ident) → 符号
    pat_refs: dict[int, Symbol] = field(default_factory=dict)  # id(CasePat/BindPat) → 符号
    decl_syms: dict[int, Symbol] = field(default_factory=dict)  # id(LetStmt/ForInStmt/FnDecl) → 符号
    fn_params: dict[int, list[Symbol]] = field(default_factory=dict)  # fn 符号 id → 参数符号（接收者在前，跳过 "_"）
    builtin: dict[str, Symbol] = field(default_factory=dict)  # 预载符号
    diags: list["Diagnostic"] = field(default_factory=list)

    def has_errors(self) -> bool:
        return bool(self.diags)
