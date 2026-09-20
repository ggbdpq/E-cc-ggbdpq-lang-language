"""Conformance case 执行器。

run 模式 = 三路差分（解释器 / →Go 产物 / →JS 产物）stdout 逐字节一致
+ exitCode 0 + stderr 空；check 模式 = 诊断断言（code+ruleId+severity+line 多重集）。
后端执行依赖宿主工具链（go build / node）：缺失时结果为 skipped——不计入通过。
"""

from __future__ import annotations

import os
import subprocess
import tempfile
from dataclasses import dataclass, field

from ..backends.go_backend import transpile
from ..backends.js_backend import transpile_js
from ..contract.rules import rule_of
from ..errors import Diagnostic, LangError, LangRuntimeError
from ..interp.interp import Interp
from ..loader.loader import load_program
from ..pipeline import analyze_stmts, check_stmts
from .manifest import Case, parse_manifest

_TIMEOUT = 30


@dataclass(eq=False)
class CaseResult:
    name: str = ""
    category: str = ""
    mode: str = ""
    ok: bool = False
    skipped: bool = False  # 工具链缺失等环境性跳过（不计入通过）
    note: str = ""


def run_case(case_dir: str) -> CaseResult:
    try:
        with open(os.path.join(case_dir, "case.yaml"), "r", encoding="utf-8") as f:
            c: Case = parse_manifest(f.read())
    except (OSError, ValueError) as e:
        return CaseResult(name=os.path.basename(case_dir), note=str(e))
    res = CaseResult(name=c.name, category=c.category, mode=c.mode)

    try:
        prog = load_program(case_dir, False)
    except (LangError, Exception) as e:
        res.note = f"装载失败: {e}"
        return res

    if c.mode == "run":
        return run_differential(c, prog.stmts, res)
    if c.mode == "check":
        return run_diagnostic(c, prog.stmts, res)
    res.note = f"未知 mode {c.mode}"
    return res


def _check_diags(stmts) -> list[Diagnostic]:
    diags, _ = check_stmts(stmts)
    return diags


def run_differential(c: Case, stmts, res: CaseResult) -> CaseResult:
    # 正确程序前提：前端必须零诊断
    diags = _check_diags(stmts)
    if diags:
        res.note = "run 模式 case 前端诊断: " + "; ".join(d.render() for d in diags)
        return res
    try:
        a = analyze_stmts(stmts)
    except Exception as e:  # noqa: BLE001
        res.note = f"分析失败: {e}"
        return res

    # 1) 解释器（进程内）
    import io
    buf = io.StringIO()
    try:
        Interp(buf).run(a.ast)
    except (LangRuntimeError, LangError) as e:
        res.note = f"解释器执行失败: {e}"
        return res
    out_interp = buf.getvalue()

    # 2) →Go 产物（go build + 执行）
    def go_backend(d: str) -> str:
        src = transpile(a.prog)
        with open(os.path.join(d, "main.go"), "w", encoding="utf-8", newline="\n") as f:
            f.write(src)
        r = subprocess.run(["go", "build", "-o", "prog.exe", "main.go"], cwd=d,
                           capture_output=True, timeout=_TIMEOUT)
        if r.returncode != 0:
            raise RuntimeError("go build: " + r.stderr.decode("utf-8", "replace")[-1500:])
        r2 = subprocess.run([os.path.join(d, "prog.exe")], cwd=d,
                            capture_output=True, timeout=_TIMEOUT)
        if r2.returncode != 0:
            raise RuntimeError("prog: " + r2.stderr.decode("utf-8", "replace")[-500:])
        return r2.stdout.decode("utf-8")

    def js_backend(d: str) -> str:
        src = transpile_js(a.prog)
        p = os.path.join(d, "out.mjs")
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(src)
        r = subprocess.run(["node", p], cwd=d, capture_output=True, timeout=_TIMEOUT)
        if r.returncode != 0:
            raise RuntimeError("node: " + r.stderr.decode("utf-8", "replace")[-1500:])
        return r.stdout.decode("utf-8")

    for tool_name, backend in (("go", go_backend), ("node", js_backend)):
        if _which(tool_name) is None:
            res.note = f"宿主缺 {tool_name}，差分跳过（不计入通过）"
            res.skipped = True
            return res
    for tool_name, backend in (("go", go_backend), ("node", js_backend)):
        try:
            d = tempfile.mkdtemp(prefix=f"conformance-{c.name}-", dir=_work_tmp())
            got = backend(d)
        except Exception as e:  # noqa: BLE001
            res.note = f"{tool_name}: {e}"
            return res
        if got != c.expect.stdout:
            res.note = (f"{tool_name} stdout 不一致\n期望: {c.expect.stdout!r}\n实际: {got!r}")
            return res

    if out_interp != c.expect.stdout:
        res.note = f"解释器 stdout 不一致\n期望: {c.expect.stdout!r}\n实际: {out_interp!r}"
        return res
    res.ok = True
    return res


def run_diagnostic(c: Case, stmts, res: CaseResult) -> CaseResult:
    diags = _check_diags(stmts)
    got: dict[str, int] = {}
    for d in diags:
        key = f"{d.code}|{rule_of(d.code)}|error|{d.line}"
        got[key] = got.get(key, 0) + 1
    want: dict[str, int] = {}
    for d in c.expect.diagnostics:
        rule = d.rule_id or rule_of(d.code)
        key = f"{d.code}|{rule}|{d.severity}|{d.line}"
        want[key] = want.get(key, 0) + 1
    missing: list[str] = []
    extra: list[str] = []
    for k, n in want.items():
        if got.get(k, 0) < n:
            missing.append(f"{k}×{n - got.get(k, 0)}")
    for k, n in got.items():
        if want.get(k, 0) < n:
            extra.append(f"{k}×{n - want.get(k, 0)}")
    if missing or extra:
        missing.sort()
        extra.sort()
        res.note = f"诊断不匹配 缺{','.join(missing)} 多{','.join(extra)}"
        return res
    res.ok = True
    return res


def run_suite(root: str) -> tuple[list[CaseResult], int]:
    """遍历 root 下 <category>/<case>/ 全部 case（无 case.yaml 的目录视为套件错误）。"""
    all_results: list[CaseResult] = []
    for cat in sorted(os.listdir(root)):
        cat_dir = os.path.join(root, cat)
        if not os.path.isdir(cat_dir):
            continue
        for cd in sorted(os.listdir(cat_dir)):
            case_dir = os.path.join(cat_dir, cd)
            if not os.path.isdir(case_dir):
                continue
            if not os.path.exists(os.path.join(case_dir, "case.yaml")):
                raise LangError(f"套件目录 {case_dir} 缺 case.yaml")
            r = run_case(case_dir)
            if not r.name:
                r.name = cd
            all_results.append(r)
    fails = sum(1 for r in all_results if not r.ok)
    return all_results, fails


def _which(name: str) -> str | None:
    from shutil import which
    return which(name)


def _work_tmp() -> str:
    """Windows 的 go 拒绝在系统临时根目录构建；用仓库内 .tmp 目录承载产物。"""
    base = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))), ".tmp-build")
    os.makedirs(base, exist_ok=True)
    return base
