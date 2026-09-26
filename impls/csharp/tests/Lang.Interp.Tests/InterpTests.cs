using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Interp.Tests;

/// <summary>
/// 双解释器单元测试（期望值全部手算自 specs/conformance；lang-rs 对抗验证重点逐轴覆盖）：
/// 求值序（EVAL-*）、闭包按引用捕获（loop/mutable/nested/escaping）、引用读写穿透、
/// 值语义（struct 拷贝/容器共享）、match 四分类、<c>?</c> 传播、并发确定子集、
/// 运行时错误分类、astral 码点轴、输出面（float 'g' 形/插入序/嵌套引号/JSON）。
/// </summary>
public class InterpTests
{
    private static (IReadOnlyList<Stmt> Stmts, Analysis Analysis) Compile(string src)
    {
        var (program, perr) = RunHelper.Parse(src, 0);
        Assert.True(perr is null, $"parse 失败: {perr}");
        var analysis = RunHelper.Analyze(program!);
        Assert.True(!analysis.HasErrors(),
            $"语义诊断: {string.Join("; ", analysis.Diags.Select(d => $"{d.Code} {d.Message}"))}");
        return (program!, analysis);
    }

    /// <summary>AST 路 + IR 路同源码双跑，stdout 逐字节一致且等于期望（差分最小单元）。</summary>
    private static void AssertBothPaths(string src, string expected)
    {
        var (stmts, analysis) = Compile(src);
        var ast = RunHelper.RunAst(stmts, analysis);
        Assert.True(ast.Ok, $"AST 路运行错误: {ast.Error}\n源码:\n{src}");
        Assert.Equal(expected, ast.Stdout);
        var ir = RunHelper.RunIr(stmts, analysis);
        Assert.True(ir.Ok, $"IR 路运行错误: {ir.Error}\n源码:\n{src}");
        Assert.Equal(expected, ir.Stdout);
    }

    private static (bool Ok, string Stdout, string? Error) RunAst(string src)
    {
        var (stmts, analysis) = Compile(src);
        return RunHelper.RunAst(stmts, analysis);
    }

    // ———— 求值序（EVAL-ORDER-001/CALL-002/ASSIGN-003/INDEX-005/SHORTCUT-006/GO-007） ————

    [Fact]
    public void Eval_callee_before_args_left_to_right()
    {
        AssertBothPaths(
            """
            fn t(s: string) {
                println(s)
            }
            fn f(): int {
                t("callee")
                return 1
            }
            fn main() {
                println(f() + f() * 0)
            }
            """,
            "callee\ncallee\n1\n");
    }

    [Fact]
    public void Eval_assign_target_components_before_rhs()
    {
        // EVAL-ASSIGN-003：地址组件（a 的 idx()、v() 之前）先于右值
        AssertBothPaths(
            """
            var log = [0]
            fn idx(): int {
                push(log, 1)
                return 0
            }
            fn rhs(): int {
                push(log, 2)
                return 7
            }
            fn main() {
                var a = [0, 0]
                a[idx()] = rhs()
                println(log[0], log[1], log[2], a[0])
            }
            """,
            "0 1 2 7\n");
    }

    [Fact]
    public void Eval_index_obj_before_idx()
    {
        AssertBothPaths(
            """
            fn obj(): [int] {
                println("obj")
                return [1, 2]
            }
            fn idx(): int {
                println("idx")
                return 1
            }
            fn main() {
                println(obj()[idx()])
            }
            """,
            "obj\nidx\n2\n");
    }

    [Fact]
    public void Eval_short_circuit_skips_rhs()
    {
        AssertBothPaths(
            """
            fn t(): bool {
                println("rhs")
                return true
            }
            fn main() {
                let a = false && t()
                let b = true || t()
                let c = true && t()
                println(a, b, c)
            }
            """,
            "rhs\nfalse true true\n");
    }

    [Fact]
    public void Eval_map_literal_pairs_in_order()
    {
        // EVAL-LITERAL-004：键先值后、逐对左到右
        AssertBothPaths(
            """
            fn k(n: int): int {
                println("K")
                return n
            }
            fn v(n: int): int {
                println("V")
                return n * 10
            }
            fn main() {
                let m = {k(1): v(1), k(2): v(2)}
                println(m[1], m[2])
            }
            """,
            "K\nV\nK\nV\n10 20\n");
    }

