using Lang.Core;

namespace Lang.Runtime.Tests;

/// <summary>
/// 环境 arena（specs/values.md VALUE-CAPTURE-004）：cell 语义、按引用捕获、
/// loop 每迭代子环境、let/var 可变性与遮蔽。
/// </summary>
public class EnvTests
{
    private static long ReadInt(EnvArena envs, EnvId env, uint slot) =>
        Assert.IsType<Value.Int>(T.Ok(envs.Get(env, slot))).V;

    // —— VALUE-CAPTURE-004 / conformance: closure-capture-by-reference ——

    [Fact]
    public void Capture_SharesOwnerCell_ClosureCounter()
    {
        // var n = 0；闭包体「n = n + 1」三次 → 定义处读 3（按引用捕获的计数器语义）
        var envs = new EnvArena();
        var main = envs.Root();
        var nSlot = T.Ok(envs.Define(main, "n", T.I(0), mutable: true));

        // 闭包捕获 = 记录定义处 EnvId；名字解析返回属主 cell
        var (owner, ownerSlot) = T.Ok(envs.Lookup(main, "n"))!.Value;
        Assert.Equal(main, owner);
        Assert.Equal(nSlot, ownerSlot);

        for (var expected = 1L; expected <= 3; expected++)
        {
            // 闭包体：经属主 cell 读旧值、写新值（不产生副本）
            var cur = Assert.IsType<Value.Int>(T.Ok(envs.Get(owner, ownerSlot))).V;
            T.Ok(envs.Set(owner, ownerSlot, T.I(cur + 1)));
            Assert.Equal(expected, ReadInt(envs, main, nSlot));
        }
    }

    [Fact]
    public void LoopCapture_FreshCellPerIteration()
    {
        // for 循环每迭代新子环境：迭代内闭包捕获的 i 互不串扰
        // 手算：迭代0 定义 i=0，迭代1 定义 i=1；迭代0 闭包读 i 恒为 0
        var envs = new EnvArena();
        var main = envs.Root();
        var iter0 = envs.Child(main);
        var iter1 = envs.Child(main);
        var s0 = T.Ok(envs.Define(iter0, "i", T.I(0), mutable: false));
        var s1 = T.Ok(envs.Define(iter1, "i", T.I(1), mutable: true));

        Assert.Equal(0, ReadInt(envs, iter0, s0));
        // 迭代 1 的 i 变化不影响迭代 0 的捕获
        T.Ok(envs.Set(iter1, s1, T.I(99)));
        Assert.Equal(0, ReadInt(envs, iter0, s0));
        Assert.Equal(99, ReadInt(envs, iter1, s1));
    }

    [Fact]
    public void LetCellImmutable_VarCellMutable()
    {
        var envs = new EnvArena();
        var root = envs.Root();
        var lv = T.Ok(envs.Define(root, "a", T.I(1), mutable: false));
        var vv = T.Ok(envs.Define(root, "b", T.I(1), mutable: true));
        Assert.Equal(RtErrorKind.ImmutableAssign, T.Err(envs.Set(root, lv, T.I(2))).Kind);
        T.Ok(envs.Set(root, vv, T.I(2)));
        Assert.Equal(2, ReadInt(envs, root, vv));
    }

    [Fact]
    public void Lookup_WalksParentChain_AndReturnsOwnerWithShadowing()
    {
        var envs = new EnvArena();
        var root = envs.Root();
        var mid = envs.Child(root);
        var leaf = envs.Child(mid);
        var rs = T.Ok(envs.Define(root, "g", T.I(1), mutable: true));
        // 叶子解析全局变量 → 属主是根
        Assert.Equal((root, rs), T.Ok(envs.Lookup(leaf, "g")));
        // 就近遮蔽：叶子层重定义后解析到叶子（对抗重点：shadowed capture）
        var ls = T.Ok(envs.Define(leaf, "g", T.I(3), mutable: true));
        Assert.Equal((leaf, ls), T.Ok(envs.Lookup(leaf, "g")));
        Assert.Null(T.Ok(envs.Lookup(leaf, "不存在")));
    }

    [Fact]
    public void Lookup_AstralName()
    {
        var envs = new EnvArena();
        var root = envs.Root();
        var child = envs.Child(root);
        var slot = T.Ok(envs.Define(root, "计数🦀", T.I(7), mutable: true));
        Assert.Equal((root, slot), T.Ok(envs.Lookup(child, "计数🦀")));
        T.Ok(envs.Set(root, slot, T.S("𝕏")));
        Assert.Equal(T.S("𝕏"), T.Ok(envs.Get(root, slot)));
    }

    [Fact]
    public void DuplicateDefine_RejectedAsScopeError()
    {
        var envs = new EnvArena();
        var root = envs.Root();
        T.Ok(envs.Define(root, "x", Value.Unit.Default, mutable: true));
        Assert.Equal(RtErrorKind.Scope, T.Err(envs.Define(root, "x", Value.Unit.Default, mutable: true)).Kind);
    }

    [Fact]
    public void Redefine_OverwritesValueAndFreezesBinding()
    {
        // fn 声明语句可重复执行：后者覆盖前者（与证据 env.redefine 一致）
        var envs = new EnvArena();
        var root = envs.Root();
        var f1 = new Value.Closure(new FunctionId(0), root);
        var f2 = new Value.Closure(new FunctionId(1), root);
        var s1 = T.Ok(envs.Define(root, "f", f1, mutable: false));
        var s2 = T.Ok(envs.Redefine(root, "f", f2));
        Assert.Equal(s1, s2);
        Assert.Equal(f2, T.Ok(envs.Get(root, s2)));
        // 覆盖后的 cell 恒不可变
        Assert.False(T.Ok(envs.CellAt(root, s2)).Mutable);
        Assert.Equal(RtErrorKind.ImmutableAssign, T.Err(envs.Set(root, s2, T.I(1))).Kind);
    }
}
