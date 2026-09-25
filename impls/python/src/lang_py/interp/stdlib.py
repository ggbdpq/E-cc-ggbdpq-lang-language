"""标准库委托最小面（stdlib.go 语义等价）：json/chan/isNone + HttpResp 预载。

JSON 两端逐字节确定（RUNTIME-JSON-001/002）：
- 解析：数字按整/浮分型（int64 装得下为 int），null → None，对象键按字母序载入；
- 序列化：映射键按字母序、None → null、紧凑分隔、HTML 转义 <>&（对齐 Go json.Marshal）。
"""

from __future__ import annotations

import json as _pyjson
import math

from ..errors import LangRuntimeError
from .values import (ArrVal, Builtin, ChanVal, EnumVal, MapVal, StructDef,
                     StructVal, UNIT, UnitVal, format_g, go_quote, i64, type_name)


def err_result(it, msg: str):
    """Err(error{msg})（recv 空 / http 失败的统一构造）。"""
    err_val = StructVal(it.structs["error"], [msg])
    return EnumVal(it.enums["Result"], 1, [err_val])


def register_stdlib(reg, structs: dict, enums: dict) -> None:
    """注册 stdlib 内建与预载 HttpResp/http。reg/structs/enums 由 Interp 提供。"""
    structs["HttpResp"] = StructDef("HttpResp", ["status", "body"])
    http_def = StructDef("http", ["get"])
    structs["http"] = http_def

    def http_get(it, args):
        if len(args) != 2:
            raise LangRuntimeError(f"http.get 需要 2 个参数（url, headers），得到 {len(args)} 个")
        # 网络面：宿主 subprocess/IO 层不进 Semantic Core；解释器内联实现
        import urllib.request
        import urllib.error
        if not isinstance(args[0], str):
            raise LangRuntimeError(f"http.get 的 url 须为 string，得到 {type_name(args[0])}")
        url = args[0]
        headers: dict[str, str] = {}
        if not isinstance(args[1], UnitVal):
            if not isinstance(args[1], MapVal):
                raise LangRuntimeError(
                    f"http.get 的 headers 须为 map[string]string，得到 {type_name(args[1])}")
            for k, v in args[1].entries():
                if isinstance(k, str) and isinstance(v, str):
                    headers[k] = v
        req = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                body = resp.read()
                status = resp.status
        except urllib.error.HTTPError as e:  # 任何响应（含 4xx/5xx）= Ok
            body = e.read()
            status = e.code
        except Exception as e:  # 网络错误 = Err
            return err_result_http(enums, structs, str(e))
        resp_val = StructVal(structs["HttpResp"], [status, body.decode("utf-8", "replace")])
        return EnumVal(enums["Result"], 0, [resp_val])

    # http 服务值：字段 0 是内建 http.get
    def _http_get_stub(it, args):
        return http_get(it, args)

    reg("jsonParse", json_parse_builtin)
    reg("jsonStringify", json_stringify_builtin)

    def make_chan(it, args):
        if len(args) != 0:
            raise LangRuntimeError("makeChan 不需要参数")
        return ChanVal()

    def send(it, args):
        if len(args) != 2:
            raise LangRuntimeError(f"send 需要 2 个参数（ch, v），得到 {len(args)} 个")
        if not isinstance(args[0], ChanVal):
            raise LangRuntimeError(f"send 需要通道，得到 {type_name(args[0])}")
        if len(args[0].items) >= 1024:
            raise LangRuntimeError("channel 缓冲已满（1024）")
        args[0].items.append(args[1])
        return UNIT

    def recv(it, args):
        if len(args) != 1:
            raise LangRuntimeError(f"recv 需要 1 个参数（ch），得到 {len(args)} 个")
        if not isinstance(args[0], ChanVal):
            raise LangRuntimeError(f"recv 需要通道，得到 {type_name(args[0])}")
        if not args[0].items:
            return err_result(it, "channel 空")
        v = args[0].items.pop(0)
        return EnumVal(it.enums["Result"], 0, [v])

    def is_none(it, args):
        if len(args) != 1:
            raise LangRuntimeError(f"isNone 需要 1 个参数，得到 {len(args)} 个")
        if isinstance(args[0], EnumVal):
            return args[0].def_.name == "Option" and args[0].case == 1
        return False

    reg("makeChan", make_chan)
    reg("send", send)
    reg("recv", recv)
    reg("isNone", is_none)

    # http 值本身：StructVal，get 字段持内建（在注册后构造需要闭包引用）
    def build_http_value():
        return StructVal(http_def, [Builtin("http.get", http_get)])
    return build_http_value


def err_result_http(enums, structs, msg: str):
    err_val = StructVal(structs["error"], [msg])
    return EnumVal(enums["Result"], 1, [err_val])


# ———— JSON ————

