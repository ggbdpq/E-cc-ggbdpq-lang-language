using Lang.Edit;
using Lang.Refactor;
using Xunit;

namespace Lang.Refactor.Tests;

/// <summary>organize-imports 与 safe-remove 计划语义（证据：impls/rust/crates/lang-refactor/src/{imports,remove}.rs）。</summary>
public class ImportsRemoveTests
{
    private static (WorkspaceEdit? Edit, string? Error) Organize(string src)
    {
        var (ws, refusal) = Refactorings.OrganizeImports("main.lang-go", src);
        return (ws, refusal?.Message);
    }

    private static string? Apply(string src, WorkspaceEdit ws)
    {
        var current = new Dictionary<string, string> { ["main.lang-go"] = src };
        var (applied, error) = EditTransaction.Apply(current, ws);
        Assert.Null(error);
        return applied!.GetValueOrDefault("main.lang-go");
    }

    [Fact]
    public void Sorts_and_dedupes()
    {
        var src = "import \"./zeta\"\nimport \"./alpha\"\nimport \"./zeta\"\n\nfn main() {\n    println(\"x\")\n}\n";
        var (ws, err) = Organize(src);
        Assert.Null(err);
        Assert.Equal(
            "import \"./alpha\"\nimport \"./zeta\"\n\nfn main() {\n    println(\"x\")\n}\n",
            Apply(src, ws!));
    }

    [Fact]
    public void Trailing_comments_travel_with_lines()
    {
        var src = "import \"./b\" // 后注 B\nimport \"./a\" // 后注 A\nfn main() {}\n";
        var (ws, err) = Organize(src);
        Assert.Null(err);
        Assert.Equal(
            "import \"./a\" // 后注 A\nimport \"./b\" // 后注 B\nfn main() {}\n",
            Apply(src, ws!));
    }

    [Fact]
    public void Standalone_comment_preserved()
    {
        var src = "import \"./b\"\n// 工具组\nimport \"./a\"\nfn main() {}\n";
        var (ws, err) = Organize(src);
        Assert.Null(err);
        var got = Apply(src, ws!);
        var commentAt = got!.IndexOf("// 工具组", StringComparison.Ordinal);
        var importA = got.IndexOf("import \"./a\"", StringComparison.Ordinal);
        var importB = got.IndexOf("import \"./b\"", StringComparison.Ordinal);
        Assert.True(commentAt >= 0);
        Assert.True(commentAt < importA);
        Assert.True(importA < importB);
    }

    [Fact]
    public void Already_organized_is_noop()
    {
        Assert.Empty(Organize("import \"./a\"\nimport \"./b\"\nfn main() {}\n").Edit!.Edits);
        Assert.Empty(Organize("fn main() {}\n").Edit!.Edits);
        Assert.Empty(Organize("import \"./only\"\nfn main() {}\n").Edit!.Edits);
    }

    [Fact]
    public void Code_inside_region_refuses()
    {
        var src = "import \"./b\"\nlet x = 1\nimport \"./a\"\nfn main() {}\n";
        var (ws, err) = Organize(src);
        Assert.Null(ws);
        Assert.Contains("夹有代码", err);
    }

    // —— safe-remove ——

    private const string RemovableSrc = """
        fn used(): int {
            return 1
        }
        fn unused(): int {
            return 2
        }
        fn main() {
            println(used())
        }
        """;

    private static RefactorInput Input(string src)
    {
        Assert.True(RefactorInput.TryFromFiles([("main.lang-go", src)], out var input, out var err),
            err ?? "");
        return input!;
    }

    [Fact]
    public void Safe_remove_refuses_referenced_fn_with_sites()
    {
        var inp = Input(RemovableSrc);
        Assert.True(inp.TryResolveTarget("used", out var target, out _));
        var verdict = Refactorings.SafeRemove(inp, target);
        Assert.Null(verdict.Error);
        Assert.Null(verdict.Applied);
        Assert.NotNull(verdict.NotRemovable);
        Assert.Contains("引用", verdict.NotRemovable.Reason);
        Assert.NotEmpty(verdict.NotRemovable.RefSites);
        // 证据形态 path:line:col（1 基）
        Assert.StartsWith("main.lang-go:", verdict.NotRemovable.RefSites[0]);
    }

    [Fact]
    public void Safe_remove_removes_unreferenced_fn()
    {
        var inp = Input(RemovableSrc);
        Assert.True(inp.TryResolveTarget("unused", out var target, out _));
        var verdict = Refactorings.SafeRemove(inp, target);
        Assert.Null(verdict.Error);
        Assert.Null(verdict.NotRemovable);
        Assert.Single(verdict.Applied!.Edits);

        // 应用：声明消失、无残留空行
        var (applied, error) = EditTransaction.Apply(inp.Current(), verdict.Applied);
        Assert.Null(error);
        var got = applied!["main.lang-go"];
        Assert.DoesNotContain("unused", got);
        Assert.Contains("fn used", got);
        Assert.DoesNotContain("\n\n\n", got);
    }

    [Fact]
    public void Safe_remove_refuses_method_and_local()
    {
        var src = """
            struct P {
                x: int
            }
            fn (p P).m(): int {
                let local = 1
                return p.x + local
            }
            fn main() {
                let q = P { x: 3 }
                println(q.m())
            }
            """;
        var inp = Input(src);
        Assert.True(inp.TryResolveTarget("m", out var m, out _));
        var verdict = Refactorings.SafeRemove(inp, m);
        Assert.Null(verdict.Error);
        Assert.Contains("仅支持顶层 fn 与全局变量", verdict.NotRemovable!.Reason);

        Assert.True(inp.TryResolveTarget("local", out var local, out _));
        var v2 = Refactorings.SafeRemove(inp, local);
        Assert.Contains("仅支持顶层 fn 与全局变量", v2.NotRemovable!.Reason);
    }

    [Fact]
    public void Safe_remove_extern_refused()
    {
        var src = "extern \"host\" {\n    fn hostfn(n: int): int\n}\n";
        var inp = Input(src);
        Assert.True(inp.TryResolveTarget("hostfn", out var target, out _));
        var verdict = Refactorings.SafeRemove(inp, target);
        Assert.Null(verdict.Error);
        Assert.Contains("extern", verdict.NotRemovable!.Reason);
    }

    [Fact]
    public void Safe_remove_unknown_target()
    {
        var inp = Input(RemovableSrc);
        var verdict = Refactorings.SafeRemove(inp, new Lang.Core.SymbolId(9999));
        Assert.Null(verdict.Applied);
        Assert.Null(verdict.NotRemovable);
        Assert.Equal(SafeRemoveErrorKind.Unknown, verdict.Error);
    }
}
