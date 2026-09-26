using Lang.Conformance;
using Xunit;

namespace Lang.Conformance.Tests;

/// <summary>套件扫描器专项（布局规则 + 仓库真值 151 case）。</summary>
public class ScanTests
{
    /// <summary>仓库真值：conformance/ 恰好 151 个 case（仓库实测数；套件扩容时同步更新本断言）。</summary>
    [Fact]
    public void Real_suite_has_exactly_151_parsable_cases()
    {
        var root = SuiteScanner.DefaultSuiteRoot();

        Assert.True(
            SuiteScanner.TryScan(root, out var cases, out var failures),
            $"套件扫描应零失败，实际 {failures.Count} 条：{string.Join("; ", failures.Take(5).Select(f => $"{f.Path}: {f.Reason}"))}");

        Assert.Equal(151, cases.Count);
        // 确定性：两次扫描逐项相等（含目录路径与解析内容；record 成员含 List，按指纹逐项比）
        static string Fingerprint(ScannedCase c) =>
            System.Text.Json.JsonSerializer.Serialize(c.Case) + "|" + c.Dir;
        Assert.True(SuiteScanner.TryScan(root, out var again, out _));
        Assert.Equal(cases.Select(Fingerprint), again.Select(Fingerprint));
        // 排序 = (category, case 目录名)
        var sorted = cases.OrderBy(c => c.Case.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Case.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(cases.Select(Fingerprint), sorted.Select(Fingerprint));
        // run/check 两种模式都真实存在（防比较逻辑退化为死代码）
        Assert.Contains(cases, c => c.Case.Mode == CaseMode.Run);
        Assert.Contains(cases, c => c.Case.Mode == CaseMode.Check);
        // 16 类实测清点（语料快照；扩容时同步）
        Assert.Equal(16, cases.Select(c => c.Case.Category).Distinct().Count());
    }

    [Fact]
    public void Collects_all_failures()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"lang-cs-scan-{Environment.ProcessId}-{Path.GetRandomFileName()}");
        try
        {
            var good = Path.Combine(tmp, "types", "good-case");
            Directory.CreateDirectory(good);
            File.WriteAllText(Path.Combine(good, "case.yaml"),
                "name: good-case\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    ok\n  stderr: \"\"\n");
            var missing = Path.Combine(tmp, "types", "missing-manifest");
            Directory.CreateDirectory(missing);
            var bad = Path.Combine(tmp, "syntax", "bad-manifest");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "case.yaml"), "name: bad\nunknown_key: 1\n");
            var mismatched = Path.Combine(tmp, "match", "elsewhere");
            Directory.CreateDirectory(mismatched);
            File.WriteAllText(Path.Combine(mismatched, "case.yaml"),
                "name: not-elsewhere\ncategory: match\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n");
            Directory.CreateDirectory(Path.Combine(tmp, "empty-category"));

            Assert.False(SuiteScanner.TryScan(tmp, out _, out var failures));
            Assert.Equal(4, failures.Count);
            Assert.Contains(failures, f => f.Reason == "缺 case.yaml");
            Assert.Contains(failures, f => f.Reason.Contains("unknown"));
            Assert.Contains(failures, f => f.Reason.Contains("不一致"));
            Assert.Contains(failures, f => f.Reason.Contains("empty-category"));
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }
}
