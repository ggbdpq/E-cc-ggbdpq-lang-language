namespace Lang.Verify;

/// <summary>
/// lang-verify —— 单程序全管线验证（specs/runner-contract.md 五动词「verify」）。
/// <b>本波骨架</b>：只登记九段执行序与报告形状，不实现管线。
///
/// <para>执行序冻结：parse → resolve → typecheck → hir → ir → effect → compile → execute →
/// differential；任一段 Failed → 后续段全部 Skipped（「上游 &lt;段&gt; 未通过，短路」）。
/// 诚实纪律：Skipped ≠ 通过 ≠ 失败——报告恒含全 9 段，短路段显式在册
/// （specs/runner-contract.md：失败输出失败阶段 + 已完成阶段；工具链缺失必须显式
/// skipped，不得静默跳过或谎报通过）。</para>
/// </summary>

/// <summary>九段管线阶段（枚举数值序 = 执行序，冻结）。</summary>
public enum Stage
{
    Parse,
    Resolve,
    Typecheck,
    Hir,
    Ir,
    Effect,
    Compile,
    Execute,
    Differential,
}

/// <summary>阶段判定三态。</summary>
public enum StageOutcomeKind
{
    /// <summary>该段执行且通过。</summary>
    Passed,

    /// <summary>该段执行且失败（管线在此短路；报告的 failed_stage 取执行序最前的 Failed）。</summary>
    Failed,

    /// <summary>该段未执行（上游失败短路 / 工具链缺失等环境原因）；Note 注明原因。</summary>
    Skipped,
}

/// <summary>单段判定（Note：Failed 带证据、Skipped 带原因、Passed 为空）。</summary>
public sealed record StageOutcome(Stage Stage, StageOutcomeKind Kind, string Note = "");

/// <summary>verify 报告：恒含全 9 段（扫描序 = <see cref="Stage"/> 执行序）。</summary>
public sealed record VerifyReport(IReadOnlyList<StageOutcome> Stages)
{
    /// <summary>执行序最前的 Failed 段；整体通过为 null。</summary>
    public Stage? FailedStage() =>
        Stages.FirstOrDefault(s => s.Kind == StageOutcomeKind.Failed) is { } first ? first.Stage : null;

    /// <summary>整体通过 = 无 Failed（Skipped 不算失败，但调用方应自查短路段）。</summary>
    public bool Passed => Stages.All(s => s.Kind != StageOutcomeKind.Failed);
}

/// <summary>verify 入口签名（本波登记，不实现）：输入程序目录（目录即包），输出全 9 段报告。</summary>
public interface IVerifyPipeline
{
    VerifyReport Verify(string dir);
}
