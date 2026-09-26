"""lang-py 单元测试：语法前端、语义核心、解释器、契约层。

覆盖 specs 的关键语义点与诊断码；conformance 差分在 tests/conformance_test.py。
"""

import io
import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src"))

from lang_py.errors import DiagnosticList, LangRuntimeError, ParseError  # noqa: E402
from lang_py.interp.interp import Interp  # noqa: E402
from lang_py.pipeline import analyze  # noqa: E402
from lang_py.syntax.lexer import lex  # noqa: E402
from lang_py.syntax.parser import parse  # noqa: E402

NL = chr(10)


def run_src(src: str) -> str:
    a = analyze(src)
    buf = io.StringIO()
    Interp(buf).run(a.ast)
    return buf.getvalue()


def first_diag(src: str):
    try:
        analyze(src)
    except DiagnosticList as dl:
        return dl.diags[0]
    return None


def diags_of(src: str) -> list:
    try:
        analyze(src)
    except DiagnosticList as dl:
        return list(dl.diags)
    return []


# ———— 词法（LEX）————

class TestLexer:
    def test_hex_normalizes_to_decimal(self):
        toks = lex("0xFF")
        assert [(t.kind.name, t.lit) for t in toks] == [("INT", "255"), ("EOF", "")]

    def test_range_not_float(self):
        toks = lex("1..3")
        assert [t.lit for t in toks] == ["1", "..", "3", ""]

    def test_invalid_escape(self):
        with pytest.raises(Exception) as e:
            lex('"a' + chr(92) + 'qb"')
        assert "转义" in str(e.value)

    def test_unicode_ident(self):
        toks = lex("变量1 = 1")
        assert toks[0].kind.name == "IDENT"

    def test_underscore_is_wildcard_token(self):
        toks = lex("_")
        assert toks[0].kind.name == "UNDERSCORE"


# ———— 语法（PARSE）————

class TestParser:
    def test_control_head_rejects_struct_literal(self):
        with pytest.raises(Exception):
            parse("fn main() { if P{x: 1} { } }")

    def test_parenthesized_struct_literal_ok(self):
        parse("struct P { x: int }" + NL + "fn main() { if (P{x: 1}).x > 0 { } }")

    def test_match_arms_need_newline(self):
        with pytest.raises(Exception):
            parse("fn main() { let x = match 1 { 1 => 2, 2 => 3 } }")

    def test_go_target_must_be_call(self):
        with pytest.raises(Exception):
            parse("fn main() { go f }")

    def test_repl_incomplete_on_eof(self):
        with pytest.raises(ParseError) as e:
            parse("fn main() {")
        assert e.value.incomplete


# ———— 名字解析（NAME / E2xxx）————

class TestResolver:
    def test_duplicate_def(self):
        d = first_diag("fn f() { let a = 1" + NL + " let a = 2 }")
        assert d.code == "E2001"

    def test_undefined(self):
        d = first_diag("fn main() { x }")
        assert d.code == "E2002"

    def test_let_reassign(self):
        d = first_diag("fn main() { let x = 1" + NL + " x = 2 }")
        assert d.code == "E2005"

    def test_break_outside_loop(self):
        d = first_diag("fn main() { break }")
        assert d.code == "E2007"

    def test_return_outside_fn(self):
        d = first_diag("return 1")
        assert d.code == "E2008"

    def test_nested_type_decl(self):
        d = first_diag("fn main() { struct P { x: int } }")
        assert d.code == "E2010"

    def test_shadowing_legal(self):
        # lang-go 无裸块；块作用域经 if 产生
        src = NL.join([
            "fn main() {",
            "    let x = 1",
            "    if true {",
            "        let x = 2",
            "        println(x)",
            "    }",
            "    println(x)",
            "}",
        ])
        assert diags_of(src) == []

    def test_ctor_name_clash_with_preloaded_ctor(self):
        # 预载构造器 Some 占用构造器命名空间 → E2003
        d = first_diag("enum E { Some }")
        assert d.code == "E2003"

    def test_case_clash_with_builtin_fn(self):
        # case 与内建函数名冲突 → E2006
        d = first_diag("enum E { println }")
        assert d.code == "E2006"


