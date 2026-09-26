using Lang.Core;
using Lang.Edit;
using Xunit;

namespace Lang.Edit.Tests;

/// <summary>编辑事务骨架：数据形状 + 码点 offset 契约 + 拒绝语义登记。</summary>
public class EditTests
{
    [Fact]
    public void Text_edit_offsets_are_code_points_not_chars()
    {
        // 接口契约 §0 对抗轴：TextEdit offset 一律 0 基 Unicode 码点（Lang.Edit 模块头注释）。
        // "🦀🦀x" 中 'x' 的码点 offset = 2，但 char index = 4——TextEdit 只认前者；
        // 实现切片时必须经 SourceText.CpToCharIndex 折算。
        var src = new SourceText("main.lang-go", "🦀🦀x\n");
        Assert.Equal(4, src.LengthCp);
        Assert.Equal(4, src.CpToCharIndex(2)); // 'x' 的 char index = 4 ≠ 码点 offset 2

        var edit = new TextEdit("main.lang-go", StartOffset: 2, EndOffset: 3, Replacement: "y");
        Assert.Equal(2, edit.StartOffset);
        Assert.Equal(3, edit.EndOffset);
        Assert.Equal("y", edit.Replacement);
    }

    [Fact]
    public void Workspace_edit_aggregates_cross_file_edits_and_preconditions()
    {
        var ws = new WorkspaceEdit(
            [
                new TextEdit("main.lang-go", 0, 3, "let 甲"),
                new TextEdit("util.lang-go", 4, 4, "🦀"),
            ],
            [
                new Precondition("main.lang-go", "sha256hex-a"),
                new Precondition("util.lang-go", "sha256hex-b"),
            ]);

        Assert.Equal(2, ws.Edits.Count);
        Assert.Equal(2, ws.Preconditions.Count);
        Assert.Equal("util.lang-go", ws.Preconditions[1].File);

        // 拒绝语义冻结登记（specs/agent-protocol.md §6；Io 为实现波次对齐 rust
        // ApplyError 补登的第五类——文件不存在/读写 IO 失败）
        Assert.Equal(
            ["MissingPrecondition", "PreconditionFailed", "Overlap", "OutOfRange", "Io"],
            Enum.GetValues<ApplyErrorKind>().Select(k => k.ToString()));
    }
}
