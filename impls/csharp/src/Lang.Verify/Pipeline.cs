using Lang.Contract;
using Lang.Core;
using Lang.Ir;
using Lang.Loader;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Verify;

/// <summary>verify 输入：模块目录（runner-contract：输入一律为程序目录）或内存文件集。
/// 行为证据：impls/rust/crates/lang-verify/src/input.rs。</summary>
public sealed record VerifyInput(IReadOnlyList<(string Path, string Text)> Files)
{
    /// <summary>内存文件集（测试/宿主直供；路径字典序 = 确定性）。</summary>
    public static VerifyInput FromFiles(IEnumerable<(string Path, string Text)> files) =>
        new(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());

    /// <summary>import 抽取委托（仅抽 import；解析面错误由管线 Parse 段报告）。</summary>
    private static readonly ImportsOf ImportsViaParse = (relPath, source) =>
    {
        var parsed = Parser.Parse(new SourceText(relPath, source), new SourceId(0));
        if (parsed.Error is not null)
        {
            return [];
        }
        return parsed.Program.OfType<Stmt.Import>()
            .Select(im => new ImportRef(im.Path, (int)im.Span.Start.Line)).ToList();
    };

    /// <summary>从目录装载（lang-loader：目录即包 + import 展开；与 run/check 同一装载面）。
    /// 装载失败（目录不可读 / import 展开/循环错误）返回 null + 消息（归 Parse 段报告）。</summary>
    public static bool TryFromDir(string dir, out VerifyInput? input, out string? error)
    {
        if (!ModuleLoader.TryLoadProgram(dir, includeTests: false, ImportsViaParse,
                out var program, out var loadError) || program is null)
        {
            input = null;
            error = loadError?.Message ?? "装载失败";
            return false;
        }
        input = FromFiles(program.Files.Select(f => (f.RelPath, f.Source)));
        error = null;
        return true;
    }

    /// <summary>逐文件解析（路径字典序分配 SourceId）；任一文件解析失败即失败。</summary>
    public bool TryParseAll(out List<(string Path, IReadOnlyList<Stmt> Stmts)> parsed, out (string Path, Diagnostic Error)? failure)
    {
        parsed = [];
        failure = null;
        for (var i = 0; i < Files.Count; i++)
        {
            var (path, text) = Files[i];
            var result = Parser.Parse(new SourceText(path, text), new SourceId((uint)i));
            if (result.Error is not null)
            {
                failure = (path, result.Error);
                return false;
            }
            parsed.Add((path, result.Program));
        }
        return true;
    }
}

/// <summary>
/// verify 九段管线编排（specs/runner-contract.md verify 动词）。行为证据：
/// impls/rust/crates/lang-verify/src/pipeline.rs。
///
/// <para>执行序 = 冻结 <see cref="Stage"/> 九段：parse → resolve → typecheck → hir → ir →
/// effect → compile → execute → differential。语义分段：</para>
/// <list type="bullet">
/// <item>Parse/Resolve/Typecheck = 前端三段（Lang.Syntax + Lang.Semantic，真实现）；</item>
/// <item>Hir = 前端下降（<see cref="IVerifyDriver.LowerHir"/>；DefaultVerifyDriver 全接）；</item>
/// <item>Ir = <see cref="IVerifyDriver.LowerIr"/> + Validator（A3 接线；未接线如实 Skipped）；</item>
/// <item>Effect = <see cref="EffectFacts"/>（AST + SymbolId 事实 → lang_contract 不动点）；</item>
/// <item>Compile = →Go/→JS 生成可生成性（A5 CodeGen，经驱动注入）；</item>
/// <item>Execute/Differential = 执行路产物三要素比对（缺失路显式 Skipped，不谎报全路一致）。</item>
/// </list>
///
/// <para>短路纪律：任一段 Failed → 后续段全部 Skipped（「上游 X 未通过，短路」）且在报告中
/// 显式可见；Skipped = 环境性跳过，不算失败也不算通过——不静默、不谎报。Import 语句在
/// 合并点统一滤除（装载指令非运行时语句；loader 产物保留 Import 原句，消费方自滤义务）。</para>
/// </summary>
public static class VerifyEngine
{
    /// <summary>对模块目录跑全管线（DefaultVerifyDriver；runner-contract 的 verify 目录语义）。</summary>
    public static VerifyReport Verify(string dir) => Verify(dir, new DefaultVerifyDriver());

