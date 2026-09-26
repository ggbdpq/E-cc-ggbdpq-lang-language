using System.Globalization;
using Lang.BackendGo;
using Lang.BackendJs;
using Lang.Ir;
using Lang.Verify;
using LangFormatter = Lang.Formatter.Formatter;

namespace Lang.Cli;

/// <summary>基础动词实现（对照 lang-rs lang-cli commands.rs / Go cli.go 命令面）。
/// 本波接线 check / fmt / build / verify；run/test/inspect/dump/refactor 等
/// 解释器与图查询各域交付后分批扩表。</summary>
internal static class Commands
{
    /// <summary>是否带某旗标。</summary>
    internal static bool HasFlag(IReadOnlyList<string> args, string flag) => args.Any(a => a == flag);

    /// <summary>第一个非旗标位置参数（默认 "."；<c>--json</c> 等旗标被跳过）。</summary>
    internal static string Positional(IReadOnlyList<string> args) =>
        args.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";

    /// <summary><c>check [目录] [--json]</c>：装载 → 语义检查 + 稳定诊断码
    /// （诊断契约：有诊断 exit 1；装载器错误非 Diagnostic，走 stderr）。</summary>
    internal static int CmdCheck(IReadOnlyList<string> args)
    {
        string dir = Positional(args);
        bool asJson = HasFlag(args, "--json");
        switch (Loader.LoadModule(dir, includeTests: false))
        {
            case Loader.LoadResult.LoadError load:
                if (asJson)
                {
                    Out.PrintJson(Out.ErrorEnvelopeOf("check", $"装载失败: {load.Message}"));
                }
                else
                {
                    Console.Error.WriteLine($"装载失败: {load.Message}");
                }

                return 1;

            case Loader.LoadResult.ParseError parse:
                // 解析诊断属诊断面：text 形走 check 文本契约，json 形走 check 信封
                if (asJson)
                {
                    Out.PrintJson(Out.CheckEnvelopeOf(parse.Diagnostics, parse.Names));
                }
                else
                {
                    Console.Error.WriteLine($"检查未通过:\n{Out.FormatDiags(parse.Diagnostics, parse.Names)}");
                }

                return 1;

            case Loader.LoadResult.Ok ok:
                {
                    var diags = ok.Module.Analysis.Diags;
                    if (asJson)
                    {
                        Out.PrintJson(Out.CheckEnvelopeOf(diags, ok.Module.Names));
                    }
                    else if (diags.Count > 0)
                    {
                        Console.Error.WriteLine($"检查未通过:\n{Out.FormatDiags(diags, ok.Module.Names)}");
                        return 1;
                    }
                    else
                    {
                        Console.WriteLine("检查通过");
                    }

                    return diags.Count == 0 ? 0 : 1;
                }

            default:
                throw new InvalidOperationException("LoadResult 变体未穷尽（内部不变量）");
        }
    }

    /// <summary><c>fmt [目录]</c>：目录内（不递归）<c>.lang-go</c> 就地规范化，
    /// 打印改动文件名 + 计数（Go runFmt 证据同文案）。</summary>
    internal static int CmdFmt(IReadOnlyList<string> args)
    {
        string dir = Positional(args);
        string[] names;
        try
        {
            names = Directory.EnumerateFiles(dir, "*.lang-go")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读目录失败: {ex.Message}");
            return 2;
        }

        int changed = 0;
        foreach (string name in names)
        {
            string path = Path.Combine(dir, name);
            string src;
            try
            {
                src = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"读文件失败: {ex.Message}");
                return 1;
            }

            var result = LangFormatter.FormatSource(src);
            if (result.Error is not null)
            {
                uint line = result.Error.PrimarySpan?.Start.Line ?? 0;
                Console.Error.WriteLine($"格式化 {name} 失败: 第{line}行: {result.Error.Message}");
                return 1;
            }

            if (result.Output == src)
            {
                continue;
            }

            try
            {
                File.WriteAllText(path, result.Output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"写文件失败: {ex.Message}");
                return 1;
            }

            Console.WriteLine(name);
            changed++;
        }

