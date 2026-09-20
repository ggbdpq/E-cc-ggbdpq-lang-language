"""lang-go-py CLI：全部动词的编排层（cli.go 语义等价）。

路径安全（v0.1 约定）：run/test/fmt/check 等固定作用于当前目录（目录即包，
入口 main.lang-go），不接收路径参数；CLI 只编排，不承载语言语义。
"""

from __future__ import annotations

import io
import json as _json
import os
import sys

from .. import SCHEMA_VERSION, TOOL_VERSION
from ..backends.go_backend import transpile
from ..backends.js_backend import transpile_js
from ..bind.bind import bind_dir
from ..conformance.manifest import parse_manifest
from ..conformance.runner import CaseResult, run_case, run_suite
from ..conformance.verify import verify_dir
from ..contract.graph_dto import (callers_envelope, callees_envelope,
                                  effects_envelope, graph_dep_envelope,
                                  impact_envelope)
from ..contract.model import (KIND_CHECK, check_json, dependencies_envelope,
                              error_envelope, references_envelope,
                              symbols_envelope, type_envelope)
from ..contract.dump import hir_json, ir_json
from ..contract.dumpast import ast_json
from ..edit.edit import TextEdit, WorkspaceEdit, apply as apply_edit
from ..errors import Diagnostic, DiagnosticList, LangError, LangRuntimeError, ParseError
from ..fix.fix import plan_dir
from ..formatter.formatter import format_src
from ..graph.builder import build_graph
from ..interp.interp import Interp
from ..loader.loader import load_program
from ..pipeline import analyze_stmts, check_stmts, format_diags
from ..refactor.refactor import (organize_imports, rename as refactor_rename,
                                 resolve_target, safe_remove_check,
                                 safe_remove_edits)
from ..syntax.parser import parse

ENTRY_FILE = "main.lang-go"


def _print_json(v) -> None:
    print(_json.dumps(v, ensure_ascii=False, indent=2))


def _has_flag(args: list[str], flag: str) -> bool:
    return flag in args


def usage() -> None:
    sys.stdout.write(f"""lang-go-py {TOOL_VERSION} — 三路执行个人语言的 Python 实现（解释器 / →Go / →JS）

用法:
  lang-go-py            启动 REPL（:quit 退出，:help 帮助）
  lang-go-py run        运行当前目录的 main.lang-go
  lang-go-py test       运行当前目录 *_test.lang-go 里的 test* 函数
  lang-go-py check      语义检查（resolver + 类型检查，稳定诊断码）
  lang-go-py inspect    symbols|refs|type|dependencies|effects|callers|callees|impact|references|dependency --json 语义查询
  lang-go-py dump       ast|hir|ir --json 输出编译器内部树
  lang-go-py fix --safe [--dry-run] [--json]
                        机械安全修复（重复/未使用 import、行尾空白、EOF 换行）+ 验证闭环
  lang-go-py refactor rename|organize-imports|safe-remove [--symbol N] [--to NAME]
                        [--apply] [--json]
                        语义级重构（SymbolId + 引用集，禁止 Regex）；默认只报编辑，--apply 落盘+验证闭环
  lang-go-py verify [--json]
                        全管线验证（parse→…→多路差分一致）+ conformance 套件同为 runner contract 入口
  lang-go-py fmt        就地格式化当前目录 .lang-go
  lang-go-py build      语义检查后写 out/main.go（build js 写 out/main.mjs）
  lang-go-py bind       生成 extern "go" 声明草案（bind.d/）
  lang-go-py conformance [root]   跑跨实现 conformance 套件（默认仓库顶层 conformance/）
  lang-go-py version    版本

文档: specs/（语言规范）  conformance/README.md（验收判定）
""")


