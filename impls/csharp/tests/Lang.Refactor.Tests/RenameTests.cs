using Lang.Core;
using Lang.Edit;
using Lang.Refactor;
using Xunit;

namespace Lang.Refactor.Tests;

/// <summary>rename 计划语义（行为证据：impls/rust/crates/lang-refactor/src/rename.rs 测试，1:1 对齐）。</summary>
public class RenameTests
{
    private const string Src = """
        fn add(a: int, b: int): int {
            return a + b
        }
        fn twice(x: int): int {
            return add(x, x)
        }
        fn main() {
            let s = add(1, 2)
            println(s)
        }
        """;

    private static RefactorInput Input(string src)
    {
        Assert.True(RefactorInput.TryFromFiles([("main.lang-go", src)], out var input, out var err),
            err ?? "装载失败");
        return input!;
    }

    private static SymbolId ByName(RefactorInput input, string name)
    {
        Assert.True(input.TryResolveTarget(name, out var id, out var err), err ?? "");
        return id;
    }

    private static string Apply(RefactorInput input, WorkspaceEdit ws)
    {
        var (applied, error) = Lang.Edit.EditTransaction.Apply(input.Current(), ws);
        Assert.Null(error);
        return applied!["main.lang-go"];
    }

    [Fact]
    public void Rename_fn_rewrites_decl_and_all_call_sites()
    {
        var inp = Input(Src);
        var (ws, refusal) = Refactorings.Rename(inp, ByName(inp, "add"), "sum");
        Assert.Null(refusal);
        // 定义位 + 两处调用点
        Assert.Equal(3, ws!.Edits.Count);
        var outText = Apply(inp, ws);
        Assert.Contains("fn sum(a: int, b: int): int", outText);
        Assert.Contains("sum(x, x)", outText);
        Assert.Contains("let s = sum(1, 2)", outText);
        Assert.DoesNotContain("add(", outText);
    }

    [Fact]
    public void Rename_var_covers_read_and_write()
    {
        var src = "fn main() {\n    var n = 1\n    n = n + 1\n    println(n)\n}\n";
        var inp = Input(src);
        var (ws, refusal) = Refactorings.Rename(inp, ByName(inp, "n"), "acc");
        Assert.Null(refusal);
        var outText = Apply(inp, ws!);
        Assert.Contains("var acc = 1", outText);
        Assert.Contains("acc = acc + 1", outText);
        Assert.Contains("println(acc)", outText);
    }

    [Fact]
    public void Rename_param_rewrites_decl_and_body_refs()
    {
        var src = "fn f(alpha: int): int {\n    return alpha + 1\n}\n";
        var inp = Input(src);
        var (ws, refusal) = Refactorings.Rename(inp, ByName(inp, "alpha"), "base");
        Assert.Null(refusal);
        var outText = Apply(inp, ws!);
        Assert.Contains("fn f(base: int): int", outText);
        Assert.Contains("return base + 1", outText);
    }

    [Fact]
    public void Rename_method_covers_call_site_via_receiver_type()
    {
        var src = """
            struct P {
                x: int
            }
            fn (p P).len2(): int {
                return p.x * p.x
            }
            fn main() {
                let q = P { x: 3 }
                println(q.len2())
            }
            """;
        var inp = Input(src);
        var (ws, refusal) = Refactorings.Rename(inp, ByName(inp, "len2"), "area");
        Assert.Null(refusal);
        var outText = Apply(inp, ws!);
        Assert.Contains("fn (p P).area(): int", outText);
        Assert.Contains("q.area()", outText);
        Assert.DoesNotContain("len2", outText);
    }

    [Fact]
    public void Rename_match_binding_works()
    {
        var src = """
            enum E {
                A
            }
            fn main() {
                let v = match A {
                    A => 7
                }
                println(v)
            }
            """;
        var inp = Input(src);
        var (ws, refusal) = Refactorings.Rename(inp, ByName(inp, "v"), "answer");
        Assert.Null(refusal);
        var outText = Apply(inp, ws!);
        Assert.Contains("let answer = match A", outText);
        Assert.Contains("println(answer)", outText);
    }

