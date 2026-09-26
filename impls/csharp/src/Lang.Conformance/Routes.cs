using System.Diagnostics;
using System.Text;
using Lang.BackendGo;
using Lang.BackendJs;
using Lang.Core;
using Lang.Interp;
using Lang.Ir;
using Lang.Loader;
using Lang.Runtime;
using Lang.Semantic;
using Lang.Syntax;
using InterpEngine = Lang.Interp.Interp;

namespace Lang.Conformance;

/// <summary>
/// 四路执行器适配层（specs/runner-contract.md run 动词；对齐 lang-rs 第四波 routes.rs）。
/// 每路自 case 目录独立走「装载 → skip 门 → 语义分析 → 执行」：
/// - <see cref="InterpAstRoute"/>：AST 参考解释器（<see cref="Interp"/>）；
/// - <see cref="InterpIrRoute"/>：Compile 全链（AST→HIR→IR）→ IR 解释器（<see cref="IrMachine"/>）；
/// - <see cref="GoRoute"/>：Compile → →Go 生成 → go build → 执行产物；
/// - <see cref="JsRoute"/>：Compile → →JS 生成 → node 执行（.mjs 不能被宿主直接启动）。
///
/// 诚实纪律：装载/解析失败与执行失败 = <see cref="RouteResult.Fail"/>；工具链缺失与
/// skip 门（extern FFI / 宿主 http.* 内建 / 静态诊断非空的 run case）= <see cref="RouteResult.Skip"/>
/// ——跳过不计入通过（runner-contract 通用约束 5），不静默、不谎报。
/// import 语句在装载合并时滤除（装载指令非运行时语句——四路一致的前提）。
/// </summary>

/// <summary>装载并分析完成的一个 case 程序（合并语句流 + 语义分析产物）。</summary>
public sealed record LoadedCase(IReadOnlyList<Stmt> Stmts, Analysis Analysis);

/// <summary>case 装载器：Lang.Loader 目录即包 → 逐文件 parse（SourceId 按装载序）→
/// 合并语句流（剥除 import）→ <see cref="LangSemantic.Analyze"/>。与 Lang.Cli.Load 同一口径。</summary>
public static class CaseLoader
{
    /// <summary>装载失败（文件缺失/import 环/parse 失败）返回 false + 消息。</summary>
    public static bool TryLoad(string caseDir, out IReadOnlyList<Stmt>? stmts, out string? error)
    {
        stmts = null;
        error = null;
        if (!ModuleLoader.TryLoadProgram(caseDir, includeTests: false, ParseImportsOf, out var program, out var loadErr))
        {
            error = loadErr!.Message;
            return false;
        }

        if (program!.Files.Count == 0)
        {
            error = "case 目录无 .lang-go 源文件";
            return false;
        }

        var merged = new List<Stmt>();
        var sid = 0u;
        foreach (var f in program!.Files)
        {
            var parsed = Parser.Parse(new SourceText(f.RelPath, f.Source), new SourceId(sid++));
            if (parsed.Error is not null)
            {
                error = $"{f.RelPath}: parse 失败: {parsed.Error.Message}";
                return false;
            }

            // import 语句剥除（装载指令非运行时语句；解释器拒绝执行 Import、
            // IR lowering 不消费——滤除后四路一致）
            foreach (var s in parsed.Program)
            {
                if (s is not Stmt.Import)
                {
                    merged.Add(s);
                }
            }
        }

        stmts = merged;
        return true;
    }

    /// <summary>ModuleLoader ImportsOf 接线：Lang.Syntax parse 抽顶层 Import（失败返回空——
    /// 错误在装载后的逐文件 parse 阶段统一报告；与 Lang.Cli.Load.ParseImports 同口径）。</summary>
    private static IReadOnlyList<ImportRef> ParseImportsOf(string relPath, string source)
    {
        var parsed = Parser.Parse(new SourceText(relPath, source), new SourceId(0));
        if (parsed.Error is not null)
        {
            return [];
        }

        var refs = new List<ImportRef>();
        foreach (var s in parsed.Program)
        {
            if (s is Stmt.Import imp)
            {
                refs.Add(new ImportRef(imp.Path, (int)imp.Sp.Start.Line));
            }
        }

        return refs;
    }

