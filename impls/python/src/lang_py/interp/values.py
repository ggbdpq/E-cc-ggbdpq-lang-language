"""运行时值模型与输出格式化（VALUE-* / RUNTIME-OUTPUT-003）。

标量用宿主原生类型（int/float/bool/str，动态边界）；复合值用显式类：
ArrVal/MapVal 引用语义（共享），StructVal/EnumVal 值语义（copy_value 递归拷贝）。
int 一律 64 位环绕语义（GAP-001，与 →Go 一致）。

格式化函数（fmt_val/format_g/go_quote）是跨实现硬契约：
与 Go 参考的 fmtVal / strconv 'g' / strconv.Quote 逐字节一致（GAP-002/003）。
"""

from __future__ import annotations

import math
import unicodedata
from dataclasses import dataclass, field
from typing import Callable, Optional

from ..errors import LangRuntimeError
from ..syntax import ast_nodes as A

I64_MASK = (1 << 64) - 1
I64_SIGN = 1 << 63


def i64(v: int) -> int:
    """按 int64 环绕取值（二进制补码）。"""
    v &= I64_MASK
    return v - (1 << 64) if v >= I64_SIGN else v


def i64_div(a: int, b: int) -> int:
    """Go 语义整除：向零截断。"""
    q = abs(a) // abs(b)
    return -q if (a < 0) != (b < 0) else q


def i64_mod(a: int, b: int) -> int:
    """Go 语义取模：符号随被除数。"""
    return a - i64_div(a, b) * b


class UnitVal:
    __slots__ = ()

    def __repr__(self) -> str:  # pragma: no cover
        return "()"


UNIT = UnitVal()


@dataclass(eq=False)
class RangeVal:
    lo: int
    hi: int


@dataclass(eq=False)
class ArrVal:
    items: list


@dataclass(eq=False)
class StructDef:
    name: str
    fields: list[str]
    methods: dict = field(default_factory=dict)


@dataclass(eq=False)
class EnumDef:
    name: str
    cases: list[str]

    def case_index(self, name: str) -> int:
        for i, c in enumerate(self.cases):
            if c == name:
                return i
        return -1


@dataclass(eq=False)
class StructVal:
    def_: StructDef
    fields: list


@dataclass(eq=False)
class EnumVal:
    def_: EnumDef
    case: int
    payloads: list = field(default_factory=list)


@dataclass(eq=False)
class PtrVal:
    cell: list  # 单元素可变格 [value]


@dataclass(eq=False)
class ChanVal:
    items: list = field(default_factory=list)


@dataclass(eq=False)
class Closure:
    name: str
    decl: A.FnDecl
    env: "object"  # interp.Env


@dataclass(eq=False)
class Builtin:
    name: str
    fn: Callable


# ———— 值语义 ————

def copy_value(v):
    """struct 递归拷贝、enum 载荷递归拷贝；容器共享（VALUE-COPY-001/002）。"""
    if isinstance(v, StructVal):
        return StructVal(v.def_, [copy_value(f) for f in v.fields])
    if isinstance(v, EnumVal):
        return EnumVal(v.def_, v.case, [copy_value(p) for p in v.payloads])
    return v


def type_name(v) -> str:
    t = type(v)
    if t is bool:
        return "bool"
    if t is int:
        return "int"
    if t is float:
        return "float"
    if t is str:
        return "string"
    if t is UnitVal:
        return "()"
    if t is ArrVal:
        return "数组"
    if t is MapVal:
        return "映射"
    if t is RangeVal:
        return "区间"
    if t is StructVal:
        return v.def_.name
    if t is EnumVal:
        return v.def_.name
    if t is Closure:
        return "函数"
    if t is Builtin:
        return "内建函数"
    if t is PtrVal:
        return "引用"
    if t is ChanVal:
        return "chan"
    return "未知值"


def equals(a, b) -> bool:
    """== 语义：标量同型比较、enum 同 case 递归比较；容器不可比（RUNTIME-EQ-003）。"""
    ta, tb = type(a), type(b)
    if ta is int:
        if tb is not int:
            raise LangRuntimeError(type_err(a, b))
        return a == b
    if ta is float:
        if tb is not float:
            raise LangRuntimeError(type_err(a, b))
        return a == b
    if ta is bool:
        if tb is not bool:
            raise LangRuntimeError(type_err(a, b))
        return a == b
    if ta is str:
        if tb is not str:
            raise LangRuntimeError(type_err(a, b))
        return a == b
    if ta is EnumVal:
        if tb is not EnumVal or b.def_ is not a.def_ or b.case != a.case:
            return False
        for x, y in zip(a.payloads, b.payloads):
            if not equals(x, y):
                return False
        return True
    raise LangRuntimeError(f"{type_name(a)} 不可用 == 比较")


def type_err(a, b) -> str:
    return f"{type_name(a)} 与 {type_name(b)} 不可比较（无隐式转换）"


def truthy(v) -> bool:
    if type(v) is not bool:
        raise LangRuntimeError(f"条件须为 bool，得到 {type_name(v)}")
    return v


def compare(a, b) -> Optional[int]:
    """同型 int/float/str 大小比较；不可比返回 None。"""
    ta, tb = type(a), type(b)
    if ta is int and tb is int:
        return (a > b) - (a < b)
    if ta is float and tb is float:
        return (a > b) - (a < b)
    if ta is str and tb is str:
        return (a > b) - (a < b)
    return None


# ———— 输出格式 ————

