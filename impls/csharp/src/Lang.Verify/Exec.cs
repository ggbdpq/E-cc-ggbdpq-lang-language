using Lang.BackendGo;
using Lang.BackendJs;
using Lang.Hir;
using Lang.Ir;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Verify;

/// <summary>一路执行的产物（差分比对面：stdout 逐字节 + exitCode + stderr 为空）。</summary>
public sealed record RunOutput(string Stdout, string Stderr, int ExitCode);

/// <summary>
/// 执行段失败形态：Skipped = 环境性跳过（不算失败，管线显式在册），Failed = 真实失败。
/// </summary>
public sealed record ExecError(string Message, bool IsSkipped)
{
    /// <summary>环境性跳过（工具缺失/实现未接线；不静默、不谎报）。</summary>
    public static ExecError Skip(string message) => new(message, true);

    /// <summary>真实失败。</summary>
    public static ExecError Fail(string message) => new(message, false);
}

/// <summary>段产物或失败二选一（受控错误通道，不 throw 表达用户/环境态）。</summary>
public readonly record struct ExecResult<T>(T? Value, ExecError? Error)
{
    public static ExecResult<T> Ok(T value) => new(value, null);

    public static ExecResult<T> Err(ExecError error) => new(default, error);
}

/// <summary>AST 执行段的输入：合并语句序列 + 同一棵树的语义分析产物。</summary>
public sealed record ExecContext(IReadOnlyList<Stmt> Stmts, Analysis Analysis);

/// <summary>
/// 执行段注入面（specs/runner-contract.md verify 动词 + 通用约束 5）。行为证据：
/// impls/rust/crates/lang-verify/src/exec.rs。
///
/// <para>管线的执行段（HIR 下降 / HIR→IR 下降 / →Go 生成 / →JS 生成 / AST 解释 /
/// IR 解释 / Go 宿主执行 / JS 宿主执行）全部经 <see cref="IVerifyDriver"/> 注入：
/// A3/A4/A5 的实现方认领各自段，管线只认本面。工具链（go/node）缺失必须以
/// <see cref="ExecError.Skip"/> 显式呈现——不静默跳过、不谎报通过。</para>
/// </summary>
public interface IVerifyDriver
{
    /// <summary>AST 语句序列 → Typed HIR（前端第 4 段的事实源）。</summary>
    ExecResult<HirProg> LowerHir(IReadOnlyList<Stmt> stmts, Analysis analysis);

    /// <summary>HIR → Core IR lowering（A3；未接入 → Skipped）。analysis 供需要
    /// 语义查询面（TypeTables/SymbolArena）的实现方使用。</summary>
    ExecResult<IrProgram> LowerIr(HirProg hir, Analysis analysis);

    /// <summary>→Go 生成（A5 生成段；可生成性判据）。</summary>
    ExecResult<string> GenerateGo(IrProgram prog);

    /// <summary>→JS 生成（A5 生成段；可生成性判据）。</summary>
    ExecResult<string> GenerateJs(IrProgram prog);

    /// <summary>AST 参考解释器（A4）。</summary>
    ExecResult<RunOutput> RunAst(ExecContext ctx);

    /// <summary>IR 解释器（A4）。</summary>
    ExecResult<RunOutput> RunIr(IrProgram prog);

    /// <summary>→Go 生成 + go 工具链执行（A5 + 宿主；缺失 → Skipped）。</summary>
    ExecResult<RunOutput> RunGo(IrProgram prog);

    /// <summary>→JS 生成 + node 工具链执行（A5 + 宿主；缺失 → Skipped）。</summary>
    ExecResult<RunOutput> RunJs(IrProgram prog);
}

/// <summary>全段 Skipped 桩：编排/短路测试用（管线对 Skipped 的可见性即本桩的验收）。</summary>
public sealed class StubVerifyDriver : IVerifyDriver
{
    public static readonly StubVerifyDriver Instance = new();

    public ExecResult<HirProg> LowerHir(IReadOnlyList<Stmt> stmts, Analysis analysis) =>
        ExecResult<HirProg>.Err(ExecError.Skip("桩：下降段未注入"));

