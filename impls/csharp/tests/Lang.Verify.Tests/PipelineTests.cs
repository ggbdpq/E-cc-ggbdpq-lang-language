using Lang.Contract;
using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Verify;
using Xunit;

namespace Lang.Verify.Tests;

/// <summary>
/// Effect 段事实提取（行为证据：impls/rust/crates/lang-verify/src/effects.rs 测试，1:1 对齐）。
/// </summary>
public class EffectFactsTests
{
    private static IReadOnlyList<EffectEntry> Solve(string src)
    {
        var parsed = Parser.Parse(new SourceText("main.lang-go", src), new SourceId(1));
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, new SourceId(1));
        var result = EffectFacts.Solve(parsed.Program, analysis);
        Assert.Null(result.Error);
        return result.Value!;
    }

    private static string[] EffectsOf(IReadOnlyList<EffectEntry> entries, string name) =>
        entries.FirstOrDefault(e => e.Name == name)?.Effects.Select(e => e.AsStr()).ToArray()
        ?? throw new InvalidOperationException($"节点 {name} 应在效果表里");

    /// <summary>直接效果：println→IO；纯函数 = [PURE]。</summary>
    [Fact]
    public void Direct_io_and_pure()
    {
        var src = "fn log() {\n    println(\"x\")\n}\nfn pure(): int {\n    return 1\n}\n";
        var entries = Solve(src);
        Assert.Equal(["IO"], EffectsOf(entries, "log"));
        Assert.Equal(["PURE"], EffectsOf(entries, "pure"));
    }

    /// <summary>1 级传播：main → f → println ⇒ main IO。</summary>
    [Fact]
    public void One_level_propagation()
    {
        var src = "fn f(): int {\n    println(\"x\")\n    return 1\n}\nfn main() {\n    println(f())\n}\n";
        var entries = Solve(src);
        Assert.Equal(["IO"], EffectsOf(entries, "main"));
    }

    /// <summary>3 级传播 + 递归收敛（互递归环上 IO 全员可达）。</summary>
    [Fact]
    public void Three_level_and_mutual_recursion()
    {
        var src = """
            fn a(n: int): int {
                if n <= 0 {
                    return b(n)
                }
                return a(n - 1)
            }
            fn b(n: int): int {
                return c(n)
            }
            fn c(n: int): int {
                println(n)
                return n
            }
            fn main() {
                println(a(3))
            }
            """;
        var entries = Solve(src);
        Assert.Equal(["IO"], EffectsOf(entries, "c"));
        Assert.Equal(["IO"], EffectsOf(entries, "b"));
        Assert.Equal(["IO"], EffectsOf(entries, "a"));
        Assert.Equal(["IO"], EffectsOf(entries, "main"));
    }

    /// <summary>go 语句 → SPAWN 直接效果；go 目标效果沿边传播。</summary>
    [Fact]
    public void Spawn_marks_direct_and_propagates_target()
    {
        var src = "fn work(): int {\n    makeChan(1)\n    return 1\n}\nfn main() {\n    go work()\n    return\n}\n";
        var entries = Solve(src);
        Assert.Equal(["CHANNEL"], EffectsOf(entries, "work"));
        Assert.Equal(["SPAWN", "CHANNEL"], EffectsOf(entries, "main"));
    }

    /// <summary>channel 内建面（makeChan/send/recv → CHANNEL）。</summary>
    [Fact]
    public void Channel_builtins()
    {
        var src = "fn main() {\n    let ch = makeChan(1)\n    send(ch, 1)\n    println(recv(ch))\n}\n";
        Assert.Equal(["IO", "CHANNEL"], EffectsOf(Solve(src), "main"));
    }

    /// <summary>extern → FFI 直接效果，并沿调用边传播到 caller。</summary>
    [Fact]
    public void Extern_ffi_and_caller_propagation()
    {
        var src = "extern \"host\" {\n    fn hostfn(n: int): int\n}\nfn caller(): int {\n    return hostfn(1)\n}\n";
        var entries = Solve(src);
        Assert.Equal(["FFI"], EffectsOf(entries, "hostfn"));
        Assert.Equal(["FFI"], EffectsOf(entries, "caller"));
    }

    /// <summary>方法调用边：接收者静态类型 → struct.methods（效果照常传播）。</summary>
    [Fact]
    public void Method_call_edge_via_receiver_type()
    {
        var src = """
            struct P {
                x: int
            }
            fn (p P).loud(): int {
                println(p.x)
                return p.x
            }
            fn main() {
                let q = P { x: 3 }
                println(q.loud())
            }
            """;
        var entries = Solve(src);
        Assert.Equal(["IO"], EffectsOf(entries, "loud"));
        Assert.Equal(["IO"], EffectsOf(entries, "main"));
    }

    /// <summary>顶层全局初始化的调用效果归属合成 init 节点。</summary>
    [Fact]
    public void Top_level_init_node()
    {
        var src = "fn mk(): int {\n    println(\"init\")\n    return 1\n}\nlet g = mk()\n";
        Assert.Equal(["IO"], EffectsOf(Solve(src), "init"));
    }

    /// <summary>确定性：同一输入两次求解逐项相等。</summary>
    [Fact]
    public void Deterministic()
    {
        var src = "fn f() {\n    println(1)\n}\nfn main() {\n    go f()\n    println(2)\n}\n";
        var e1 = Solve(src);
        var e2 = Solve(src);
        Assert.Equal(e1, e2);
    }
}

