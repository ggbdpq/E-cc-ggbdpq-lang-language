"""前端管线：parse → resolve → typecheck → HIR → IR。

CLI（check/build）与测试共用；诊断带稳定码（E1xxx 解析 / E2xxx 解析名 /
E3xxx-E4xxx 类型与模式）。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from .errors import Diagnostic, DiagnosticList, ParseError
from .hir import build as hir_build
from .hir import nodes as H
from . import ir
from .loader.loader import load_program
from .semantic.resolver import resolve
from .syntax import ast_nodes as A
from .syntax.parser import parse
from .typesys import checker


@dataclass(eq=False)
class Analyzed:
    """全前端产物（HIR/IR/符号/类型），契约层一次性取用。"""

    ast: list[A.Stmt]
    res: object
    ty: object
    hir: H.Prog
    prog: ir.nodes.Prog


def parse_src(src: str) -> list[A.Stmt]:
    return parse(src)


def _parse_diag(err: Exception) -> Diagnostic:
    if isinstance(err, ParseError):
        return Diagnostic("E1001", err.msg, err.line)
    return Diagnostic("E1001", str(err), 0)


def check_stmts(prog: list[A.Stmt]) -> tuple[list[Diagnostic], None]:
    """前端检查（已解析语句序列）。返回 (诊断, None)；诊断空 = 通过。"""
    res = resolve(prog)
    if res.has_errors():
        return list(res.diags), None
    ty = checker.check(prog, res)
    if ty.has_errors():
        return list(ty.diags), None
    return [], None


def analyze_stmts(prog: list[A.Stmt]) -> Analyzed:
    """已解析语句 → 全前端产物。有诊断时抛 DiagnosticList。"""
    res = resolve(prog)
    if res.has_errors():
        raise DiagnosticList(list(res.diags))
    ty = checker.check(prog, res)
    if ty.has_errors():
        raise DiagnosticList(list(ty.diags))
    h = hir_build(prog, res, ty)
    return Analyzed(ast=prog, res=res, ty=ty, hir=h, prog=ir.lower(h))


def analyze(src: str) -> Analyzed:
    try:
        prog = parse(src)
    except (ParseError, Exception) as e:  # noqa: BLE001 —— 词法/解析错误统一 E1001
        if isinstance(e, ParseError):
            raise DiagnosticList([_parse_diag(e)]) from None
        raise
    return analyze_stmts(prog)


def load_analyze(directory: str, include_tests: bool = False) -> tuple[object, Analyzed]:
    """装载目录即包并分析。装载错误抛 LangError，诊断抛 DiagnosticList。"""
    program = load_program(directory, include_tests)
    a = analyze_stmts(program.stmts)
    return program, a


def format_diags(diags: list[Diagnostic]) -> str:
    return "\n".join(d.render() for d in diags)