    public ExecResult<IrProgram> LowerIr(HirProg hir, Analysis analysis) =>
        ExecResult<IrProgram>.Err(ExecError.Skip("桩：执行段未注入"));

    public ExecResult<string> GenerateGo(IrProgram prog) =>
        ExecResult<string>.Err(ExecError.Skip("桩：生成段未注入"));

    public ExecResult<string> GenerateJs(IrProgram prog) =>
        ExecResult<string>.Err(ExecError.Skip("桩：生成段未注入"));

    public ExecResult<RunOutput> RunAst(ExecContext ctx) =>
        ExecResult<RunOutput>.Err(ExecError.Skip("桩：执行段未注入"));

    public ExecResult<RunOutput> RunIr(IrProgram prog) =>
        ExecResult<RunOutput>.Err(ExecError.Skip("桩：执行段未注入"));

    public ExecResult<RunOutput> RunGo(IrProgram prog) =>
        ExecResult<RunOutput>.Err(ExecError.Skip("桩：执行段未注入"));

    public ExecResult<RunOutput> RunJs(IrProgram prog) =>
        ExecResult<RunOutput>.Err(ExecError.Skip("桩：执行段未注入"));
}

/// <summary>
/// 内建执行驱动：前端下降全接（Lang.Hir HirBuild）+ HIR→IR 下降（A3 IrLowerer，
/// 语义查询面构造期注入）+ 双后端生成（A5 CodeGen）+ 宿主工具链执行（go/node
/// 探测缓存）。AST/IR 解释器（A4）留注入委托，未接线段如实 Skipped。
/// </summary>
public sealed class DefaultVerifyDriver : IVerifyDriver
{
    private readonly ILowerIr? _irLowerer;
    private readonly Func<ExecContext, ExecResult<RunOutput>>? _runAst;
    private readonly Func<IrProgram, ExecResult<RunOutput>>? _runIr;
    private bool? _goOk;
    private bool? _nodeOk;

    /// <summary>构造：irLowerer（缺省 = A3 IrLowerer）与 runAst/runIr（A4 接线点，
    /// null = 该段未接线）为注入面。</summary>
    public DefaultVerifyDriver(
        ILowerIr? irLowerer = null,
        Func<ExecContext, ExecResult<RunOutput>>? runAst = null,
        Func<IrProgram, ExecResult<RunOutput>>? runIr = null)
    {
        _irLowerer = irLowerer;
        _runAst = runAst;
        _runIr = runIr;
    }

    public ExecResult<HirProg> LowerHir(IReadOnlyList<Stmt> stmts, Analysis analysis)
    {
        var hir = HirBuild.BuildHir(stmts, analysis);
        return hir.IsOk
            ? ExecResult<HirProg>.Ok(hir.Value!)
            : ExecResult<HirProg>.Err(ExecError.Fail($"HIR 构建: {hir.Error}"));
    }

    public ExecResult<IrProgram> LowerIr(HirProg hir, Analysis analysis)
    {
        // 默认接线 = A3 真实现（IrLowerer 构造期注入语义查询面）；宿主可注入替换
        var lowerer = _irLowerer ?? new IrLowerer(analysis.Types, analysis.Symbols);
        var lowered = lowerer.Lower(hir);
        return lowered.IsOk
            ? ExecResult<IrProgram>.Ok(lowered.Value!)
            : ExecResult<IrProgram>.Err(ExecError.Fail($"HIR→IR lowering: {lowered.Error}"));
    }

    public ExecResult<string> GenerateGo(IrProgram prog)
    {
        var ok = BackendGo.CodeGen.TryGenerate(prog, out var source, out var error);
        return ok && source is not null
            ? ExecResult<string>.Ok(source)
            : ExecResult<string>.Err(ExecError.Fail($"→Go 生成: {error?.Message}"));
    }

    public ExecResult<string> GenerateJs(IrProgram prog)
    {
        var ok = BackendJs.CodeGen.TryGenerate(prog, out var source, out var error);
        return ok && source is not null
            ? ExecResult<string>.Ok(source)
            : ExecResult<string>.Err(ExecError.Fail($"→JS 生成: {error?.Message}"));
    }