def _json_to_value(raw, it):
    """Go JSON 表示 → lang-go 值；null → None（RESULT-NULL-002）。"""
    if raw is None:
        return EnumVal(it.enums["Option"], 1)
    if isinstance(raw, bool):
        return raw
    if isinstance(raw, str):
        return raw
    if isinstance(raw, _Num):
        return raw.v
    if isinstance(raw, list):
        return ArrVal([_json_to_value(el, it) for el in raw])
    if isinstance(raw, dict):
        m = MapVal()
        for k in sorted(raw):
            m.set(k, _json_to_value(raw[k], it))
        return m
    return UNIT


class _Num:
    """保持"int 优先"的已解析数字。"""
    __slots__ = ("v",)

    def __init__(self, v) -> None:
        self.v = v


def _parse_int(s: str):
    try:
        n = int(s, 10)
    except ValueError:
        return None
    if -(2 ** 63) <= n < 2 ** 63:
        return n
    return None


def _parse_num(s: str):
    n = _parse_int(s)
    if n is not None:
        return _Num(n)
    return _Num(float(s))


def json_parse_builtin(it, args):
    if len(args) != 1:
        raise LangRuntimeError(f"jsonParse 需要 1 个参数，得到 {len(args)} 个")
    if not isinstance(args[0], str):
        raise LangRuntimeError(f"jsonParse 需要字符串，得到 {type_name(args[0])}")
    try:
        dec = _pyjson.JSONDecoder(parse_int=_parse_num, parse_float=_parse_num,
                                  parse_constant=_reject_constant)
        raw, _ = dec.raw_decode(args[0])
    except ValueError as e:
        raise LangRuntimeError(f"JSON 解析失败: {_clean_json_err(str(e))}") from None
    return _json_to_value(raw, it)


def _reject_constant(name):
    raise ValueError(f"json: invalid literal {name}")


def _clean_json_err(msg: str) -> str:
    # Go 解析错误的措辞不同；此处只保留 Python 的定位信息（错误文本属 message 漂移面）
    return msg


def value_to_json(v):
    """lang-go 值 → 可 JSON 化的宿主对象；None → null。"""
    t = type(v)
    if t is UnitVal:
        return None
    if t is bool:
        return v
    if t is int:
        return v
    if t is float:
        return v
    if t is str:
        return v
    if t is EnumVal:
        if v.def_.name == "Option" and v.case == 1:
            return None
        raise LangRuntimeError(f"{v.def_.cases[v.case]} 不可 JSON 序列化")
    if t is ArrVal:
        return [value_to_json(el) for el in v.items]
    if t is MapVal:
        out = {}
        for k, val in v.entries():
            if not isinstance(k, str):
                raise LangRuntimeError(f"JSON 对象键须为 string，得到 {type_name(k)}")
            out[k] = value_to_json(val)
        return out
    raise LangRuntimeError(f"{type_name(v)} 不可 JSON 序列化")


def _json_float(f: float) -> str:
    """Go json.Marshal 的浮点形态：'f' 为主，abs<1e-6 或 >=1e21 走 'e'（指数 e-0X → e-X）。"""
    if math.isnan(f) or math.isinf(f):
        raise LangRuntimeError("JSON 序列化失败: json: unsupported value")
    g = format_g(f)
    # format_g 是 'g' 形态（阈值 21 与 json 相同）；差异仅在 e 指数的零清洁
    if "e" in g:
        mant, _, exp = g.partition("e")
        if exp.startswith("e-0"):
            exp = "e-" + exp[3:]
        g = mant + exp
    return g


def _json_string(s: str) -> str:
    out = ['"']
    for ch in s:
        o = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        elif o < 0x20:
            out.append(f"\\u{o:04x}")
        elif ch in "<>&":
            out.append({"<": "\\u003c", ">": "\\u003e", "&": "\\u0026"}[ch])
        elif o in (0x2028, 0x2029):
            out.append(f"\\u{o:04x}")
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def _json_marshal(v) -> str:
    t = type(v)
    if v is None:
        return "null"
    if t is bool:
        return "true" if v else "false"
    if t is int:
        return str(v)
    if t is float:
        return _json_float(v)
    if t is str:
        return _json_string(v)
    if t is list:
        return "[" + ",".join(_json_marshal(el) for el in v) + "]"
    if t is dict:
        keys = sorted(v)
        return "{" + ",".join(_json_string(k) + ":" + _json_marshal(v[k]) for k in keys) + "}"
    raise LangRuntimeError("JSON 序列化失败: json: unsupported value")


def json_stringify_builtin(it, args):
    if len(args) != 1:
        raise LangRuntimeError(f"jsonStringify 需要 1 个参数，得到 {len(args)} 个")
    j = value_to_json(args[0])
    return _json_marshal(j)
