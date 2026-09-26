using Lang.Verify;
using Xunit;

namespace Lang.Verify.Tests;

/// <summary>verify 骨架：九段执行序冻结 + 报告语义（Skipped ≠ 失败，短路显式在册）。</summary>
public class VerifyTests
{
    [Fact]
    public void Stage_order_is_frozen_execution_sequence()
    {
        // specs/runner-contract.md：parse → resolve → typecheck → hir → ir → effect →
        // compile → execute → differential（枚举数值序 = 执行序）。
        Assert.Equal(
            ["Parse", "Resolve", "Typecheck", "Hir", "Ir", "Effect", "Compile", "Execute", "Differential"],
            Enum.GetValues<Stage>().Select(s => s.ToString()));
        Assert.Equal(9, Enum.GetValues<Stage>().Length);
    }

    [Fact]
    public void Report_marks_failed_stage_and_explicit_short_circuits()
    {
        // 形如 parse 失败夹具：parse Failed，后 8 段全部 Skipped 短路（Skipped ≠ 通过 ≠ 失败）。
        var stages = Enum.GetValues<Stage>()
            .Select((s, i) => new StageOutcome(s,
                i == 0 ? StageOutcomeKind.Failed : StageOutcomeKind.Skipped,
                i == 0 ? "E1001: 非法字符 🦀" : "上游 Parse 未通过，短路"))
            .ToList();
        var report = new VerifyReport(stages);

        Assert.False(report.Passed);
        Assert.Equal(Stage.Parse, report.FailedStage());
        Assert.Equal(9, report.Stages.Count); // 报告恒含全 9 段
        Assert.Equal(8, report.Stages.Count(s => s.Kind == StageOutcomeKind.Skipped));

        // 全绿报告：无 Failed，failed_stage 为 null
        var green = new VerifyReport(
            [.. Enum.GetValues<Stage>().Select(s => new StageOutcome(s, StageOutcomeKind.Passed))]);
        Assert.True(green.Passed);
        Assert.Null(green.FailedStage());
    }
}
