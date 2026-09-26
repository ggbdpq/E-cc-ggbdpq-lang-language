using Lang.Contract;
using Lang.Core;
using Lang.Ir;
using Xunit;
using ContractEffect = Lang.Contract.Effect;
using IrEffect = Lang.Ir.Effect;

namespace Lang.Contract.Tests;

/// <summary>
/// 与 Lang.Ir 暂驻 Effect 族的对照测试（A3 过渡形态，见 docs/lang-cs/status/A3-hir-ir.md）。
/// 契约：A6 落地 Lang.Contract 后 A3 迁移为消费本程序集——迁移前名与契约序必须 1:1。
/// A3 完成迁移（删除 Lang.Ir.Effect）时本测试类同步退役。
/// </summary>
public class IrBridgeTests
{
    [Fact]
    public void Effect_enums_align_one_to_one()
    {
        // 契约序 9 变体逐位同名（PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI）。
        Assert.Equal(9, EffectText.All.Count);
        Assert.Equal(EffectText.All.Count, EffectExt.All.Count);
        for (var i = 0; i < EffectText.All.Count; i++)
        {
            Assert.Equal(EffectText.All[i].AsStr(), EffectExt.All[i].AsStr());
        }
    }

    [Fact]
    public void Builtin_mapping_agrees_with_lang_ir()
    {
        // 对照面 = Lang.Ir 三方对照表全量 20 名 + 宿主扩展名 + 未知名（含 astral）。
        var names = Builtins.Table.Select(b => b.Name)
            .Append("http.get")
            .Append("")
            .Append("未知内建🦀")
            .ToList();
        Assert.Equal(23, names.Count);

        foreach (var name in names)
        {
            // 双方枚举声明序同为契约序，名字即身份；null（纯内建）在两侧一致。
            var ir = Builtins.BuiltinDirectEffect(name)?.AsStr();
            var contract = Effects.BuiltinDirectEffect(name)?.AsStr();
            Assert.Equal(ir, contract);
        }
    }

    [Fact]
    public void Ir_effect_bearing_insts_match_spec_names()
    {
        // specs/agent-protocol.md §3：直接效果来自 IR（println/print→IO、http.get→NET、
        // makeChan/send/recv→CHANNEL、extern→FFI、go 语句→SPAWN）。
        static string[] NameOf(IReadOnlyList<IrEffect> effects) => [.. effects.Select(e => e.AsStr())];

        Assert.Equal(NameOf([IrEffect.Spawn]),
            NameOf(Builtins.InstDirectEffects(new Inst.Spawn(new FunctionId(0), 0))));
        Assert.Equal(NameOf([IrEffect.Channel]),
            NameOf(Builtins.InstDirectEffects(new Inst.ChanNew())));
        Assert.Equal(NameOf([IrEffect.Channel]),
            NameOf(Builtins.InstDirectEffects(new Inst.ChanSend())));
        Assert.Equal(NameOf([IrEffect.Channel]),
            NameOf(Builtins.InstDirectEffects(new Inst.ChanRecv())));
        Assert.Equal(NameOf([IrEffect.Io]),
            NameOf(Builtins.InstDirectEffects(new Inst.CallBuiltin("println", 1, false))));
        Assert.Equal(NameOf([IrEffect.Net]),
            NameOf(Builtins.InstDirectEffects(new Inst.CallBuiltin("http.get", 1, true))));
        Assert.Empty(Builtins.InstDirectEffects(new Inst.CallBuiltin("len", 1, true)));
    }

    [Fact]
    public void Contract_engine_consumes_ir_facts()
    {
        // IR 接线形态预演：IR 直接效果事实（数值 = 两侧同为契约序，cast 保名）经
        // InstDirectEffects/FnDirectEffects 桥进 CallGraph.SetDirect，不动点结果符合推导规则。
        var g = new CallGraph();
        g.AddFn(new FunctionId(0), "hostWrite"); // extern 形（FnDirectEffects → FFI）
        g.AddFn(new FunctionId(1), "worker");    // go 目标
        g.AddFn(new FunctionId(2), "main");      // go 语句所在函数
        var externFn = new IrFunction("hostWrite", 0, new FnSig(1, false),
            Array.Empty<LocalDecl>(), Array.Empty<Block>(), "go:hostWrite");
        foreach (var e in Builtins.FnDirectEffects(externFn))
        {
            g.SetDirect(new FunctionId(0), (ContractEffect)(int)e);
        }
        foreach (var e in Builtins.InstDirectEffects(new Inst.Spawn(new FunctionId(1), 0)))
        {
            g.SetDirect(new FunctionId(2), (ContractEffect)(int)e);
        }
        g.SetBuiltinCall(new FunctionId(1), "println");
        g.AddCall(new FunctionId(2), new FunctionId(1));

        var outp = g.Solve();

        Assert.Equal([ContractEffect.Ffi], outp[0].Effects);
        Assert.Equal([ContractEffect.Io], outp[1].Effects);
        Assert.Equal([ContractEffect.Io, ContractEffect.Spawn], outp[2].Effects);
    }
}
