using Lang.Core;

namespace Lang.Contract;

/// <summary>
/// Effects fixpoint 引擎（specs/runtime.md EFFECT-001 + specs/agent-protocol.md §3 Effect）。
///
/// 效果是<b>元数据</b>，不是约束系统：无效果类型注解、无静态效果检查。
/// 本引擎只做「直接效果 → 调用图 → 不动点」三步；输入是泛型调用图
/// （节点 = FunctionId + 函数名，边 = 调用/go 目标），不接语义核——
/// 直接效果从 IR 的提取（println→IO、go 语句→SPAWN 等）由 IR 接线方调用
/// <see cref="Effects.BuiltinDirectEffect"/> 与 <see cref="CallGraph.SetDirect"/> 完成。
///
/// 传播语义（保守）：caller 的效果集 ⊇ 全部被调函数的效果集；
/// <c>go f()</c> 产生直接 SPAWN，并对 f 的调用边照常传播（并发效果的保守近似）。
/// 格是 {9 个效果} 的幂集，单调增长必有不动点——递归/互递归/纯环都收敛。
/// </summary>

/// <summary>
/// 效果枚举（枚举数值序 = 声明序 = <see cref="EffectText.All"/> 契约序：
/// PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI）。
/// FS/TIME/RANDOM 为保留枚举——当前语言无生产者（specs/runtime.md EFFECT-001）。
/// </summary>
public enum Effect
{
    Pure,
    Io,
    Fs,
    Net,
    Time,
    Random,
    Spawn,
    Channel,
    Ffi,
}

public static class EffectText
{
    /// <summary>固定契约顺序（输出去重按此序；禁 alphabet/哈希序）。</summary>
    public static readonly IReadOnlyList<Effect> All =
    [
        Effect.Pure, Effect.Io, Effect.Fs, Effect.Net, Effect.Time,
        Effect.Random, Effect.Spawn, Effect.Channel, Effect.Ffi,
    ];

    public static string AsStr(this Effect e) => e switch
    {
        Effect.Pure => "PURE",
        Effect.Io => "IO",
        Effect.Fs => "FS",
        Effect.Net => "NET",
        Effect.Time => "TIME",
        Effect.Random => "RANDOM",
        Effect.Spawn => "SPAWN",
        Effect.Channel => "CHANNEL",
        Effect.Ffi => "FFI",
        _ => throw new InvalidOperationException("Effect 变体未穷尽"),
    };
}

/// <summary>效果面公共入口（内建名映射；extern→FFI 与 go→SPAWN 来自 IR 结构，由接线方 SetDirect 挂）。</summary>
public static class Effects
{
    /// <summary>内建调用名 → 直接效果（specs/agent-protocol.md §3 推导规则；未列出的内建视为 PURE）。</summary>
    public static Effect? BuiltinDirectEffect(string builtin) => builtin switch
    {
        "println" or "print" => Effect.Io,
        "http.get" => Effect.Net,
        "makeChan" or "send" or "recv" => Effect.Channel,
        _ => null,
    };
}

/// <summary>单个函数的效果查询条目（effects 数组按契约序去重；无效果时为 [PURE]）。
/// 集合成员按序列结构比较（record 默认按引用，这里覆写为逐元素）。</summary>
public sealed record EffectEntry(FunctionId Id, string Name, IReadOnlyList<Effect> Effects)
{
    public bool Equals(EffectEntry? other) =>
        other is not null && Id.Equals(other.Id) && Name == other.Name
            && Effects.SequenceEqual(other.Effects);

    public override int GetHashCode()
    {
        var h = new System.HashCode();
        h.Add(Id);
        h.Add(Name);
        foreach (var e in Effects)
        {
            h.Add(e);
        }
        return h.ToHashCode();
    }
}

/// <summary>效果图输入错误的类别（节点未登记 / 重复登记——泛型输入的良构性由构造期保证）。</summary>
public enum EffectsErrorKind { DuplicateFn, UnknownFn }

/// <summary>效果图构造期拒绝（受控异常类型：登记重复 / 端点未登记）。</summary>
public sealed class EffectsGraphException(EffectsErrorKind kind, FunctionId id)
    : Exception(kind switch
    {
        EffectsErrorKind.DuplicateFn => $"函数节点重复登记: {id.Value}",
        _ => $"函数节点未登记: {id.Value}",
    })
{
    public EffectsErrorKind Kind { get; } = kind;
    public FunctionId Id { get; } = id;
}

