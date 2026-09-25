"""extern "go" 声明草案生成（bind.go 语义等价的 Python 实现）。

扫描 bind.d/ 下 .go 文件的导出函数，按 langGo 类型映射生成 extern 声明草案。
草案需人工审核（FFI-001：bind 面不进 Conformance）。
类型映射：整数族→int、浮点族→float、string/bool/error 直映、byte/rune→int、
any→any、[]T→[T]；(T, error) 双返回→Result[T, error]；其余跳过并注明原因。
"""

from __future__ import annotations

import os
import re

from ..errors import LangError

_GO_TYPE_MAP = {
    "int": "int", "int8": "int", "int16": "int", "int32": "int", "int64": "int",
    "uint": "int", "uint8": "int", "uint16": "int", "uint32": "int", "uint64": "int",
    "byte": "int", "rune": "int",
    "float32": "float", "float64": "float",
    "string": "string", "bool": "bool", "error": "error",
    "any": "any",
}

# 顶层导出函数签名（单行匹配足够覆盖草案场景；复杂跨行签名按不可绑定跳过）
_FUNC_RE = re.compile(
    r"^func\s+([A-Z]\w*)\s*\(([^)]*)\)\s*(?:\(([^)]*)\)|([^\n]*?))?\s*\{"
)


def _parse_type(text: str) -> tuple[str, bool, str]:
    """Go 类型文本 → (langGo 类型, 是否支持, 原文)。"""
    t = text.strip()
    if not t:
        return "", False, t
    if t.startswith("[]"):
        elem, ok, _ = _parse_type(t[2:])
        if not ok:
            return "", False, t
        return f"[{elem}]", True, t
    if t.startswith("*") or t.startswith("map[") or t.startswith("chan ") \
            or t.startswith("func(") or t.startswith("struct{") or "interface{" in t:
        return "", False, t
    if "." in t:  # 命名类型（如 time.Duration）
        return "", False, t
    mapped = _GO_TYPE_MAP.get(t)
    if mapped is None:
        return "", False, t
    return mapped, True, t


def _split_params(text: str) -> list[tuple[str, str]]:
    """参数表切分为 (名字或空, 类型)。仅处理顶层逗号。"""
    parts: list[str] = []
    depth = 0
    cur: list[str] = []
    for ch in text:
        if ch in "([{" :
            depth += 1
        elif ch in ")]}":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    if "".join(cur).strip():
        parts.append("".join(cur))
    out: list[tuple[str, str]] = []
    for p in parts:
        p = p.strip()
        if not p:
            continue
        toks = p.rsplit(" ", 1)
        if len(toks) == 2 and all(x and (x[0].isalpha() or x[0] == "_") for x in [toks[0]]) \
                and toks[0] not in ("map", "chan", "func", "interface", "struct"):
            out.append((toks[0], toks[1]))
        else:
            out.append(("", p))
    return out


def _go_func_to_sig(name: str, params_text: str, results_text: str) -> tuple[str, str]:
    """导出函数 → langGo 声明；返回 (sig, skip)。skip 非空表示不可绑定及原因。"""
    params: list[str] = []
    for pname, ptype in _split_params(params_text):
        mapped, ok, orig = _parse_type(ptype)
        if not ok:
            return "", f"{name}({params_text}): 参数类型不支持: {orig}"
        params.append(f"{pname or 'a' + str(len(params) + 1)}: {mapped}")

    results_text = results_text.strip()
    if not results_text:
        return _fn_sig(name, params, ""), ""
    # 单返回或 (T, error)
    pairs = _split_params(results_text)
    if len(pairs) == 1:
        _, rtype = pairs[0]
        mapped, ok, orig = _parse_type(rtype)
        if not ok:
            return "", f"{name}({params_text}): 返回类型不支持: {orig}"
        return _fn_sig(name, params, mapped), ""
    if len(pairs) == 2:
        if pairs[1][1].strip() == "error":
            _, rtype = pairs[0]
            mapped, ok, orig = _parse_type(rtype)
            if not ok:
                return "", f"{name}({params_text}): 返回类型不支持: {orig}"
            return _fn_sig(name, params, f"Result[{mapped}, error]"), ""
        return "", f"{name}({params_text}): 双返回值仅支持 (T, error)"
    return "", f"{name}({params_text}): 多返回值（>2）"


def _fn_sig(name: str, params: list[str], ret: str) -> str:
    sig = f"fn {name}({', '.join(params)})"
    if ret:
        sig += f": {ret}"
    return sig


def bind_dir(dir_path: str) -> str:
    """生成 extern "go" 声明草案文本。"""
    entries = sorted(os.listdir(dir_path)) if os.path.isdir(dir_path) else []
    files = []
    for name in entries:
        full = os.path.join(dir_path, name)
        if os.path.isfile(full) and name.endswith(".go") and not name.endswith("_test.go"):
            files.append(full)
    if not files:
        raise LangError(f"{dir_path}: 无 .go 文件")

    fns: list[str] = []
    skipped: list[str] = []
    for f in files:
        with open(f, "r", encoding="utf-8") as fh:
            src = fh.read()
        for line in src.split("\n"):
            m = _FUNC_RE.match(line.strip())
            if m is None:
                continue
            name = m.group(1)
            params = m.group(2) or ""
            results = (m.group(3) or m.group(4) or "").strip()
            sig, skip = _go_func_to_sig(name, params, results)
            if skip:
                skipped.append(skip)
                continue
            fns.append(sig)
    if not fns:
        raise LangError(f"{dir_path}: 无可绑定的导出函数")

    b = ['// extern "go" 声明草案 —— 由 langGo bind 生成，签名需人工审核后使用',
         '// 绑定面仅是声明；实现需在 prelude/宿主侧另行提供']
    if skipped:
        b.append("// 跳过:")
        for sk in skipped:
            b.append(f"//   {sk}")
    b.append('extern "go" {')
    for sig in fns:
        b.append(f"    {sig}")
    b.append("}")
    return "\n".join(b) + "\n"
