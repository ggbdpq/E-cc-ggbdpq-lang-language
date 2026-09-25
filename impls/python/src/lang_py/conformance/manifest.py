"""Conformance manifest 解析（YAML 受限子集，conformance/README.md）。

支持且仅支持：顶层 `key: value`、恰一层 `expect:` 嵌套、块标量 `stdout: |`、
`diagnostics:` 下的 `- code/severity/line` 列表项；# 注释与空行。
任何超集形态（多级嵌套、流式列表、锚点）一律报错——套件拒绝模糊输入。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class DiagExpect:
    code: str
    rule_id: str = ""  # 可选：声明了才比对（向后兼容 v0.8 case）
    severity: str = ""
    line: int = 0


@dataclass
class Expect:
    exit_code: int = 0
    stdout: str = ""
    stderr: str = ""
    diagnostics: list[DiagExpect] = field(default_factory=list)


@dataclass
class Case:
    name: str
    category: str
    mode: str = "run"  # run | check
    note: str = ""
    rule_ids: list[str] = field(default_factory=list)  # case 验证的 Rule ID（v0.9 起）
    expect: Expect = field(default_factory=Expect)


def _cut_key(s: str) -> tuple[Optional[str], str, bool]:
    i = s.find(":")
    if i <= 0:
        return None, "", False
    key = s[:i].strip()
    val = s[i + 1:].strip()
    # 行尾注释（值不在引号内时）
    j = val.find(" #")
    if j >= 0 and not val.startswith('"'):
        val = val[:j].strip()
    if val.startswith('"') and val.endswith('"') and len(val) >= 2:
        val = _unquote(val)
    return key, val, True


def _unquote(s: str) -> str:
    if s == '""':
        return ""
    if not (s.startswith('"') and s.endswith('"') and len(s) >= 2):
        raise ValueError(f"仅支持双引号标量（得到 {s!r}）")
    inner = s[1:-1]
    if "\\" in inner:
        raise ValueError(f"标量不支持转义（得到 {s!r}）")
    return inner


def parse_manifest(src: str) -> Case:
    c = Case(name="", category="")
    ctx = "top"  # top | expect | stdout_block | diag | rules
    diag: Optional[DiagExpect] = None
    block_indent = -1
    lines = src.split("\n")
    for i, raw in enumerate(lines):
        raw = raw.rstrip("\r")
        if not raw.strip() or raw.strip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        line = raw.strip()

        # 块标量收集：缩进不小于块首行的行都归 stdout
        if ctx == "stdout_block":
            if indent >= block_indent:
                c.expect.stdout += raw[block_indent:] + "\n"
                continue
            ctx = "expect"

        if ctx == "rules":
            # 列表项是无值裸标量（"- RULE-ID"），不走 key: value 切分
            if line.startswith("- "):
                c.rule_ids.append(line[2:])
                continue
            ctx = "top"

        key, val, has_val = _cut_key(line)
        if not has_val:
            raise ValueError(f"manifest 第{i + 1}行: 形态不支持 {line!r}")
        if ctx == "top":
            if key == "name":
                c.name = val
            elif key == "category":
                c.category = val
            elif key == "mode":
                c.mode = val
            elif key == "note":
                c.note = val
            elif key == "ruleIds":
                if val:
                    raise ValueError(f"manifest 第{i + 1}行: ruleIds 不接受内联值")
                ctx = "rules"
            elif key == "expect":
                if val:
                    raise ValueError(f"manifest 第{i + 1}行: expect 不接受内联值")
                ctx = "expect"
            else:
                raise ValueError(f"manifest 第{i + 1}行: 未知键 {key!r}")
        elif ctx == "expect":
            if key == "exitCode":
                c.expect.exit_code = _to_int(val, i + 1)
            elif key == "stdout":
                if val != "|":
                    c.expect.stdout = val  # 单行标量（含 "" 空串）
                    continue
                ctx = "stdout_block"
                block_indent = -1
                for j in range(i + 1, len(lines)):
                    r2 = lines[j].rstrip("\r")
                    if not r2.strip():
                        continue
                    block_indent = len(r2) - len(r2.lstrip(" "))
                    break
                if block_indent < 0:
                    raise ValueError(f"manifest 第{i + 1}行: stdout 块标量为空")
            elif key == "stderr":
                c.expect.stderr = val
            elif key == "diagnostics":
                if val:
                    raise ValueError(f"manifest 第{i + 1}行: diagnostics 不接受内联值")
                ctx = "diag"
            else:
                raise ValueError(f"manifest 第{i + 1}行: expect 下未知键 {key!r}")
        elif ctx == "diag":
            if key is not None and not key.startswith("- "):
                if diag is not None:
                    _diag_field(diag, key, val, i + 1)  # 同一 "- " 项的延续字段
                    continue
                raise ValueError(f'manifest 第{i + 1}行: diagnostics 项须以 "- " 开头')
            if diag is not None:
                c.expect.diagnostics.append(diag)
                diag = None
            diag = DiagExpect(code="")
            _diag_field(diag, (key or "")[2:], val, i + 1)
        else:
            raise ValueError(f"manifest 第{i + 1}行: 形态不支持")
    if ctx == "diag" and diag is not None:
        c.expect.diagnostics.append(diag)

    # 收尾校验
    if not c.name or not c.category:
        raise ValueError("manifest 缺 name/category")
    if c.mode == "run":
        if c.expect.exit_code != 0:
            raise ValueError("run 模式 exitCode 必须为 0（v0.8 不比对运行时错误文本）")
        if c.expect.stderr != "":
            raise ValueError("run 模式 stderr 必须为空串")
        if not c.expect.stdout.endswith("\n") or "\n\n" in c.expect.stdout:
            raise ValueError("run 模式 stdout 须为块标量且无空行")
    elif c.mode == "check":
        if not c.expect.diagnostics:
            raise ValueError("check 模式至少声明一条诊断")
    else:
        raise ValueError(f'mode 须为 run|check（得到 {c.mode!r}）')
    return c


def _to_int(val: str, line_no: int) -> int:
    try:
        return int(val)
    except ValueError:
        raise ValueError(f"manifest 第{line_no}行: 须为整数") from None


def _diag_field(d: DiagExpect, key: str, val: str, line_no: int) -> None:
    if key == "code":
        d.code = val
    elif key == "ruleId":
        d.rule_id = val
    elif key == "severity":
        d.severity = val
    elif key == "line":
        d.line = _to_int(val, line_no)
    else:
        raise ValueError(f"manifest 第{line_no}行: 诊断项未知键 {key!r}")
