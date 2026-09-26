using Lang.Contract;
using Lang.Core;
using Xunit;

namespace Lang.Contract.Tests;

/// <summary>
/// Effects fixpoint 专项（docs/lang-rs 对抗验证重点 Effects 节 1–9 + EFFECT-001）。
/// 8 条对抗：直接/1 级/3 级/递归/互递归/spawn/channel/纯环 + 契约序/确定性/并集/映射表/良构性。
/// </summary>
public class EffectsTests
{
    private static FunctionId Id(uint n) => new(n);

    /// <summary>对抗 1：直接效果与纯函数缺省 PURE。</summary>
    [Fact]
    public void Direct_effect_and_default_pure()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "show");
        g.AddFn(Id(2), "add");
        g.SetBuiltinCall(Id(1), "println");

        var outp = g.Solve();

        Assert.Equal(
        [
            new EffectEntry(Id(1), "show", [Effect.Io]),
            new EffectEntry(Id(2), "add", [Effect.Pure]),
        ],
            outp);
    }

    /// <summary>对抗 2：1 级传播。</summary>
    [Fact]
    public void One_level_propagation()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "main");
        g.AddFn(Id(2), "helper");
        g.SetBuiltinCall(Id(2), "println");
        g.AddCall(Id(1), Id(2));

        var outp = g.Solve();

        Assert.Equal([Effect.Io], outp[0].Effects);
        Assert.Equal([Effect.Io], outp[1].Effects);
    }

    /// <summary>对抗 3：3 级传播（a→b→c→NET 全链获得）。</summary>
    [Fact]
    public void Three_level_propagation()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "a");
        g.AddFn(Id(2), "b");
        g.AddFn(Id(3), "c");
        g.SetBuiltinCall(Id(3), "http.get");
        g.AddCall(Id(1), Id(2));
        g.AddCall(Id(2), Id(3));

        foreach (var e in g.Solve())
        {
            Assert.Equal([Effect.Net], e.Effects);
        }
    }

    /// <summary>对抗 4：自递归收敛（单调格保证不动点）。</summary>
    [Fact]
    public void Recursion_converges()
    {
        var g = new CallGraph();
        g.AddFn(Id(10), "fact");
        g.SetBuiltinCall(Id(10), "println");
        g.AddCall(Id(10), Id(10));

        var outp = g.Solve();

        var entry = Assert.Single(outp);
        Assert.Equal([Effect.Io], entry.Effects);
    }

    /// <summary>对抗 5：互递归——效果经环双向传播。</summary>
    [Fact]
    public void Mutual_recursion_shares_effects()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "isEven");
        g.AddFn(Id(2), "isOdd");
        g.SetBuiltinCall(Id(2), "recv");
        g.AddCall(Id(1), Id(2));
        g.AddCall(Id(2), Id(1));

        foreach (var e in g.Solve())
        {
            Assert.Equal([Effect.Channel], e.Effects);
        }
    }

    /// <summary>对抗 6：go 语句 = 直接 SPAWN + 被调效果保守并入；契约序 IO &lt; SPAWN。</summary>
    [Fact]
    public void Spawn_gives_spawn_plus_callee_effects()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "main");
        g.AddFn(Id(2), "worker");
        g.SetBuiltinCall(Id(2), "println");
        // go worker()：直接 SPAWN（IR 接线方挂）+ 调用边（保守传播）
        g.SetDirect(Id(1), Effect.Spawn);
        g.AddCall(Id(1), Id(2));

        var outp = g.Solve();

        Assert.Equal([Effect.Io, Effect.Spawn], outp[0].Effects);
        Assert.Equal([Effect.Io], outp[1].Effects);
    }

    /// <summary>对抗 7：channel 三内建全部落在 CHANNEL。</summary>
    [Fact]
    public void Channel_builtins()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "produce");
        g.SetBuiltinCall(Id(1), "makeChan");
        g.SetBuiltinCall(Id(1), "send");
        g.SetBuiltinCall(Id(1), "recv");

        var outp = g.Solve();

        Assert.Equal([Effect.Channel], outp[0].Effects);
    }

    /// <summary>对抗 8：纯环——互调无效果，双方保持 PURE（不得凭空长效果）。</summary>
    [Fact]
    public void Pure_cycle_stays_pure()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "ping");
        g.AddFn(Id(2), "pong");
        g.AddCall(Id(1), Id(2));
        g.AddCall(Id(2), Id(1));

        foreach (var e in g.Solve())
        {
            Assert.Equal([Effect.Pure], e.Effects);
        }
    }

    [Fact]
    public void Effects_emitted_in_contract_order()
    {
        // 输出去重按契约序，与登记顺序无关（禁迭代序外泄）。
        var g = new CallGraph();
        g.AddFn(Id(1), "m");
        g.SetDirect(Id(1), Effect.Ffi);
        g.SetDirect(Id(1), Effect.Io);
        g.SetDirect(Id(1), Effect.Spawn);

        var outp = g.Solve();

        Assert.Equal([Effect.Io, Effect.Spawn, Effect.Ffi], outp[0].Effects);
    }

    [Fact]
    public void Solve_is_insertion_order_independent()
    {
        // 同一内容、不同登记顺序 → 求解输出逐项相等（确定性）。
        static IReadOnlyList<EffectEntry> Build(bool reversed)
        {
            var g = new CallGraph();
            var order = reversed ? new[] { 3u, 2u, 1u } : new[] { 1u, 2u, 3u };
            foreach (var n in order)
            {
                g.AddFn(new FunctionId(n), $"f{n}");
            }
            g.SetBuiltinCall(Id(3), "println");
            g.AddCall(Id(1), Id(2));
            g.AddCall(Id(2), Id(3));
            return g.Solve();
        }

        Assert.Equal(Build(false), Build(true));
    }

    [Fact]
    public void Caller_unions_all_callee_effects()
    {
        // 混合传播：多个 callee 的效果并集进入 caller。
        var g = new CallGraph();
        g.AddFn(Id(1), "main");
        g.AddFn(Id(2), "log");
        g.AddFn(Id(3), "fetch");
        g.SetBuiltinCall(Id(2), "println");
        g.SetBuiltinCall(Id(3), "http.get");
        g.AddCall(Id(1), Id(2));
        g.AddCall(Id(1), Id(3));

        var outp = g.Solve();

        Assert.Equal([Effect.Io, Effect.Net], outp[0].Effects);
    }

    [Fact]
    public void Builtin_mapping_table()
    {
        // 内建映射表锁定（extern 形状的 FFI 由调用方 SetDirect——IR 接线点）。
        Assert.Equal(Effect.Io, Effects.BuiltinDirectEffect("println"));
        Assert.Equal(Effect.Io, Effects.BuiltinDirectEffect("print"));
        Assert.Equal(Effect.Net, Effects.BuiltinDirectEffect("http.get"));
        Assert.Equal(Effect.Channel, Effects.BuiltinDirectEffect("makeChan"));
        Assert.Equal(Effect.Channel, Effects.BuiltinDirectEffect("send"));
        Assert.Equal(Effect.Channel, Effects.BuiltinDirectEffect("recv"));
        Assert.Null(Effects.BuiltinDirectEffect("len"));
        Assert.Null(Effects.BuiltinDirectEffect("未知内建🦀"));

        var g = new CallGraph();
        g.AddFn(Id(1), "hostWrite");
        g.SetDirect(Id(1), Effect.Ffi);
        Assert.Equal([Effect.Ffi], g.Solve()[0].Effects);
    }

    [Fact]
    public void Rejects_unregistered_and_duplicate_nodes()
    {
        var g = new CallGraph();
        g.AddFn(Id(1), "a");

        var dup = Assert.Throws<EffectsGraphException>(() => g.AddFn(Id(1), "a2"));
        Assert.Equal(EffectsErrorKind.DuplicateFn, dup.Kind);
        var unknownCallee = Assert.Throws<EffectsGraphException>(() => g.AddCall(Id(1), Id(2)));
        Assert.Equal(EffectsErrorKind.UnknownFn, unknownCallee.Kind);
        var unknownCaller = Assert.Throws<EffectsGraphException>(() => g.AddCall(Id(9), Id(1)));
        Assert.Equal(EffectsErrorKind.UnknownFn, unknownCaller.Kind);
        var unknownDirect = Assert.Throws<EffectsGraphException>(() => g.SetDirect(Id(9), Effect.Io));
        Assert.Equal(EffectsErrorKind.UnknownFn, unknownDirect.Kind);
    }
}