    /// <summary>对模块目录跑全管线（驱动注入形态）。</summary>
    public static VerifyReport Verify(string dir, IVerifyDriver driver)
    {
        if (!VerifyInput.TryFromDir(dir, out var input, out var error) || input is null)
        {
            // 装载失败归 Parse 段报告（目录不可读 / import 展开/循环错误）
            return FailShortCircuit(Stage.Parse, error ?? "装载失败");
        }
        return RunPipeline(input, driver);
    }

    /// <summary>对内存文件集跑全管线（宿主直供；测试主路径）。</summary>
    public static VerifyReport VerifyFiles(
        IReadOnlyList<(string Path, string Text)> files, IVerifyDriver? driver = null) =>
        RunPipeline(VerifyInput.FromFiles(files), driver ?? new DefaultVerifyDriver());

    /// <summary>全管线编排：逐段推进，任一失败即短路。</summary>
    public static VerifyReport RunPipeline(VerifyInput input, IVerifyDriver driver)
    {
        var stages = new List<StageOutcome>();

        // —— 1) Parse ——
        if (!input.TryParseAll(out var files, out var failure))
        {
            var (path, diag) = failure!.Value;
            return FailShortCircuit(Stage.Parse,
                $"{path} 第 {diag.PrimarySpan?.Start.Line ?? 0} 行: {diag.Message}");
        }
        stages.Add(new StageOutcome(Stage.Parse, StageOutcomeKind.Passed));

        // import 是装载指令非运行时语句：装载器为文件级合并、产物保留 Import 原句，
        // AST 解释器对其报错而 lowering 静默跳过——在合并点统一滤除
        // （下游 effect/execute 消费同一 merged；消费方自滤义务）。
        var merged = new List<Stmt>();
        foreach (var (_, stmts) in files)
        {
            foreach (var s in stmts)
            {
                if (s is not Stmt.Import)
                {
                    merged.Add(s);
                }
            }
        }

        // —— 2) Resolve / 3) Typecheck（一次 analyze；resolver 失败时 checker 不运行）——
        var analysis = LangSemantic.Analyze(merged, new SourceId(0));
        if (!analysis.Checked && analysis.HasErrors())
        {
            return With(stages, Stage.Resolve, DiagSummary(analysis));
        }
        stages.Add(new StageOutcome(Stage.Resolve, StageOutcomeKind.Passed));
        if (analysis.HasErrors())
        {
            return With(stages, Stage.Typecheck, DiagSummary(analysis));
        }
        stages.Add(new StageOutcome(Stage.Typecheck, StageOutcomeKind.Passed));

        // —— 4) Hir ——
        var hirResult = driver.LowerHir(merged, analysis);
        if (hirResult.Error is not null)
        {
            return hirResult.Error.IsSkipped
                ? SkipFrom(stages, Stage.Hir, hirResult.Error.Message)
                : FailShortCircuitFrom(stages, Stage.Hir, hirResult.Error.Message);
        }
        stages.Add(new StageOutcome(Stage.Hir, StageOutcomeKind.Passed));
        var hir = hirResult.Value!;

        // —— 5) Ir ——
        IrProgram? ir = null;
        var irResult = driver.LowerIr(hir, analysis);
        if (irResult.Error is not null)
        {
            if (irResult.Error.IsSkipped)
            {
                stages.Add(new StageOutcome(Stage.Ir, StageOutcomeKind.Skipped, irResult.Error.Message));
            }
            else
            {
                return FailShortCircuitFrom(stages, Stage.Ir, irResult.Error.Message);
            }
        }
        else
        {
            var irError = Validator.Validate(irResult.Value!);
            if (irError is not null)
            {
                return FailShortCircuitFrom(stages, Stage.Ir, $"IR 校验: {irError}");
            }
            ir = irResult.Value;
            stages.Add(new StageOutcome(Stage.Ir, StageOutcomeKind.Passed));
        }

        // —— 6) Effect ——
        var effects = EffectFacts.Solve(merged, analysis);
        if (effects.Error is not null)
        {
            return FailShortCircuitFrom(stages, Stage.Effect, effects.Error.Message);
        }
        stages.Add(new StageOutcome(Stage.Effect, StageOutcomeKind.Passed));
        _ = effects.Value; // 效果面由 inspect.effects 消费；本段判据 = 求解成功

        // —— 7) Compile（→Go / →JS 生成可生成性；产物本体由执行段重新生成）——
        if (ir is not null)
        {
            var goGen = driver.GenerateGo(ir);
            if (goGen.Error is not null)
            {
                return goGen.Error.IsSkipped
                    ? SkipFrom(stages, Stage.Compile, goGen.Error.Message)
                    : FailShortCircuitFrom(stages, Stage.Compile, goGen.Error.Message);
            }
            var jsGen = driver.GenerateJs(ir);
            if (jsGen.Error is not null)
            {
                return jsGen.Error.IsSkipped
                    ? SkipFrom(stages, Stage.Compile, jsGen.Error.Message)
                    : FailShortCircuitFrom(stages, Stage.Compile, jsGen.Error.Message);
            }
            stages.Add(new StageOutcome(Stage.Compile, StageOutcomeKind.Passed));
        }
        else
        {
            stages.Add(new StageOutcome(Stage.Compile, StageOutcomeKind.Skipped, "上游 ir 阶段未产出 IR"));
        }

        // —— 8) Execute（ast 为参考路：真失败即短路；未接线则按可用性参与，
        //         与 ir/go/js 同列 Skipped 名单——不静默、不谎报）——
        var outs = new List<(string Name, RunOutput Output)>();
        var skips = new List<string>();
        var ctx = new ExecContext(merged, analysis);
        var astResult = driver.RunAst(ctx);
        if (astResult.Error is not null)
        {
            if (astResult.Error.IsSkipped)
            {
                skips.Add(SkipNote("ast", astResult.Error.Message));
            }
            else
            {
                return FailShortCircuitFrom(stages, Stage.Execute, $"ast: {astResult.Error.Message}");
            }
        }
        else
        {
            outs.Add(("ast", astResult.Value!));
        }

        if (ir is not null)
        {
            var irRun = driver.RunIr(ir);
            if (irRun.Error is not null)
            {
                if (irRun.Error.IsSkipped)
                {
                    skips.Add(SkipNote("ir", irRun.Error.Message));
                }
                else
                {
                    return FailShortCircuitFrom(stages, Stage.Execute, $"ir: {irRun.Error.Message}");
                }
            }
            else
            {
                outs.Add(("ir", irRun.Value!));
            }
            var goRun = driver.RunGo(ir);
            if (goRun.Error is not null)
            {
                if (goRun.Error.IsSkipped)
                {
                    skips.Add(SkipNote("go", goRun.Error.Message));
                }
                else
                {
                    return FailShortCircuitFrom(stages, Stage.Execute, $"go: {goRun.Error.Message}");
                }
            }
            else
            {
                outs.Add(("go", goRun.Value!));
            }
            var jsRun = driver.RunJs(ir);
            if (jsRun.Error is not null)
            {
                if (jsRun.Error.IsSkipped)
                {
                    skips.Add(SkipNote("js", jsRun.Error.Message));
                }
                else
                {
                    return FailShortCircuitFrom(stages, Stage.Execute, $"js: {jsRun.Error.Message}");
                }
            }
            else
            {
                outs.Add(("js", jsRun.Value!));
            }
        }

        if (outs.Count == 0)
        {
            stages.Add(new StageOutcome(Stage.Execute, StageOutcomeKind.Skipped,
                $"无可用执行路（{skips.Count} 段跳过: {string.Join(", ", skips)}）"));
            return FillSkipped(stages, Stage.Execute);
        }
        var badIdx = outs.FindIndex(o => o.Output.ExitCode != 0 || o.Output.Stderr.Length > 0);
        if (badIdx >= 0)
        {
            var (badName, badOutput) = outs[badIdx];
            return FailShortCircuitFrom(stages, Stage.Execute,
                $"执行路 {badName} 非零退出（code={badOutput.ExitCode}）: {badOutput.Stderr}");
        }
        stages.Add(new StageOutcome(Stage.Execute, StageOutcomeKind.Passed));

        // —— 9) Differential（stdout 逐字节 + exitCode + stderr 全比对；
        //         缺失路显式呈现，不谎报全路一致）——
        if (outs.Count < 2)
        {
            stages.Add(new StageOutcome(Stage.Differential, StageOutcomeKind.Skipped,
                $"可比执行路不足（{outs.Count} 路）: {string.Join(", ", skips)}"));
            return new VerifyReport(stages);
        }
        var (baseName, baseOut) = outs[0];
        foreach (var (name, output) in outs.Skip(1))
        {
            if (output.Stdout != baseOut.Stdout || output.ExitCode != baseOut.ExitCode
                || output.Stderr.Length > 0)
            {
                return FailShortCircuitFrom(stages, Stage.Differential,
                    $"执行路 {name} 与 {baseName} 不一致: stdout \"{output.Stdout}\" vs \"{baseOut.Stdout}\"，" +
                    $"exit {output.ExitCode} vs {baseOut.ExitCode}");
            }
        }
        if (skips.Count > 0)
        {
            stages.Add(new StageOutcome(Stage.Differential, StageOutcomeKind.Skipped,
                $"已比对 {outs.Count} 路逐字节一致；缺失路: {string.Join(", ", skips)}"));
            return new VerifyReport(stages);
        }
        stages.Add(new StageOutcome(Stage.Differential, StageOutcomeKind.Passed));
        return new VerifyReport(stages);
    }