# ———— 类型（TYPE / E3xxx / MATCH / E4xxx）————

class TestChecker:
    def test_annotated_mismatch(self):
        d = first_diag("fn main() { let s: string = 1 }")
        assert d.code == "E3001"

    def test_int_float_mix(self):
        d = first_diag("fn main() { let x = 1 + 1.5 }")
        assert d.code == "E3002"

    def test_arg_count(self):
        d = first_diag("fn f(a: int) { }" + NL + "fn main() { f(1, 2) }")
        assert d.code == "E3003"

    def test_missing_param_annotation(self):
        d = first_diag("fn f(a) { }")
        assert d.code == "E3007"

    def test_try_needs_result_fn(self):
        src = NL.join([
            "fn f(): int {",
            "    let x = g()?",
            "    return x",
            "}",
            "fn g(): Result[int, error] {",
            "    return Ok(1)",
            "}",
        ])
        d = first_diag(src)
        assert d.code == "E3005"

    def test_try_operand_must_be_result(self):
        src = NL.join([
            "fn f(): Result[int, error] {",
            "    let x = 1?",
            "    return Ok(x)",
            "}",
        ])
        d = first_diag(src)
        assert d.code == "E3008"

    def test_result_second_param_must_be_error(self):
        d = first_diag("fn f(): Result[int, int] { return Ok(1) }")
        assert d.code == "E3009"

    def test_non_iterable(self):
        d = first_diag("fn main() { for x in 1 { } }")
        assert d.code == "E3010"

    def test_not_on_int(self):
        d = first_diag("fn main() { let x = !1 }")
        assert d.code == "E3011"

    def test_ref_only_var(self):
        d = first_diag("fn main() { let x = &(1 + 2) }")
        assert d.code == "E3012"

    def test_string_index_must_be_int(self):
        d = first_diag('fn main() { let s = "abc"' + NL + ' println(s["a"]) }')
        assert d.code == "E3013"

    def test_map_key_float(self):
        d = first_diag("fn main() { let m = {}" + NL + " m[1.5] = 1 }")
        assert d.code == "E3014"

    def test_enum_match_not_exhaustive(self):
        src = NL.join([
            "enum E { A, B }",
            "fn f(e: E): int {",
            "    return match e {",
            "        A => 1",
            "    }",
            "}",
        ])
        d = first_diag(src)
        assert d.code == "E4001"

    def test_enum_match_with_literal_arm(self):
        src = NL.join([
            "enum E { A, B }",
            "fn f(e: E): int {",
            "    return match e {",
            "        A => 1",
            "        2 => 3",
            "    }",
            "}",
        ])
        d = first_diag(src)
        assert d.code == "E4002"

    def test_result_option_mixed(self):
        src = NL.join([
            "fn f(r: Result[int, error]): int {",
            "    return match r {",
            "        Ok(v) => v",
            "        None => 0",
            "    }",
            "}",
        ])
        d = first_diag(src)
        assert d.code == "E4003"


# ———— 运行时（interp）————