    [Fact]
    public void Eval_method_recv_before_args()
    {
        AssertBothPaths(
            """
            struct P {
                n: int
            }
            fn (p P).add(x: int): int {
                return p.n + x
            }
            fn arg(): int {
                println("arg")
                return 3
            }
            fn main() {
                println(P{n: 4}.add(arg()))
            }
            """,
            "arg\n7\n");
    }

    // ———— 闭包（VALUE-CAPTURE-004 按引用捕获） ————

    [Fact]
    public void Closure_mutable_capture_shared_cell()
    {
        AssertBothPaths(
            """
            fn main() {
                var total = 0
                let add = fn(n: int) {
                    total += n
                }
                add(3)
                add(4)
                println(total)
            }
            """,
            "7\n");
    }

    [Fact]
    public void Closure_loop_capture_fresh_per_iteration()
    {
        // CTRL-FORIN-003：循环变量每轮重新绑定；闭包捕获互不串扰
        AssertBothPaths(
            """
            fn main() {
                let fns: [fn(): int] = []
                for i in 0..3 {
                    let f = fn(): int {
                        return i * 10
                    }
                    push(fns, f)
                }
                println(fns[0](), fns[1](), fns[2]())
            }
            """,
            "0 10 20\n");
    }

    [Fact]
    public void Closure_escaping_and_nested()
    {
        AssertBothPaths(
            """
            fn counter(): fn(): int {
                var n = 0
                return fn(): int {
                    n = n + 1
                    return n
                }
            }
            fn main() {
                let c = counter()
                let d = counter()
                println(c(), c(), d())
            }
            """,
            "1 2 1\n");
    }

    // ———— 引用（VALUE-REF-003）与值语义（VALUE-COPY-001/ELEM-009） ————

    [Fact]
    public void Ref_read_write_alias_visibility()
    {
        // &x 读穿透可见；r.v 经引用写字段即写原值（values/reference-* 真值同形）
        AssertBothPaths(
            """
            struct P {
                v: int
            }
            fn main() {
                var s = P{v: 1}
                let r = &s
                r.v = 2
                println(s.v, r.v)
                var n = 5
                let rn = &n
                n = 6
                println(n)
            }
            """,
            "2 2\n6\n");
    }

    [Fact]
    public void Element_read_out_is_copy_not_alias()
    {
        // VALUE-ELEM-009：读出即副本——xs[0] 拷贝进 head，写 head 不动容器
        AssertBothPaths(
            """
            struct P {
                v: int
            }
            fn main() {
                let xs = [P{v: 1}]
                let head = xs[0]
                head.v = 9
                println(xs[0].v, head.v)
                xs[0].v = 7
                println(xs[0].v, head.v)
            }
            """,
            "1 9\n7 9\n");
    }

    [Fact]
    public void Struct_copy_semantics_nested()
    {
        AssertBothPaths(
            """
            struct Inner {
                v: int
            }
            struct Outer {
                inner: Inner
            }
            fn main() {
                let a = Outer{inner: Inner{v: 1}}
                let b = a
                b.inner.v = 100
                println(a.inner.v, b.inner.v)
            }
            """,
            "1 100\n");
    }

    [Fact]
    public void Container_share_slice_and_map_alias()
    {
        // VALUE-SHARE-002：容器赋值共享底层（原位变更对全部别名可见）
        AssertBothPaths(
            """
            fn main() {
                let a = [1]
                let b = a
                push(b, 2)
                println(a, b)
                let m = {"k": 1}
                let n = m
                n["k"] = 5
                println(m, n)
            }
            """,
            "[1, 2] [1, 2]\n{\"k\": 5} {\"k\": 5}\n");
    }

    [Fact]
    public void Container_equality_rejected_statically()
    {
        // RUNTIME-EQ-003 面：容器 == 在 C# 实现里由 checker 静态拒绝（E3001），
        // 语义面 = 「容器不可用 == 比较」；与 lang-rs 运行时分类不同层、同结论。
        var (program, perr) = RunHelper.Parse(
            """
            fn main() {
                let a = [1]
                let b = [1]
                println(a == b)
            }
            """, 0);
        Assert.Null(perr);
        var analysis = RunHelper.Analyze(program!);
        Assert.True(analysis.HasErrors());
        Assert.Contains(analysis.Diags, d => d.Code == DiagnosticCode.E3001);
    }