        Console.WriteLine($"{changed.ToString(CultureInfo.InvariantCulture)} 个文件已格式化");
        return 0;
    }

    /// <summary><c>build [go|js] [目录]</c>：检查 → IR 降形 → 代码生成 → 写
    /// <c>{目录}/out/main.go|main.mjs</c>（Go runBuild 证据同文案提示）。</summary>
    internal static int CmdBuild(IReadOnlyList<string> args)
    {
        var positionals = args.Where(a => !a.StartsWith('-')).ToList();
        string target = "go";
        string dir = ".";
        if (positionals.Count > 0)
        {
            if (positionals[0] is "go" or "js")
            {
                target = positionals[0];
                dir = positionals.Count > 1 ? positionals[1] : ".";
            }
            else if (Directory.Exists(positionals[0]))
            {
                dir = positionals[0];
            }
            else
            {
                Console.Error.WriteLine($"未知目标: {positionals[0]}（可选 go / js）");
                return 2;
            }
        }

        var module = LoadChecked(dir);
        if (module is null)
        {
            return 1;
        }

        var compiled = Compiler.Compile(module.Merged, module.Analysis);
        if (!compiled.IsOk)
        {
            Console.Error.WriteLine($"降形失败: {compiled.Error}");
            return 1;
        }

        bool isGo = target == "go";
        string? src;
        string errText;
        bool generated;
        if (isGo)
        {
            generated = BackendGo.CodeGen.TryGenerate(compiled.Value!, out src, out var errGo);
            errText = errGo?.Message ?? "";
        }
        else
        {
            generated = BackendJs.CodeGen.TryGenerate(compiled.Value!, out src, out var errJs);
            errText = errJs?.Message ?? "";
        }

        if (!generated)
        {
            Console.Error.WriteLine($"{(isGo ? "→Go" : "→JS")} 生成失败: {errText}");
            return 1;
        }

        string outDir = Path.Combine(dir, "out");
        try
        {
            Directory.CreateDirectory(outDir);
            string file = isGo ? "main.go" : "main.mjs";
            File.WriteAllText(Path.Combine(outDir, file), src);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"写产物失败: {ex.Message}");
            return 1;
        }

        string display = dir == "." ? "out" : $"{dir}/out";
        Console.WriteLine(isGo
            ? $"{display}/main.go（go build ./out 或 go run ./out/main.go 执行）"
            : $"{display}/main.mjs（node out/main.mjs 执行）");
        return 0;
    }

    /// <summary><c>verify [目录] [--json]</c>：九段管线 + 报告打印。
    /// 退出码：报告 passed（含 Skipped）= 0，否则 1。
    /// 第三波查缺补漏：A4 双解释器落地后接线 ast/ir 差分路（四路全比）。</summary>
    internal static int CmdVerify(IReadOnlyList<string> args)
    {
        string dir = Positional(args);
        var driver = new DefaultVerifyDriver(
            runAst: ctx =>
            {
                var sink = new CollectSink();
                var r = new Lang.Interp.Interp(new Lang.Runtime.Machine())
                    .Run(ctx.Stmts, ctx.Analysis, sink);
                return r.IsOk
                    ? ExecResult<RunOutput>.Ok(new RunOutput(sink.ToString(), "", 0))
                    : ExecResult<RunOutput>.Err(ExecError.Fail(r.Error!.Message));
            },
            runIr: prog =>
            {
                var sink = new CollectSink();
                var r = new Lang.Interp.IrMachine(prog).Run(sink);
                return r.IsOk
                    ? ExecResult<RunOutput>.Ok(new RunOutput(sink.ToString(), "", 0))
                    : ExecResult<RunOutput>.Err(ExecError.Fail(r.Error!.Message));
            });
        var report = VerifyEngine.Verify(dir, driver);
        if (HasFlag(args, "--json"))
        {
            Out.PrintJson(new Out.VerifyEnvelope
            {
                Passed = report.Passed,
                FailedStage = report.FailedStage() is { } fs ? StageText(fs) : null,
                Stages = [.. report.Stages.Select(s => new Out.VerifyStageDto
                {
                    Stage = StageText(s.Stage),
                    Outcome = s.Kind switch
                    {
                        StageOutcomeKind.Passed => "passed",
                        StageOutcomeKind.Failed => "failed",
                        _ => "skipped",
                    },
                    Note = s.Note,
                })],
            });
        }
        else
        {
            foreach (var outcome in report.Stages)
            {
                string line = outcome.Kind switch
                {
                    StageOutcomeKind.Passed => $"{StageText(outcome.Stage)} 通过",
                    StageOutcomeKind.Failed => $"{StageText(outcome.Stage)} 失败: {outcome.Note}",
                    _ => $"{StageText(outcome.Stage)} 跳过: {outcome.Note}",
                };
                Console.WriteLine(line);
            }

            Console.WriteLine(report.FailedStage() is { } failed
                ? $"验证失败: {StageText(failed)}"
                : "验证通过");
        }

        return report.Passed ? 0 : 1;
    }

    /// <summary>阶段名（lang-verify Stage::as_str 同文：小写契约串）。</summary>
    internal static string StageText(Stage stage) => stage switch
    {
        Stage.Parse => "parse",
        Stage.Resolve => "resolve",
        Stage.Typecheck => "typecheck",
        Stage.Hir => "hir",
        Stage.Ir => "ir",
        Stage.Effect => "effect",
        Stage.Compile => "compile",
        Stage.Execute => "execute",
        Stage.Differential => "differential",
        _ => throw new InvalidOperationException("Stage 变体未穷尽（内部不变量）"),
    };

    /// <summary>装载 + 语义检查（build 用）：诊断非空打印 check 文本契约即停。</summary>
    private static Loader.CliModule? LoadChecked(string dir)
    {
        switch (Loader.LoadModule(dir, includeTests: false))
        {
            case Loader.LoadResult.LoadError load:
                Console.Error.WriteLine($"装载失败: {load.Message}");
                return null;
            case Loader.LoadResult.ParseError parse:
                Console.Error.WriteLine($"检查未通过:\n{Out.FormatDiags(parse.Diagnostics, parse.Names)}");
                return null;
            case Loader.LoadResult.Ok ok:
                if (ok.Module.Analysis.Diags.Count > 0)
                {
                    Console.Error.WriteLine(
                        $"检查未通过:\n{Out.FormatDiags(ok.Module.Analysis.Diags, ok.Module.Names)}");
                    return null;
                }

                return ok.Module;
            default:
                throw new InvalidOperationException("LoadResult 变体未穷尽（内部不变量）");
        }
    }
}
