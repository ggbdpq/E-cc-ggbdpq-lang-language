using Lang.Core;
using Lang.Ir;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>
/// IR 文本 dump：golden 2 例（期望文本手写，dump 前先过 validate）+ 确定性 + 转义 + 浮点。
/// dump 是调试视图非跨版本契约，但同一程序集内格式由本类 golden 锁定。
/// </summary>
public class DumpTests
{
    [Fact]
    public void Golden_minimal_program()
    {
        var prog = TestSupport.Prog(
            structs: new StructDecl[]
            {
                new("Point", Array.Empty<string>(), new FieldDecl[] { new("x", null), new("y", null) }),
            },
            enums: new EnumDecl[]
            {
                new("结果", new[] { "T" }, new EnumCase[]
                {
                    new("Some", new TypeId?[] { null }), new("None", Array.Empty<TypeId?>()),
                }),
            },
            globals: new GlobalDecl[] { new("计数器", true, null) },
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false, locals: new[] { TestSupport.L("a") },
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[]
                            {
                                new Inst.ConstI64(1), new Inst.LocalSet(new LocalId(0)),
                                new Inst.LocalGet(new LocalId(0)),
                            },
                            new Terminator.Return(true)),
                    }),
            },
            main: new FunctionId(0));

        Assert.Null(Validator.Validate(prog));
        var expected = """
            entry init=-
            entry main=0
            struct 0 "Point" fields=2
              field "x"
              field "y"
            enum 0 "结果" <T> cases=2
              case 0 "Some" payloads=1
              case 1 "None" payloads=0
            global 0 "计数器" mut
            func 0 "main" captures=0 params=0 ret=0
              block 0:
                const.i64 1
                local.set 0
                local.get 0
                return 1
            """ + "\n";
        Assert.Equal(expected, IrDumper.DumpProgram(prog));
    }

    [Fact]
    public void Golden_extern_and_type_params_and_init()
    {
        var prog = TestSupport.Prog(
            structs: new StructDecl[]
            {
                new("Pair", new[] { "A", "B" }, new FieldDecl[] { new("first", null), new("second", null) }),
            },
            globals: new GlobalDecl[] { new("name", false, null) },
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Goto(new BlockId(1))),
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
                TestSupport.Fn("setup", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
                TestSupport.Fn("hostRead", 0, 1, true, externHost: "go:hostRead"),
            },
            init: new FunctionId(1),
            main: new FunctionId(0));

        Assert.Null(Validator.Validate(prog));
        var expected = """
            entry init=1
            entry main=0
            struct 0 "Pair" <A, B> fields=2
              field "first"
              field "second"
            global 0 "name" imm
            func 0 "main" captures=0 params=0 ret=0
              block 0:
                goto 1
              block 1:
                return 0
            func 1 "setup" captures=0 params=0 ret=0
              block 0:
                return 0
            func 2 "hostRead" extern="go:hostRead"
            """ + "\n";
        Assert.Equal(expected, IrDumper.DumpProgram(prog));
    }

    [Fact]
    public void Dump_is_deterministic_byte_for_byte()
    {
        var a = IrDumper.DumpProgram(TestSupport.FullFeature());
        var b = IrDumper.DumpProgram(TestSupport.FullFeature());
        Assert.Equal(a, b);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.True(a[i] == b[i], $"位置 {i} 不相等（确定性破坏）");
        }
    }

    [Fact]
    public void Full_feature_program_contains_every_inst_and_terminator_mnemonic()
    {
        var prog = TestSupport.FullFeature();
        Assert.Null(Validator.Validate(prog));
        var text = IrDumper.DumpProgram(prog);
        string[] mnemonics =
        {
            "const.i64 1", "const.f64 1.5", "const.bool true", "const.str \"héllo 🦀\"",
            "local.get 0", "local.set 0", "global.get 0", "global.set 0",
            "unary.neg", "binary.add", "pop",
            "call 1", "call.indirect argc=1 ret=1", "call.builtin \"println\" argc=1 ret=0",
            "call.builtin \"len\" argc=1 ret=1",
            "spawn 1 argc=1", "closure.new 2 captures=1",
            "index.get", "index.set", "slice.get",
            "field.get \"x\"", "field.set \"x\"", "array.push",
            "array.new 2", "map.new 1", "struct.new 0", "enum.new 0 0", "range.new",
            "box.new", "deref", "ref.set", "enum.payload.get 0",
            "iter.new", "iter.next 8",
            "chan.new", "chan.send", "chan.recv",
            "try err=7 fail=10",
            // 终接 6 条
            "branch 1 2", "switch.int 1->3 default=4", "switch.enum 0 0->5 1->6 default=-",
            "goto 7", "return 0", "unreachable",
        };
        foreach (var m in mnemonics)
        {
            Assert.True(text.Contains(m, StringComparison.Ordinal), $"缺助记符: {m}");
        }
    }

    [Fact]
    public void All_binops_and_unary_not_have_stable_mnemonics()
    {
        var insts = new List<Inst> { new Inst.ConstI64(2), new Inst.Unary(Unop.Not), new Inst.Pop() };
        foreach (var op in Enum.GetValues<Binop>())
        {
            insts.Add(new Inst.ConstI64(1));
            insts.Add(new Inst.ConstI64(1));
            insts.Add(new Inst.Binary(op));
            insts.Add(new Inst.Pop());
        }
        var fn = TestSupport.Fn("ops", 0, 0, false,
            blocks: new[] { TestSupport.B(insts.ToArray(), new Terminator.Return(false)) });
        Assert.Null(Validator.Validate(TestSupport.Prog(fns: new[] { fn })));
        var text = IrDumper.DumpFunction(fn, new FunctionId(3));
        string[] expected =
        {
            "unary.not", "binary.eq", "binary.ne", "binary.lt", "binary.le", "binary.gt", "binary.ge",
            "binary.add", "binary.sub", "binary.mul", "binary.div", "binary.rem",
            "binary.bit.and", "binary.bit.or", "binary.bit.xor", "binary.shl", "binary.shr",
        };
        foreach (var m in expected)
        {
            Assert.True(text.Contains(m, StringComparison.Ordinal), $"缺助记符: {m}");
        }
    }

    [Fact]
    public void String_escape_keeps_astral_code_points_intact()
    {
        // astral 对抗轴：🦀𝕏 占 2 个 char，转义不得拆开代理对；控制字符 \u{1} 用 rust 同款拼写。
        var fn = TestSupport.Fn("main", 0, 0, false,
            blocks: new[]
            {
                TestSupport.B(
                    new Inst[]
                    {
                        new Inst.ConstStr("a\"b\\c\nd🦀𝕏\u0001"),
                        new Inst.Pop(),
                    },
                    new Terminator.Return(false)),
            });
        var text = IrDumper.DumpFunction(fn, new FunctionId(0));
        Assert.Contains("const.str \"a\\\"b\\\\c\\nd🦀𝕏\\u{1}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Float_formatting_shortest_roundtrip_with_trailing_zero()
    {
        static string DumpConst(double v)
        {
            var fn = TestSupport.Fn("f", 0, 0, false,
                blocks: new[]
                {
                    TestSupport.B(new Inst[] { new Inst.ConstF64(v), new Inst.Pop() },
                        new Terminator.Return(false)),
                });
            return IrDumper.DumpFunction(fn, new FunctionId(0));
        }
        Assert.Contains("const.f64 1.5", DumpConst(1.5), StringComparison.Ordinal);
        Assert.Contains("const.f64 2.0", DumpConst(2.0), StringComparison.Ordinal);
        Assert.Contains("const.f64 -0.25", DumpConst(-0.25), StringComparison.Ordinal);
        Assert.Contains("const.f64 0.1", DumpConst(0.1), StringComparison.Ordinal);
        Assert.Contains("const.f64 inf", DumpConst(double.PositiveInfinity), StringComparison.Ordinal);
        Assert.Contains("const.f64 -inf", DumpConst(double.NegativeInfinity), StringComparison.Ordinal);
        Assert.Contains("const.f64 NaN", DumpConst(double.NaN), StringComparison.Ordinal);
    }

    [Fact]
    public void Dump_function_of_extern_has_no_blocks()
    {
        var fn = TestSupport.Fn("hostWrite", 0, 2, false, externHost: "go:hostWrite");
        var text = IrDumper.DumpFunction(fn, new FunctionId(7));
        Assert.Equal("func 7 \"hostWrite\" extern=\"go:hostWrite\"\n", text);
    }
}
