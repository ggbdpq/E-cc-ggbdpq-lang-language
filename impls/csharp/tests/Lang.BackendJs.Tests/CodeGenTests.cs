using Lang.Core;
using Lang.Ir;

namespace Lang.BackendJs.Tests;

/// <summary>发射面形状断言 + 确定性 + 常量发射形单元（对照 lang-rs e2e 同批口径）。</summary>
public class CodeGenTests
{
    [Fact]
    public void Fixture_validates_and_generation_is_deterministic()
    {
        var prog = Fixture.Build();
        Assert.Null(Validator.Validate(prog));
        Assert.True(CodeGen.TryGenerate(prog, out var a, out var errA), errA?.Message);
        Assert.True(CodeGen.TryGenerate(prog, out var b, out var errB), errB?.Message);
        Assert.Equal(a, b); // 同 IR 两次生成必须逐字节相等
        Assert.Contains("function lf6_main(...as) {", a);
        Assert.Contains("let __pc = 0;", a);
    }

    [Fact]
    public void Emission_covers_fixture_shape()
    {
        Assert.True(CodeGen.TryGenerate(Fixture.Build(), out var src, out var err), err?.Message);
        // 函数名下标化 + 状态机骨架
        Assert.Contains("function lf2_triple(...as) {", src);
        Assert.Contains("case 6: {", src);
        // 全局 var + init 调用 + 程序入口
        Assert.Contains("var g0_counter = undefined;", src);
        Assert.Contains("lf0_init();", src);
        Assert.Contains("lf6_main();", src);
        // enum 构造经 langGoEnumVal；int 常量为 BigInt 字面量（含负数带括号形）
        Assert.Contains("langGoEnumVal(\"Option\", \"Some\"", src);
        Assert.Contains("(-1n)", src);
        // spawn：同步确定子集 = 直接调用 + 实参逐个拷贝快照（TASK-SPAWN-002）
        Assert.Contains("lf5_send_task(langGoCopy(l21), langGoCopy(l20));", src);
        // Try：Err 存槽跳 fail 块（改写 __pc）
        Assert.Contains("if (langGoIsErr(_t", src);
        Assert.Contains("__pc = 24; break;", src);
        // B2 教训：ArrayPush 发射拼接形
        Assert.Contains("langGoPushArr(", src);
        // B3 对应面：RangeNew 值对象（JS 动态域无需 int 断言）
        Assert.Contains("{ __langGoRange: true, Lo: 1n, Hi: 4n }", src);
        // B1 写链教训：数组元素 struct 链写的 IndexGet 发射 Ref 读形
        Assert.Contains("langGoIndexRef(", src);
        // SwitchInt：BigInt 比较
        Assert.Contains("=== 1n) { __pc = 21; break; }", src);
    }

    [Fact]
    public void Float_literal_forms()
    {
        Assert.Equal("1.5e0", CodeGen.JsFloatLit(1.5));
        Assert.Equal("2.5e-1", CodeGen.JsFloatLit(0.25));
        Assert.Equal("-1.75e2", CodeGen.JsFloatLit(-175.0));
        Assert.Equal("NaN", CodeGen.JsFloatLit(double.NaN));
        Assert.Equal("Infinity", CodeGen.JsFloatLit(double.PositiveInfinity));
        Assert.Equal("-Infinity", CodeGen.JsFloatLit(double.NegativeInfinity));
        Assert.Equal("0", CodeGen.JsFloatLit(0.0));
        Assert.Equal("-0", CodeGen.JsFloatLit(-0.0));
    }

    [Fact]
    public void String_literal_forms()
    {
        // JSON 转义面、无 HTML 转义（源码字面量解析后内容不变）；astral 原样
        Assert.Equal("\"ab\\n\\\"q\\\\\"", CodeGen.JsStrLit("ab\n\"q\\"));
        Assert.Equal("\"🦀𝕏\"", CodeGen.JsStrLit("🦀𝕏"));
        Assert.Equal("\"\\u0001\"", CodeGen.JsStrLit("\u0001"));
        Assert.Equal("\"<b>&\"", CodeGen.JsStrLit("<b>&"));
    }

    [Fact]
    public void Ident_sanitizing()
    {
        Assert.Equal("a_b", CodeGen.SanitizeIdent("a-b"));
        Assert.Equal("a𝕏b", CodeGen.SanitizeIdent("a𝕏b"));
    }

    [Fact]
    public void Unknown_builtin_is_controlled_error_not_exception()
    {
        var prog = new IrProgram(
            [], [], [],
            [new IrFunction("f", 0, new FnSig(0, false), [],
                [new Block([new Inst.CallBuiltin("nope", 0, false)], new Terminator.Return(false))], null)],
            null, null);
        Assert.False(CodeGen.TryGenerate(prog, out var src, out var err));
        Assert.Null(src);
        Assert.NotNull(err);
        Assert.Contains("未知内建 nope", err.Message);
    }
}
