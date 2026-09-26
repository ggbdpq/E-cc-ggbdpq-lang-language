using Lang.Cli;

namespace Lang.Cli.Tests;

/// <summary>
/// run / test 命令 E2E（真执行链：装载 → 语义分析 → AST 参考解释器 stdout 直出）。
/// Console 重定向与 <c>[Collection]</c> 串行；临时目录夹具。
/// </summary>
[Collection("cli-console")]
public class RunTestE2E
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
            // 归一化换行（测试重定向下 WriteLine 产 \r\n；断言按 \n 口径）
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
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
    public void Run_prints_program_stdout_and_exits_zero()
    {
        string dir = WriteModule("run", ("main.lang-go", "fn main() {\n    println(\"hi\")\n    println(1 + 2)\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("run", dir);
            Assert.Equal(0, code);
            Assert.Equal("hi\n3\n", stdout);
            Assert.Equal("", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Run_without_main_executes_top_level_only()
    {
        string dir = WriteModule("runnomain", ("main.lang-go", "println(\"top\")\nfn helper() {\n    return 1\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("run", dir);
            Assert.Equal(0, code);
            Assert.Equal("top\n", stdout);
            Assert.Equal("", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Run_dirty_analysis_prints_check_envelope_exit_one()
    {
        string dir = WriteModule("rundirty", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("run", dir);
            Assert.Equal(1, code);
            Assert.Equal("", stderr);
            Assert.Contains("\"kind\": \"check\"", stdout);
            Assert.Contains("\"success\": false", stdout);
            Assert.Contains("\"code\": \"E2002\"", stdout);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Run_parse_error_reports_text_form_exit_one()
    {
        string dir = WriteModule("runparse", ("main.lang-go", "fn main() {\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("run", dir);
            Assert.Equal(1, code);
            Assert.Equal("", stdout);
            Assert.Contains("检查未通过:", stderr);
            Assert.Contains("[E1001]", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Run_load_error_goes_to_stderr()
    {
        string dir = WriteModule("runloaderr", ("main.lang-go", "import \"../outside\"\n\nfn main() {}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("run", dir);
            Assert.Equal(1, code);
            Assert.Equal("", stdout);
            Assert.Contains("装载失败:", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Run_runtime_error_reports_error_exit_one()
    {
        string dir = WriteModule(
            "runrt", ("main.lang-go", "fn main() {\n    let xs = [1, 2]\n    println(xs[5])\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("run", dir);
            Assert.Equal(1, code);
            Assert.Contains("错误:", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Test_discovers_test_functions_and_skips_main()
    {
        // main 里的 println 不得出现在 test 输出（evalChunk 等价：不调 main）
        string dir = WriteModule(
            "testrun",
            ("main.lang-go", "fn main() {\n    println(\"MAIN-MUST-NOT-RUN\")\n}\n"),
            ("main_test.lang-go",
                "fn testAdd() {\n    assert(1 + 2 == 3, \"add\")\n}\n\nfn testStr() {\n    assert(str(7) == \"7\", \"str\")\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("test", dir);
            Assert.Equal(0, code);
            Assert.DoesNotContain("MAIN-MUST-NOT-RUN", stdout);
            Assert.Contains("PASS testAdd\n", stdout);
            Assert.Contains("PASS testStr\n", stdout);
            Assert.Contains("\n2 通过, 0 失败\n", stdout);
            Assert.Equal("", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Test_failure_reports_fail_line_and_exit_one()
    {
        string dir = WriteModule(
            "testfail",
            ("main_test.lang-go", "fn testBad() {\n    assert(1 == 2, \"one is not two\")\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("test", dir);
            Assert.Equal(1, code);
            Assert.Contains("FAIL testBad:", stdout);
            Assert.Contains("one is not two", stdout);
            Assert.Contains("\n0 通过, 1 失败\n", stdout);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Test_state_is_shared_across_tests_in_declaration_order()
    {
        string dir = WriteModule(
            "testshare",
            ("main.lang-go", "var counter = 0\nfn bump() {\n    counter += 1\n}\n"),
            ("main_test.lang-go",
                "fn testBump() {\n    bump()\n    assert(counter == 1, \"after bump\")\n}\n\nfn testSeesBump() {\n    assert(counter == 1, \"shared state\")\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("test", dir);
            Assert.Equal(0, code);
            Assert.Contains("PASS testBump\n", stdout);
            Assert.Contains("PASS testSeesBump\n", stdout);
            Assert.Contains("\n2 通过, 0 失败\n", stdout);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Test_without_test_files_prints_honest_message()
    {
        string dir = WriteModule("notests", ("main.lang-go", "fn main() {}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("test", dir);
            Assert.Equal(0, code);
            Assert.Equal("未发现 *_test.lang-go\n", stdout);
            Assert.Equal("", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
