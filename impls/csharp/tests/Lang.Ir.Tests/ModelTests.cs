using Lang.Core;
using Lang.Hir;
using Lang.Ir;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>模型层：同构造两次逐字段相等（确定性契约）+ lowering 入口签名形状。</summary>
public class ModelTests
{
    [Fact]
    public void Full_feature_program_built_twice_is_value_equal()
    {
        var a = TestSupport.FullFeature();
        var b = TestSupport.FullFeature();
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Value_equality_distinguishes_mutations()
    {
        var a = TestSupport.FullFeature();
        var b = TestSupport.FullFeature();
        Assert.NotEqual(a, b with { Main = new FunctionId(1) });
        Assert.NotEqual(a, b with { Init = new FunctionId(1) });

        var fns = b.Functions.ToArray();
        fns[0] = fns[0] with { Name = "main2" };
        Assert.NotEqual(a, b with { Functions = fns });

        // 块内指令差异也可感知（SwitchInt 臂表走逐元素相等）。
        var blocks = fns[0].Blocks.ToArray();
        blocks[1] = blocks[1] with
        {
            Term = new Terminator.SwitchInt(new[] { (2L, new BlockId(3)) }, new BlockId(4)),
        };
        fns[0] = fns[0] with { Blocks = blocks };
        Assert.NotEqual(a, b with { Functions = fns });
    }

    [Fact]
    public void Inst_sample_table_covers_37_variants()
    {
        // 指令集面：37 变体（5 带效果 + 32 纯）；与 lang-rs 逐变体 1:1。
        Assert.Equal(37, TestSupport.AllInstSamples().Count);
        Assert.Equal(37, TestSupport.AllInstSamples().Select(i => i.GetType()).Distinct().Count());
    }

    [Fact]
    public void Entry_points_and_lower_error_shapes()
    {
        var ep = new EntryPoints(new FunctionId(1), new FunctionId(0));
        Assert.Equal(1u, ep.Init!.Value.Value);
        Assert.Equal(0u, ep.Main!.Value.Value);
        Assert.Null(new EntryPoints(null, null).Init);

        var err = new IrLowerError("ctor 符号无法映射");
        Assert.Contains("ctor 符号无法映射", err.ToString());

        var ok = LowerResult<IrProgram>.Ok(TestSupport.FullFeature());
        Assert.True(ok.IsOk);
        Assert.Null(ok.Error);
        var fail = LowerResult<IrProgram>.Fail("坏 HIR");
        Assert.False(fail.IsOk);
        Assert.Null(fail.Value);
    }
}