def main() -> int:
    sys.setrecursionlimit(200000)
    args = sys.argv[1:]
    if not args:
        return repl()
    cmd = args[0]
    if cmd == "run":
        return run_file()
    if cmd == "test":
        return run_tests()
    if cmd == "fmt":
        return run_fmt()
    if cmd == "check":
        if _has_flag(args[1:], "--json"):
            return run_check_json()
        return run_check()
    if cmd == "inspect":
        return run_inspect(args[1:])
    if cmd == "dump":
        return run_dump(args[1:])
    if cmd == "fix":
        return run_fix(args[1:])
    if cmd == "refactor":
        return run_refactor(args[1:])
    if cmd == "verify":
        return run_verify(args[1:])
    if cmd == "bind":
        return run_bind()
    if cmd == "build":
        target = args[1] if len(args) >= 2 else "go"
        return run_build(target)
    if cmd == "conformance":
        if len(args) >= 2:
            root = args[1]
        else:
            # 默认套件锚定仓库顶层（与 CWD 无关）：cli/main.py → lang_py/cli → lang_py → src → python → 仓库根
            root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                 "..", "..", "..", "..", "conformance"))
        return run_conformance(root)
    if cmd in ("version", "--version", "-v"):
        print("lang-go-py", TOOL_VERSION)
        return 0
    if cmd in ("help", "--help", "-h"):
        usage()
        return 0
    print(f"未知命令 {cmd!r}\n", file=sys.stderr)
    usage()
    return 2


def _entry_path() -> str:
    return os.path.join(os.getcwd(), ENTRY_FILE)


def run_file() -> int:
    try:
        prog = load_program(".", False)
    except LangError as e:
        print("装载失败:", e, file=sys.stderr)
        return 1
    buf = _StdoutBridge(sys.stdout)
    try:
        Interp(buf).run(prog.stmts)
    except (LangRuntimeError, LangError) as e:
        print("错误:", e, file=sys.stderr)
        return 1
    return 0


class _StdoutBridge(io.TextIOBase):
    """把解释器输出桥接到宿主 stdout（UTF-8 确定性）。"""

    def __init__(self, out) -> None:
        self._out = out

    def write(self, s: str) -> int:
        self._out.write(s)
        return len(s)


def run_tests() -> int:
    try:
        prog = load_program(".", True)
    except LangError as e:
        print("装载失败:", e, file=sys.stderr)
        return 1
    it = Interp(_StdoutBridge(sys.stdout))
    try:
        it.run(prog.stmts)  # 顶层初始化（含测试文件追加）
    except (LangRuntimeError, LangError) as e:
        print("装载失败:", e, file=sys.stderr)
        return 1
    tests = prog.tests
    if not tests:
        print("未发现 *_test.lang-go")
        return 0
    passed = failed = 0
    for name in tests:
        try:
            it.call_by_name(name)
        except (LangRuntimeError, LangError) as e:
            failed += 1
            print(f"FAIL {name}: {e}")
        else:
            passed += 1
            print(f"PASS {name}")
    print(f"\n{passed} 通过, {failed} 失败")
    return 1 if failed else 0


def run_fmt() -> int:
    try:
        entries = sorted(os.listdir("."))
    except OSError as e:
        print("读目录失败:", e, file=sys.stderr)
        return 2
    changed = 0
    for name in entries:
        if not name.endswith(".lang-go") or not os.path.isfile(name):
            continue
        with open(name, "rb") as f:
            src = f.read().decode("utf-8")
        try:
            out = format_src(src)
        except (ParseError, Exception) as e:  # noqa: BLE001
            print(f"格式化 {name} 失败: {e}", file=sys.stderr)
            return 1
        if out != src:
            with open(name, "wb") as f:
                f.write(out.encode("utf-8"))
            print(name)
            changed += 1
    print(f"{changed} 个文件已格式化")
    return 0


def run_check() -> int:
    try:
        prog = load_program(".", False)
    except LangError as e:
        print("装载失败:", e, file=sys.stderr)
        return 1
    diags, _ = check_stmts(prog.stmts)
    if diags:
        print("检查未通过:", file=sys.stderr)
        print(format_diags(diags), file=sys.stderr)
        return 1
    print("检查通过")
    return 0


def run_check_json() -> int:
    try:
        prog = load_program(".", False)
    except LangError as e:
        _print_json(error_envelope(KIND_CHECK, "装载失败: " + str(e)))
        return 1
    diags, _ = check_stmts(prog.stmts)
    out = check_json(diags)
    _print_json(out)
    return 0 if out["success"] else 1


def _analyze_current():
    prog = load_program(".", False)
    a = analyze_stmts(prog.stmts)
    return prog, a