class TestInterp:
    def test_hello(self):
        assert run_src('fn main() { println("hello") }') == "hello\n"

    def test_float_no_trailing_zero(self):
        assert run_src("fn main() { println(1.0, 2.5) }") == "1 2.5\n"

    def test_eval_order_call_args(self):
        src = NL.join([
            'var log = ""',
            "fn mk(s: string, v: int): int {",
            "    log += s",
            "    return v",
            "}",
            "fn main() {",
            '    println(mk("a", 1), mk("b", 2))',
            "    println(log)",
            "}",
        ])
        assert run_src(src) == "1 2\nab\n"

    def test_assign_target_address_first(self):
        src = NL.join([
            "var xs = [0, 0]",
            "var i = 0",
            "fn bump(): int {",
            "    i = 1",
            "    return 1",
            "}",
            "fn main() {",
            "    xs[bump()] = 7",
            "    println(xs, i)",
            "}",
        ])
        assert run_src(src) == "[0, 7] 1\n"

    def test_struct_copy_semantics(self):
        src = NL.join([
            "struct P { x: int }",
            "fn main() {",
            "    var p = P{x: 1}",
            "    let q = p",
            "    q.x = 9",
            "    println(p.x, q.x)",
            "}",
        ])
        assert run_src(src) == "1 9\n"

    def test_array_aliasing(self):
        src = NL.join([
            "fn main() {",
            "    let a = [1]",
            "    let b = a",
            "    push(b, 2)",
            "    println(len(a))",
            "}",
        ])
        assert run_src(src) == "2\n"

    def test_map_insertion_order(self):
        assert run_src('fn main() { println({"b": 1, "a": 2}) }') == '{"b": 1, "a": 2}\n'

    def test_json_stringify_sorted_keys(self):
        assert run_src('fn main() { println(jsonStringify({"b": 1, "a": true})) }') \
            == '{"a":true,"b":1}\n'

    def test_json_null_to_none(self):
        src = NL.join([
            "fn main() {",
            '    let v = jsonParse("null")',
            "    println(isNone(v))",
            "}",
        ])
        assert run_src(src) == "true\n"

    def test_string_codepoint_index(self):
        assert run_src('fn main() { println("héllo"[1], len("héllo")) }') == "é 5\n"

    def test_int64_wrap(self):
        assert run_src("fn main() { println(9223372036854775807 + 1) }") == "-9223372036854775808\n"

    def test_trunc_division(self):
        assert run_src("fn main() { println(-7 / 2, -7 % 3) }") == "-3 -1\n"

    def test_closure_capture_by_reference(self):
        src = NL.join([
            "fn counter(): fn(): int {",
            "    var n = 0",
            "    return fn(): int {",
            "        n = n + 1",
            "        return n",
            "    }",
            "}",
            "fn main() {",
            "    let c = counter()",
            "    println(c(), c(), c())",
            "}",
        ])
        assert run_src(src) == "1 2 3\n"

    def test_try_propagation(self):
        src = NL.join([
            "fn divide(a: int, b: int): Result[int, error] {",
            "    if b == 0 {",
            '        return Err(err("div by zero"))',
            "    }",
            "    return Ok(a / b)",
            "}",
            "fn safeDiv(a: int, b: int): Result[int, error] {",
            "    let q = divide(a, b)?",
            "    return Ok(q)",
            "}",
            "fn main() {",
            "    println(safeDiv(9, 2))",
            "    println(safeDiv(7, 0))",
            "}",
        ])
        assert run_src(src) == "Ok(4)" + NL + 'Err(error{msg: "div by zero"})' + NL

    def test_chan_send_recv(self):
        src = NL.join([
            "fn producer(ch: chan[int]) {",
            "    send(ch, 42)",
            "}",
            "fn main() {",
            "    let ch = makeChan()",
            "    go producer(ch)",
            "    println(recv(ch))",
            "}",
        ])
        assert run_src(src) == "Ok(42)\n"

    def test_channel_full_is_runtime_error(self):
        src = NL.join([
            "fn main() {",
            "    let ch = makeChan()",
            "    for i in 0..1025 {",
            "        send(ch, i)",
            "    }",
            "}",
        ])
        a = analyze(src)
        with pytest.raises(LangRuntimeError):
            Interp(io.StringIO()).run(a.ast)


# ———— 格式化器 ————

class TestFormatter:
    FORMATS = [
        'fn main() { println("hello") }',
        "fn add(a: int, b: int): int { return a + b }",
        "enum E { A(int), B }" + NL + "fn f(e: E): int { return match e { A(v) => v" + NL + " B => 0 } }",
        "// 前置注释" + NL + "fn main() { let x = 1  // 行尾注释" + NL + " println(x) }",
        "struct P { x: int, y: int }",
        'fn main() { let m = {"a": 1}' + NL + ' m["b"] = 2' + NL + ' println(m["a"]..m["b"]) }',
    ]

    def test_idempotent(self):
        from lang_py.formatter.formatter import format_src
        for src in self.FORMATS:
            once = format_src(src)
            assert format_src(once) == once, src

    def test_comments_preserved(self):
        from lang_py.formatter.formatter import format_src
        out = format_src("// 一" + NL + "// 二" + NL + "fn main() { }")
        assert "一" in out and "二" in out

    def test_semantics_preserved(self):
        from lang_py.formatter.formatter import format_src
        src = "fn main() { let x=1+2*3" + NL + " println(x) }"
        assert run_src(format_src(src)) == run_src(src)


