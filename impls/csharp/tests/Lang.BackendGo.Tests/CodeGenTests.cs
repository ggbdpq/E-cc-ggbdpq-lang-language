using Lang.Core;
using Lang.Ir;

namespace Lang.BackendGo.Tests;

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
        Assert.StartsWith("package main", a);
        Assert.Contains("func lf6_main(as ...any) any {", a);
        Assert.Contains("langGoWaitAll()", a);
    }

    [Fact]
    public void Emission_covers_fixture_shape()
    {
        Assert.True(CodeGen.TryGenerate(Fixture.Build(), out var src, out var err), err?.Message);
        // 函数名下标化（防撞名/关键字）
        Assert.Contains("func lf2_triple(as ...any) any {", src);
        // struct：Go 类型改名 + 原名打印 + 动态字段接口
        Assert.Contains("type S0_P struct {", src);
        Assert.Contains("\"P\" + \"{\" + strings.Join", src);
        Assert.Contains("case \"x\":", src);
        // 全局 var + init
        Assert.Contains("var g0_counter any", src);
        Assert.Contains("lf0_init()", src);
        // enum 构造经 langGoEnumVal
        Assert.Contains("langGoEnumVal(\"Option\", \"Some\"", src);
        // spawn：实参快照 []any（EVAL-GO-007）
        Assert.Contains("langGoGo(func(sa ...any) { lf5_send_task(sa...) }, []any{", src);
        // Try：Err 存槽跳 fail 块
        Assert.Contains("if langGoIsErr(_t", src);
        // B2 教训：ArrayPush 发射拼接形
        Assert.Contains("langGoPushArr(", src);
        // B3 教训：RangeNew 操作数经 langGoInt 断言
        Assert.Contains("langGoRange{Lo: langGoInt(", src);
        // B1 写链教训：数组元素 struct 链写的 IndexGet 发射 Ref 读形
        Assert.Contains("langGoIndexRef(", src);
        // has_ret=false 的调用（println）按语句发射，不当表达式压栈
        Assert.Contains("\n\t\tlangGoPrintln(", src);
    }

    [Fact]
    public void Float_literal_forms()
    {
        // Rust {:e} 同形（最短尾数 + e 指数无填充），float64(...) 包装
        Assert.Equal("float64(1.5e0)", CodeGen.GoFloatLit(1.5));
        Assert.Equal("float64(2.5e-1)", CodeGen.GoFloatLit(0.25));
        Assert.Equal("float64(1e-5)", CodeGen.GoFloatLit(1e-5));
        Assert.Equal("float64(-1.75e2)", CodeGen.GoFloatLit(-175.0));
        Assert.Equal("math.NaN()", CodeGen.GoFloatLit(double.NaN));
        Assert.Equal("math.Inf(1)", CodeGen.GoFloatLit(double.PositiveInfinity));
        Assert.Equal("math.Inf(-1)", CodeGen.GoFloatLit(double.NegativeInfinity));
        Assert.Equal("float64(0)", CodeGen.GoFloatLit(0.0));
        Assert.Equal("math.Copysign(0, -1)", CodeGen.GoFloatLit(-0.0));
    }

    [Fact]
    public void Ident_sanitizing_and_field_names()
    {
        // 消毒：非字母数字/下划线 → _（按码点；𝕏 是数学字母类保留，🦀 是符号转 _）
        Assert.Equal("a_b", CodeGen.SanitizeIdent("a-b"));
        Assert.Equal("_𝕏", CodeGen.SanitizeIdent("🦀𝕏"));
        Assert.Equal("a𝕏b", CodeGen.SanitizeIdent("a𝕏b"));
        // 关键字/数字开头加 X 前缀（经发射面验证：字段名 type → Xtype）
        var prog = new IrProgram(
            [new StructDecl("S", [], [new FieldDecl("type", null), new FieldDecl("1f", null)])],
            [], [], [], null, null);
        Assert.True(CodeGen.TryGenerate(prog, out var src, out var err), err?.Message);
        Assert.Contains("Xtype any", src);
        Assert.Contains("X1f any", src);
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
