using Lang.Core;
using Lang.Refactor;
using Xunit;

namespace Lang.Refactor.Tests;

/// <summary>重构骨架：三入口签名与拒绝语义登记（specs/agent-protocol.md §7）。</summary>
public class RefactorTests
{
    [Fact]
    public void Rename_refusal_taxonomy_is_frozen()
    {
        // 冻结七类：Unknown/UnsupportedKind/InvalidName/Conflict/Dirty/NoDeclSite/UntypedReceiver
        Assert.Equal(
            ["Unknown", "UnsupportedKind", "InvalidName", "Conflict", "Dirty", "NoDeclSite", "UntypedReceiver"],
            Enum.GetValues<RenameRefusalKind>().Select(k => k.ToString()));
    }

    [Fact]
    public void Safe_remove_evidence_shape()
    {
        // 不可删证据：原因 + refSites（path:line:col，码点列）；astral 路径原样保留。
        var evidence = new NotRemovable("目标被引用 2 处", ["main.lang-go:3:1", "util🦀.lang-go:7:5"]);

        Assert.Equal(2, evidence.RefSites.Count);
        Assert.Contains("util🦀.lang-go:7:5", evidence.RefSites);
        // 顶层错误两类冻结：Unknown（目标不存在）/ UnsupportedKind（非顶层 fn/全局变量）
        Assert.Equal(["Unknown", "UnsupportedKind"], Enum.GetValues<SafeRemoveErrorKind>().Select(k => k.ToString()));
    }
}