/// <summary>
/// 泛型调用图 + 直接效果输入。
///
/// 节点必须先 <see cref="AddFn"/> 登记才能挂效果/边；全部容器有序（SortedDictionary/SortedSet，
/// 枚举数值序 = 契约序），求解输出按 FunctionId 升序，迭代序确定（接口契约 §5：禁哈希序外泄）。
/// </summary>
public sealed class CallGraph
{
    private readonly SortedDictionary<uint, string> _names = new();
    private readonly SortedDictionary<uint, SortedSet<Effect>> _direct = new();
    private readonly SortedDictionary<uint, SortedSet<uint>> _edges = new();

    /// <summary>登记函数节点；重复登记拒绝（防调用方把同名函数当同一节点静默合并）。</summary>
    public void AddFn(FunctionId id, string name)
    {
        if (!_names.TryAdd(id.Value, name))
        {
            throw new EffectsGraphException(EffectsErrorKind.DuplicateFn, id);
        }
    }

    public bool HasFn(FunctionId id) => _names.ContainsKey(id.Value);

    /// <summary>挂一条直接效果（可多次调用累积）。</summary>
    public void SetDirect(FunctionId id, Effect effect)
    {
        NodeEffects(id).Add(effect);
    }

    /// <summary>由内建调用名推导并挂直接效果；非效果内建（视为 PURE）静默忽略。</summary>
    public void SetBuiltinCall(FunctionId id, string builtin)
    {
        if (Effects.BuiltinDirectEffect(builtin) is { } e)
        {
            SetDirect(id, e);
        }
    }

    /// <summary>加一条 caller → callee 边（go 目标同用）；端点必须已登记，重复边幂等。</summary>
    public void AddCall(FunctionId caller, FunctionId callee)
    {
        Require(caller);
        Require(callee);
        if (!_edges.TryGetValue(caller.Value, out var set))
        {
            _edges[caller.Value] = set = [];
        }
        set.Add(callee.Value);
    }

    /// <summary>不动点求解：直接效果沿调用边传播到收敛；结果按 FunctionId 升序。</summary>
    public IReadOnlyList<EffectEntry> Solve()
    {
        // 初始 = 直接效果；eff 值单调增长，格有限 → 必收敛。
        var eff = new SortedDictionary<uint, SortedSet<Effect>>();
        foreach (var (id, set) in _direct)
        {
            eff[id] = new SortedSet<Effect>(set);
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            // SortedDictionary 迭代 = FunctionId 升序；只改 value 结构不改 key 集合，迭代安全。
            foreach (var (caller, callees) in _edges)
            {
                if (!eff.TryGetValue(caller, out var cur))
                {
                    eff[caller] = cur = [];
                }
                foreach (var callee in callees)
                {
                    // 自环边：caller 的候选增益就是自身当前集——并集无新增，跳过
                    // （也避免枚举与修改同一 SortedSet 实例）。
                    if (callee == caller)
                    {
                        continue;
                    }
                    if (!eff.TryGetValue(callee, out var calleeEff))
                    {
                        continue;
                    }
                    foreach (var e in calleeEff)
                    {
                        changed |= cur.Add(e);
                    }
                }
            }
        }

        var outp = new List<EffectEntry>(_names.Count);
        foreach (var (id, name) in _names)
        {
            eff.TryGetValue(id, out var set);
            outp.Add(new EffectEntry(new FunctionId(id), name, Ordered(set)));
        }
        return outp;
    }

    private SortedSet<Effect> NodeEffects(FunctionId id)
    {
        Require(id);
        if (!_direct.TryGetValue(id.Value, out var set))
        {
            _direct[id.Value] = set = [];
        }
        return set;
    }

    private void Require(FunctionId id)
    {
        if (!_names.ContainsKey(id.Value))
        {
            throw new EffectsGraphException(EffectsErrorKind.UnknownFn, id);
        }
    }

    /// <summary>效果集 → 契约输出序：去重按 All 声明序；空集输出 [PURE]。</summary>
    private static IReadOnlyList<Effect> Ordered(SortedSet<Effect>? set) =>
        set is null || set.Count == 0 ? [Effect.Pure] : [.. set];
}
