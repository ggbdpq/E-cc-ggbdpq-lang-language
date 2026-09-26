"""Checker 契约点测试：ADR-0005 赋值顶层 ? 放行 + NAME-SCOPE-001 块级作用域。"""

import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src"))

from lang_py.pipeline import check_stmts  # noqa: E402
from lang_py.pipeline import analyze  # noqa: E402
from lang_py.syntax import ast_nodes as A  # noqa: E402
from lang_py.syntax.parser import parse  # noqa: E402
from lang_py.typesys.model import TK  # noqa: E402


def codes_of(src: str) -> list:
    prog = parse(src)
    diags, _ = check_stmts(prog)
    return [d.code for d in diags]


def find_let(stmts, name):
    for s in stmts:
        if isinstance(s, A.LetStmt):
            if s.name == name:
                return s
        elif isinstance(s, A.FnDecl):
            r = find_let(s.body, name)
            if r is not None:
                return r
        elif isinstance(s, A.IfStmt):
            r = find_let(s.then, name)
            if r is None and s.els is not None:
                r = find_let(s.els, name)
            if r is not None:
                return r
        elif isinstance(s, A.WhileStmt):
            r = find_let(s.body, name)
            if r is not None:
                return r
        elif isinstance(s, A.ForInStmt):
            r = find_let(s.body, name)
            if r is not None:
                return r
    return None


def init_kind(src: str, name: str) -> TK:
    a = analyze(src)
    d = find_let(a.ast, name)
    assert d is not None, f"未找到 let {name}"
    t = a.ty.expr_ty.get(id(d.init))
    assert t is not None, f"let {name} 的初值无推断类型"
    return t.kind


class TestADR0005赋值顶层问号:
    def test_result函数内零诊断(self):
        src = (
            "fn f(): Result[int, error] { return Ok(1) }\n"
            "fn g(): Result[int, error] {\n"
            "var x = 0\n"
            "x = f()?\n"
            "return Ok(x)\n"
            "}\n"
            "fn main() { }"
        )
        assert codes_of(src) == []

    def test复合赋值右值同规则(self):
        src = (
            "fn f(): Result[int, error] { return Ok(1) }\n"
            "fn g(): Result[int, error] {\n"
            "var x = 0\n"
            "x += f()?\n"
            "return Ok(x)\n"
            "}\n"
            "fn main() { }"
        )
        assert codes_of(src) == []

    def test未注解result仍拒绝(self):
        src = (
            "fn f(): Result[int, error] { return Ok(1) }\n"
            "fn g() {\n"
            "var x = 0\n"
            "x = f()?\n"
            "}\n"
            "fn main() { }"
        )
        assert codes_of(src) == ["E3005"]


class TestADR0005扩展表达式语句顶层问号:
    def test_result函数内零诊断(self):
        src = (
            "fn f(): Result[int, error] { return Ok(1) }\n"
            "fn g(): Result[int, error] {\n"
            "f()?\n"
            "return Ok(0)\n"
            "}\n"
            "fn main() { }"
        )
        assert codes_of(src) == []

    def test未注解result仍拒绝(self):
        src = (
            "fn f(): Result[int, error] { return Ok(1) }\n"
            "fn g() {\n"
            "f()?\n"
            "}\n"
            "fn main() { }"
        )
        assert codes_of(src) == ["E3005"]

    def test语句内嵌套仍拒绝(self):
        src = (
            "fn f(): Result[int, error] { return Ok(1) }\n"
            "fn g(): Result[int, error] {\n"
            "f()? + 1\n"
            "return Ok(0)\n"
            "}\n"
            "fn main() { }"
        )
        assert codes_of(src) == ["E3005"]


class TestNameScope001块级作用域:
    def test块内let不写函数层(self):
        src = 'fn main() {\nlet a = 1\nif true {\nlet a = "s"\n}\nlet c = a\nprintln(c)\n}'
        assert codes_of(src) == []
        assert init_kind(src, "c") == TK.INT

    def test循环变量不写函数层(self):
        src = (
            'fn main() {\nlet x = "s"\nfor x in [1, 2] {\nlet y = x\nprintln(y)\n}\n'
            "let z = x\nprintln(z)\n}"
        )
        assert codes_of(src) == []
        assert init_kind(src, "z") == TK.STRING

    def test_match臂绑定不写函数层(self):
        src = (
            "enum E { A(int)\n"
            "    B\n"
            "}\n"
            "fn main() {\n"
            '    let v = "s"\n'
            "    var e = A(1)\n"
            "    match e {\n"
            "        A(v) => println(v)\n"
            "        B => println(0)\n"
            "    }\n"
            "    let z = v\n"
            "    println(z)\n"
            "}"
        )
        assert codes_of(src) == []
        assert init_kind(src, "z") == TK.STRING
