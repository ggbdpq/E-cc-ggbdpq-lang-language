using System.Collections.Generic;

namespace Lang.Ir;

/// <summary>
/// 效果枚举（契约顺序 = <see cref="EffectExt"/> All 声明序：PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI；
/// specs/runtime.md EFFECT-001 + specs/agent-protocol.md §3）。
/// <b>过渡期常量</b>：Lang.Contract（A6）未就位，效果枚举与内建映射暂驻本程序集；
/// A6 落地后本枚举删除、改消费 Lang.Contract 的同名类型（对接面：名与契约序 1:1，已登记 status）。
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

public static class EffectExt
{
    /// <summary>固定契约顺序（输出去重按此序；禁 alphabet/哈希序）。</summary>
    public static readonly IReadOnlyList<Effect> All = new[]
    {
        Effect.Pure, Effect.Io, Effect.Fs, Effect.Net, Effect.Time,
        Effect.Random, Effect.Spawn, Effect.Channel, Effect.Ffi,
    };

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

/// <summary>
/// IR 内建三方对照表 + 直接效果提取（A3↔A4↔A6 对接胶水）。
/// 三方事实源（本模块不新造事实，只把三方钉在同一行）：
/// 1. <b>runtime 名单</b>：Lang.Runtime 的 core 注册表冻结 20 名，BuiltinId = 注册序下标（0..=19）；
///    <b>Lang.Runtime 未就位</b>——本表先落常量并登记 status，A4 落地后测试补锁名
///    （经公共 API lookup/name 双向断言，lang-rs 先例）。
/// 2. <b>效果映射</b>：<see cref="Builtins.BuiltinDirectEffect"/>（println/print→IO、http.get→NET、
///    makeChan/send/recv→CHANNEL；映射未列出的名 = 纯）——Lang.Contract 未就位，暂驻本程序集（见上）。
/// 3. <b>IR 形态</b>：makeChan/send/recv 已升为专指令（ChanNew/ChanSend/ChanRecv），
///    其余 17 名走 <see cref="Inst.CallBuiltin"/>（Name 与注册表名字面逐字节一致）。
/// 决策：CallBuiltin.Name 不做名单硬校验（validator 放行任意名）——宿主可扩展内建（如 http.get），
/// 效果提取按映射计算，未知名缺省视为纯（保守正确）。
/// </summary>
public enum IrBuiltinForm
{
    /// <summary>Inst.CallBuiltin。</summary>
    Call,
    /// <summary>已升为专指令（makeChan→ChanNew、send→ChanSend、recv→ChanRecv）。</summary>
    Inst,
}

/// <summary>对照表条目：「IR 内建名 ↔ runtime BuiltinId ↔ 直接效果」三方锁定。</summary>
public sealed record BuiltinEntry(
    /// <summary>内建名（CallBuiltin.Name 与 runtime 注册名逐字节一致）。</summary>
    string Name,
    /// <summary>Lang.Runtime core 注册表中的下标（BuiltinId）。</summary>
    uint RuntimeId,
    /// <summary>直接效果（null = 纯）；与 <see cref="Builtins.BuiltinDirectEffect"/> 一致。</summary>
    Effect? DirectEffect,
    /// <summary>IR 侧落地形态。</summary>
    IrBuiltinForm Form);

public static class Builtins
{
    /// <summary>
    /// 内建三方对照表：runtime core 注册表全量 20 名，顺序 = core() 注册序（即 BuiltinId 序）。
    /// IR 侧合法 CallBuiltin 名 = 表中 Form == Call 的 17 名。
    /// </summary>
    public static readonly IReadOnlyList<BuiltinEntry> Table = new[]
    {
        new BuiltinEntry("println", 0, Effect.Io, IrBuiltinForm.Call),
        new BuiltinEntry("print", 1, Effect.Io, IrBuiltinForm.Call),
        new BuiltinEntry("len", 2, null, IrBuiltinForm.Call),
        new BuiltinEntry("str", 3, null, IrBuiltinForm.Call),
        new BuiltinEntry("int", 4, null, IrBuiltinForm.Call),
        new BuiltinEntry("float", 5, null, IrBuiltinForm.Call),
        new BuiltinEntry("keys", 6, null, IrBuiltinForm.Call),
        new BuiltinEntry("values", 7, null, IrBuiltinForm.Call),
        new BuiltinEntry("has", 8, null, IrBuiltinForm.Call),
        new BuiltinEntry("push", 9, null, IrBuiltinForm.Call),
        new BuiltinEntry("assert", 10, null, IrBuiltinForm.Call),
        new BuiltinEntry("err", 11, null, IrBuiltinForm.Call),
        new BuiltinEntry("bytes", 12, null, IrBuiltinForm.Call),
        new BuiltinEntry("char", 13, null, IrBuiltinForm.Call),
        new BuiltinEntry("makeChan", 14, Effect.Channel, IrBuiltinForm.Inst),
        new BuiltinEntry("send", 15, Effect.Channel, IrBuiltinForm.Inst),
        new BuiltinEntry("recv", 16, Effect.Channel, IrBuiltinForm.Inst),
        new BuiltinEntry("isNone", 17, null, IrBuiltinForm.Call),
        new BuiltinEntry("jsonParse", 18, null, IrBuiltinForm.Call),
        new BuiltinEntry("jsonStringify", 19, null, IrBuiltinForm.Call),
    };