    // ———— match / Option / Result / ? ————

    [Fact]
    public void Match_enum_payload_bind_and_wildcard()
    {
        AssertBothPaths(
            """
            enum Shape {
                Circle(int)
                Rect(int, int)
            }
            fn area(s: Shape): int {
                return match s {
                    Circle(r) => r * r * 3
                    Rect(w, h) => w * h
                    _ => 0
                }
            }
            fn main() {
                println(area(Circle(2)), area(Rect(3, 4)))
            }
            """,
            "12 12\n");
    }

    [Fact]
    public void Try_propagates_err_to_function_boundary()
    {
        AssertBothPaths(
            """
            fn mayFail(n: int): Result[int, error] {
                if n < 0 {
                    return Err(err("neg"))
                }
                return Ok(n * 2)
            }
            fn caller(n: int): Result[int, error] {
                let v = mayFail(n)?
                return Ok(v + 1)
            }
            fn main() {
                println(caller(5))
                println(caller(-1))
            }
            """,
            "Ok(11)\nErr(error{msg: \"neg\"})\n");
    }

    [Fact]
    public void Try_assignment_top_level_ok()
    {
        // ADR-0005：赋值顶层 ? 放行
        AssertBothPaths(
            """
            fn r(): Result[int, error] {
                return Ok(2)
            }
            fn main(): Result[int, error] {
                var acc = 0
                acc = r()?
                return Ok(acc)
            }
            """,
            "");
    }

    // ———— 并发确定子集（TASK-SPAWN-002 / CHAN-*） ————

    [Fact]
    public void Spawn_args_snapshot_and_chan_fifo()
    {
        AssertBothPaths(
            """
            fn worker(ch: chan[int], v: int) {
                send(ch, v)
            }
            fn main() {
                var x = 1
                let ch = makeChan()
                go worker(ch, x)
                x = 2
                println(x)
                println(recv(ch))
            }
            """,
            "2\nOk(1)\n");
    }

    [Fact]
    public void Chan_empty_recv_is_err_value()
    {
        // CHAN-RECV-003：空渠道 Err 文本逐字冻结
        AssertBothPaths(
            """
            fn main() {
                let ch = makeChan()
                println(recv(ch))
            }
            """,
            "Err(\"channel 空\")\n");
    }

    // ———— 运行时错误分类（RUNTIME-FAIL-001） ————

    [Fact]
    public void Runtime_errors_classified()
    {
        var (ok, _, error) = RunAst(
            """
            fn main() {
                let a = [1]
                println(a[5])
            }
            """);
        Assert.False(ok);
        Assert.Contains("越界", error);

        var div = RunAst(
            """
            fn main() {
                let n = 0
                println(1 / n)
            }
            """);
        Assert.False(div.Ok);

        var noArm = RunAst(
            """
            fn main() {
                println(match 5 {
                    1 => "one"
                })
            }
            """);
        Assert.False(noArm.Ok);
        Assert.Contains("无匹配分支", noArm.Error);
    }

    [Fact]
    public void Diagnostics_non_empty_refuses_to_run()
    {
        var (program, _) = RunHelper.Parse("fn main() { println(x) }", 0);
        var analysis = RunHelper.Analyze(program!);
        Assert.True(analysis.HasErrors());
        var sink = new StringSink();
        var result = new Interp(new Lang.Runtime.Machine()).Run(program!, analysis, sink);
        Assert.False(result.IsOk);
        Assert.Equal(string.Empty, sink.ToString());
    }

    [Fact]
    public void Extern_call_reports_translation_only()
    {
        var (ok, _, error) = RunAst(
            """
            extern "go" {
                fn host(n: int): int
            }
            fn main() {
                println(host(1))
            }
            """);
        Assert.False(ok);
        Assert.Contains("仅转译可用", error);
    }

    // ———— astral / 码点轴 ————