def _resolve_symbol_arg(a, arg: str, g=None) -> int:
    """数字 → SymbolID；名字 → 首个同名用户符号；未找到 → -1。"""
    if not arg:
        return -1
    if arg.isdigit():
        node = g.nodes.get(int(arg)) if g is not None else None
        if node is not None:
            return int(arg)
        return -1
    for sym in a.res.symbols:
        if sym.builtin_mark:
            continue
        if sym.name == arg:
            return sym.id
    return -1


def run_inspect(args: list[str]) -> int:
    if not args:
        _print_json(error_envelope("inspect",
                                   "缺少子命令：symbols|refs|type|dependencies|effects|callers|callees|impact|references"))
        return 2
    sub = args[0]
    kind = "inspect." + sub
    if not _has_flag(args, "--json"):
        print("inspect 仅支持 --json 输出（v0.4+）", file=sys.stderr)
        return 2
    file_arg = ""
    line = col = 0
    depth = -1
    target = ""
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--file" and i + 1 < len(args):
            file_arg = args[i + 1]
        elif a == "--line" and i + 1 < len(args):
            line = int(args[i + 1]) if args[i + 1].isdigit() else 0
        elif a == "--column" and i + 1 < len(args):
            col = int(args[i + 1]) if args[i + 1].isdigit() else 0
        elif a == "--depth" and i + 1 < len(args):
            depth = int(args[i + 1]) if args[i + 1].lstrip("-").isdigit() else -1
        elif a == "--symbol" and i + 1 < len(args):
            target = args[i + 1]
        elif not a.startswith("-") and a != sub and not target \
                and sub not in ("symbols", "dependencies", "effects"):
            target = a
        i += 1

    try:
        prog, a = _analyze_current()
    except LangError as e:
        _print_json(error_envelope(kind, "装载失败: " + str(e)))
        return 1
    except DiagnosticList as dl:
        _print_json(error_envelope(kind, format_diags(dl.diags)))
        return 1

    if sub == "symbols":
        _print_json(symbols_envelope(prog.stmts, a.res, a.ty))
        return 0
    if sub in ("refs", "references"):
        sym_id = -1
        if target:
            sym_id = _resolve_symbol_arg(a, target)
            if sym_id < 0:
                _print_json(error_envelope(kind, "未知符号 " + target))
                return 1
        _print_json(references_envelope(prog.stmts, a.res, sym_id))
        return 0
    if sub in ("callers", "callees", "impact"):
        g = build_graph(prog.stmts, a.res, a.ty, a.hir, a.prog, prog.files)
        sid = _resolve_symbol_arg(a, target, g)
        if sid <= 0:
            _print_json(error_envelope(kind, "未知符号 " + target))
            return 1
        if sub == "callers":
            _print_json(callers_envelope(g, g.callers(sid, depth)))
        elif sub == "callees":
            _print_json(callees_envelope(g, g.callees(sid, depth)))
        else:
            _print_json(impact_envelope(g.impact(sid, depth)))
        return 0
    if sub == "type":
        if line == 0:
            _print_json(error_envelope(kind, "缺少 --line/--column 参数"))
            return 2
        env, ok = type_envelope(prog.stmts, a.res, a.ty, file_arg, line, col)
        _print_json(env)
        return 0 if ok else 1
    if sub == "dependencies":
        _print_json(dependencies_envelope(prog.files))
        return 0
    if sub == "dependency":
        target_file = target or file_arg or ENTRY_FILE
        g = build_graph(prog.stmts, a.res, a.ty, a.hir, a.prog, prog.files)
        dep = g.file_dep(target_file)
        if dep is None:
            _print_json(error_envelope(kind, "未知文件 " + target_file))
            return 1
        _print_json(graph_dep_envelope(dep))
        return 0
    if sub == "effects":
        _print_json(effects_envelope(a.prog))
        return 0
    _print_json(error_envelope(kind, "未知子命令 " + sub))
    return 2


