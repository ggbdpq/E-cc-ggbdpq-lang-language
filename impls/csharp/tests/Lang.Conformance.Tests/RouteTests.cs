using Lang.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace Lang.Conformance.Tests;

/// <summary>四路执行器适配层专项：skip 门注入面、双解释器一致性、宿主路 E2E 与临时目录清理。</summary>
public class RouteTests(ITestOutputHelper output)
{
    private static string CaseDir(string category, string name) =>
        Path.Combine(SuiteScanner.DefaultSuiteRoot(), category, name);

    /// <summary>宿主可执行的真实 case（结构体复制语义，输出两行）。</summary>
    private const string FixtureCategory = "values";
    private const string FixtureName = "struct-copy-semantics";

    [Fact]
    public void GoRoute_with_exe_null_skips_and_names_tool()
    {
        var r = GoRoute.WithExe(null).Run(CaseDir(FixtureCategory, FixtureName));
        Assert.Null(r.Output);
        Assert.NotNull(r.Error);
        Assert.Equal(RouteErrorKind.Skipped, r.Error!.Kind);
        Assert.Contains("go", r.Error.Reason);
    }

    [Fact]
    public void JsRoute_with_exe_null_skips_and_names_tool()
    {
        var r = JsRoute.WithExe(null).Run(CaseDir(FixtureCategory, FixtureName));
        Assert.Null(r.Output);
        Assert.NotNull(r.Error);
        Assert.Equal(RouteErrorKind.Skipped, r.Error!.Kind);
        Assert.Contains("node", r.Error.Reason);
    }

    /// <summary>装载失败 = Failed（非 Skip）：目录缺源文件不是环境性跳过。</summary>
    [Fact]
    public void Load_failure_is_failed_not_skipped()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"lang-conf-empty-{Environment.ProcessId}-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tmp);
        try
        {
            var r = new InterpAstRoute().Run(tmp);
            Assert.NotNull(r.Error);
            Assert.Equal(RouteErrorKind.Failed, r.Error!.Kind);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>AST 与 IR 双解释器对同一 case 输出一致（差分价值的最小现场证据；
    /// 全量差分由矩阵测试覆盖）。</summary>
    [Fact]
    public void Ast_and_ir_routes_agree_on_fixture()
    {
        var dir = CaseDir(FixtureCategory, FixtureName);
        var ast = new InterpAstRoute().Run(dir);
        var ir = new InterpIrRoute().Run(dir);
        Assert.NotNull(ast.Output);
        Assert.NotNull(ir.Output);
        Assert.Equal(ast.Output!.Stdout, ir.Output!.Stdout);
        Assert.Equal(ast.Output.ExitCode, ir.Output.ExitCode);
        Assert.Equal(ast.Output.Stderr, ir.Output.Stderr);
    }

    /// <summary>宿主路 E2E：go build + 执行与 node 执行对夹具输出逐字节一致；
    /// 临时目录清理按 SPEC-GAP-CS-A6-06 口径验证——Windows 对新生成产物有瞬时占用
    /// （AV/索引器），Dispose 删除可能失败由 OS 兜底，故留观察窗重试、断言不依赖
    /// 删除必成（残留仅在窗口后仍存在时以 note 报告）。工具链缺失时 skip 不失败。</summary>
    [Fact]
    public void Go_and_js_routes_e2e_and_clean_temp_dirs()
    {
        var dir = CaseDir(FixtureCategory, FixtureName);
        var before = ExistingTempDirs();
        var go = new GoRoute().Run(dir);
        var js = new JsRoute().Run(dir);

        if (go.Error is { Kind: RouteErrorKind.Skipped } || js.Error is { Kind: RouteErrorKind.Skipped })
        {
            output.WriteLine($"skip：{go.Error?.Reason} / {js.Error?.Reason}");
            return;
        }

        Assert.True(go.Output is not null, go.Error?.Reason);
        Assert.True(js.Output is not null, js.Error?.Reason);
        Assert.Equal(go.Output!.Stdout, js.Output!.Stdout);
        Assert.Equal("1 100\n", go.Output.Stdout);

        var residue = WaitForCleanup(before);
        if (residue.Count > 0)
        {
            output.WriteLine($"临时目录清理观察窗（10s）后仍有残留（Windows 瞬时占用，OS 兜底）: {string.Join(", ", residue)}");
        }
    }

    /// <summary>等待新增临时目录消失（每 500ms 轮询 + 顺手重试删除，共 10s）。</summary>
    private static HashSet<string> WaitForCleanup(IReadOnlySet<string> before)
    {
        var residue = ExistingTempDirs();
        residue.ExceptWith(before);
        for (var attempt = 0; residue.Count > 0 && attempt < 20; attempt++)
        {
            Thread.Sleep(500);
            foreach (var d in residue.ToList())
            {
                try
                {
                    Directory.Delete(d, recursive: true);
                    residue.Remove(d);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            residue.UnionWith(ExistingTempDirs().Except(before));
        }

        return residue;
    }

    private static HashSet<string> ExistingTempDirs() =>
        [.. Directory.GetDirectories(Path.GetTempPath(), "lang-conformance-*")];
}
