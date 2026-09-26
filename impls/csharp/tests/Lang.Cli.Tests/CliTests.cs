namespace Lang.Cli.Tests;

/// <summary>CLI 骨架：版本行钉死 + 退出码契约（Console 重定向在本类内串行执行）。</summary>
[Collection("cli-console")]
public class CliTests
{
    [Fact]
    public void Version_line_is_pinned()
    {
        // 任务冻结：--version 输出恰为 "lang-cs 0.1.0"
        Assert.Equal("lang-cs 0.1.0", CliApp.VersionLine());
    }

    [Fact]
    public void Version_commands_exit_zero_with_pinned_line()
    {
        foreach (string cmd in new[] { "version", "--version", "-V" })
        {
            var stdout = new StringWriter();
            TextWriter old = Console.Out;
            Console.SetOut(stdout);
            try
            {
                Assert.Equal(0, CliApp.CliMain([cmd]));
            }
            finally
            {
                Console.SetOut(old);
            }

            Assert.Equal("lang-cs 0.1.0" + Environment.NewLine, stdout.ToString());
        }
    }

    [Fact]
    public void Unknown_command_and_no_args_exit_two_with_usage_on_stderr()
    {
        foreach (string[] args in new[] { new[] { "nope" }, Array.Empty<string>() })
        {
            var stderr = new StringWriter();
            TextWriter old = Console.Error;
            Console.SetError(stderr);
            try
            {
                Assert.Equal(2, CliApp.CliMain(args));
            }
            finally
            {
                Console.SetError(old);
            }

            Assert.Equal(CliApp.Usage(), stderr.ToString());
        }
    }

    [Fact]
    public void Help_commands_exit_zero_with_usage_on_stdout()
    {
        foreach (string cmd in new[] { "help", "--help", "-h" })
        {
            var stdout = new StringWriter();
            TextWriter old = Console.Out;
            Console.SetOut(stdout);
            try
            {
                Assert.Equal(0, CliApp.CliMain([cmd]));
            }
            finally
            {
                Console.SetOut(old);
            }

            Assert.Equal(CliApp.Usage(), stdout.ToString());
        }
    }

    [Fact]
    public void Usage_lists_implemented_verbs()
    {
        string u = CliApp.Usage();
        Assert.Contains("version", u);
        Assert.Contains("help", u);
        // 第三波全命令接线（run/test/inspect/dump/refactor）
        Assert.Contains("run [目录]", u);
        Assert.Contains("test [目录]", u);
        Assert.Contains("check [目录] [--json]", u);
        Assert.Contains("inspect", u);
        Assert.Contains("dump", u);
        Assert.Contains("refactor", u);
    }

    [Fact]
    public void Astral_command_arg_rejected_cleanly()
    {
        // astral 码点作为未知命令：干净走 usage + 退出码 2，不崩
        Assert.Equal(2, CliApp.CliMain(["𝕏🦀"]));
    }
}