    [Fact]
    public void Astral_codepoint_len_index_compare()
    {
        // len=码点数；索引按码点；< 走码点序（U+E000 < U+20000，UTF-16 序相反）。
        // 语言字符串字面量无 \u 转义（LEX-007），astral/私用区字符以原文嵌入。
        var src = "fn main() {\n" +
                  "    let s = \"a\U0001F980b\U0001D54F\"\n" +
                  "    println(len(s))\n" +
                  "    println(s[1], s[3])\n" +
                  "    println(\"ab\" < \"b\")\n" +
                  "    println(\"\uE000\" < \"\U00020000\")\n" +
                  "}\n";
        AssertBothPaths(src, "4\n\U0001F980 \U0001D54F\ntrue\ntrue\n");
    }

    [Fact]
    public void Bytes_and_char_helpers()
    {
        AssertBothPaths(
            """
            fn main() {
                let bs = bytes("🦀")
                println(len(bs), bs[0])
                println(char(0x1F980), char(65))
            }
            """,
            "4 240\n🦀 A\n");
    }

    // ———— 输出面（RUNTIME-OUTPUT-003/004 + RUNTIME-JSON-002） ————

    [Fact]
    public void Output_floats_go_g_format()
    {
        AssertBothPaths(
            """
            fn main() {
                println(1.0, 0.1, 1000000.0, 0.0001, 1.0 / 3.0)
                println(-0.0, 2.5, 123456.0)
                println(int(-2.7), float(2))
            }
            """,
            "1 0.1 1e+06 0.0001 0.3333333333333333\n-0 2.5 123456\n-2 2\n");
    }

    [Fact]
    public void Output_map_insertion_order_and_nested_quote()
    {
        AssertBothPaths(
            """
            fn main() {
                let m = {"b": 2, "a": 1}
                println(m)
                m["a"] = 10
                println(m)
                println(keys(m), values(m))
                println({"内": ["嵌套", "🦀"]})
            }
            """,
            "{\"b\": 2, \"a\": 1}\n{\"b\": 2, \"a\": 10}\n[\"b\", \"a\"] [2, 10]\n{\"内\": [\"嵌套\", \"🦀\"]}\n");
    }

    [Fact]
    public void Output_struct_enum_option_result_error()
    {
        // Ok/Err 受 RESULT-RETURN-004 约束：只能出现在 Result 函数的 return 位置
        AssertBothPaths(
            """
            struct P {
                x: int
            }
            enum E {
                A
                B(int)
            }
            fn mk(n: int): Result[int, error] {
                if n > 0 {
                    return Ok(n)
                }
                return Err(err("boom"))
            }
            fn main() {
                println(P{x: 1})
                println(A, B(7))
                println(Some(1), None)
                println(mk(1), mk(0))
            }
            """,
            "P{x: 1}\nA B(7)\nSome(1) None\nOk(1) Err(error{msg: \"boom\"})\n");
    }

    [Fact]
    public void Json_stringify_key_order_and_parse()
    {
        AssertBothPaths(
            """
            fn main() {
                println(jsonStringify({"b": 2, "a": [1, None]}))
                let parsed = jsonParse("{\"k\": [1, 2.5, null]}")
                println(parsed)
                println(isNone(jsonParse("null")))
                println(jsonParse("42"), jsonParse("2.5"), jsonParse("true"))
            }
            """,
            "{\"a\":[1,null],\"b\":2}\n{\"k\": [1, 2.5, None]}\ntrue\n42 2.5 true\n");
    }

    [Fact]
    public void Float_nan_comparisons_are_false()
    {
        // TYPE-CONV-005 v1.0.1（ADR-0006/RS-007）：NaN 参与任何比较一律 false，
        // != 恒 true（含自身）。NaN 由 inf - inf 造出（1.0 循环 ×10 静默溢出为 +Inf）。
        AssertBothPaths(
            """
            fn main() {
                var big = 1.0
                for i in 0..400 {
                    big = big * 10.0
                }
                let nan = big - big
                println(nan == nan)
                println(nan != nan)
                println(nan < 1.0, nan <= 1.0, nan > 1.0, nan >= 1.0)
                println(1.0 < nan, 1.0 >= nan)
                println(nan == 1.0, nan != 1.0)
            }
            """,
            "false\ntrue\nfalse false false false\nfalse false\nfalse true\n");
    }

