"""注解解析（anno.go 语义等价）：源码注解 → 语义类型。前置条件：checker 已通过。

E3001/E3009 的注解期诊断（Option/Result 参数个数、Result 第二参数须 error）
在 checker.resolve_type_ex 内产出（含定位）；本模块是无诊断的 HIR 构建入口，
未知名/不完整注解一律保守落 UNKNOWN。
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Optional

from ..semantic.symbols import KENUM, KSTRUCT
from ..syntax import ast_nodes as A
from .model import (ANY, BOOL, ERROR_T, FLOAT, INT, STRING, UNKNOWN, Type,
                    chan_of, enum_of, fn_of, map_of, option_of, ref_of,
                    result_of, slice_of, struct_of, type_arg_of)

if TYPE_CHECKING:
    from ..semantic.symbols import ResolveResult


def resolve_anno(res: "ResolveResult", t: Optional[A.TypeEx], tparams: list[str]) -> Type:
    if t is None:
        return UNKNOWN
    return _resolve(res, t, set(tparams))


def _lookup_type_sym(res: "ResolveResult", name: str) -> Optional["object"]:
    s = res.builtin.get(name)
    if s is not None and s.kind == KSTRUCT:
        return s
    for s in res.symbols:
        if s.kind in (KSTRUCT, KENUM) and s.name == name:
            return s
    return None


def _resolve(res: "ResolveResult", t: A.TypeEx, tps: set[str]) -> Type:
    if isinstance(t, A.NamedType):
        name = t.name
        if name == "int":
            return INT
        if name == "float":
            return FLOAT
        if name == "string":
            return STRING
        if name == "bool":
            return BOOL
        if name == "error":
            return ERROR_T
        if name == "any":
            return ANY
        if name == "chan":
            elem = UNKNOWN
            if len(t.args) == 1:
                elem = _resolve(res, t.args[0], tps)
            return chan_of(elem)
        if name == "Option":
            if len(t.args) == 1:
                return option_of(_resolve(res, t.args[0], tps))
            return option_of(UNKNOWN)
        if name == "Result":
            ok = UNKNOWN
            if len(t.args) >= 1:
                ok = _resolve(res, t.args[0], tps)
            return result_of(ok, ERROR_T)
        if name in tps:
            return type_arg_of(name)
        sym = _lookup_type_sym(res, name)
        if sym is not None:
            return enum_of(sym) if sym.kind == KENUM else struct_of(sym)
        return UNKNOWN
    if isinstance(t, A.ArrType):
        return slice_of(_resolve(res, t.elem, tps))
    if isinstance(t, A.MapType):
        return map_of(_resolve(res, t.key, tps), _resolve(res, t.val, tps))
    if isinstance(t, A.PtrType):
        return ref_of(_resolve(res, t.elem, tps))
    if isinstance(t, A.FnType):
        ps = [_resolve(res, p, tps) for p in t.params]
        ret = UNKNOWN
        if t.ret is not None:
            ret = _resolve(res, t.ret, tps)
        return fn_of(ps, ret)
    return UNKNOWN
