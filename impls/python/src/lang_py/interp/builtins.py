"""内建函数（builtin.go 语义等价）。预载 Option/Result/error 由 Interp 负责。

约定：实现失败 raise LangRuntimeError（文本与 Go 参考一致）；
json/chan/isNone 在 stdlib.py（与 Go 的 stdlib.go 同分界）。
标量值即宿主 int/float/bool/str（动态边界，bool 先于 int 判别）。
"""

from __future__ import annotations

from ..errors import LangRuntimeError
from .values import ArrVal, MapVal, RangeVal, StructVal, UNIT
from .values import copy_value, fmt_val, i64, i64_div, i64_mod, truthy, type_name


def _arity(name: str, args: list, n: int) -> None:
    if len(args) != n:
        raise LangRuntimeError(f"{name} 需要 {n} 个参数，得到 {len(args)} 个")


def register_builtins(reg) -> None:
    """reg(name, fn) 逐个注册内建；fn(it, args) -> value。"""

    def println(it, args):
        it.write(fmt_args(args) + "\n")
        return UNIT

    def print_(it, args):
        it.write(fmt_args(args))
        return UNIT

    def len_(it, args):
        _arity("len", args, 1)
        v = args[0]
        if isinstance(v, str):
            return len(v)
        if isinstance(v, ArrVal):
            return len(v.items)
        if isinstance(v, MapVal):
            return len(v)
        if isinstance(v, RangeVal):
            return v.hi - v.lo if v.hi > v.lo else 0
        raise LangRuntimeError(f"len 不支持 {type_name(v)}")

    def str_(it, args):
        _arity("str", args, 1)
        return fmt_val(args[0], True)

    def int_(it, args):
        _arity("int", args, 1)
        v = args[0]
        if type(v) is int:
            return v
        if type(v) is float:
            # int(float) 边界（TYPE-CONV-005 v1.0.1，ADR-0006/RS-006 跨实现硬契约）：
            # NaN → 0；正/负溢出饱和到 int64 边界；其余向零截断。
            # 不依赖宿主 int(v)：Python 对 NaN 抛 ValueError、越界抛 OverflowError
            if v != v:  # NaN（IEEE 唯一自不等值）
                return 0
            if v >= 2.0 ** 63:
                return i64(2 ** 63 - 1)
            if v <= -(2.0 ** 63):
                return i64(-(2 ** 63))
            return i64(int(v))
        if isinstance(v, str):
            try:
                return i64(int(v.strip(), 10))
            except ValueError:
                raise LangRuntimeError(f"无法把 {_q(v)} 转为 int") from None
        raise LangRuntimeError(f"int() 不支持 {type_name(v)}")

    def float_(it, args):
        _arity("float", args, 1)
        v = args[0]
        if type(v) is int:
            return float(v)
        if type(v) is float:
            return v
        if isinstance(v, str):
            try:
                return _parse_float(v.strip())
            except ValueError:
                raise LangRuntimeError(f"无法把 {_q(v)} 转为 float") from None
        raise LangRuntimeError(f"float() 不支持 {type_name(v)}")

    def keys(it, args):
        _arity("keys", args, 1)
        if not isinstance(args[0], MapVal):
            raise LangRuntimeError(f"keys 需要映射，得到 {type_name(args[0])}")
        return ArrVal(args[0].keys_snapshot())

    def values(it, args):
        _arity("values", args, 1)
        if not isinstance(args[0], MapVal):
            raise LangRuntimeError(f"values 需要映射，得到 {type_name(args[0])}")
        return ArrVal(args[0].values_snapshot())

    def has(it, args):
        _arity("has", args, 2)
        if not isinstance(args[0], MapVal):
            raise LangRuntimeError(f"has 需要映射，得到 {type_name(args[0])}")
        return args[0].has(args[1])

    def push(it, args):
        if len(args) < 2:
            raise LangRuntimeError("push 需要至少 2 个参数")
        if not isinstance(args[0], ArrVal):
            raise LangRuntimeError(f"push 需要数组，得到 {type_name(args[0])}")
        args[0].items.extend(args[1:])
        return args[0]

    def assert_(it, args):
        if len(args) < 1 or len(args) > 2:
            raise LangRuntimeError("assert 需要 1~2 个参数")
        t = truthy(args[0])
        if not t:
            if len(args) == 2:
                raise LangRuntimeError(f"assert 失败: {fmt_val(args[1], True)}")
            raise LangRuntimeError("assert 失败")
        return UNIT

    def err(it, args):
        _arity("err", args, 1)
        if not isinstance(args[0], str):
            raise LangRuntimeError(f"err 需要字符串，得到 {type_name(args[0])}")
        return StructVal(it.structs["error"], [args[0]])

    def bytes_(it, args):
        _arity("bytes", args, 1)
        if not isinstance(args[0], str):
            raise LangRuntimeError(f"bytes 需要字符串，得到 {type_name(args[0])}")
        return ArrVal(list(args[0].encode("utf-8")))

    def char(it, args):
        _arity("char", args, 1)
        if type(args[0]) is not int:
            raise LangRuntimeError(f"char 需要 int，得到 {type_name(args[0])}")
        n = args[0]
        if not 0 <= n <= 0x10FFFF or 0xD800 <= n <= 0xDFFF:
            return "�"  # Go string(rune) 对无效码点的替换
        return chr(n)

    reg("println", println)
    reg("print", print_)
    reg("len", len_)
    reg("str", str_)
    reg("int", int_)
    reg("float", float_)
    reg("keys", keys)
    reg("values", values)
    reg("has", has)
    reg("push", push)
    reg("assert", assert_)
    reg("err", err)
    reg("bytes", bytes_)
    reg("char", char)


def fmt_args(args: list) -> str:
    return " ".join(fmt_val(a, True) for a in args)


def _q(s: str) -> str:
    """Go %q（strconv.Quote）。"""
    from .values import go_quote
    return go_quote(s)


def _parse_float(s: str) -> float:
    # Go strconv.ParseFloat 拒绝 "inf"/"nan" 等词法（解释器输入均来自字面量/JSON，不含）
    return float(s)
