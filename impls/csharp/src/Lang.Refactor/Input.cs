using Lang.Core;
using Lang.Loader;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Refactor;

/// <summary>模块中的一个文件：相对路径 + 源文本 + 全量词元 + 语句序列。</summary>
public sealed record RefactorFile(string Path, SourceText Text, IReadOnlyList<Token> Tokens, IReadOnlyList<Stmt> Stmts)
{
    /// <summary>分析用的源身份（= 路径字典序位次）。</summary>
    public SourceId Source { get; init; }
}

/// <summary>
/// 重构输入：模块文件（源码 + 词元 + 语句）+ 合并语句序列 + 语义分析。
/// 行为证据：impls/rust/crates/lang-refactor/src/input.rs。
///
/// <para>构造纪律（lang-semantic 冻结契约）：<see cref="LangSemantic.Analyze"/> 的查询视图按
/// AST 节点引用取事实——<see cref="FromFiles"/> 先建合并语句序列，再对<b>同一份缓冲</b>跑分析，
/// 之后不再改动该序列。SourceId 按文件路径字典序（Ordinal）分配（确定性），span → 文件反查走
/// <see cref="PathOf"/>。</para>
/// </summary>
public sealed class RefactorInput
{
    private readonly Dictionary<uint, RefactorFile> _byId;

    private RefactorInput(List<RefactorFile> sources, IReadOnlyList<Stmt> merged, Analysis analysis)
    {
        Sources = sources;
        Merged = merged;
        Analysis = analysis;
        _byId = sources.ToDictionary(f => f.Source.Value, f => f);
    }

    /// <summary>文件集合（SourceId 分配序 = 路径字典序）。</summary>
    public IReadOnlyList<RefactorFile> Sources { get; }

    /// <summary>合并后的语句序列（NAME-MERGE-010；分析所消费的同一缓冲）。</summary>
    public IReadOnlyList<Stmt> Merged { get; }

    /// <summary>语义分析产物（refs/declSyms/symbols 查询面）。</summary>
    public Analysis Analysis { get; }

    /// <summary>从 (相对路径, 源码) 列表构造；任一文件解析失败返回 false（重构只对
    /// 可解析模块负责；解析面归 verify 管线报告）。</summary>
    public static bool TryFromFiles(IEnumerable<(string Path, string Text)> files,
        out RefactorInput? input, out string? error)
    {
        var sorted = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var sources = new List<RefactorFile>();
        var merged = new List<Stmt>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var (path, text) = sorted[i];
            var sid = new SourceId((uint)i);
            var st = new SourceText(path, text);
            var parsed = Parser.Parse(st, sid);
            if (parsed.Error is not null)
            {
                input = null;
                error = $"load: {path} 第 {parsed.Error.PrimarySpan?.Start.Line ?? 0} 行: {parsed.Error.Message}";
                return false;
            }
            var lexed = Lexer.Lex(st, sid);
            var toks = lexed.Ok ? lexed.Tokens : [];
            merged.AddRange(parsed.Program);
            sources.Add(new RefactorFile(path, st, toks, parsed.Program) { Source = sid });
        }
        var analysis = LangSemantic.Analyze(merged, new SourceId(0));
        input = new RefactorInput(sources, merged, analysis);
        error = null;
        return true;
    }

    /// <summary>从目录装载（lang-loader：目录即包 + import 展开）。</summary>
    public static bool TryLoad(string dir, out RefactorInput? input, out string? error)
    {
        if (!ModuleLoader.TryLoadProgram(dir, includeTests: false, ImportsViaParse,
                out var program, out var loadError) || program is null)
        {
            input = null;
            error = loadError?.Message ?? "load: 装载失败";
            return false;
        }
        return TryFromFiles(program.Files.Select(f => (f.RelPath, f.Source)), out input, out error);
    }

    /// <summary>import 抽取委托（仅抽 import；解析面错误由 TryFromFiles 报）。</summary>
    public static readonly ImportsOf ImportsViaParse = (relPath, source) =>
    {
        var text = new SourceText(relPath, source);
        var parsed = Parser.Parse(text, new SourceId(0));
        if (parsed.Error is not null)
        {
            return [];
        }
        return parsed.Program.OfType<Stmt.Import>()
            .Select(im => new ImportRef(im.Path, (int)im.Span.Start.Line)).ToList();
    };

    /// <summary>「分析时」的文件内容快照（apply 事务的 precondition 比对基线）。</summary>
    public IReadOnlyDictionary<string, string> Current()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Sources)
        {
            dict[f.Path] = f.Text.Text;
        }
        return dict;
    }

    /// <summary>span → 文件相对路径（未登记 → ""，与契约面一致）。</summary>
    public string PathOf(Span span) =>
        _byId.TryGetValue(span.Source.Value, out var f) ? f.Path : "";

    /// <summary>span → 文件（未登记 → null）。</summary>
    public RefactorFile? FileOf(Span span) =>
        _byId.TryGetValue(span.Source.Value, out var f) ? f : null;

    /// <summary>基线诊断码多重集（apply 闭环的零回归断言基线；按码字符串序）。</summary>
    public List<string> BaselineCodes()
    {
        var codes = Analysis.Diags.Select(d => d.Code.CodeText()).ToList();
        codes.Sort(StringComparer.Ordinal);
        return codes;
    }

    /// <summary>目标解析（协议 &lt;sym&gt;：数字 SymbolId 或符号名；预载符号不可指名）。
    /// 名字命中多个 → 拒绝要求用 id（与证据一致）。</summary>
    public bool TryResolveTarget(string arg, out SymbolId target, out string? error)
    {
        target = default;
        if (arg.Length == 0)
        {
            error = "缺少 --symbol 参数";
            return false;
        }
        if (arg.All(char.IsAsciiDigit))
        {
            if (!uint.TryParse(arg, out var n)
                || n >= (uint)Analysis.Symbols.Count
                || Analysis.Symbols.Get(new SymbolId(n)).IsBuiltin)
            {
                error = $"未知符号 ID {arg}";
                return false;
            }
            target = new SymbolId(n);
            error = null;
            return true;
        }
        var hits = Analysis.Symbols.Items
            .Where(s => !s.IsBuiltin && s.Name == arg)
            .Select(s => s.Id).ToList();
        switch (hits.Count)
        {
            case 1:
                target = hits[0];
                error = null;
                return true;
            case 0:
                error = $"未知符号 {arg}";
                return false;
            default:
                error = $"符号名 {arg} 对应多个符号，请用 --symbol <id>";
                return false;
        }
    }
}
