using Lang.Core;
using Lang.Ir;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>
/// 直接效果提取（A6 效果引擎对接面）：5 条带效果指令逐条 + 32 条无效果指令逐条枚举 + extern。
/// </summary>
public class EffectTests
{
    private static IrProgram ProgWithTables() => TestSupport.FullFeature();

    [Fact]
    public void Spawn_yields_spawn_effect()
    {
        var got = Builtins.InstDirectEffects(new Inst.Spawn(new FunctionId(0), 0));
        Assert.Equal(new[] { Effect.Spawn }, got);
    }

    [Fact]
    public void Chan_instructions_yield_channel_effect()
    {
        Assert.Equal(new[] { Effect.Channel }, Builtins.InstDirectEffects(new Inst.ChanNew()));
        Assert.Equal(new[] { Effect.Channel }, Builtins.InstDirectEffects(new Inst.ChanSend()));
        Assert.Equal(new[] { Effect.Channel }, Builtins.InstDirectEffects(new Inst.ChanRecv()));
    }

    [Fact]
    public void CallBuiltin_effect_via_mapping()
    {
        Assert.Equal(new[] { Effect.Io }, Builtins.InstDirectEffects(new Inst.CallBuiltin("println", 1, false)));
        Assert.Equal(new[] { Effect.Io }, Builtins.InstDirectEffects(new Inst.CallBuiltin("print", 1, false)));
        Assert.Equal(new[] { Effect.Net }, Builtins.InstDirectEffects(new Inst.CallBuiltin("http.get", 1, true)));
        Assert.Equal(new[] { Effect.Channel }, Builtins.InstDirectEffects(new Inst.CallBuiltin("makeChan", 0, true)));
        Assert.Empty(Builtins.InstDirectEffects(new Inst.CallBuiltin("len", 1, true)));
        Assert.Empty(Builtins.InstDirectEffects(new Inst.CallBuiltin("未知内建🦀", 1, false)));
    }

    [Fact]
    public void All_32_non_effect_instructions_yield_no_effect()
    {
        var effectBearing = new HashSet<Type>
        {
            typeof(Inst.Spawn), typeof(Inst.ChanNew), typeof(Inst.ChanSend),
            typeof(Inst.ChanRecv), typeof(Inst.CallBuiltin),
        };
        var samples = TestSupport.AllInstSamples();
        // 覆盖面守卫：样本表 37 条 = 5 带效果 + 32 无效果。
        Assert.Equal(37, samples.Count);
        int pureCount = 0;
        foreach (var inst in samples)
        {
            if (effectBearing.Contains(inst.GetType()))
            {
                continue;
            }
            pureCount++;
            Assert.True(Builtins.InstDirectEffects(inst).Count == 0, $"{inst} 应无直接效果（保守缺省为纯）");
        }
        Assert.Equal(32, pureCount);
    }

    [Fact]
    public void Extern_fn_yields_ffi_and_normal_fn_empty()
    {
        var externFn = TestSupport.Fn("hostWrite", 0, 1, false, externHost: "go:hostWrite");
        Assert.Equal(new[] { Effect.Ffi }, Builtins.FnDirectEffects(externFn));

        var normal = TestSupport.Fn("f", 0, 0, false,
            blocks: new[] { TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)) });
        Assert.Empty(Builtins.FnDirectEffects(normal));
    }

    [Fact]
    public void Full_feature_program_effect_bearing_instruction_count()
    {
        // 全覆盖程序里带效果指令：spawn 1 + chan 三专指令各 1 + println 1（http.get 未用）。
        var prog = ProgWithTables();
        int effectInsts = 0;
        foreach (var fn in prog.Functions)
        {
            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Insts)
                {
                    effectInsts += Builtins.InstDirectEffects(inst).Count;
                }
            }
        }
        Assert.Equal(5, effectInsts);
    }
}