    /// <summary>
    /// 内建调用名 → 直接效果（过渡期本地映射，A6 的 Lang.Contract 就位后替换为消费方实现）；
    /// 未列出的内建视为 PURE。extern 声明（→FFI）与 go 语句（→SPAWN）来自 IR 结构而非调用名。
    /// </summary>
    public static Effect? BuiltinDirectEffect(string builtin) => builtin switch
    {
        "println" or "print" => Effect.Io,
        "http.get" => Effect.Net,
        "makeChan" or "send" or "recv" => Effect.Channel,
        _ => null,
    };

    /// <summary>
    /// 一条指令的直接效果集（specs/agent-protocol.md §3 推导规则的 IR 侧提取；
    /// effects 引擎消费方：Lang.Contract 的 CallGraph.set_direct）。
    /// 映射（每条指令至多一个直接效果）：
    /// - Spawn → SPAWN（go 语句；被调效果沿调用边另行保守传播）；
    /// - ChanNew/ChanSend/ChanRecv → CHANNEL（makeChan/send/recv 的专指令形态）；
    /// - CallBuiltin → <see cref="BuiltinDirectEffect"/>（未列出/纯名 = 空；http.get→NET 在此路径生效）；
    /// - 其余指令 → 空（保守缺省：新增效果面指令漏登记不会虚增效果）。
    /// 新增效果面指令必须在此登记。
    /// </summary>
    public static IReadOnlyList<Effect> InstDirectEffects(Inst inst) => inst switch
    {
        Inst.Spawn => new[] { Effect.Spawn },
        Inst.ChanNew or Inst.ChanSend or Inst.ChanRecv => new[] { Effect.Channel },
        Inst.CallBuiltin call when BuiltinDirectEffect(call.Name) is { } e => new[] { e },
        _ => System.Array.Empty<Effect>(),
    };

    /// <summary>
    /// 函数声明的直接效果（IR 结构形状，非指令）：extern 声明 → FFI。
    /// 普通函数返回空——其函数体内指令的直接效果由调用方遍历 blocks[*].insts 逐条取
    /// <see cref="InstDirectEffects"/> 累积。
    /// </summary>
    public static IReadOnlyList<Effect> FnDirectEffects(IrFunction func) =>
        func.ExternHost is not null ? new[] { Effect.Ffi } : System.Array.Empty<Effect>();
}