    /// <summary>跳过路记录（`路名(原因)`，差分段的显式呈现素材）。</summary>
    private static string SkipNote(string name, string reason) => $"{name}({reason})";

    /// <summary>诊断摘要（resolve/typecheck 失败消息；码面 + 首条消息）。</summary>
    private static string DiagSummary(Analysis analysis)
    {
        var codes = analysis.Diags.Select(d => d.Code.CodeText()).Order(StringComparer.Ordinal).ToList();
        var first = analysis.Diags.Count > 0
            ? $"第 {analysis.Diags[0].PrimarySpan?.Start.Line ?? 0} 行: {analysis.Diags[0].Message}"
            : "";
        return $"{analysis.Diags.Count} 条诊断 [{string.Join(", ", codes)}]: {first}";
    }

    /// <summary>失败段入报告并短路后续段（Skipped 显式可见）。</summary>
    private static VerifyReport FailShortCircuitFrom(
        List<StageOutcome> done, Stage failed, string message)
    {
        done.Add(new StageOutcome(failed, StageOutcomeKind.Failed, message));
        return FillSkipped(done, failed);
    }

    private static VerifyReport FailShortCircuit(Stage failed, string message) =>
        FailShortCircuitFrom([], failed, message);

    /// <summary>段即跳（非失败；后续段照常短路在册）。</summary>
    private static VerifyReport SkipFrom(List<StageOutcome> done, Stage skipped, string reason)
    {
        done.Add(new StageOutcome(skipped, StageOutcomeKind.Skipped, reason));
        return FillSkipped(done, skipped);
    }

    private static VerifyReport With(List<StageOutcome> done, Stage failed, string message) =>
        FailShortCircuitFrom(done, failed, message);

    /// <summary>把 failed 之后的全部阶段补成 Skipped（短路可见性）。</summary>
    private static VerifyReport FillSkipped(List<StageOutcome> done, Stage failed)
    {
        var seen = new HashSet<Stage>(done.Select(o => o.Stage));
        foreach (var s in Enum.GetValues<Stage>())
        {
            if (s > failed && !seen.Contains(s))
            {
                done.Add(new StageOutcome(s, StageOutcomeKind.Skipped,
                    $"上游 {failed} 未通过，短路"));
            }
        }
        return new VerifyReport(done);
    }
}
