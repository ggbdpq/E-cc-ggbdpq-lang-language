using Lang.Core;
using Lang.Ir;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>内建三方对照表：20 行锁定「内建名 ↔ runtime BuiltinId ↔ 直接效果 ↔ IR 形态」。</summary>
public class BuiltinsTests
{
    [Fact]
    public void Table_has_exactly_20_rows_with_sequential_runtime_ids()
    {
        Assert.Equal(20, Builtins.Table.Count);
        for (uint i = 0; i < 20; i++)
        {
            Assert.Equal(i, Builtins.Table[(int)i].RuntimeId);
        }
    }

    [Fact]
    public void Table_names_are_unique()
    {
        var names = Builtins.Table.Select(e => e.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Form_split_is_17_call_plus_3_inst()
    {
        Assert.Equal(17, Builtins.Table.Count(e => e.Form == IrBuiltinForm.Call));
        Assert.Equal(3, Builtins.Table.Count(e => e.Form == IrBuiltinForm.Inst));
        // makeChan/send/recv 升为专指令，其余走 CallBuiltin。
        var instForm = Builtins.Table.Where(e => e.Form == IrBuiltinForm.Inst).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "makeChan", "send", "recv" }, instForm);
        Assert.Equal(14u, Builtins.Table.First(e => e.Name == "makeChan").RuntimeId);
        Assert.Equal(15u, Builtins.Table.First(e => e.Name == "send").RuntimeId);
        Assert.Equal(16u, Builtins.Table.First(e => e.Name == "recv").RuntimeId);
    }

    [Fact]
    public void Table_direct_effect_column_matches_builtin_direct_effect_mapping()
    {
        foreach (var e in Builtins.Table)
        {
            Assert.Equal(e.DirectEffect, Builtins.BuiltinDirectEffect(e.Name));
        }
    }

    [Fact]
    public void Direct_effect_mapping_freeze()
    {
        Assert.Equal(Effect.Io, Builtins.BuiltinDirectEffect("println"));
        Assert.Equal(Effect.Io, Builtins.BuiltinDirectEffect("print"));
        Assert.Equal(Effect.Net, Builtins.BuiltinDirectEffect("http.get"));
        Assert.Equal(Effect.Channel, Builtins.BuiltinDirectEffect("makeChan"));
        Assert.Equal(Effect.Channel, Builtins.BuiltinDirectEffect("send"));
        Assert.Equal(Effect.Channel, Builtins.BuiltinDirectEffect("recv"));
        Assert.Null(Builtins.BuiltinDirectEffect("len"));
        Assert.Null(Builtins.BuiltinDirectEffect("不存在🦀"));
        Assert.Null(Builtins.BuiltinDirectEffect(""));
    }

    [Fact]
    public void Effect_contract_order_and_text()
    {
        // 契约序：PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI（禁 alphabet/哈希序）。
        Assert.Equal(
            new[] { "PURE", "IO", "FS", "NET", "TIME", "RANDOM", "SPAWN", "CHANNEL", "FFI" },
            EffectExt.All.Select(e => e.AsStr()).ToArray());
    }
}
