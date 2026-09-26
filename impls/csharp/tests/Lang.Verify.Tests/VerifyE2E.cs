using Lang.Contract;
using Lang.Hir;
using Lang.Ir;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Verify;
using Xunit;

namespace Lang.Verify.Tests;

/// <summary>
/// lang-verify E2E：真实夹具上的九段管线（行为证据：
/// impls/rust/crates/lang-verify/tests/e2e.rs，1:1 对齐矩阵；本机执行路实况见状态文件）。
/// 夹具落系统临时目录，Dispose 清理（不落仓库）。
/// </summary>
public class VerifyE2E : IDisposable
{
    /// <summary>全绿夹具：跨函数调用 + 字符串绑定 + 两次输出（多路逐字节可比）。</summary>
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

    private const string ExpectedStdout = "3\nok\n";

    private readonly string _dir;

    public VerifyE2E()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            $"lang-verify-e2e-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
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

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    /// <summary>包装 DefaultVerifyDriver：记录各路真跑 stdout（差分逐字节断言用），可篡改 js 路。</summary>
    private sealed class RecordingDriver : IVerifyDriver
    {
        private readonly DefaultVerifyDriver _inner = new();
        private readonly bool _tamperJs;

        public RecordingDriver(bool tamperJs = false) => _tamperJs = tamperJs;

        public List<(string Route, string Stdout)> Outputs { get; } = [];

        public ExecResult<HirProg> LowerHir(IReadOnlyList<Stmt> stmts, Analysis analysis) =>
            _inner.LowerHir(stmts, analysis);

        public ExecResult<IrProgram> LowerIr(HirProg hir, Analysis analysis) =>
            _inner.LowerIr(hir, analysis);

        public ExecResult<string> GenerateGo(IrProgram prog) => _inner.GenerateGo(prog);

        public ExecResult<string> GenerateJs(IrProgram prog) => _inner.GenerateJs(prog);

        public ExecResult<RunOutput> RunAst(ExecContext ctx) => _inner.RunAst(ctx);

        public ExecResult<RunOutput> RunIr(IrProgram prog) => _inner.RunIr(prog);

        public ExecResult<RunOutput> RunGo(IrProgram prog)
        {
            var r = _inner.RunGo(prog);
            if (r.Error is null)
            {
                Outputs.Add(("go", r.Value!.Stdout));
            }
            return r;
        }

        public ExecResult<RunOutput> RunJs(IrProgram prog)
        {
            var r = _inner.RunJs(prog);
            if (r.Error is null)
            {
                var stdout = r.Value!.Stdout;
                Outputs.Add(("js", _tamperJs ? "4\nWRONG\n" : stdout));
                return _tamperJs
                    ? ExecResult<RunOutput>.Ok(new RunOutput("4\nWRONG\n", r.Value.Stderr, r.Value.ExitCode))
                    : r;
            }
            return r;
        }
    }

