"""开发期快速 harness：跑 conformance 全套件（manifest 驱动）。

用法: python tests/run_conformance_dev.py [--interp-only]
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from lang_py.conformance.manifest import parse_manifest  # noqa: E402
from lang_py.conformance.runner import run_diagnostic, run_differential  # noqa: E402
from lang_py.loader.loader import load_program  # noqa: E402

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
CONF = os.path.join(ROOT, "conformance")


def main() -> int:
    fails = 0
    total = 0
    for cat in sorted(os.listdir(CONF)):
        cat_dir = os.path.join(CONF, cat)
        if not os.path.isdir(cat_dir):
            continue
        for case_name in sorted(os.listdir(cat_dir)):
            case_dir = os.path.join(cat_dir, case_name)
            if not os.path.isdir(case_dir):
                continue
            total += 1
            try:
                with open(os.path.join(case_dir, "case.yaml"), encoding="utf-8") as f:
                    case = parse_manifest(f.read())
                prog = load_program(case_dir, False)
            except Exception as e:  # noqa: BLE001 —— 装载失败即 case 失败（与 runner 口径一致）
                fails += 1
                print(f"[FAIL] {cat}/{case_name} (load): {str(e)[:160]}")
                continue
            from lang_py.conformance.runner import CaseResult
            res = CaseResult(name=case.name, category=case.category, mode=case.mode)
            if case.mode == "check":
                res = run_diagnostic(case, prog.stmts, res)
                status = "ok" if res.ok else "FAIL"
            else:
                res = run_differential(case, prog.stmts, res)
                status = "ok" if res.ok else "FAIL"
            if status != "ok":
                fails += 1
            detail = "" if res.ok else res.note[:220]
            print(f"[{status}] {cat}/{case_name} ({res.mode}): {detail}")
    print(f"\n{total - fails}/{total} 通过")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
