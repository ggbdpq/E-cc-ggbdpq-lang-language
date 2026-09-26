using Lang.Edit;
using Lang.Refactor;
using Xunit;

namespace Lang.Refactor.Tests;

/// <summary>
/// lang-refactor E2E：真实夹具目录上的三入口闭环（行为证据：
/// impls/rust/crates/lang-refactor/tests/e2e.rs，1:1 对齐矩阵）。
/// 夹具落系统临时目录，Dispose 清理（不落仓库）。
/// </summary>
public class RefactorE2E
{

    // —— 矩阵 1：rename 跨文件闭环 ——

    private const string UtilSrc = """
        fn scale(x: int, k: int): int {
            return x * k
        }
        fn shrink(x: int): int {
            return scale(x, 1)
        }
        """;

    private const string MainSrc = """
        import "./util"
        fn main() {
            let r = scale(21, 2)
            println(r)
        }
        """;

    /// <summary>R1：跨文件 rename（main+util）→ 3 处编辑跨 2 文件 → 闭环 → 落盘重载零回归。</summary>
    [Fact]
    public void R1_rename_cross_file_closed_loop()
    {
        var e2e = Fixture.New("rename", ("main.lang-go", MainSrc), ("util.lang-go", UtilSrc));
        try
        {
            var input = e2e.Load();
            Assert.True(input.Analysis.Diags.Count == 0, "夹具应零诊断");
            Assert.True(input.TryResolveTarget("scale", out var target, out _));
            var baseline = input.BaselineCodes();

            // dry-run 计划：定义位 + util 内调用点 + main 调用点 = 3 处编辑、跨 2 文件
            var (ws, refusal) = Refactorings.Rename(input, target, "multiply");
            Assert.Null(refusal);
            Assert.Equal(3, ws!.Edits.Count);
            var files = ws.Edits.Select(e => e.File).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["main.lang-go", "util.lang-go"], files);
            // 每个涉及文件都有独立 precondition（sha256）
            Assert.Equal(2, ws.Preconditions.Count);

            // 闭环：precondition → apply → format → reparse → re-analyze → 零回归
            var (contents, error) = Refactorings.ApplyAndVerify(ws, input);
            Assert.Null(error);

            // 落盘（CLI 落盘面的等价动作），再重载验证语义事实
            foreach (var (file, text) in contents!)
            {
                e2e.Write(file, text);
            }
            var reloaded = e2e.Load();
            Assert.Empty(reloaded.Analysis.Diags);
            Assert.Equal(baseline, reloaded.BaselineCodes()); // 诊断零回归
            // 新名解析得到同形状符号；旧名消失
            Assert.True(reloaded.TryResolveTarget("multiply", out _, out _));
            Assert.False(reloaded.TryResolveTarget("scale", out _, out _));
            // 文本事实：定义与全部调用点已改名
            Assert.Contains("fn multiply(x: int, k: int): int", e2e.Read("util.lang-go"));
            Assert.Contains("return multiply(x, 1)", e2e.Read("util.lang-go"));
            Assert.Contains("let r = multiply(21, 2)", e2e.Read("main.lang-go"));
            Assert.DoesNotContain("scale", e2e.Read("util.lang-go"));
            Assert.DoesNotContain("scale", e2e.Read("main.lang-go"));
        }
        finally
        {
            e2e.Dispose();
        }
    }

    /// <summary>R2：rename 后外部改动文件 → 盘上事务哈希不符拒绝、两个文件零字节半写。</summary>
    [Fact]
    public void R2_rename_precondition_failure_rejects()
    {
        var e2e = Fixture.New("rename-stale", ("main.lang-go", MainSrc), ("util.lang-go", UtilSrc));
        try
        {
            var input = e2e.Load();
            Assert.True(input.TryResolveTarget("scale", out var target, out _));
            var (ws, refusal) = Refactorings.Rename(input, target, "multiply");
            Assert.Null(refusal);

            // 外部改动 main.lang-go（源码自分析后已变化）：盘上事务读当前内容对哈希
            e2e.Write("main.lang-go", MainSrc + "\nlet stale = 1\n");
            var err = EditTransaction.ApplyToDirectory(ws!, e2e.Dir);
            Assert.Equal(ApplyErrorKind.PreconditionFailed, err!.Kind);
            // 文件未被半写：外部改动原样保留，计划编辑未落任何一字节
            Assert.Contains("let stale = 1", e2e.Read("main.lang-go"));
            Assert.DoesNotContain("multiply", e2e.Read("main.lang-go"));
            Assert.DoesNotContain("multiply", e2e.Read("util.lang-go"));
        }
        finally
        {
            e2e.Dispose();
        }
    }

    /// <summary>R3：struct/enum 目标显式拒绝；新名冲突拒绝。</summary>
    [Fact]
    public void R3_rename_conservative_refusals()
    {
        var src = """
            struct P {
                x: int
            }
            enum E {
                A
            }
            fn alpha(): int {
                return 1
            }
            fn beta(): int {
                return 2
            }
            fn main() {
                println(alpha())
                println(beta())
            }
            """;
        var e2e = Fixture.New("rename-refuse", ("main.lang-go", src));
        try
        {
            var input = e2e.Load();
            Assert.True(input.TryResolveTarget("P", out var p, out _));
            Assert.Equal(RenameRefusalKind.UnsupportedKind,
                Refactorings.Rename(input, p, "Q").Refusal!.Kind);
            Assert.True(input.TryResolveTarget("E", out var en, out _));
            Assert.Equal(RenameRefusalKind.UnsupportedKind,
                Refactorings.Rename(input, en, "F").Refusal!.Kind);
            // 新名冲突（beta 已存在）→ 拒绝
            Assert.True(input.TryResolveTarget("alpha", out var a, out _));
            Assert.Equal(RenameRefusalKind.Conflict,
                Refactorings.Rename(input, a, "beta").Refusal!.Kind);
        }
        finally
        {
            e2e.Dispose();
        }
    }

    // —— 矩阵 2：organize-imports ——

    /// <summary>R4：乱序+重复 import（含被引模块文件）→ 排序去重、行尾注释随行、空行与函数体不动；落盘重载零诊断。</summary>
    [Fact]
    public void R4_organize_imports_sorts_and_dedupes()
    {
        const string messy = """
            import "./zeta"
            import "./alpha" // 首组
            import "./zeta"
            import "./mid"

            fn main() {
                println("x")
            }
            """;
        // 被 import 的模块必须真实存在（重载走 loader 的 import 展开）
        var e2e = Fixture.New("imports",
            ("main.lang-go", messy + "\n"),
            ("alpha.lang-go", "let a = 1\n"),
            ("mid.lang-go", "let m = 2\n"),
            ("zeta.lang-go", "let z = 3\n"));
        try
        {
            var source = e2e.Read("main.lang-go");
            var (ws, refusal) = Refactorings.OrganizeImports("main.lang-go", source);
            Assert.Null(refusal);
            // 单点区间替换编辑 + 独立 precondition
            Assert.Single(ws!.Edits);
            Assert.Single(ws.Preconditions);

            var current = new Dictionary<string, string> { ["main.lang-go"] = source };
            var (applied, error) = EditTransaction.Apply(current, ws);
            Assert.Null(error);
            var got = applied!["main.lang-go"];
            Assert.Equal(
                "import \"./alpha\" // 首组\nimport \"./mid\"\nimport \"./zeta\"\n\nfn main() {\n    println(\"x\")\n}\n",
                got);

            // 落盘后重载分析仍零诊断（import 重排不引入语义变化）
            e2e.Write("main.lang-go", got);
            Assert.Empty(e2e.Load().Analysis.Diags);
        }
        finally
        {
            e2e.Dispose();
        }
    }

    /// <summary>R5：import 区间夹代码 → Scattered 保守拒绝。</summary>
    [Fact]
    public void R5_organize_imports_refuses_scattered_code()
    {
        var src = "import \"./b\"\nlet x = 1\nimport \"./a\"\nfn main() {}\n";
        var (ws, refusal) = Refactorings.OrganizeImports("main.lang-go", src);
        Assert.Null(ws);
        Assert.Equal(OrganizeRefusalKind.Scattered, refusal!.Kind);
    }

    // —— 矩阵 3：safe-remove ——

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

    /// <summary>R6：被引用顶层 fn → 拒绝 + refSites（path:line:col）证据。</summary>
    [Fact]
    public void R6_safe_remove_refuses_referenced_fn_with_sites()
    {
        var e2e = Fixture.New("remove-used", ("main.lang-go", RemovableSrc));
        try
        {
            var input = e2e.Load();
            Assert.True(input.TryResolveTarget("used", out var target, out _));
            var verdict = Refactorings.SafeRemove(input, target);
            Assert.Null(verdict.Error);
            Assert.Null(verdict.Applied);
            var evidence = verdict.NotRemovable!;
            Assert.Contains("引用", evidence.Reason);
            Assert.NotEmpty(evidence.RefSites);
            Assert.StartsWith("main.lang-go:", evidence.RefSites[0]);
        }
        finally
        {
            e2e.Dispose();
        }
    }

    /// <summary>R7：未引用顶层 fn → 删除编辑 → 闭环 → 声明消失、无残留空行、重载零诊断。</summary>
    [Fact]
    public void R7_safe_remove_removes_unreferenced_fn()
    {
        var e2e = Fixture.New("remove-unused", ("main.lang-go", RemovableSrc));
        try
        {
            var input = e2e.Load();
            Assert.True(input.TryResolveTarget("unused", out var target, out _));
            var verdict = Refactorings.SafeRemove(input, target);
            Assert.Null(verdict.Error);
            Assert.Null(verdict.NotRemovable);
            Assert.Single(verdict.Applied!.Edits);

            var (contents, error) = Refactorings.ApplyAndVerify(verdict.Applied, input);
            Assert.Null(error);
            e2e.Write("main.lang-go", contents!["main.lang-go"]);
            var got = e2e.Read("main.lang-go");
            Assert.DoesNotContain("unused", got); // 声明已删除
            Assert.Contains("fn used", got); // 其余声明保留
            Assert.DoesNotContain("\n\n\n", got); // 删除不残留空行
            var reloaded = e2e.Load();
            Assert.Empty(reloaded.Analysis.Diags);
            Assert.False(reloaded.TryResolveTarget("unused", out _, out _));
            Assert.True(reloaded.TryResolveTarget("used", out _, out _));
        }
        finally
        {
            e2e.Dispose();
        }
    }

    /// <summary>R8：方法/局部变量目标 → 「仅支持顶层 fn 与全局变量」拒绝。</summary>
    [Fact]
    public void R8_safe_remove_refuses_method_and_local()
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
        var e2e = Fixture.New("remove-kind", ("main.lang-go", src));
        try
        {
            var input = e2e.Load();
            Assert.True(input.TryResolveTarget("m", out var m, out _));
            var verdict = Refactorings.SafeRemove(input, m);
            Assert.Null(verdict.Error);
            Assert.Contains("仅支持顶层 fn 与全局变量", verdict.NotRemovable!.Reason);
        }
        finally
        {
            e2e.Dispose();
        }
    }
}

/// <summary>夹具目录（系统临时目录；Dispose 清理，不落仓库）。</summary>
internal sealed class Fixture : IDisposable
{
    private readonly string _dir;

    private Fixture(string tag, params (string Name, string Src)[] files)
    {
        _dir = Path.Combine(Path.GetTempPath(),
            $"lang-refactor-e2e-{tag}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        foreach (var (name, src) in files)
        {
            File.WriteAllText(Path.Combine(_dir, name), src);
        }
    }

    public static Fixture New(string tag, params (string Name, string Src)[] files) => new(tag, files);

    public string Dir => _dir;

    public string Read(string name) => File.ReadAllText(Path.Combine(_dir, name));

    public void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    public RefactorInput Load()
    {
        Assert.True(RefactorInput.TryLoad(_dir, out var input, out var err), err ?? "");
        return input!;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