    /// <summary>V1：全绿夹具 + DefaultVerifyDriver 全链（目录动词形）→ 前八段判据 + 差分。</summary>
    [Fact]
    public void V1_green_fixture_full_chain_dir_verb()
    {
        Write("main.lang-go", Green);
        var driver = new RecordingDriver();
        var r = VerifyEngine.Verify(_dir, driver);

        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Parse));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Resolve));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Typecheck));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Hir));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Ir));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Effect));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Compile));
        // 执行段：宿主工具链就绪即 Passed（ast/ir 路待 A4，Skipped 点名）
        Assert.True(KindOf(r, Stage.Execute) == StageOutcomeKind.Passed,
            $"execute={KindOf(r, Stage.Execute)}: {NoteOf(r, Stage.Execute)} | 全报告: {Dump(r)}");
        Assert.True(r.Passed);
        Assert.Null(r.FailedStage());

        // 差分判据：可比路（go/js 真跑）stdout 逐字节独立断言（不依赖差分消息）
        if (KindOf(r, Stage.Differential) == StageOutcomeKind.Passed)
        {
            Assert.Equal(2, driver.Outputs.Count); // go + js 真跑
            Assert.All(driver.Outputs, o => Assert.Equal(ExpectedStdout, o.Stdout));
            Assert.Equal(["go", "js"], driver.Outputs.Select(o => o.Route).ToList());
        }
        else
        {
            // 工具缺失的环境性跳过必须点名缺失路（不谎报全路一致）
            var note = NoteOf(r, Stage.Differential);
            Assert.True(note.Contains("已比对") || note.Contains("可比执行路不足"), note);
            Assert.All(driver.Outputs, o => Assert.Equal(ExpectedStdout, o.Stdout));
        }
    }

    /// <summary>V2：verify_files 动词形（内存文件集）→ 与 V1 同链路。</summary>
    [Fact]
    public void V2_verify_files_memory_input()
    {
        var r = VerifyEngine.VerifyFiles([("main.lang-go", Green)]);
        Assert.True(r.Passed);
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Ir));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Effect));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Compile));
        Assert.True(KindOf(r, Stage.Execute) == StageOutcomeKind.Passed,
            $"execute: {Dump(r)}");
        Assert.Null(r.FailedStage());
    }

    /// <summary>V3：前端失败夹具 ×3 → 失败段 + 短路 Skipped 全在册、failed_stage 按执行序。</summary>
    [Fact]
    public void V3_frontend_failures_short_circuit()
    {
        // parse 失败
        var rp = VerifyEngine.VerifyFiles([("main.lang-go", "fn main( {")], StubVerifyDriver.Instance);
        Assert.Equal(Stage.Parse, rp.FailedStage());
        Assert.Equal(9, rp.Stages.Count);

        // resolve 失败
        var rr = VerifyEngine.VerifyFiles(
            [("main.lang-go", "fn main() {\n    println(ghost)\n}\n")], StubVerifyDriver.Instance);
        Assert.Equal(Stage.Resolve, rr.FailedStage());
        Assert.Contains("E2002", NoteOf(rr, Stage.Resolve));

        // typecheck 失败
        var rt = VerifyEngine.VerifyFiles(
            [("main.lang-go", "fn main() {\n    let x: int = \"s\"\n    println(x)\n}\n")],
            StubVerifyDriver.Instance);
        Assert.Equal(Stage.Typecheck, rt.FailedStage());
        Assert.Contains("E30", NoteOf(rt, Stage.Typecheck));
    }

    /// <summary>V4：篡改 js 路输出 → Differential Failed 带证据、failed_stage=Differential。</summary>
    [Fact]
    public void V4_tampered_js_route_fails_differential()
    {
        var driver = new RecordingDriver(tamperJs: true);
        var r = VerifyEngine.VerifyFiles([("main.lang-go", Green)], driver);
        Assert.False(r.Passed);
        Assert.Equal(Stage.Differential, r.FailedStage());
        Assert.Equal(StageOutcomeKind.Failed, KindOf(r, Stage.Differential));
        Assert.Contains("不一致", NoteOf(r, Stage.Differential));
        Assert.Contains("WRONG", NoteOf(r, Stage.Differential));
        // 失败证据含两路 stdout 逐字节文本（真实换行内嵌于引号内）
        Assert.Contains("4\nWRONG\n", NoteOf(r, Stage.Differential));
        Assert.Contains(ExpectedStdout, NoteOf(r, Stage.Differential));
    }

    /// <summary>V5：StubExec + 全绿夹具 → 前端三段真跑、其余全 Skipped 在册、整体绿。</summary>
    [Fact]
    public void V5_stub_driver_exec_segments_all_skipped_but_green()
    {
        var r = VerifyEngine.VerifyFiles([("main.lang-go", Green)], StubVerifyDriver.Instance);
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Parse));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Resolve));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Typecheck));
        foreach (var s in new[]
                 {
                     Stage.Hir, Stage.Ir, Stage.Effect, Stage.Compile, Stage.Execute, Stage.Differential,
                 })
        {
            Assert.Equal(StageOutcomeKind.Skipped, KindOf(r, s));
        }
        Assert.True(r.Passed); // Skipped ≠ 失败
        Assert.Null(r.FailedStage());
    }

    /// <summary>V6：目录装载（含被引模块文件）→ import 滤除后 ir/compile/execute 全过。</summary>
    [Fact]
    public void V6_dir_with_imports_full_chain()
    {
        Write("main.lang-go", "import \"./util\"\nfn main() {\n    println(twice(21))\n}\n");
        Write("util.lang-go", "fn twice(x: int): int {\n    return x * 2\n}\n");
        var r = VerifyEngine.Verify(_dir);
        Assert.True(r.Passed, $"报告: {string.Join("; ", r.Stages.Select(o => $"{o.Stage}={o.Kind} {o.Note}"))}");
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Ir));
        Assert.Equal(StageOutcomeKind.Passed, KindOf(r, Stage.Execute));
        Assert.Null(r.FailedStage());
    }

    private static StageOutcomeKind KindOf(VerifyReport r, Stage s) =>
        r.Stages.First(o => o.Stage == s).Kind;

    private static string NoteOf(VerifyReport r, Stage s) =>
        r.Stages.First(o => o.Stage == s).Note;

    private static string Dump(VerifyReport r) =>
        string.Join("; ", r.Stages.Select(o => $"{o.Stage}={o.Kind} {o.Note}"));
}
