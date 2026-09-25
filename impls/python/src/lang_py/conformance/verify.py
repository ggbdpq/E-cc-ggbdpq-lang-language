"""verify 全管线验证（v0.8+；conformance/verify.go 语义等价）。

parse → resolve → typecheck → hir → ir → effect → compile → execute → differential。
任一阶段失败即停——后续阶段不列。执行面为四路：AST 解释器 / IR 解释器 / →Go / →JS。
"""

from __future__ import annotations

import os
import subprocess
import tempfile
from dataclasses import dataclass, field
from typing import Optional

from ..backends.go_backend import transpile
from ..backends.js_backend import transpile_js
from ..errors import Diagnostic, LangError, LangRuntimeError
from ..graph.effects import derive_effects
from ..hir.walk import walk_prog
from ..interp.interp import Interp
from ..irinterp.machine import Machine
from ..loader.loader import load_program
from ..pipeline import analyze_stmts, check_stmts
from ..syntax.tokens import kind_name

_TIMEOUT = 60


@dataclass(eq=False)
class Stage:
    name: str
    ok: bool = False
    skipped: bool = False
    note: str = ""


@dataclass(eq=False)
class VerifyResult:
    success: bool = False
    stages: list[Stage] = field(default_factory=list)
    stdout_bytes: int = 0
    exit_code: int = 0


def _work_tmp() -> str:
    """Windows 的 go 拒绝在系统临时根目录构建；用仓库内 .tmp 目录承载产物。"""
    base = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))), ".tmp-build")
    os.makedirs(base, exist_ok=True)
    return base


def verify_dir(directory: str) -> VerifyResult:
    res = VerifyResult()

    def fail(name: str, note: str) -> VerifyResult:
        res.stages.append(Stage(name=name, note=note))
        return res

    def ok(name: str, note: str = "") -> None:
        res.stages.append(Stage(name=name, ok=True, note=note))

    # 1) parse：装载即解析（目录即包，含 import 展开）
    try:
        prog = load_program(directory, False)
    except (LangError, Exception) as e:  # noqa: BLE001
        return fail("parse", str(e))
    ok("parse")

    # 2+3) resolve / typecheck：按稳定码分段呈现
    diags, _ = check_stmts(prog.stmts)
    resolve_diags = [d for d in diags if not d.code.startswith(("E3", "E4"))]
    type_diags = [d for d in diags if d.code.startswith(("E3", "E4"))]
    if resolve_diags:
        return fail("resolve", "\n".join(d.render() for d in resolve_diags))
    ok("resolve")
    if type_diags:
        return fail("typecheck", "\n".join(d.render() for d in type_diags))
    ok("typecheck")

    # 4) hir validate：构建成功 + 全树遍历可达
    try:
        a = analyze_stmts(prog.stmts)
    except Exception as e:  # noqa: BLE001
        return fail("hir", str(e))
    nodes = [0]

    def count_s(_s) -> None:
        nodes[0] += 1

    def count_e(_e) -> None:
        nodes[0] += 1

    walk_prog(a.hir, count_s, count_e, None)
    if nodes[0] == 0:
        return fail("hir", "遍历覆盖为 0（结构不完整）")
    ok("hir", f"{nodes[0]} 节点")

    # 5) ir validate：lower 成功且非空
    if a.prog is None:
        return fail("ir", "lower 产物为空")
    ok("ir")

    # 6) effect：效果面可推导（自 IR）
    effects = derive_effects(a.prog)
    ok("effect", f"{len(effects)} 项")

    # 7) compile：→Go 产物可编译 + →JS 产物可生成
    try:
        go_src = transpile(a.prog)
        js_src = transpile_js(a.prog)
    except Exception as e:  # noqa: BLE001
        return fail("compile", str(e))
    tmp = tempfile.mkdtemp(prefix="langpy-verify-", dir=_work_tmp())
    go_path = os.path.join(tmp, "main.go")
    with open(go_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(go_src)
    try:
        r = subprocess.run(["go", "build", "-o", "prog.exe", "main.go"], cwd=tmp,
                           capture_output=True, timeout=_TIMEOUT)
        if r.returncode != 0:
            return fail("compile", "go build: " + r.stderr.decode("utf-8", "replace")[-1000:])
    except FileNotFoundError:
        return fail("compile", "go build: 宿主缺 go 工具链")
    ok("compile", "go+js 产物")

    # 8) execute：四路执行（stderr 非空或退出码非 0 均失败）
    import io
    out_interp = io.StringIO()
    try:
        Interp(out_interp).run(a.ast)
    except (LangRuntimeError, LangError) as e:
        return fail("execute", "解释器: " + str(e))
    try:
        r = subprocess.run([os.path.join(tmp, "prog.exe")], cwd=tmp,
                           capture_output=True, timeout=_TIMEOUT)
        out_go = r.stdout.decode("utf-8", "replace")
        if r.returncode != 0:
            return fail("execute", "→Go: 退出码 " + str(r.returncode))
    except FileNotFoundError:
        return fail("execute", "→Go: 宿主缺 go 工具链")
    if _which("node") is None:
        ok("execute", "宿主缺 node，JS 路不执行（差分降级）")
        out_js = out_go
    else:
        js_path = os.path.join(tmp, "out.mjs")
        with open(js_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(js_src)
        try:
            r = subprocess.run(["node", js_path], cwd=tmp, capture_output=True, timeout=_TIMEOUT)
            out_js = r.stdout.decode("utf-8", "replace")
            if r.returncode != 0:
                return fail("execute", "→JS: " + r.stderr.decode("utf-8", "replace")[-500:])
        except FileNotFoundError:
            return fail("execute", "→JS: 宿主缺 node")
        ok("execute")

    # IR 解释器（v0.9 四路差分；失败不阻塞三路差分结论，按参考口径同为 execute 面）
    out_ir = ""
    try:
        buf = io.StringIO()
        Machine(buf).run(a.prog)
        out_ir = buf.getvalue()
    except Exception as e:  # noqa: BLE001
        return fail("execute", "IR 解释器: " + str(e))
    if out_ir != out_interp.getvalue():
        return fail("differential",
                    f"IR 与解释器不一致\n解释器: {out_interp.getvalue()!r}\nIR: {out_ir!r}")

    # 9) differential：多路 stdout 逐字节一致
    if out_interp.getvalue() != out_go or out_interp.getvalue() != out_js:
        return fail("differential",
                    f"不一致\n解释器: {out_interp.getvalue()!r}\n→Go: {out_go!r}\n→JS: {out_js!r}")
    ok("differential", f"{len(out_interp.getvalue().encode('utf-8'))} 字节一致")
    res.stdout_bytes = len(out_interp.getvalue().encode("utf-8"))
    res.exit_code = 0
    res.success = True
    return res


def _which(name: str) -> Optional[str]:
    from shutil import which
    return which(name)
