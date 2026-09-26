using Lang.Cli;

namespace Lang.Cli.Tests;

/// <summary>
/// fmt / build / verify 命令 E2E（真执行链：格式化落盘、降形 + 双后端生成产物、
/// 九段管线含 go/node 工具链执行）。Console 重定向与 CliTests 同集合串行。
/// </summary>
[Collection("cli-console")]
public class ToolingTests
{
    private static (int Code, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        TextWriter oldOut = Console.Out;
        TextWriter oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int code = CliApp.CliMain(args.ToList());
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static string WriteModule(string tag, params (string Name, string Source)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lang_cs_cli_{Environment.ProcessId}_{tag}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        foreach (var (name, source) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), source);
        }

        return dir;
    }

    [Fact]
    public void Fmt_rewrites_unformatted_source_and_is_idempotent()
    {
        string dir = WriteModule("fmt", ("main.lang-go", "fn main() { println( \"hi\" ) }\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("fmt", dir);
            Assert.Equal(0, code);
            Assert.Equal("", stderr);
            Assert.Contains("main.lang-go", stdout);
            Assert.Contains("1 个文件已格式化", stdout);
            var formatted = File.ReadAllText(Path.Combine(dir, "main.lang-go"));
            Assert.NotEqual("fn main() { println( \"hi\" ) }\n", formatted);

            // 幂等：第二遍零改动
            var (code2, stdout2, _) = RunCli("fmt", dir);
            Assert.Equal(0, code2);
            Assert.Contains("0 个文件已格式化", stdout2);
            Assert.Equal(formatted, File.ReadAllText(Path.Combine(dir, "main.lang-go")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Build_go_and_js_write_deterministic_artifacts()
    {
        string dir = WriteModule("build", ("main.lang-go", "fn main() {\n    println(\"hi\")\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("build", "go", dir);
            Assert.Equal(0, code);
            Assert.Equal("", stderr);
            string goArtifact = File.ReadAllText(Path.Combine(dir, "out", "main.go"));
            Assert.StartsWith("package main", goArtifact);

            var (codeJs, _, _) = RunCli("build", "js", dir);
            Assert.Equal(0, codeJs);
            string jsArtifact = File.ReadAllText(Path.Combine(dir, "out", "main.mjs"));
            Assert.Contains("function lf", jsArtifact);

            // 确定性：再次生成逐字节相等
            var goArtifact2 = RunCli("build", "go", dir);
            Assert.Equal(0, goArtifact2.Code);
            Assert.Equal(goArtifact, File.ReadAllText(Path.Combine(dir, "out", "main.go")));

            // 未知目标 = 用法错误
            var (codeBad, _, _) = RunCli("build", "wasm", dir);
            Assert.Equal(2, codeBad);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Build_dirty_module_reports_diagnostics_exit_one()
    {
        string dir = WriteModule("builddirty", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, _, stderr) = RunCli("build", "go", dir);
            Assert.Equal(1, code);
            Assert.Contains("检查未通过:", stderr);
            Assert.Contains("[E2002]", stderr);
            Assert.False(Directory.Exists(Path.Combine(dir, "out")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>宿主工具链探测（与 Lang.Verify DefaultVerifyDriver 的 PATH 探测同口径）；
    /// 缺工具时 verify 的差分段如实 Skipped，有工具时四路全比——断言按可用性分支。</summary>
    private static bool ToolAvailable(string exe, string flag)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe, flag)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
            return p is not null && p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool GoAndNodeAvailable() =>
        ToolAvailable("go", "version") && ToolAvailable("node", "--version");

    [Fact]
    public void Verify_clean_module_passes_with_honest_skips()
    {
        string dir = WriteModule("verify", ("main.lang-go", "fn main() {\n    println(\"hi\")\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("verify", dir);
            Assert.Equal(0, code);
            Assert.Contains("ir 通过", stdout);
            Assert.Contains("compile 通过", stdout);
            Assert.Contains("execute 通过", stdout);
            // A4 双解释器已接线（第三波）：有 go+node 工具链时四路差分全过；
            // 缺工具时缺失路如实 Skipped（不谎报全路一致）——两种环境态都在册。
            Assert.Matches(@"differential (通过|跳过)", stdout);
            Assert.Contains("验证通过", stdout);
            Assert.DoesNotContain("失败", stdout);
            if (GoAndNodeAvailable())
            {
                Assert.Contains("differential 通过", stdout);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Verify_dirty_module_short_circuits_exit_one()
    {
        string dir = WriteModule("verifydirty", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("verify", dir);
            Assert.Equal(1, code);
            Assert.Contains("验证失败:", stdout);
            Assert.Contains("跳过:", stdout); // 上游失败短路显式在册
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Verify_json_envelope_shape()
    {
        string dir = WriteModule("verifyjson", ("main.lang-go", "fn main() {\n    println(\"hi\")\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("verify", dir, "--json");
            Assert.Equal(0, code);
            Assert.Contains("\"kind\": \"verify\"", stdout);
            Assert.Contains("\"passed\": true", stdout);
            Assert.Contains("\"failedStage\": null", stdout);
            Assert.Contains("\"stage\": \"ir\"", stdout);
            Assert.Contains("\"outcome\": \"passed\"", stdout);
            // 有工具链时四路全比（无 skipped）；缺工具时缺失路显式在册
            if (GoAndNodeAvailable())
            {
                Assert.DoesNotContain("\"outcome\": \"skipped\"", stdout);
            }
            else
            {
                Assert.Contains("\"outcome\": \"skipped\"", stdout);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
