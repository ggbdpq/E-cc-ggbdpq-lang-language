using Lang.Core;
using Lang.Edit;
using Lang.Refactor;
using Lang.Semantic;

namespace Lang.Cli;

/// <summary>
/// <c>refactor rename|organize-imports|safe-remove [--symbol N] [--to NAME] [--apply] [--json] [dir]</c>。
/// 语义级重构走 Lang.Refactor 冻结入口（SymbolId 事实，禁 Regex）；默认 dry-run 只报编辑，
/// --apply 经 <see cref="Refactorings.ApplyAndVerify"/> 闭环（precondition → apply → format →
/// 重新分析零回归）后落盘（lang-rs refactor.rs 同分工）。
/// </summary>
internal static class RefactorCmd
{
    private sealed class RefactorArgs
    {
        public string? Symbol;
        public string? To;
        public bool Apply;
        public bool Json;
        public string Dir = ".";
    }

    private static RefactorArgs ParseArgs(IReadOnlyList<string> rest)
    {
        var outArgs = new RefactorArgs();
        var i = 0;
        while (i < rest.Count)
        {
            var a = rest[i];
            switch (a)
            {
                case "--symbol" when i + 1 < rest.Count:
                    outArgs.Symbol = rest[i + 1];
                    i++;
                    break;
                case "--to" when i + 1 < rest.Count:
                    outArgs.To = rest[i + 1];
                    i++;
                    break;
                case "--apply":
                    outArgs.Apply = true;
                    break;
                case "--json":
                    outArgs.Json = true;
                    break;
                default:
                    if (!a.StartsWith('-'))
                    {
                        outArgs.Dir = a;
                    }

                    break;
            }

            i++;
        }

        return outArgs;
    }

    internal static int CmdRefactor(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Out.PrintJson(Out.ErrorEnvelopeOf(
                "refactor", "缺少子命令：rename|organize-imports|safe-remove"));
            return 2;
        }

        var sub = args[0];
        if (sub is not ("rename" or "organize-imports" or "safe-remove"))
        {
            Out.PrintJson(Out.ErrorEnvelopeOf("refactor", $"未知子命令 {sub}"));
            return 2;
        }

        var kind = $"refactor.{sub}";
        var a = ParseArgs(args.Skip(1).ToList());
        if (!RefactorInput.TryLoad(a.Dir, out var input, out var loadError) || input is null)
        {
            return Fail(kind, loadError ?? "装载失败", a.Json);
        }