def run_dump(args: list[str]) -> int:
    if not args:
        _print_json(error_envelope("dump", "缺少子命令：ast|hir|ir"))
        return 2
    sub = args[0]
    if not _has_flag(args, "--json"):
        print("dump 仅支持 --json 输出（v0.4）", file=sys.stderr)
        return 2
    kind = "dump." + sub
    try:
        prog = load_program(".", False)
    except LangError as e:
        _print_json(error_envelope(kind, "装载失败: " + str(e)))
        return 1
    if sub == "ast":
        try:
            a = analyze_stmts(prog.stmts)
        except DiagnosticList as dl:
            _print_json(error_envelope(kind, format_diags(dl.diags)))
            return 1
        _print_json({"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
                     "kind": kind, "nodes": ast_json(prog.stmts, a.res, a.ty)})
    elif sub == "hir":
        try:
            a = analyze_stmts(prog.stmts)
        except DiagnosticList as dl:
            _print_json(error_envelope(kind, format_diags(dl.diags)))
            return 1
        _print_json({"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
                     "kind": kind, "nodes": hir_json(a.hir)})
    elif sub == "ir":
        try:
            a = analyze_stmts(prog.stmts)
        except DiagnosticList as dl:
            _print_json(error_envelope(kind, format_diags(dl.diags)))
            return 1
        _print_json({"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
                     "kind": kind, "nodes": ir_json(a.prog)})
    else:
        _print_json(error_envelope(kind, "未知子命令 " + sub))
        return 2
    return 0


def _fix_stop(kind: str, stage: str, msg: str) -> dict:
    return {"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
            "kind": kind, "success": False, "stage": stage, "error": msg}


def _verify_closure(kind: str) -> int:
    """编辑后验证闭环：parse → resolve → typecheck。失败输出结构化原因。"""
    try:
        prog = load_program(".", False)
    except LangError as e:
        msg = "parse/resolve: " + str(e)
        _print_json(_fix_stop(kind, "verify", msg))
        print("STOP——编辑后验证失败:", msg, file=sys.stderr)
        return 1
    diags, _ = check_stmts(prog.stmts)
    if diags:
        msg = "typecheck: " + format_diags(diags)
        _print_json(_fix_stop(kind, "verify", msg))
        print("STOP——编辑后验证失败:", msg, file=sys.stderr)
        return 1
    return 0


def run_fix(args: list[str]) -> int:
    kind = "fix"
    if not _has_flag(args, "--safe"):
        _print_json(error_envelope(kind, "v0.5 仅支持 --safe（机械可证明安全修复）"))
        return 2
    dry = _has_flag(args, "--dry-run")
    as_json = _has_flag(args, "--json")
    try:
        ws = plan_dir(".")
    except OSError as e:
        _print_json(error_envelope(kind, str(e)))
        return 1
    if as_json or dry:
        _print_json({"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
                     "kind": kind, "dryRun": dry, "applied": False,
                     "workspaceEdit": _ws_to_json(ws)})
        if dry:
            return 0
    try:
        apply_edit(ws, ".")
    except Exception as e:  # noqa: BLE001
        _print_json(_fix_stop(kind, "apply", "apply: " + str(e)))
        print("STOP——应用编辑失败:", e, file=sys.stderr)
        return 1
    if _verify_closure(kind) != 0:
        return 1
    if as_json:
        _print_json({"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
                     "kind": kind, "success": True, "applied": True,
                     "editCount": len(ws.edits)})
    else:
        print(f"safe fix 完成（{len(ws.edits)} 处编辑），验证通过")
    return 0


def _ws_to_json(ws: WorkspaceEdit) -> dict:
    return {
        "edits": [{"file": e.file, "startOffset": e.start_offset,
                   "endOffset": e.end_offset, "replacement": e.replacement}
                  for e in ws.edits],
        "preconditions": [{"file": p.file, "fileHash": p.file_hash}
                          for p in ws.preconditions],
    }


