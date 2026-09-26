namespace Lang.Runtime.Tests;

/// <summary>
/// 定义表（A2 对齐契约）：预载 id 顺序为契约的一部分——
/// error=StructDefId(0)、Option=EnumDefId(0)、Result=EnumDefId(1)。
/// </summary>
public class DefsTests
{
    [Fact]
    public void PreludeIds_AreStable()
    {
        var defs = DefTables.WithPrelude();
        Assert.Equal(new StructDefId(0), defs.ErrorDef());
        Assert.Equal(new EnumDefId(0), defs.OptionDef());
        Assert.Equal(new EnumDefId(1), defs.ResultDef());
        // 手算真值：Option 恰有 Some/None 两 case，None 为 1 号（isNone 语义依赖此序）
        Assert.Equal(new[] { "Some", "None" }, T.Ok(defs.EnumDef(defs.OptionDef())).Cases);
        Assert.Equal(new[] { "Ok", "Err" }, T.Ok(defs.EnumDef(defs.ResultDef())).Cases);
        Assert.Equal(new[] { "msg" }, T.Ok(defs.StructDef(defs.ErrorDef())).Fields);
    }

    [Fact]
    public void Register_AppendsStableIds()
    {
        var defs = DefTables.WithPrelude();
        var s = defs.RegisterStruct("P", new[] { "v" });
        var e = defs.RegisterEnum("Color", new[] { "Red" });
        Assert.Equal(new StructDefId(1), s);
        Assert.Equal(new EnumDefId(2), e);
        Assert.Equal("P", T.Ok(defs.StructDef(s)).Name);
        Assert.Equal("Color", T.Ok(defs.EnumDef(e)).Name);
        // 预载 id 不因追加漂移
        Assert.Equal(new EnumDefId(0), defs.OptionDef());
    }

    [Fact]
    public void Register_AstralNamesKept()
    {
        var defs = DefTables.WithPrelude();
        var s = defs.RegisterStruct("点🦀", new[] { "坐标𝕏" });
        var def = T.Ok(defs.StructDef(s));
        Assert.Equal("点🦀", def.Name);
        Assert.Equal(new[] { "坐标𝕏" }, def.Fields);
    }

    [Fact]
    public void OobId_IsInternalError()
    {
        var defs = DefTables.WithPrelude();
        Assert.Equal(RtErrorKind.Internal, T.Err(defs.StructDef(new StructDefId(99))).Kind);
        Assert.Equal(RtErrorKind.Internal, T.Err(defs.EnumDef(new EnumDefId(99))).Kind);
    }
}