    [Fact]
    public void Struct_rename_refused()
    {
        var src = "struct P {\n    x: int\n}\nfn main() {}\n";
        var inp = Input(src);
        var (_, refusal) = Refactorings.Rename(inp, ByName(inp, "P"), "Q");
        Assert.Equal(RenameRefusalKind.UnsupportedKind, refusal!.Kind);
    }

    [Fact]
    public void Conflict_with_existing_name_refused()
    {
        var inp = Input(Src);
        var (_, refusal) = Refactorings.Rename(inp, ByName(inp, "add"), "twice");
        Assert.Equal(RenameRefusalKind.Conflict, refusal!.Kind);
    }

    [Fact]
    public void Invalid_and_reserved_names_refused()
    {
        var inp = Input(Src);
        var t = ByName(inp, "add");
        Assert.Equal(RenameRefusalKind.InvalidName, Refactorings.Rename(inp, t, "1bad").Refusal!.Kind);
        Assert.Equal(RenameRefusalKind.InvalidName, Refactorings.Rename(inp, t, "fn").Refusal!.Kind);
        Assert.Equal(RenameRefusalKind.InvalidName, Refactorings.Rename(inp, t, "println").Refusal!.Kind);
        Assert.Equal(RenameRefusalKind.InvalidName, Refactorings.Rename(inp, t, "").Refusal!.Kind);
    }

    [Fact]
    public void Untyped_receiver_refuses_conservatively()
    {
        // 无注解参数作接收者 → 同名调用点类型未知 → 拒绝（不半改）
        var src = """
            struct P {
                x: int
            }
            fn (p P).m(): int {
                return p.x
            }
            fn probe(q) {
                return q.m()
            }
            fn main() {
                let z = P { x: 3 }
                println(z.m())
                println(probe(z))
            }
            """;
        var inp = Input(src);
        var (_, refusal) = Refactorings.Rename(inp, ByName(inp, "m"), "n");
        Assert.Equal(RenameRefusalKind.UntypedReceiver, refusal!.Kind);
    }

    [Fact]
    public void Resolve_target_by_name_and_id()
    {
        var inp = Input(Src);
        Assert.True(inp.TryResolveTarget("add", out var add, out _));
        Assert.True(inp.TryResolveTarget(add.Value.ToString(), out var byNum, out _));
        Assert.Equal(add, byNum);
        // 预载符号 id 不可指名
        Assert.False(inp.TryResolveTarget("2", out _, out _));
        Assert.False(inp.TryResolveTarget("999", out _, out _));
        Assert.False(inp.TryResolveTarget("", out _, out _));
    }

    /// <summary>冻结 IRefactoring 三入口（会话形态）签名透传：rename / organize-imports / safe-remove。</summary>
    [Fact]
    public void Frozen_session_interface_passthrough()
    {
        var inp = Input(Src);
        var session = new RefactoringSession(inp);
        Assert.True(inp.TryResolveTarget("add", out var add, out _));
        var (edit, refusal) = session.Rename(add, "sum");
        Assert.Null(refusal);
        Assert.Equal(3, edit!.Edits.Count);

        var (orgEdit, orgRefusal) = session.OrganizeImports("main.lang-go",
            "import \"./zeta\"\nimport \"./alpha\"\nimport \"./zeta\"\n\nfn main() {\n    println(\"x\")\n}\n");
        Assert.Null(orgRefusal);
        Assert.Single(orgEdit!.Edits);

        // twice 从未被引用 → 可删（单条删除编辑）
        Assert.True(inp.TryResolveTarget("twice", out var twice, out _));
        var (rmEdit, rmEvidence, rmError) = session.SafeRemove(twice);
        Assert.Null(rmError);
        Assert.Null(rmEvidence);
        Assert.Single(rmEdit!.Edits);
    }
}