def format_g(f: float) -> str:
    """Go strconv.FormatFloat(f, 'g', -1, 64)：最短十进制；指数 < -4 或 >= 6 走 %e
    （shortest 精度下 eprec=6，已与 go1.26 输出逐字节差分验证）。GAP-002。"""
    if math.isnan(f):
        return "NaN"
    if math.isinf(f):
        return "+Inf" if f > 0 else "-Inf"
    if f == 0.0:
        return "-0" if math.copysign(1.0, f) < 0 else "0"
    neg = f < 0
    s = repr(-f if neg else f)
    if "e" in s or "E" in s:
        mant, _, exp_s = s.replace("E", "e").partition("e")
        exp = int(exp_s)
    else:
        mant, exp = s, 0
    if "." in mant:
        int_part, frac_part = mant.split(".")
    else:
        int_part, frac_part = mant, ""
    digits = int_part + frac_part
    dp = len(int_part) + exp
    digits = digits.rstrip("0")
    if not digits:
        digits = "0"
    while digits.startswith("0"):
        digits = digits[1:]
        dp -= 1
    e10 = dp - 1
    if e10 < -4 or e10 >= 6:
        out = digits[0]
        if len(digits) > 1:
            out += "." + digits[1:]
        out += "e"
        if e10 < 0:
            out += "-"
        else:
            out += "+"
        out += f"{abs(e10):02d}"
    else:
        if dp <= 0:
            out = "0." + "0" * (-dp) + digits
        elif dp >= len(digits):
            out = digits + "0" * (dp - len(digits))
        else:
            out = digits[:dp] + "." + digits[dp:]
    return "-" + out if neg else out


_PRINT_EXTRA = {"\a": "\\a", "\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r",
                "\t": "\\t", "\v": "\\v"}


def _is_print(r: str) -> bool:
    if r == " ":
        return True
    cat = unicodedata.category(r)
    return cat[0] in "LMNPS"


def go_quote(s: str) -> str:
    """Go strconv.Quote：双引号包裹；\\ \" 与控制字符转义；非打印字符 \\x/\\u/\\U。"""
    out = ['"']
    for r in s:
        if r == '"':
            out.append('\\"')
        elif r == "\\":
            out.append("\\\\")
        elif r in _PRINT_EXTRA:
            out.append(_PRINT_EXTRA[r])
        elif _is_print(r):
            out.append(r)
        else:
            cp = ord(r)
            if cp < 0x80:
                out.append(f"\\x{cp:02x}")
            elif cp < 0x10000:
                out.append(f"\\u{cp:04x}")
            else:
                out.append(f"\\U{cp:08x}")
    out.append('"')
    return "".join(out)


def fmt_val(v, top: bool) -> str:
    t = type(v)
    if t is int:
        return str(v)
    if t is float:
        return format_g(v)
    if t is bool:
        return "true" if v else "false"
    if t is str:
        return v if top else go_quote(v)
    if t is UnitVal:
        return "()"
    if t is ArrVal:
        return "[" + ", ".join(fmt_val(it, False) for it in v.items) + "]"
    if t is MapVal:
        parts = [fmt_val(k, False) + ": " + fmt_val(v.get(k), False) for k in v.keys_snapshot()]
        return "{" + ", ".join(parts) + "}"
    if t is StructVal:
        parts = [v.def_.fields[i] + ": " + fmt_val(f, False) for i, f in enumerate(v.fields)]
        return v.def_.name + "{" + ", ".join(parts) + "}"
    if t is EnumVal:
        name = v.def_.cases[v.case]
        if not v.payloads:
            return name
        return name + "(" + ", ".join(fmt_val(p, False) for p in v.payloads) + ")"
    if t is RangeVal:
        return str(v.lo) + ".." + str(v.hi)
    if t is Closure:
        return f"<fn {v.name}>" if v.name else "<fn 匿名>"
    if t is Builtin:
        return f"<内建 {v.name}>"
    if t is PtrVal:
        return "&" + fmt_val(v.cell[0], False)
    return "?"


def fmt_value(v) -> str:
    """导出格式化（REPL 与工具用）。"""
    return fmt_val(v, True)


def _map_key_string(k) -> Optional[str]:
    t = type(k)
    if t is int:
        return f"i:{k}"
    if t is str:
        return "s:" + k
    if t is bool:
        return "b:true" if k else "b:false"
    return None


class MapVal:
    """保插入序的映射（VALUE-MAP-006）。键限 int/string/bool（编码隔离）。"""

    __slots__ = ("m",)

    def __init__(self) -> None:
        self.m: dict[str, tuple] = {}  # keyString → (原键, 值)

    def get(self, k):
        ks = _map_key_string(k)
        if ks is None:
            return None
        entry = self.m.get(ks)
        return entry[1] if entry is not None else None

    def get_or_none(self, k):
        return self.get(k)

    def set(self, k, v) -> None:
        ks = _map_key_string(k)
        if ks is None:
            raise LangRuntimeError(f"映射键须为 int/string/bool，得到 {type_name(k)}")
        self.m[ks] = (k, v)

    def has(self, k) -> bool:
        ks = _map_key_string(k)
        return ks is not None and ks in self.m

    def keys_snapshot(self) -> list:
        return [entry[0] for entry in self.m.values()]

    def values_snapshot(self) -> list:
        return [entry[1] for entry in self.m.values()]

    def entries(self):
        return list(self.m.values())

    def __len__(self) -> int:
        return len(self.m)


@dataclass(eq=False)
class Ctor:
    """enum case 构造器表项：所属 enum 定义、case 序号、载荷个数"""
    def_: EnumDef
    idx: int
    arity: int