def run_refactor(args: list[str]) -> int:
    if not args:
        _print_json(error_envelope("refactor", "缺少子命令：rename|organize-imports|safe-remove"))
        return 2
    sub = args[0]
    kind = "refactor." + sub
    if sub not in ("rename", "organize-imports", "safe-remove"):
        _print_json(error_envelope(kind, "未知子命令 " + sub))
        return 2
    symbol = to = ""
    apply_flag = _has_flag(args, "--apply")
    i = 0
    while i < len(args):
        if args[i] == "--symbol" and i + 1 < len(args):
            symbol = args[i + 1]
        elif args[i] == "--to" and i + 1 < len(args):
            to = args[i + 1]
        i += 1

    try:
        prog = load_program(".", False)
        by_file: dict[str, str] = {}
        default = ""
        for fi in prog.files:
            path = os.path.join(".", *fi.path.split("/"))
            with open(path, "rb") as f:
                content = f.read().decode("utf-8")
            by_file[fi.path] = content
            if fi.path == ENTRY_FILE or not default:
                default = content
        from ..refactor.refactor import Sources
        srcs = Sources(default=default, by_file=by_file)
        a = analyze_stmts(prog.stmts)
    except LangError as e:
        _print_json(error_envelope(kind, "装载失败: " + str(e)))
        return 1
    except DiagnosticList as dl:
        _print_json(error_envelope(kind, format_diags(dl.diags)))
        return 1

    edits: list[TextEdit] = []
    sym_json: dict = {}
    try:
        if sub in ("rename", "safe-remove"):
            target = resolve_target(a.res, symbol)
            sym_json = {"id": target.id, "name": target.name,
                        "kind": _kind_str(target)}
            if sub == "rename":
                if not to:
                    _print_json(error_envelope(kind, "缺少 --to 参数"))
                    return 2
                edits = refactor_rename(prog.stmts, srcs, a.res, a.hir, target, to)
            else:
                chk = safe_remove_check(prog.stmts, a.res, target)
                if not chk.removable:
                    _print_json({"schemaVersion": SCHEMA_VERSION,
                                 "toolVersion": TOOL_VERSION, "kind": kind,
                                 "removable": False, "reason": chk.reason,
                                 "refSites": chk.ref_sites, "symbol": sym_json})
                    return 1
                edits = safe_remove_edits(prog.stmts, srcs, a.res, target)
        else:  # organize-imports
            for p in sorted(by_file):
                out, changed = organize_imports(by_file[p])
                if changed:
                    edits.append(TextEdit(file=p, start_offset=0,
                                          end_offset=len(by_file[p]),
                                          replacement=out))
    except LangError as e:
        _print_json(error_envelope(kind, str(e)))
        return 1

    # 前置哈希：编辑涉及的全部文件
    preconds = []
    seen: set[str] = set()
    from ..edit.edit import hash_bytes
    for e in edits:
        if e.file in seen:
            continue
        seen.add(e.file)
        preconds.append({"file": e.file,
                         "fileHash": hash_bytes(srcs.source(e.file).encode("utf-8"))})
    ws = WorkspaceEdit(edits=edits)
    ws.preconditions = [_p_to_precond(p) for p in preconds]

    def report(applied: bool) -> int:
        _print_json({"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
                     "kind": kind, "applied": applied, "editCount": len(edits),
                     "symbol": sym_json, "workspaceEdit": _ws_to_json(ws)})
        return 0

    if not apply_flag:
        return report(False)
    try:
        apply_edit(ws, ".")
    except Exception as e:  # noqa: BLE001
        _print_json(_fix_stop(kind, "apply", str(e)))
        print("STOP——应用编辑失败:", e, file=sys.stderr)
        return 1
    if _verify_closure(kind) != 0:
        return 1
    return report(True)


def _kind_str(sym) -> str:
    from ..semantic.symbols import KFN, KMETHOD, KVAR, KPARAM, KSTRUCT, KENUM, KCTOR
    return {KFN: "function", KMETHOD: "method", KVAR: "variable",
            KPARAM: "parameter", KSTRUCT: "struct", KENUM: "enum",
            KCTOR: "constructor"}.get(sym.kind, "unknown")


def _p_to_precond(p: dict):
    from ..edit.edit import Precondition
    return Precondition(file=p["file"], file_hash=p["fileHash"])


