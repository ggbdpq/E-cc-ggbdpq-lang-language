namespace Lang.Cli;

/// <summary>
/// lang-cs 命令行（手写 arg 分派，零 NuGet 新依赖）。
/// 已接线：version/help + check/fmt/build/verify（第二波）+ run/test/inspect/dump/refactor
/// （第三波——解释器与图查询各域已交付）。
/// </summary>
public static class CliApp
{
    /// <summary>语义化版本。</summary>
    public const string Version = "0.1.0";

    /// <summary><c>--version</c> 的规范输出行：恰为 <c>lang-cs 0.1.0</c>。</summary>
    public static string VersionLine() => $"lang-cs {Version}";

    /// <summary>用法文本（stdout/stderr 共用）。</summary>
    public static string Usage() =>
        """
        lang-cs 工具链

        用法: lang-cs <命令> [参数]

        命令:
          version, --version, -V   打印版本
          help, --help, -h         打印本帮助
          run [目录]               运行 main（AST 参考解释器）
          test [目录]              运行 *_test.lang-go 里的 test* 函数
          check [目录] [--json]    语义检查（有诊断退出码 1）
          fmt [目录]               就地规范化 .lang-go（不递归）
          build [go|js] [目录]     降形 + 生成产物到 out/
          verify [目录] [--json]   九段全管线验证
          inspect                  symbols|refs|type|effects|callers|callees|impact [目录] [--json]
          dump                     ast|ir [目录]（确定性调试视图）
          refactor                 rename|organize-imports|safe-remove [--symbol N] [--to NAME]
                                   [--apply] [--json]（默认 dry-run）
        """ + "\n";

    /// <summary>入口分派（返回进程退出码）：version/help → 0；各命令实现；
    /// 未知命令/无参 → usage 到 stderr + 2。</summary>
    public static int CliMain(IReadOnlyList<string> args)
    {
        switch (args.Count > 0 ? args[0] : null)
        {
            case "version" or "--version" or "-V":
                Console.WriteLine(VersionLine());
                return 0;
            case "help" or "--help" or "-h":
                Console.Out.Write(Usage());
                return 0;
            case "run":
                return RunTest.CmdRun(args.Skip(1).ToList());
            case "test":
                return RunTest.CmdTest(args.Skip(1).ToList());
            case "check":
                return Commands.CmdCheck(args.Skip(1).ToList());
            case "fmt":
                return Commands.CmdFmt(args.Skip(1).ToList());
            case "build":
                return Commands.CmdBuild(args.Skip(1).ToList());
            case "verify":
                return Commands.CmdVerify(args.Skip(1).ToList());
            case "inspect":
                return Inspect.CmdInspect(args.Skip(1).ToList());
            case "dump":
                return DumpCmd.CmdDump(args.Skip(1).ToList());
            case "refactor":
                return RefactorCmd.CmdRefactor(args.Skip(1).ToList());
            default:
                Console.Error.Write(Usage());
                return 2;
        }
    }
}
