using Lang.Cli;

namespace Lang.Cli.Tests;

/// <summary>
/// check 命令 E2E（真执行链：装载 → parse → 语义分析 → 诊断渲染/信封 → 退出码）。
/// Console 重定向 + <c>[Collection]</c> 与 CliTests 串行（避免流重定向互踩）。
/// 临时目录夹具，测试末尾清理。
/// </summary>
[Collection("cli-console")]
public class CheckTests
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

    /// <summary>建临时目录并写入 .lang-go 文件（返回目录路径）。</summary>
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
    public void Check_clean_module_prints_pass_and_exits_zero()
    {
        string dir = WriteModule("clean", ("main.lang-go", "fn main() {}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("check", dir);
            Assert.Equal(0, code);
            Assert.Equal("检查通过" + Environment.NewLine, stdout);
            Assert.Equal("", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Check_undefined_name_reports_line_anchored_diagnostic_exit_one()
    {
        string dir = WriteModule("undef", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, _, stderr) = RunCli("check", dir);
            Assert.Equal(1, code);
            Assert.Contains("检查未通过:", stderr);
            Assert.Contains("main.lang-go:第2行: [E2002]", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Check_json_envelope_shape_and_exit_codes()
    {
        string dir = WriteModule("json", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("check", dir, "--json");
            Assert.Equal(1, code);
            // 字段序 = schema 序（schemaVersion, toolVersion, kind, success, diagnostics）
            int kind = stdout.IndexOf("\"kind\": \"check\"", StringComparison.Ordinal);
            int success = stdout.IndexOf("\"success\": false", StringComparison.Ordinal);
            int diags = stdout.IndexOf("\"diagnostics\": [", StringComparison.Ordinal);
            int schema = stdout.IndexOf("\"schemaVersion\": \"1\"", StringComparison.Ordinal);
            int tool = stdout.IndexOf("\"toolVersion\": \"0.1.0\"", StringComparison.Ordinal);
            Assert.True(schema >= 0 && tool > schema && kind > tool && success > kind && diags > success,
                $"信封字段序不符:\n{stdout}");
            Assert.Contains("\"code\": \"E2002\"", stdout);
            Assert.Contains("\"ruleId\": \"NAME-UNDEF-004\"", stdout);
            Assert.Contains("\"severity\": \"error\"", stdout);
            Assert.Contains("\"file\": \"main.lang-go\"", stdout);
            Assert.Contains("\"line\": 2", stdout);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        string dir2 = WriteModule("json-clean", ("main.lang-go", "fn main() {}\n"));
        try
        {
            var (code, stdout, _) = RunCli("check", dir2, "--json");
            Assert.Equal(0, code);
            Assert.Contains("\"success\": true", stdout);
            Assert.Contains("\"diagnostics\": []", stdout);
        }
        finally
        {
            Directory.Delete(dir2, true);
        }
    }

    [Fact]
    public void Check_parse_error_reports_e1001_exit_one()
    {
        string dir = WriteModule("parse", ("main.lang-go", "fn main() {\n"));
        try
        {
            var (code, _, stderr) = RunCli("check", dir);
            Assert.Equal(1, code);
            Assert.Contains("[E1001]", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Check_load_error_goes_to_stderr_text_and_error_envelope_json()
    {
        // import 逃出模块根：装载器错误（非 Diagnostic）→ stderr + 1
        string dir = WriteModule("loaderr", ("main.lang-go", "import \"../outside\"\n\nfn main() {}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("check", dir);
            Assert.Equal(1, code);
            Assert.Equal("", stdout);
            Assert.Contains("装载失败:", stderr);

            var (code2, stdout2, stderr2) = RunCli("check", dir, "--json");
            Assert.Equal(1, code2);
            Assert.Equal("", stderr2);
            Assert.Contains("\"kind\": \"check\"", stdout2);
            Assert.Contains("\"error\": \"装载失败:", stdout2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Check_diag_file_attribution_across_imports()
    {
        // 多文件模块：诊断锚保留各自文件的 SourceId，file 前缀逐文件消歧
        string dir = WriteModule(
            "multi",
            ("a.lang-go", "import \"./b\"\n\nfn main() {\n    let x = f()\n    let y = g\n}\n"),
            ("b.lang-go", "fn f(): int {\n    return nope\n}\n"));
        try
        {
            var (code, _, stderr) = RunCli("check", dir);
            Assert.Equal(1, code);
            // a.lang-go 的 g 未定义（第 5 行）
            Assert.Contains("a.lang-go:第5行: [E2002]", stderr);
            // 被导入文件 b.lang-go 的 nope 未定义（第 2 行）
            Assert.Contains("b.lang-go:第2行: [E2002]", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