    /// <summary>skip 门：extern FFI 或宿主 http.* 内建调用（保守全树遍历）→ true。
    /// 语义：这两类程序只在「转译 + 宿主」域有意义，解释器域与差分域外；
    /// 当前语料 0 例，检测保留防套件扩容后静默误判。</summary>
    public static bool NeedsHostOrFfi(IReadOnlyList<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            if (StmtHasHostOrFfi(s))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StmtHasHostOrFfi(Stmt s) => s switch
    {
        Stmt.Extern => true,
        Stmt.Let w => ExprHasHostOrFfi(w.Init),
        Stmt.Assign w => ExprHasHostOrFfi(w.Target) || ExprHasHostOrFfi(w.Value),
        Stmt.ExprStmt w => ExprHasHostOrFfi(w.Expr),
        Stmt.Return w => w.Value is { } v && ExprHasHostOrFfi(v),
        Stmt.If w => ExprHasHostOrFfi(w.Cond) || BodyHasHostOrFfi(w.ThenBody) || BodyHasHostOrFfi(w.ElseBody ?? []),
        Stmt.While w => ExprHasHostOrFfi(w.Cond) || BodyHasHostOrFfi(w.Body),
        Stmt.ForIn w => ExprHasHostOrFfi(w.Iter) || BodyHasHostOrFfi(w.Body),
        Stmt.FnDecl w => BodyHasHostOrFfi(w.Body),
        Stmt.Go w => ExprHasHostOrFfi(w.Call),
        _ => false,
    };

    private static bool BodyHasHostOrFfi(IReadOnlyList<Stmt> body)
    {
        foreach (var s in body)
        {
            if (StmtHasHostOrFfi(s))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExprHasHostOrFfi(Expr e) => e switch
    {
        Expr.Call w => CalleeName(w.Callee) is { } name && (name == "http.get" || name.StartsWith("http.", StringComparison.Ordinal))
            || ExprHasHostOrFfi(w.Callee) || ArgsHasHostOrFfi(w.Args),
        Expr.Unary w => ExprHasHostOrFfi(w.Operand),
        Expr.Binary w => ExprHasHostOrFfi(w.Lhs) || ExprHasHostOrFfi(w.Rhs),
        Expr.Range w => ExprHasHostOrFfi(w.Lo) || ExprHasHostOrFfi(w.Hi),
        Expr.Index w => ExprHasHostOrFfi(w.Obj) || ExprHasHostOrFfi(w.Idx),
        Expr.Slice w => ExprHasHostOrFfi(w.Obj) || ExprHasHostOrFfi(w.Lo) || ExprHasHostOrFfi(w.Hi),
        Expr.Field w => ExprHasHostOrFfi(w.Obj),
        Expr.Try w => ExprHasHostOrFfi(w.Operand),
        Expr.StructLit w => w.Fields.Any(f => ExprHasHostOrFfi(f.Value)),
        Expr.MapLit w => w.Items.Any(i => ExprHasHostOrFfi(i.Key) || ExprHasHostOrFfi(i.Value)),
        Expr.ArrLit w => w.Items.Any(ExprHasHostOrFfi),
        Expr.Match w => ExprHasHostOrFfi(w.Scrutinee) || w.Arms.Any(a => ExprHasHostOrFfi(a.Body)),
        Expr.FnLit w => BodyHasHostOrFfi(w.Body),
        _ => false,
    };

    private static bool ArgsHasHostOrFfi(IReadOnlyList<Expr> args)
    {
        foreach (var a in args)
        {
            if (ExprHasHostOrFfi(a))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>调用目标名（仅 Ident callee 具名可判；方法/匿名调用交由递归子遍历）。</summary>
    private static string? CalleeName(Expr callee) => callee is Expr.Ident id ? id.Name : null;
}

/// <summary>四路共享的装载 + skip 门 + 分析前置（任一门命中 → outcome 带原因，返回 null）。</summary>
internal static class RoutePipeline
{
    public static LoadedCase? Prepare(string caseDir, out RouteResult? outcome)
    {
        outcome = null;
        if (!CaseLoader.TryLoad(caseDir, out var stmts, out var err))
        {
            outcome = RouteResult.Fail($"装载失败: {err}");
            return null;
        }

        if (CaseLoader.NeedsHostOrFfi(stmts!))
        {
            outcome = RouteResult.Skip("extern FFI/宿主 http.* 内建：解释器与差分域外（统一 skip 门）");
            return null;
        }

        var analysis = LangSemantic.Analyze(stmts!, new SourceId(0));
        if (analysis.HasErrors())
        {
            var codes = string.Join(",", analysis.Diags
                .Select(d => d.Code.CodeText())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
            outcome = RouteResult.Skip($"静态诊断非空（check 语义面，非 run 比对对象）: {codes}");
            return null;
        }

        return new LoadedCase(stmts!, analysis);
    }
}

/// <summary>内存输出汇（进程 stdout 的替身；四路解释器路共用）。</summary>
internal sealed class RouteSink : OutputSink
{
    private readonly StringBuilder _sb = new();
    public void Write(string s) => _sb.Append(s);
    public override string ToString() => _sb.ToString();
}

/// <summary>AST 参考解释器路：Interp.Run 直接遍历语法树（参考 oracle）。</summary>
public sealed class InterpAstRoute : IRunRoute
{
    public string Name => Routes.InterpAst;

    public RouteResult Run(string caseDir)
    {
        var lc = RoutePipeline.Prepare(caseDir, out var gate);
        if (gate is not null)
        {
            return gate;
        }

        var sink = new RouteSink();
        var r = new InterpEngine(new Machine()).Run(lc!.Stmts, lc.Analysis, sink);
        return r.IsOk
            ? RouteResult.Ok(new RouteOutput(sink.ToString(), "", 0))
            : RouteResult.Fail($"AST 解释器: {r.Error!.Message}");
    }
}

/// <summary>IR 解释器路：Compiler.Compile（AST→HIR→IR 全链）→ IrMachine 栈式执行。</summary>
public sealed class InterpIrRoute : IRunRoute
{
    public string Name => Routes.InterpIr;

    public RouteResult Run(string caseDir)
    {
        var lc = RoutePipeline.Prepare(caseDir, out var gate);
        if (gate is not null)
        {
            return gate;
        }

        var compiled = Compiler.Compile(lc!.Stmts, lc.Analysis);
        if (!compiled.IsOk)
        {
            return RouteResult.Fail($"Compile: {compiled.Error}");
        }

        var sink = new RouteSink();
        var r = new IrMachine(compiled.Value!).Run(sink);
        return r.IsOk
            ? RouteResult.Ok(new RouteOutput(sink.ToString(), "", 0))
            : RouteResult.Fail($"IR 解释器: {r.Error!.Message}");
    }
}

/// <summary>→Go 产物路：Compile → CodeGen → go build → 执行产物（系统临时目录，用后清理）。
/// 工具探测：env LANG_CS_GO → PATH → 已知路径（与 A5 E2E 同清单），每实例缓存。</summary>
public sealed class GoRoute : IRunRoute
{
    public static readonly string[] KnownGoPaths =
    [
        @"D:\DevTools\Languages\Backend\Golang\Go\bin\go.exe",
        "/usr/local/go/bin/go",
        "/usr/bin/go",
    ];

    private readonly string? _exe;
    private readonly bool _skipTool;
    private string? _resolved;

    public GoRoute() : this(null, false)
    {
    }

    /// <summary>with_exe 注入面：null = 强制 Skipped（skip 逻辑测试用）；非 null = 指定工具。</summary>
    public static GoRoute WithExe(string? exe) => new(exe, exe is null);

    private GoRoute(string? exe, bool skipTool)
    {
        _exe = exe;
        _skipTool = skipTool;
    }

    public string Name => Routes.Go;

    public RouteResult Run(string caseDir)
    {
        var lc = RoutePipeline.Prepare(caseDir, out var gate);
        if (gate is not null)
        {
            return gate;
        }

        var compiled = Compiler.Compile(lc!.Stmts, lc.Analysis);
        if (!compiled.IsOk)
        {
            return RouteResult.Fail($"Compile: {compiled.Error}");
        }

        var exe = ResolveTool();
        if (exe is null)
        {
            return RouteResult.Skip("宿主缺 go 工具链（env LANG_CS_GO/PATH/已知路径均未命中）");
        }

        if (!BackendGo.CodeGen.TryGenerate(compiled.Value!, out var source, out var genErr))
        {
            return RouteResult.Fail($"→Go 生成: {genErr?.Message}");
        }

        using var tmp = RouteTempDir.New("lang-conformance-go");
        var srcPath = System.IO.Path.Combine(tmp.Path, "main.go");
        File.WriteAllText(srcPath, source);
        var bin = System.IO.Path.Combine(tmp.Path, "main_bin.exe");
        var build = HostRun.Exec(exe, ["build", "-o", bin, srcPath], tmp.Path);
        if (build.ExitCode != 0)
        {
            return RouteResult.Fail($"go build 失败: {HostRun.Truncate(build.Stderr)}");
        }

        var run = HostRun.Exec(bin, [], tmp.Path);
        return RouteResult.Ok(new RouteOutput(run.Stdout, run.Stderr, run.ExitCode));
    }

    private string? ResolveTool()
    {
        if (_skipTool)
        {
            return null;
        }

        if (_exe is not null)
        {
            return _exe;
        }

        _resolved ??= ToolProbe.Find("LANG_CS_GO", "go", "version", KnownGoPaths);
        return _resolved;
    }
}

/// <summary>→JS 产物路：Compile → CodeGen → node 执行（.mjs 必须经 node 启动）。
/// 工具探测：env LANG_CS_NODE → PATH → 已知路径，每实例缓存。</summary>
public sealed class JsRoute : IRunRoute
{
    public static readonly string[] KnownNodePaths =
    [
        @"C:\Program Files\nodejs\node.exe",
        "/usr/bin/node",
        "/usr/local/bin/node",
    ];

    private readonly string? _exe;
    private readonly bool _skipTool;
    private string? _resolved;

    public JsRoute() : this(null, false)
    {
    }

    /// <summary>with_exe 注入面：null = 强制 Skipped；非 null = 指定工具。</summary>
    public static JsRoute WithExe(string? exe) => new(exe, exe is null);

    private JsRoute(string? exe, bool skipTool)
    {
        _exe = exe;
        _skipTool = skipTool;
    }

    public string Name => Routes.Js;

    public RouteResult Run(string caseDir)
    {
        var lc = RoutePipeline.Prepare(caseDir, out var gate);
        if (gate is not null)
        {
            return gate;
        }

        var compiled = Compiler.Compile(lc!.Stmts, lc.Analysis);
        if (!compiled.IsOk)
        {
            return RouteResult.Fail($"Compile: {compiled.Error}");
        }

        var exe = ResolveTool();
        if (exe is null)
        {
            return RouteResult.Skip("宿主缺 node 工具链（env LANG_CS_NODE/PATH/已知路径均未命中）");
        }

        if (!BackendJs.CodeGen.TryGenerate(compiled.Value!, out var source, out var genErr))
        {
            return RouteResult.Fail($"→JS 生成: {genErr?.Message}");
        }

        using var tmp = RouteTempDir.New("lang-conformance-js");
        var srcPath = System.IO.Path.Combine(tmp.Path, "main.mjs");
        File.WriteAllText(srcPath, source);
        var run = HostRun.Exec(exe, [srcPath], tmp.Path);
        return RouteResult.Ok(new RouteOutput(run.Stdout, run.Stderr, run.ExitCode));
    }

    private string? ResolveTool()
    {
        if (_skipTool)
        {
            return null;
        }

        if (_exe is not null)
        {
            return _exe;
        }

        _resolved ??= ToolProbe.Find("LANG_CS_NODE", "node", "--version", KnownNodePaths);
        return _resolved;
    }
}

/// <summary>工具探测：env 覆盖 → PATH（进程名直启）→ 已知路径逐一子进程探测。
/// versionArg：go 的版本子命令无连字符（version），node 需 --version。</summary>
internal static class ToolProbe
{
    public static string? Find(string envVar, string plainName, string versionArg, IReadOnlyList<string> knownPaths)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (env is not null && Probe(env, versionArg))
        {
            return env;
        }

        if (Probe(plainName, versionArg))
        {
            return plainName;
        }

        foreach (var candidate in knownPaths)
        {
            if (Probe(candidate, versionArg))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Probe(string exe, string versionArg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, versionArg)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            return p is not null && p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}

/// <summary>宿主进程执行（stdout/stderr 全捕获；受控错误 = 退出码 -1 + stderr 带因）。</summary>
internal static class HostRun
{
    public static RouteOutput Exec(string exe, IReadOnlyList<string> args, string cwd)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
                // 程序 stdout/stderr 契约为 UTF-8 字节流；Windows 默认按系统 ANSI
                // 代码页解码（本机 GBK），非 ASCII 输出会变 mojibake——显式指定。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return new RouteOutput("", "进程启动失败", -1);
            }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new RouteOutput(stdout, stderr, p.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return new RouteOutput("", e.Message, -1);
        }
    }

    internal static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}

/// <summary>系统临时目录下的独占工作目录（Dispose 清理；Windows 占用残留由 OS 兜底，不谎报）。</summary>
internal sealed class RouteTempDir : IDisposable
{
    private static int _seq;

    private RouteTempDir(string path) => Path = path;

    public string Path { get; }

    public static RouteTempDir New(string tag)
    {
        var seq = Interlocked.Increment(ref _seq);
        var nanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{tag}-{Environment.ProcessId}-{seq}-{nanos}");
        Directory.CreateDirectory(path);
        return new RouteTempDir(path);
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

/// <summary>
/// check 模式诊断源（<see cref="SuiteRunner.CheckDiagnostics"/> 注入的真实现）：
/// 装载 → <see cref="LangSemantic.Analyze"/> 直产诊断 → 投影
/// <c>code|ruleId|severity|line</c>（line = primarySpan.start.line，无位置诊断 = 0——
/// SPEC-GAP-CS-A6-04 口径；message 与列号是既定漂移面，不入键）。
/// 装载/解析失败 = 空诊断集：比对把期望项全报「缺」判败并带明细（check case 必须可解析）。
/// </summary>
public static class CheckSources
{
    public static IReadOnlyList<DiagActual> FromAnalysis(string caseDir)
    {
        if (!CaseLoader.TryLoad(caseDir, out var stmts, out _))
        {
            return [];
        }

        var analysis = LangSemantic.Analyze(stmts!, new SourceId(0));
        return analysis.Diags
            .Select(d => new DiagActual(
                d.Code.CodeText(),
                d.RuleId.RuleText(),
                d.Severity == Severity.Error ? "error" : "warning",
                d.PrimarySpan?.Start.Line ?? 0))
            .ToList();
    }
}