def run_verify(args: list[str]) -> int:
    kind = "verify"
    as_json = _has_flag(args, "--json")
    r = verify_dir(".")
    if as_json:
        out = {"schemaVersion": SCHEMA_VERSION, "toolVersion": TOOL_VERSION,
               "kind": kind, "success": r.success,
               "stages": [{"stage": s.name, "ok": s.ok,
                           **({"skipped": True} if s.skipped else {}),
                           **({"note": s.note} if s.note else {})} for s in r.stages]}
        if r.stdout_bytes:
            out["stdoutBytes"] = r.stdout_bytes
        if r.exit_code:
            out["exitCode"] = r.exit_code
        _print_json(out)
    else:
        for st in r.stages:
            mark = "ok  " if st.ok else ("SKIP" if st.skipped else "FAIL")
            line = f"[{mark}] {st.name}"
            if st.note:
                line += " — " + st.note
            print(line)
        if r.success:
            print("验证通过：多路执行一致")
    return 0 if r.success else 1


def run_bind() -> int:
    try:
        out = bind_dir("bind.d")
    except LangError as e:
        print("bind 失败:", e, file=sys.stderr)
        return 1
    sys.stdout.write(out)
    return 0


def run_build(target: str) -> int:
    try:
        prog = load_program(".", False)
    except LangError as e:
        print("装载失败:", e, file=sys.stderr)
        return 1
    try:
        os.makedirs("out", exist_ok=True)
    except OSError as e:
        print("建 out/ 失败:", e, file=sys.stderr)
        return 1
    try:
        a = analyze_stmts(prog.stmts)
    except DiagnosticList as dl:
        print("检查未通过:", file=sys.stderr)
        print(format_diags(dl.diags), file=sys.stderr)
        return 1
    if target == "go":
        try:
            src = transpile(a.prog)
        except LangError as e:
            print("→Go 转译失败:", e, file=sys.stderr)
            return 1
        with open(os.path.join("out", "main.go"), "wb") as f:
            f.write(src.encode("utf-8"))
        print("out/main.go（go build ./out 或 go run ./out/main.go 执行）")
        return 0
    if target == "js":
        try:
            src = transpile_js(a.prog)
        except LangError as e:
            print("→JS 转译失败:", e, file=sys.stderr)
            return 1
        with open(os.path.join("out", "main.mjs"), "wb") as f:
            f.write(src.encode("utf-8"))
        print("out/main.mjs（node out/main.mjs 执行）")
        return 0
    print("未知目标:", target, "（可选 go / js）", file=sys.stderr)
    return 2


def run_conformance(root: str) -> int:
    try:
        results, fails = run_suite(root)
    except (LangError, OSError) as e:
        print("套件错误:", e, file=sys.stderr)
        return 2
    for r in results:
        mark = "ok  " if r.ok else ("SKIP" if r.skipped else "FAIL")
        note = f" — {r.note}" if (not r.ok and r.note) else ""
        print(f"[{mark}] {r.category}/{r.name} ({r.mode}){note}")
    print(f"\n{len(results) - fails}/{len(results)} 通过")
    return 1 if fails else 0


def repl() -> int:
    it = Interp(_StdoutBridge(sys.stdout))
    print(f"lang-go-py v{TOOL_VERSION} — :help 帮助, :quit 退出")
    buf: list[str] = []
    prompt = "lang-go> "
    while True:
        sys.stdout.write(prompt)
        sys.stdout.flush()
        try:
            line = sys.stdin.readline()
        except EOFError:
            print()
            return 0
        if not line:
            print()
            return 0
        stripped = line.strip()
        if not buf:
            if stripped == "":
                continue
            if stripped in (":quit", ":q", "exit"):
                return 0
            if stripped == ":help":
                print("输入 lang-go 源码回车执行；未闭合的块会进入续行模式；:quit 退出")
                continue
        buf.append(line)
        src = "\n".join(buf) + "\n"
        try:
            prog = parse(src)
        except ParseError as e:
            if e.incomplete:
                prompt = "  ... "
                continue
            print("错误:", e)
            buf = []
            prompt = "lang-go> "
            continue
        except Exception as e:  # noqa: BLE001
            print("错误:", e)
            buf = []
            prompt = "lang-go> "
            continue
        try:
            val, has = it.eval_chunk(prog)
        except (LangRuntimeError, LangError) as e:
            print("错误:", e)
        else:
            if has:
                print("=>", _fmt_repl(val))
        buf = []
        prompt = "lang-go> "


def _fmt_repl(v) -> str:
    from ..interp.values import fmt_value
    return fmt_value(v)


if __name__ == "__main__":
    sys.exit(main())
