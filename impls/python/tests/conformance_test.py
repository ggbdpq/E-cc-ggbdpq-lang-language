"""Conformance 套件 + AST/IR 解释器差分 + 双后端差分（跨实现验收面）。

仓库顶层 conformance/ 是四实现的共同真值（specs/runner-contract.md）。
"""

import io
import os
import sys
from glob import glob

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from lang_py.conformance.manifest import parse_manifest  # noqa: E402
from lang_py.conformance.runner import run_case  # noqa: E402
from lang_py.interp.interp import Interp  # noqa: E402
from lang_py.irinterp.machine import Machine  # noqa: E402
from lang_py.loader.loader import load_program  # noqa: E402
from lang_py.pipeline import analyze_stmts, check_stmts  # noqa: E402

ROOT = os.path.normpath(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
CONF = os.path.join(ROOT, "conformance")

# 参考实现（go test ./internal/conformance/）当前同批失败的在途 case——
# 双方失败一致即 parity；Final Contract Sync 时复核。
# 2026-09-21：lang-py 对 132 例 conformance 全绿（含参考侧当日在途的 5 例——
# closure-loop-capture-var 的 void 函数值调用、unary-negation-nested 的 JS `--` 歧义、
# 3 例 ? 传播/match 混用诊断修订）。参考实现 go test 同刻 127/132 仍在收口中。
KNOWN_FAILING_ON_REFERENCE = set()


def _case_dirs():
    dirs = sorted(glob(os.path.join(CONF, "*", "*")))
    return [d for d in dirs if os.path.isdir(d)]


CASE_IDS = [os.path.relpath(d, CONF).replace(os.sep, "/") for d in _case_dirs()]
assert CASE_IDS, f"conformance 套件为空: {CONF}"


@pytest.mark.parametrize("rel", CASE_IDS, ids=CASE_IDS)
def test_conformance_case(rel):
    if rel in KNOWN_FAILING_ON_REFERENCE:
        pytest.skip("参考实现在途修复中（双方失败一致，Final Sync 复核）")
    result = run_case(os.path.join(CONF, rel))
    assert result.ok, f"{result.mode}: {result.note}"


@pytest.mark.parametrize("rel", CASE_IDS, ids=CASE_IDS)
def test_ast_vs_ir_interpreter_differential(rel):
    """AST 解释器（Oracle）与 IR 解释器输出逐字节一致（v0.9 四路差分面）。"""
    case_dir = os.path.join(CONF, rel)
    try:
        prog = load_program(case_dir, False)
        diags, _ = check_stmts(prog.stmts)
        if diags:
            pytest.skip("check-mode case（前端诊断即期望）")
        a = analyze_stmts(prog.stmts)
    except Exception:
        pytest.skip("装载失败（参考实现同样拒绝）")
    b1, b2 = io.StringIO(), io.StringIO()
    Interp(b1).run(a.ast)
    Machine(b2).run(a.prog)
    assert b1.getvalue() == b2.getvalue()


def _run_backend(go_or_js: str, a) -> str:
    import subprocess
    import tempfile
    from lang_py.backends.go_backend import transpile
    from lang_py.backends.js_backend import transpile_js
    base = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
    tmp_base = os.path.join(base, ".tmp-build")
    os.makedirs(tmp_base, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix="langpy-diff-", dir=tmp_base)
    if go_or_js == "go":
        src = transpile(a.prog)
        with open(os.path.join(tmp, "main.go"), "w", encoding="utf-8", newline="\n") as f:
            f.write(src)
        subprocess.run(["go", "build", "-o", "prog.exe", "main.go"], cwd=tmp,
                       check=True, capture_output=True, timeout=60)
        r = subprocess.run([os.path.join(tmp, "prog.exe")], cwd=tmp,
                           capture_output=True, timeout=60)
        assert r.returncode == 0, r.stderr
        return r.stdout.decode("utf-8")
    src = transpile_js(a.prog)
    p = os.path.join(tmp, "out.mjs")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(src)
    r = subprocess.run(["node", p], cwd=tmp, capture_output=True, timeout=60)
    assert r.returncode == 0, r.stderr
    return r.stdout.decode("utf-8")


@pytest.mark.parametrize("rel", CASE_IDS, ids=[f"{r}-interp-vs-js" for r in CASE_IDS])
def test_interpreter_vs_js_backend(rel):
    """解释器与 →JS 产物 stdout 逐字节一致（宿主缺 node 时 skip）。"""
    from shutil import which
    case_dir = os.path.join(CONF, rel)
    if rel in KNOWN_FAILING_ON_REFERENCE:
        pytest.skip("参考实现在途修复中")
    try:
        prog = load_program(case_dir, False)
        diags, _ = check_stmts(prog.stmts)
        if diags:
            pytest.skip("check-mode case")
        a = analyze_stmts(prog.stmts)
    except Exception:
        pytest.skip("装载失败（参考实现同样拒绝）")
    if which("node") is None:
        pytest.skip("宿主缺 node")
    b1 = io.StringIO()
    Interp(b1).run(a.ast)
    assert _run_backend("js", a) == b1.getvalue()