        return sub switch
        {
            "rename" => DoRename(kind, input, a),
            "organize-imports" => DoOrganize(kind, input, a),
            _ => DoRemove(kind, input, a),
        };
    }

    private static int Fail(string kind, string msg, bool asJson)
    {
        if (asJson)
        {
            Out.PrintJson(Out.ErrorEnvelopeOf(kind, msg));
        }
        else
        {
            Console.Error.WriteLine(msg);
        }

        return 1;
    }

    private static Out.WorkspaceEditDto WsJson(WorkspaceEdit ws) => new()
    {
        Edits = [.. ws.Edits.Select(e => new Out.TextEditDto
        {
            File = e.File,
            StartOffset = e.StartOffset,
            EndOffset = e.EndOffset,
            Replacement = e.Replacement,
        })],
        Preconditions = [.. ws.Preconditions.Select(p => new Out.PreconditionDto
        {
            File = p.File,
            FileHash = p.FileHash,
        })],
    };

    private static Out.SymbolRefDto SymbolJson(RefactorInput input, SymbolId target)
    {
        var sym = input.Analysis.Symbols.Get(target);
        return new Out.SymbolRefDto
        {
            Id = target.Value,
            Name = sym.Name,
            Kind = sym.Kind.KindName(),
        };
    }

    /// <summary>dry-run 报告。</summary>
    private static int ReportPlan(string kind, RefactorArgs a, WorkspaceEdit ws, Out.SymbolRefDto? symbol)
    {
        if (a.Json)
        {
            Out.PrintJsonSoft(new Out.RefactorPlanEnvelope
            {
                Kind = kind,
                EditCount = ws.Edits.Count,
                Symbol = symbol,
                WorkspaceEdit = WsJson(ws),
            });
        }
        else
        {
            Console.WriteLine($"重构计划（dry-run，--apply 落盘）：{ws.Edits.Count} 处编辑");
            foreach (var e in ws.Edits)
            {
                Console.WriteLine($"  {e.File}:[{e.StartOffset}..{e.EndOffset}) → \"{e.Replacement}\"");
            }
        }

        return 0;
    }

    /// <summary>应用 + 闭环验证 + 落盘（未被编辑的文件不重写）。</summary>
    private static int ApplyFlow(string kind, RefactorInput input, RefactorArgs a, WorkspaceEdit ws)
    {
        var (contents, error) = Refactorings.ApplyAndVerify(ws, input);
        if (error is not null || contents is null)
        {
            return Fail(kind, error?.ToString() ?? "闭环失败", a.Json);
        }

        var written = 0;
        foreach (var (file, text) in contents)
        {
            if (input.Current().TryGetValue(file, out var cur) && cur == text)
            {
                continue; // 未被编辑的文件不重写
            }

            var path = Path.Combine(a.Dir, file.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                return Fail(kind, $"写 {file}: {ex.Message}", a.Json);
            }

            written++;
        }

        if (a.Json)
        {
            Out.PrintJsonSoft(new Out.RefactorAppliedEnvelope
            {
                Kind = kind,
                EditCount = ws.Edits.Count,
                WrittenFiles = written,
            });
        }
        else
        {
            Console.WriteLine($"已应用 {ws.Edits.Count} 处编辑（写 {written} 个文件），验证通过");
        }

        return 0;
    }

    private static int DoRename(string kind, RefactorInput input, RefactorArgs a)
    {
        if (a.Symbol is null)
        {
            return Fail(kind, "缺少 --symbol 参数", a.Json);
        }

        if (a.To is null)
        {
            return Fail(kind, "缺少 --to 参数", a.Json);
        }

        if (!input.TryResolveTarget(a.Symbol, out var target, out var resolveError))
        {
            return Fail(kind, resolveError ?? "未知符号", a.Json);
        }

        var (edit, refusal) = Refactorings.Rename(input, target, a.To);
        if (refusal is not null || edit is null)
        {
            return Fail(kind, refusal?.ToString() ?? "rename 失败", a.Json);
        }

        return a.Apply ? ApplyFlow(kind, input, a, edit) : ReportPlan(kind, a, edit, SymbolJson(input, target));
    }

    private static int DoOrganize(string kind, RefactorInput input, RefactorArgs a)
    {
        // 每文件独立整理；变化文件以整文件替换编辑表达（Go 证据同口径）
        var edits = new List<TextEdit>();
        var preconditions = new List<Precondition>();
        foreach (var f in input.Sources)
        {
            var (edit, refusal) = Refactorings.OrganizeImports(f.Path, f.Text.Text);
            if (refusal is not null || edit is null)
            {
                return Fail(kind, refusal?.ToString() ?? "organize-imports 失败", a.Json);
            }

            edits.AddRange(edit.Edits);
            preconditions.AddRange(edit.Preconditions);
        }

        var ws = new WorkspaceEdit(edits, preconditions);
        return a.Apply ? ApplyFlow(kind, input, a, ws) : ReportPlan(kind, a, ws, null);
    }

    private static int DoRemove(string kind, RefactorInput input, RefactorArgs a)
    {
        if (a.Symbol is null)
        {
            return Fail(kind, "缺少 --symbol 参数", a.Json);
        }

        if (!input.TryResolveTarget(a.Symbol, out var target, out var resolveError))
        {
            return Fail(kind, resolveError ?? "未知符号", a.Json);
        }

        var verdict = Refactorings.SafeRemove(input, target);
        if (verdict.Error is not null)
        {
            return Fail(kind, verdict.Error switch
            {
                SafeRemoveErrorKind.Unknown => $"未知符号 {a.Symbol}",
                _ => "safe remove 仅支持顶层 fn 与全局变量",
            }, a.Json);
        }

        if (verdict.NotRemovable is { } nr)
        {
            // 不可删：证据面（removable:false + reason + refSites），退出码 1
            if (a.Json)
            {
                Out.PrintJsonSoft(new Out.RefactorNotRemovableEnvelope
                {
                    Kind = kind,
                    Reason = nr.Reason,
                    RefSites = nr.RefSites,
                    Symbol = SymbolJson(input, target),
                });
            }
            else
            {
                Console.Error.WriteLine($"不可删：{nr.Reason}");
                foreach (var s in nr.RefSites)
                {
                    Console.Error.WriteLine($"  引用点 {s}");
                }
            }

            return 1;
        }

        var ws = verdict.Applied!;
        return a.Apply ? ApplyFlow(kind, input, a, ws) : ReportPlan(kind, a, ws, SymbolJson(input, target));
    }
}