    public ExecResult<RunOutput> RunAst(ExecContext ctx) =>
        _runAst?.Invoke(ctx)
        ?? ExecResult<RunOutput>.Err(ExecError.Skip("AST 参考路未接线"));

    public ExecResult<RunOutput> RunIr(IrProgram prog) =>
        _runIr?.Invoke(prog)
        ?? ExecResult<RunOutput>.Err(ExecError.Skip("IR 解释器未接线"));

    public ExecResult<RunOutput> RunGo(IrProgram prog)
    {
        var gen = GenerateGo(prog);
        if (gen.Error is not null)
        {
            return ExecResult<RunOutput>.Err(gen.Error);
        }
        if (!Probe("go", "version"))
        {
            return ExecResult<RunOutput>.Err(ExecError.Skip("宿主缺 go 工具链"));
        }
        using var tmp = VerifyTempDir.New("lang-verify-go");
        var srcPath = Path.Combine(tmp.Path, "main.go");
        File.WriteAllText(srcPath, gen.Value);
        var bin = Path.Combine(tmp.Path, "main_bin.exe");
        var build = RunTool("go", $"build -o \"{bin}\" \"{srcPath}\"");
        if (build.ExitCode != 0)
        {
            return ExecResult<RunOutput>.Err(ExecError.Fail($"go build 失败: {build.Stderr}"));
        }
        return ExecResult<RunOutput>.Ok(RunTool(bin));
    }

    public ExecResult<RunOutput> RunJs(IrProgram prog)
    {
        var gen = GenerateJs(prog);
        if (gen.Error is not null)
        {
            return ExecResult<RunOutput>.Err(gen.Error);
        }
        if (!Probe("node", "--version"))
        {
            return ExecResult<RunOutput>.Err(ExecError.Skip("宿主缺 node 工具链"));
        }
        using var tmp = VerifyTempDir.New("lang-verify-js");
        var srcPath = Path.Combine(tmp.Path, "main.mjs");
        File.WriteAllText(srcPath, gen.Value);
        // .mjs 不能被 Windows 直接执行——必须经 node 启动
        return ExecResult<RunOutput>.Ok(RunTool("node", $"\"{srcPath}\""));
    }

    /// <summary>工具链可用性探测（每驱动实例缓存一次；go 的版本子命令无连字符，node 需 --version）。</summary>
    private bool Probe(string which, string flag)
    {
        if (which == "go")
        {
            if (_goOk.HasValue)
            {
                return _goOk.Value;
            }
            var ok = DoProbe(which, flag);
            _goOk = ok;
            return ok;
        }
        if (_nodeOk.HasValue)
        {
            return _nodeOk.Value;
        }
        var nodeOk = DoProbe(which, flag);
        _nodeOk = nodeOk;
        return nodeOk;
    }

    private static bool DoProbe(string which, string flag)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(which, flag)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
            return p is not null && p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return false;
        }
    }

    private static RunOutput RunTool(string file, string args = "")
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // 程序 stdout/stderr 契约为 UTF-8 字节流；Windows 默认按系统 ANSI
                    // 代码页解码（如 GBK），非 ASCII 输出会变 mojibake——显式指定。
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                });
            if (p is null)
            {
                return new RunOutput("", "进程启动失败", -1);
            }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new RunOutput(stdout, stderr, p.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return new RunOutput("", e.Message, -1);
        }
    }
}

/// <summary>系统临时目录下的独占工作目录（Dispose 清理；纪律：临时文件不落仓库）。</summary>
public sealed class VerifyTempDir : IDisposable
{
    private static int _seq;

    private VerifyTempDir(string path) => Path = path;

    public string Path { get; }

    public static VerifyTempDir New(string tag)
    {
        var seq = Interlocked.Increment(ref _seq);
        var nanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"{tag}-{Environment.ProcessId}-{seq}-{nanos}");
        Directory.CreateDirectory(path);
        return new VerifyTempDir(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