/// <summary>
/// 九段管线编排（行为证据：impls/rust/crates/lang-verify/src/pipeline.rs 测试，1:1 对齐；
/// C# 差异：StubVerifyDriver 对 HIR 下降同样 Skipped——C# 管线把 Hir 段也走驱动注入）。
/// </summary>
public class PipelineTests
{
    private const string Green = """
        fn add(a: int, b: int): int {
            return a + b
        }
        fn main() {
            println(add(1, 2))
            let msg = "ok"
            println(msg)
        }
        """;

    private static StageOutcomeKind KindOf(VerifyReport r, Stage s) =>
        r.Stages.First(o => o.Stage == s).Kind;

    private static string NoteOf(VerifyReport r, Stage s) =>
        r.Stages.First(o => o.Stage == s).Note;

    private static VerifyInput Input(string src) =>
        VerifyInput.FromFiles([("main.lang-go", src)]);

    /// <summary>桩驱动：全段在报告里显式可见（Skipped 不缺席），整体为绿（Skipped ≠ 失败）。</summary>
    [Fact]
    public void Stub_pipeline_all_segments_visible_and_green()
    {
        var r = VerifyEngine.RunPipeline(Input(Green), StubVerifyDriver.Instance);
        Assert.Equal(9, r.Stages.Count);
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Parse));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Resolve));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Typecheck));
        Assert.Equal(StageOutcomeKind.Skipped, KindOf(r, Stage.Hir));
        Assert.Contains("桩", NoteOf(r, Stage.Hir));
        foreach (var s in new[]
                 {
                     Stage.Ir, Stage.Effect, Stage.Compile, Stage.Execute, Stage.Differential,
                 })
        {
            Assert.Equal(StageOutcomeKind.Skipped, KindOf(r, s));
            Assert.Contains("短路", NoteOf(r, s));
        }
        Assert.True(r.Passed);
        Assert.Null(r.FailedStage());
    }

    /// <summary>Parse 失败 → 短路：后续段 Skipped 可见。</summary>
    [Fact]
    public void Parse_failure_short_circuits_with_visible_skips()
    {
        var r = VerifyEngine.RunPipeline(Input("fn main( {"), StubVerifyDriver.Instance);
        Assert.Equal(StageOutcomeKind.Failed, KindOf(r, Stage.Parse));
        Assert.Contains("main.lang-go", NoteOf(r, Stage.Parse));
        foreach (var s in new[]
                 {
                     Stage.Resolve, Stage.Typecheck, Stage.Hir, Stage.Ir,
                     Stage.Effect, Stage.Compile, Stage.Execute, Stage.Differential,
                 })
        {
            Assert.Equal(StageOutcomeKind.Skipped, KindOf(r, s));
            Assert.Contains("上游 Parse", NoteOf(r, s));
        }
        Assert.False(r.Passed);
        Assert.Equal(Stage.Parse, r.FailedStage());
    }

    /// <summary>Resolve 失败（E2002 未定义）→ typecheck 起短路。</summary>
    [Fact]
    public void Resolve_failure_short_circuits()
    {
        var src = "fn main() {\n    println(ghost)\n}\n";
        var r = VerifyEngine.RunPipeline(Input(src), StubVerifyDriver.Instance);
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Parse));
        Assert.Equal(StageOutcomeKind.Failed, KindOf(r, Stage.Resolve));
        Assert.Contains("E2002", NoteOf(r, Stage.Resolve));
        Assert.Equal(StageOutcomeKind.Skipped, KindOf(r, Stage.Typecheck));
        Assert.Contains("上游 Resolve", NoteOf(r, Stage.Typecheck));
        Assert.Equal(Stage.Resolve, r.FailedStage());
    }

    /// <summary>Typecheck 失败（E30xx 类型不匹配）→ hir 起短路。</summary>
    [Fact]
    public void Typecheck_failure_short_circuits()
    {
        var src = "fn main() {\n    let x: int = \"s\"\n    println(x)\n}\n";
        var r = VerifyEngine.RunPipeline(Input(src), StubVerifyDriver.Instance);
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Parse));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Resolve));
        Assert.Equal(StageOutcomeKind.Failed, KindOf(r, Stage.Typecheck));
        Assert.Contains("E30", NoteOf(r, Stage.Typecheck));
        Assert.Equal(StageOutcomeKind.Skipped, KindOf(r, Stage.Hir));
        Assert.Equal(Stage.Typecheck, r.FailedStage());
    }

    /// <summary>DefaultVerifyDriver（A3 lowering + A5 codegen 已接线）：ir/compile 段全过；
    /// 宿主路视 go/node 工具链参与差分（缺失显式点名）。</summary>
    [Fact]
    public void Default_driver_full_chain_through_lowering()
    {
        var r = VerifyEngine.RunPipeline(Input(Green), new DefaultVerifyDriver());
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Parse));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Resolve));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Typecheck));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Hir));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Ir));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Effect));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Compile));
        // 执行段：ast/ir 待 A4（Skipped 点名）；go/js 视宿主工具链
        var exec = KindOf(r, Stage.Execute);
        Assert.True(exec is StageOutcomeKind.Passed or StageOutcomeKind.Skipped,
            $"execute 实际: {exec}（{NoteOf(r, Stage.Execute)}）");
        var diff = KindOf(r, Stage.Differential);
        Assert.True(
            diff == StageOutcomeKind.Passed
            || (diff == StageOutcomeKind.Skipped
                && (NoteOf(r, Stage.Differential).Contains("已比对")
                    || NoteOf(r, Stage.Differential).Contains("可比执行路不足"))),
            $"differential 实际: {diff}（{NoteOf(r, Stage.Differential)}）");
        Assert.True(r.Passed);
        Assert.Null(r.FailedStage());
    }

    /// <summary>多文件输入 + Import 滤除：合并点滤除后执行段消费的 merged 无 Import
    /// （对 lang-conformance load_case 同坑的管线侧修复锁定；检测驱动断言）。</summary>
    [Fact]
    public void Import_statements_filtered_for_execution()
    {
        var input = VerifyInput.FromFiles(
        [
            ("main.lang-go", "import \"./util\"\nfn main() {\n    println(twice(21))\n}\n"),
            ("util.lang-go", "fn twice(x: int): int {\n    return x * 2\n}\n"),
        ]);
        var seenImport = false;
        var driver = new DefaultVerifyDriver(
            runAst: ctx =>
            {
                seenImport = ctx.Stmts.Any(s => s is Stmt.Import);
                return ExecResult<RunOutput>.Ok(new RunOutput("42\n", "", 0));
            });
        var r = VerifyEngine.RunPipeline(input, driver);
        Assert.False(seenImport, "合并点应滤除 Import 语句");
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Execute)); // ast 路真跑
        Assert.True(r.Passed);
    }
}
