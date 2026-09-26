using Lang.Core;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Semantic;

/// <summary>
/// 语义分析产物。除诊断与表之外，提供按 AST 节点查询的视图（内部以节点引用为键；
/// 查询时必须传入<b>同一次分析所消费的那棵 AST</b> 的引用，跨树查询返回 null）。
/// </summary>
public sealed class Analysis
{
    private readonly NodeMap<TypeId> _exprTy;
    private readonly NodeMap<TypeId> _patTy;
    private readonly Dictionary<SymbolId, TypeId> _symTy;
    private readonly Dictionary<SymbolId, TypeId> _fnTy;
    private readonly NodeMap<SymbolId> _declSyms;
    private readonly NodeMap<SymbolId> _refs;
    private readonly NodeMap<SymbolId> _patRefs;
    private readonly NodeMap<MatchKind> _matchKind;

    internal Analysis(
        IReadOnlyList<Diagnostic> diags,
        TypeTables types,
        SymbolArena symbols,
        SourceId source,
        bool checked_,
        NodeMap<TypeId> exprTy,
        NodeMap<TypeId> patTy,
        Dictionary<SymbolId, TypeId> symTy,
        Dictionary<SymbolId, TypeId> fnTy,
        NodeMap<SymbolId> declSyms,
        NodeMap<SymbolId> refs,
        NodeMap<SymbolId> patRefs,
        NodeMap<MatchKind> matchKind)
    {
        Diags = diags;
        Types = types;
        Symbols = symbols;
        Source = source;
        Checked = checked_;
        _exprTy = exprTy;
        _patTy = patTy;
        _symTy = symTy;
        _fnTy = fnTy;
        _declSyms = declSyms;
        _refs = refs;
        _patRefs = patRefs;
        _matchKind = matchKind;
    }

    /// <summary>全部诊断（resolver 在前；resolver 有诊断时 checker 不再运行，Checked=false）。</summary>
    public IReadOnlyList<Diagnostic> Diags { get; }

    /// <summary>类型 arena（含名义 def 表与预载面）。</summary>
    public TypeTables Types { get; }

    /// <summary>符号表（含预载符号；id 即创建序）。</summary>
    public SymbolArena Symbols { get; }

    /// <summary>分析入口的源身份。</summary>
    public SourceId Source { get; }

    /// <summary>checker 是否已运行（resolver 无诊断时为 true）。</summary>
    public bool Checked { get; }

    /// <summary>是否存在任何诊断（v0.4 全为 error 级）。</summary>
    public bool HasErrors() => Diags.Count != 0;

    /// <summary>表达式静态类型（checker 未运行时为 null）。</summary>
    public TypeId? TypeOf(Expr e) => _exprTy.Get(e);

    /// <summary>模式绑定的捕获类型。</summary>
    public TypeId? PatType(Pat p) => _patTy.Get(p);

    /// <summary>绑定/参数符号的类型。</summary>
    public TypeId? SymbolType(SymbolId id) => _symTy.TryGetValue(id, out var t) ? t : null;

    /// <summary>fn/方法符号的函数类型。</summary>
    public TypeId? FnType(SymbolId id) => _fnTy.TryGetValue(id, out var t) ? t : null;

    /// <summary>match 表达式的语义分类。</summary>
    public MatchKind? MatchKindOf(Expr e) => _matchKind.Get(e);

    /// <summary>Ident/StructLit 引用解析到的符号。</summary>
    public SymbolId? SymbolOfRef(Expr e) => _refs.Get(e);

    /// <summary>模式（绑定/构造）解析到的符号。</summary>
    public SymbolId? SymbolOfPat(Pat p) => _patRefs.Get(p);

    /// <summary>声明语句（Let/FnDecl/ForIn）对应的符号。</summary>
    public SymbolId? SymbolOfDecl(Stmt s) => _declSyms.Get(s);

    /// <summary>解析失败产物（仅 E1001 诊断，无表内容物）。</summary>
    internal static Analysis ParseFailed(Diagnostic error, SourceId source) => new(
        [error],
        new TypeTables(),
        new SymbolArena(),
        source,
        checked_: false,
        new NodeMap<TypeId>(),
        new NodeMap<TypeId>(),
        [],
        [],
        new NodeMap<SymbolId>(),
        new NodeMap<SymbolId>(),
        new NodeMap<SymbolId>(),
        new NodeMap<MatchKind>());
}

/// <summary>
/// 语义分析入口（冻结）：名字解析 + 类型检查一体的公共门面。
/// 行为真值：specs/ 优先于实现证据；两处 spec 修正（赋值顶层 ?、块作用域类型写入）
/// 见 Checker/Resolver 模块注。
/// </summary>
public static class LangSemantic
{
    /// <summary>语义分析入口（冻结）：输入为<b>装载器合并后的语句序列</b>（NAME-MERGE-010，
    /// 跨文件同名符号按 NAME-DUP-003 报错）。有诊断时仍返回部分产物。</summary>
    public static Analysis Analyze(IReadOnlyList<Stmt> stmts, SourceId source)
    {
        var res = ResolverDriver.ResolveProgram(stmts, source);
        var resolveDiags = res.Diags;
        var checked_ = resolveDiags.Count == 0;
        if (!checked_)
        {
            // 与证据管线一致：解析期有诊断则不再跑检查器（避免级联噪声）
            return new Analysis(
                resolveDiags,
                res.Tables,
                res.Arena,
                source,
                checked_: false,
                new NodeMap<TypeId>(),
                new NodeMap<TypeId>(),
                [],
                [],
                res.DeclSyms,
                res.Refs,
                res.PatRefs,
                new NodeMap<MatchKind>());
        }
        var checker = new Checker(res);
        var outp = checker.Check(stmts);
        return new Analysis(
            outp.Diags,
            outp.Tables,
            res.Arena,
            source,
            checked_: true,
            outp.ExprTy,
            outp.PatTy,
            outp.SymTy,
            outp.FnTy,
            res.DeclSyms,
            res.Refs,
            res.PatRefs,
            outp.MatchKinds);
    }

    /// <summary>源码直入口（冻结）：parse → Analyze；解析失败时产物仅含 E1001 诊断。</summary>
    public static Analysis AnalyzeSource(SourceText text, SourceId source)
    {
        var parsed = Parser.Parse(text, source);
        return parsed.Error is not null
            ? Analysis.ParseFailed(parsed.Error, source)
            : Analyze(parsed.Program, source);
    }

    /// <summary>
    /// 注解解析公共入口（HIR 构建阶段复用；静默通道，坏 arity 保守回退不报错）。
    /// 前置条件：analysis 无诊断（注解合法）。
    /// </summary>
    public static TypeId ResolveAnnotation(Analysis analysis, TypeEx t, IReadOnlyList<string> tparams)
    {
        var ctx = new AnnoOnlyCtx(analysis.Symbols, analysis.Types);
        return Anno.ResolveAnnoQuiet(ctx, t, tparams);
    }

    private sealed class AnnoOnlyCtx(SymbolArena symbols, TypeTables tables) : ITypeCtx
    {
        public TypeTables Tables => tables;

        public SymbolArena Arena => symbols;

        public SymbolId? LookupNamed(string name)
        {
            // 预载 error → 顶层 → arena 首个同名（与 resolver/checker 同序）
            foreach (var s in symbols.Items)
            {
                if (s.IsBuiltin && s.Kind == SymbolKind.Struct && s.Name == name)
                {
                    return s.Id;
                }
            }
            return symbols.FirstTypeNamed(name);
        }

        public void Emit(DiagnosticCode code, string msg) { }
    }
}