# ———— 契约层 ————

class TestContract:
    def test_check_json_shape(self):
        from lang_py.contract.model import check_json
        out = check_json([])
        assert out["schemaVersion"] == "1" and out["kind"] == "check" and out["success"]

    def test_rule_mapping(self):
        from lang_py.contract.rules import rule_of
        assert rule_of("E2002") == "NAME-UNDEF-004"
        assert rule_of("E4001") == "MATCH-EXHAUSTIVE-002"
        assert rule_of("E9999") == ""

    def test_inspect_symbols_stable(self):
        from lang_py.contract.model import symbols_json
        from lang_py.semantic.resolver import resolve
        from lang_py.typesys import checker as chk
        src = "fn main() { let x = 1" + NL + " println(x) }"
        prog = parse(src)
        res = resolve(prog)
        ty = chk.check(prog, res)
        prog2 = parse(src)
        res2 = resolve(prog2)
        ty2 = chk.check(prog2, res2)
        a = symbols_json(prog, res, ty)
        b = symbols_json(prog2, res2, ty2)
        assert [(s["symbolId"], s["name"], s["kind"]) for s in a] \
            == [(s["symbolId"], s["name"], s["kind"]) for s in b]

    def test_dump_ir_deterministic(self):
        from lang_py.contract.dump import ir_json
        a1 = analyze("fn main() { println(1 + 2) }")
        a2 = analyze("fn main() { println(1 + 2) }")
        assert ir_json(a1.prog) == ir_json(a2.prog)


# ———— ADR-0006 语义点（RS-006/RS-007/RS-003）————

class TestAdr0006Semantics:
    """int(float) 饱和、NaN 比较全 false、jsonStringify 非字符串键受控报错。

    Inf/NaN 由浮点乘法静默溢出构造（语言无指数字面量）；期望值全部手算。
    """

    INF_SRC = (
        "fn main() {" + NL +
        "    var big = 1.0" + NL +
        "    for i in 0..400 {" + NL +
        "        big = big * 10.0" + NL +
        "    }" + NL +
        "    println(int(big))" + NL +
        "    println(int(0.0 - big))" + NL +
        "    println(int(big - big))" + NL +
        "}"
    )

    def test_int_float_saturates_and_zeroes_nan(self):
        # RS-006：NaN → 0；±Inf 饱和到 int64 边界；其余向零截断
        assert run_src("fn main() { println(int(0.5), int(-0.5), int(2.9), int(-2.9)) }") == "0 0 2 -2" + NL
        assert run_src(self.INF_SRC) == (
            "9223372036854775807" + NL + "-9223372036854775808" + NL + "0" + NL
        )

    def test_nan_comparisons_all_false(self):
        # RS-007：NaN 参与任何比较一律 false；!= 恒 true（含自身对自身）
        src = (
            "fn main() {" + NL +
            "    var big = 1.0" + NL +
            "    for i in 0..400 {" + NL +
            "        big = big * 10.0" + NL +
            "    }" + NL +
            "    let nan = big - big" + NL +
            "    println(nan == nan)" + NL +
            "    println(nan != nan)" + NL +
            "    println(nan < 1.0, nan <= 1.0, nan > 1.0, nan >= 1.0)" + NL +
            "    println(1.0 < nan, 1.0 >= nan)" + NL +
            "}"
        )
        assert run_src(src) == (
            "false" + NL + "true" + NL + "false false false false" + NL + "false false" + NL
        )

    def test_json_stringify_non_string_key_controlled_error(self):
        # RS-003：非字符串映射键 = 受控运行时错误（RUNTIME-JSON-001），消息冻结、类型名随实型
        with pytest.raises(LangRuntimeError, match="JSON 对象键须为 string，得到 int"):
            run_src('fn main() { println(jsonStringify({1: "x"})) }')
        with pytest.raises(LangRuntimeError, match="JSON 对象键须为 string，得到 bool"):
            run_src("fn main() { println(jsonStringify({true: 1})) }")