    [Fact]
    public void Json_stringify_non_string_key_is_controlled_error()
    {
        // RS-003（ADR-0006）：非字符串映射键 = 受控运行时错误（RUNTIME-JSON-001），消息冻结
        var (ok, _, error) = RunAst(
            """
            fn main() {
                let m = {1: "x"}
                println(jsonStringify(m))
            }
            """);
        Assert.False(ok);
        Assert.Contains("JSON 对象键须为 string，得到 int", error);
    }

    [Fact]
    public void Str_int_float_builtins()
    {
        AssertBothPaths(
            """
            fn main() {
                println(str(12), str(1.5), str([1, "a"]))
                println(int("42"), float("0.25"))
                println(len("héllo"))
            }
            """,
            "12 1.5 [1, \"a\"]\n42 0.25\n5\n");
    }

    // ———— IR 专有面（init→main 双入口 / SwitchInt / 装箱） ————

    [Fact]
    public void Ir_globals_init_before_main()
    {
        AssertBothPaths(
            """
            let base = 10
            var counter = 0
            fn bump() {
                counter = counter + 1
            }
            fn main() {
                bump()
                bump()
                println(base, counter)
            }
            """,
            "10 2\n");
    }

    [Fact]
    public void Ir_int_match_dispatch()
    {
        AssertBothPaths(
            """
            fn classify(n: int): string {
                return match n {
                    0 => "zero"
                    1 => "one"
                    _ => "many"
                }
            }
            fn main() {
                println(classify(0), classify(1), classify(9))
            }
            """,
            "zero one many\n");
    }

    [Fact]
    public void Ir_while_for_break_continue()
    {
        AssertBothPaths(
            """
            fn main() {
                var i = 0
                var sum = 0
                while i < 10 {
                    i = i + 1
                    if i % 2 == 0 {
                        continue
                    }
                    if i > 7 {
                        break
                    }
                    sum += i
                }
                for x in [1, 2, 3] {
                    sum += x
                }
                for c in "ab" {
                    println(c)
                }
                println(sum)
            }
            """,
            "a\nb\n22\n");
    }

    [Fact]
    public void Ir_compound_assign_field_and_index_and_array_concat()
    {
        AssertBothPaths(
            """
            struct C {
                n: int
            }
            fn main() {
                let c = C{n: 1}
                c.n += 41
                println(c.n)
                let xs = [1, 2]
                xs[1] *= 5
                println(xs)
                var ys = [1]
                ys += [2, 3]
                println(ys, len(ys))
            }
            """,
            "42\n[1, 10]\n[1, 2, 3] 3\n");
    }

    [Fact]
    public void Prelude_builtins_names_locked()
    {
        // 预载 20 名与运行时注册表锁名（lang-rs prelude_names_match_runtime_registry 同款）
        var registry = Lang.Runtime.BuiltinRegistry.Core();
        Assert.Equal(20, registry.Count);
        Assert.Equal(20, Interp.PreludeBuiltins.Length);
        foreach (var name in Interp.PreludeBuiltins)
        {
            Assert.True(registry.Lookup(name) is not null, $"内建 {name} 未在注册表登记");
        }
    }

    [Fact]
    public void Value_formatter_chan_and_builtin_print()
    {
        // chan 打印 "?"、内建值打印 "<内建 名>"（TS 证据口径）；匿名 fn 的 AST 路
        // 打印 "<fn 匿名>"（TS value.ts：name 为空 → 匿名）。IR 路的 fnlit 函数在
        // lowering 表内名为 "<fnlit>"（lang-rs lower.rs 同形，参考实现两路本就分歧，
        // 真值套件不打印 fn 值，不进差分），故本测试只锁 AST 路。
        var src = """
            fn main() {
                let ch = makeChan()
                println(ch, str(ch))
                let f = fn(): int {
                    return 1
                }
                println(str(f))
            }
            """;
        var (stmts, analysis) = Compile(src);
        var ast = RunHelper.RunAst(stmts, analysis);
        Assert.True(ast.Ok, ast.Error);
        Assert.Equal("? ?\n<fn 匿名>\n", ast.Stdout);
    }
}
