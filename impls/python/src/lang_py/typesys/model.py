"""语义类型模型（TYPE-KINDS-001；types/type.go 的语义等价物）。

Type 不可变值对象；Struct/Enum 按 Symbol 身份同一（名义类型）；
TypeArg 为泛型擦除占位（彼此同一）。赋值兼容规则见 assignable()。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum, auto
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from ..semantic.symbols import Symbol


class TK(Enum):
    INVALID = auto()  # 错误传播占位
    INT = auto()
    FLOAT = auto()
    BOOL = auto()
    STRING = auto()
    ERROR = auto()  # error struct
    UNIT = auto()  # ()
    RANGE = auto()  # lo..hi
    ANY = auto()  # 动态边界
    UNKNOWN = auto()  # 推断不出
    NEVER = auto()
    SLICE = auto()  # [T]
    MAP = auto()  # map[K]V
    REF = auto()  # &T
    CHAN = auto()  # chan[T]
    FN = auto()
    STRUCT = auto()  # 名义
    ENUM = auto()  # 名义
    OPTION = auto()  # Option[T]
    RESULT = auto()  # Result[T, E]
    TYPE_ARG = auto()  # 泛型类型参数


@dataclass(frozen=True, eq=False)
class Type:
    """字段按 kind 选用：SLICE/REF/CHAN/OPTION → elem；MAP → key+elem；
    FN → params+ret；RESULT → params[0]=T、ret=E；STRUCT/ENUM → sym；TYPE_ARG → name。"""

    kind: TK
    elem: Optional["Type"] = None
    key: Optional["Type"] = None
    params: tuple["Type", ...] = ()
    ret: Optional["Type"] = None
    sym: Optional["Symbol"] = None
    name: str = ""  # TYPE_ARG：泛型参数名


# 预载原始类型单例
INT = Type(TK.INT)
FLOAT = Type(TK.FLOAT)
BOOL = Type(TK.BOOL)
STRING = Type(TK.STRING)
ERROR_T = Type(TK.ERROR)
UNIT = Type(TK.UNIT)
RANGE = Type(TK.RANGE)
ANY = Type(TK.ANY)
UNKNOWN = Type(TK.UNKNOWN)
NEVER = Type(TK.NEVER)
INVALID = Type(TK.INVALID)


def slice_of(elem: Type) -> Type:
    return Type(TK.SLICE, elem=elem)


def map_of(key: Type, val: Type) -> Type:
    return Type(TK.MAP, key=key, elem=val)


def ref_of(elem: Type) -> Type:
    return Type(TK.REF, elem=elem)


def chan_of(elem: Type) -> Type:
    return Type(TK.CHAN, elem=elem)


def option_of(elem: Type) -> Type:
    return Type(TK.OPTION, elem=elem)


def result_of(ok: Type, err: Type) -> Type:
    return Type(TK.RESULT, params=(ok,), ret=err)


def fn_of(params: list[Type], ret: Type) -> Type:
    return Type(TK.FN, params=tuple(params), ret=ret)


def struct_of(sym: "Symbol") -> Type:
    return Type(TK.STRUCT, sym=sym)


def enum_of(sym: "Symbol") -> Type:
    return Type(TK.ENUM, sym=sym)


def type_arg_of(name: str) -> Type:
    return Type(TK.TYPE_ARG, name=name)


def result_ok(t: Type) -> Type:
    if t.kind == TK.RESULT and len(t.params) == 1:
        return t.params[0]
    return UNKNOWN


def result_err(t: Type) -> Type:
    if t.kind == TK.RESULT and t.ret is not None:
        return t.ret
    return UNKNOWN


def type_equal(a: Optional[Type], b: Optional[Type]) -> bool:
    """结构同一（Struct/Enum 按符号同一；TypeArg 擦除后彼此同一）。"""
    if a is b:
        return True
    if a is None or b is None or a.kind != b.kind:
        return False
    k = a.kind
    if k in (TK.SLICE, TK.REF, TK.CHAN, TK.OPTION):
        return type_equal(a.elem, b.elem)
    if k == TK.MAP:
        return type_equal(a.key, b.key) and type_equal(a.elem, b.elem)
    if k == TK.FN:
        if len(a.params) != len(b.params):
            return False
        return all(type_equal(p, q) for p, q in zip(a.params, b.params)) and type_equal(a.ret, b.ret)
    if k == TK.RESULT:
        return type_equal(result_ok(a), result_ok(b)) and type_equal(result_err(a), result_err(b))
    if k in (TK.STRUCT, TK.ENUM):
        return a.sym is not None and a.sym is b.sym
    if k == TK.TYPE_ARG:
        return True
    return True  # 原始类型


def _lax_any(t: Type) -> bool:
    return t.kind in (TK.ANY, TK.UNKNOWN, TK.INVALID, TK.TYPE_ARG)


def assignable(to: Optional[Type], from_: Optional[Type]) -> bool:
    """赋值兼容（TYPE-ASSIGN-001）：
    宽松类型双向放行；容器/代数类型按元素协变；fn 参数逆协变 + 返回协变；名义同一。"""
    if to is None or from_ is None:
        return True
    if _lax_any(to) or _lax_any(from_):
        return True
    if to.kind != from_.kind:
        return False
    k = to.kind
    if k in (TK.SLICE, TK.REF, TK.CHAN, TK.OPTION):
        return assignable(to.elem, from_.elem)
    if k == TK.MAP:
        return assignable(to.key, from_.key) and assignable(to.elem, from_.elem)
    if k == TK.RESULT:
        return assignable(result_ok(to), result_ok(from_)) and assignable(result_err(to), result_err(from_))
    if k == TK.FN:
        if len(to.params) != len(from_.params):
            return False
        for tp, fp in zip(to.params, from_.params):
            if not assignable(fp, tp):  # 参数逆协变
                return False
        return assignable(to.ret, from_.ret)
    return type_equal(to, from_)


def is_numeric(t: Type) -> bool:
    return t.kind in (TK.INT, TK.FLOAT)


def is_concrete(t: Optional[Type]) -> bool:
    return t is not None and t.kind not in (TK.ANY, TK.UNKNOWN, TK.INVALID)


def _sym_name(s: Optional["Symbol"]) -> str:
    return "?" if s is None else s.name


def type_str(t: Optional[Type]) -> str:
    """诊断与 dump 用类型名（贴近源码写法）。"""
    if t is None:
        return "unknown"
    k = t.kind
    if k == TK.INVALID:
        return "invalid"
    if k == TK.INT:
        return "int"
    if k == TK.FLOAT:
        return "float"
    if k == TK.BOOL:
        return "bool"
    if k == TK.STRING:
        return "string"
    if k == TK.ERROR:
        return "error"
    if k == TK.UNIT:
        return "()"
    if k == TK.RANGE:
        return "range"
    if k == TK.ANY:
        return "any"
    if k == TK.UNKNOWN:
        return "unknown"
    if k == TK.NEVER:
        return "never"
    if k == TK.SLICE:
        return f"[{type_str(t.elem)}]"
    if k == TK.MAP:
        return f"map[{type_str(t.key)}]{type_str(t.elem)}"
    if k == TK.REF:
        return f"&{type_str(t.elem)}"
    if k == TK.CHAN:
        return f"chan[{type_str(t.elem)}]"
    if k == TK.FN:
        ps = ", ".join(type_str(p) for p in t.params)
        s = f"fn({ps})"
        if t.ret is not None and t.ret.kind != TK.UNIT:
            s += f": {type_str(t.ret)}"
        return s
    if k == TK.STRUCT:
        return _sym_name(t.sym)
    if k == TK.ENUM:
        return _sym_name(t.sym)
    if k == TK.OPTION:
        return f"Option[{type_str(t.elem)}]"
    if k == TK.RESULT:
        return f"Result[{type_str(result_ok(t))}, {type_str(result_err(t))}]"
    if k == TK.TYPE_ARG:
        return t.name if t.name else _sym_name(t.sym)
    return "?"
