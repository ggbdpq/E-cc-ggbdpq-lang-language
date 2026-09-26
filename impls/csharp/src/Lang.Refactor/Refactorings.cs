using Lang.Core;
using Lang.Edit;
using Fmt = Lang.Formatter.Formatter;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Refactor;

/// <summary>rename 拒绝详情（冻结类别 + 人类可读原因）。</summary>
public sealed record RenameRefused(RenameRefusalKind Kind, string Message)
{
    public override string ToString() => Message;
}

/// <summary>organize-imports 拒绝详情。</summary>
public sealed record OrganizeRefused(OrganizeRefusalKind Kind, string Message)
{
    public override string ToString() => Message;
}

/// <summary>safe-remove 三态结论（可删 / 不可删附证据 / 入口级错误）。</summary>
public sealed record SafeRemoveVerdict(
    WorkspaceEdit? Applied, NotRemovable? NotRemovable, SafeRemoveErrorKind? Error);

/// <summary>闭环失败（specs §6：stage apply|verify）。</summary>
public enum RefactorErrorKind { Apply, Verify }

/// <summary>闭环失败详情。</summary>
public sealed record RefactorError(RefactorErrorKind Kind, string Message)
{
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>
/// 语义重构三入口（specs/agent-protocol.md §7）。行为证据：
/// impls/rust/crates/lang-refactor/src/{rename,imports,remove,apply}.rs。
/// 纪律：基于 SymbolId + <see cref="Analysis"/> 语义引用面，<b>禁 Regex Rename</b>；
/// 默认 dry-run 只产 <see cref="WorkspaceEdit"/>（Lang.Edit），落盘走
/// precondition → apply → parse → resolve → typecheck 闭环（<see cref="ApplyAndVerify"/>）。
/// </summary>
public static class Refactorings
{
    /// <summary>新名非法字符/保留名名单（逐字对齐 TS 证据 refactor.ts reservedNames）。</summary>
    private static readonly string[] Reserved =
    [
        "let", "var", "fn", "return", "if", "else", "while", "for", "in", "match",
        "struct", "enum", "true", "false", "break", "continue", "go", "chan", "import",
        "extern", "interface", "println", "print", "len", "str", "int", "float",
        "keys", "values", "has", "push", "assert", "err", "bytes", "char",
        "jsonParse", "jsonStringify", "isNone", "makeChan", "send", "recv",
        "Some", "None", "Ok", "Err", "http",
    ];

    /// <summary>校验新名（空名/非法字符/保留名拒绝）。</summary>
    public static RenameRefused? ValidateNewName(string name)
    {
        if (name.Length == 0)
        {
            return new RenameRefused(RenameRefusalKind.InvalidName, "新名字为空");
        }
        for (var i = 0; i < name.Length; i++)
        {
            var r = name[i];
            var ok = r == '_'
                || (r >= 'a' && r <= 'z')
                || (r >= 'A' && r <= 'Z')
                || (i > 0 && r is >= '0' and <= '9');
            if (!ok)
            {
                return new RenameRefused(RenameRefusalKind.InvalidName, $"新名字含非法字符 {r}");
            }
        }
        if (Reserved.Contains(name))
        {
            return new RenameRefused(RenameRefusalKind.InvalidName,
                $"新名字 {name} 与关键字/保留字/内建名冲突");
        }
        return null;
    }

    /// <summary>计划一次 rename（dry-run）：返回跨文件 WorkspaceEdit，不落盘。</summary>
    public static (WorkspaceEdit? Edit, RenameRefused? Refusal) Rename(
        RefactorInput input, SymbolId target, string newName)
    {
        if (ValidateNewName(newName) is { } bad)
        {
            return (null, bad);
        }
        if (target.Value >= (uint)input.Analysis.Symbols.Count)
        {
            return (null, new RenameRefused(RenameRefusalKind.Unknown, $"未知符号 ID {target.Value}"));
        }
        var sym = input.Analysis.Symbols.Get(target);
        if (sym.IsBuiltin)
        {
            return (null, new RenameRefused(RenameRefusalKind.Unknown,
                $"符号 {target.Value} 是预载符号，不可 rename"));
        }
        if (sym.Name == "_")
        {
            return (null, new RenameRefused(RenameRefusalKind.InvalidName, "占位符 _ 不可 rename"));
        }
        switch (sym.Kind)
        {
            case SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Ctor:
                return (null, new RenameRefused(RenameRefusalKind.UnsupportedKind,
                    $"暂不支持对 {sym.Kind.KindName()} 的 rename（列 P1）"));
            case SymbolKind.Fn or SymbolKind.Method or SymbolKind.Var or SymbolKind.Param:
                break;
            default:
                throw new InvalidOperationException($"未登记的 SymbolKind: {sym.Kind}");
        }
        if (!input.Analysis.Checked)
        {
            return (null, new RenameRefused(RenameRefusalKind.Dirty,
                "模块存在解析诊断，引用集不完整，拒绝 rename"));
        }
        // 保守冲突检测：其他非预载符号占用新名即拒绝
        foreach (var s in input.Analysis.Symbols.Items)
        {
            if (s.IsBuiltin || s.Id == target)
            {
                continue;
            }
            if (s.Name == newName)
            {
                return (null, new RenameRefused(RenameRefusalKind.Conflict,
                    $"新名字 {newName} 与已有符号冲突（{s.Name} #{s.Id.Value}）"));
            }
        }

        var edits = new List<TextEdit>();

        // —— 定义位 ——
        switch (sym.Kind)
        {
            case SymbolKind.Fn or SymbolKind.Method:
                {
                    var decls = AstWalk.DeclStmtsOf(input, target);
                    var found = false;
                    foreach (var d in decls.OfType<Stmt.FnDecl>())
                    {
                        if (Locate.DeclNameSpan(input, d.Span, d.Recv is not null) is { } nameSp)
                        {
                            edits.Add(EditAt(input, nameSp, newName));
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        return (null, new RenameRefused(RenameRefusalKind.NoDeclSite,
                            $"未找到 {sym.Name} 的声明节点"));
                    }
                    break;
                }
            case SymbolKind.Var:
                {
                    // let/var 绑定、for 循环变量、match 绑定模式
                    var found = false;
                    foreach (var d in AstWalk.DeclStmtsOf(input, target))
                    {
                        Span? nameSp = d switch
                        {
                            Stmt.Let let => Locate.FirstIdentSpan(input, let.Span),
                            Stmt.ForIn forIn => Locate.ForinVarSpan(input, forIn.Span),
                            _ => null,
                        };
                        if (nameSp is not null)
                        {
                            edits.Add(EditAt(input, nameSp.Value, newName));
                            found = true;
                        }
                    }
                    foreach (var p in AstWalk.PatBindingsOf(input, target))
                    {
                        edits.Add(EditAt(input, p.Span, newName));
                        found = true;
                    }
                    if (!found)
                    {
                        return (null, new RenameRefused(RenameRefusalKind.NoDeclSite,
                            $"未找到绑定 {sym.Name} 的声明"));
                    }
                    break;
                }
            case SymbolKind.Param:
                {
                    var (declSpan, hasRecv, seg, locErr) = LocateParam(input, target, sym);
                    if (locErr is not null)
                    {
                        return (null, locErr);
                    }
                    if (Locate.ParamIdentSpan(input, declSpan!.Value, hasRecv, seg) is not { } paramSp)
                    {
                        return (null, new RenameRefused(RenameRefusalKind.NoDeclSite, "未在声明中定位到参数名"));
                    }
                    edits.Add(EditAt(input, paramSp, newName));
                    break;
                }
        }

        // —— 引用位：全部 Ident 引用（读/写/调用/go 目标）——
        foreach (var e in AstWalk.RefsOf(input, target))
        {
            edits.Add(EditAt(input, e.Span, newName));
        }

        // —— 方法调用点：obj.name(...) 经接收者类型解析到本方法 ——
        if (sym.Kind == SymbolKind.Method)
        {
            var refusal = CollectMethodCallSites(input, target, sym.Name, newName, edits);
            if (refusal is not null)
            {
                return (null, refusal);
            }
        }

        // 去重（同文件同起点）+ 按文件降序、区间起点降序输出
        var ordered = edits
            .OrderByDescending(e => e.File, StringComparer.Ordinal)
            .ThenByDescending(e => e.StartOffset)
            .ToList();
        var deduped = new List<TextEdit>();
        foreach (var e in ordered)
        {
            if (deduped.Count > 0)
            {
                var last = deduped[^1];
                if (last.File == e.File && last.StartOffset == e.StartOffset)
                {
                    continue;
                }
            }
            deduped.Add(e);
        }

        // precondition：每个涉及文件一条（对当前内容取哈希）
        var files = deduped.Select(e => e.File).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var current = input.Current();
        var preconditions = files.Select(f =>
            new Precondition(f, EditHash.Sha256Hex(current.TryGetValue(f, out var t) ? t : ""))).ToList();
        return (new WorkspaceEdit(deduped, preconditions), null);
    }

    private static TextEdit EditAt(RefactorInput input, Span sp, string newName) =>
        new(input.PathOf(sp), (int)sp.Start.Offset, (int)sp.End.Offset, newName);

    /// <summary>定位参数的声明：所属 fn 声明 span + 是否带接收者 + 段位（含接收者位）。
    ///
    /// 所属 fn 判定：引用点的所属上下文（参数只在属主函数体内被引用）；
    /// 从未被引用时退化为「全程序唯一含同名参数的 fn 声明」（多于一个即拒绝，
    /// 与保守冲突纪律一致）。</summary>
    private static (Span? DeclSpan, bool HasRecv, int Seg, RenameRefused? Error) LocateParam(
        RefactorInput input, SymbolId target, Symbol sym)
    {
        SymbolId? owner = null;
        foreach (var o in AstWalk.RefOwnersOf(input, target))
        {
            if (o is not null)
            {
                owner = o;
                break;
            }
        }
        Span? declSpan;
        bool hasRecv;
        if (owner is not null)
        {
            declSpan = null;
            hasRecv = false;
            foreach (var d in AstWalk.DeclStmtsOf(input, owner.Value).OfType<Stmt.FnDecl>())
            {
                declSpan = d.Span;
                hasRecv = d.Recv is not null;
                break;
            }
            if (declSpan is null)
            {
                return (null, false, 0, new RenameRefused(RenameRefusalKind.NoDeclSite, "未找到参数所属函数的声明节点"));
            }
        }
        else
        {
            var hits = new List<(Span Span, bool Recv)>();
            EachDecl(input, (span, recv, _) => hits.Add((span, recv)));
            if (hits.Count != 1)
            {
                return (null, false, 0, new RenameRefused(RenameRefusalKind.NoDeclSite,
                    $"参数 {sym.Name} 从未被引用且无法唯一定位所属函数"));
            }
            declSpan = hits[0].Span;
            hasRecv = hits[0].Recv;
        }
        // 段位：声明内同名参数位序（干净程序内唯一；同名多个本身即 E2001 脏程序）
        var seg = -1;
        EachDecl(input, (span, _, names) =>
        {
            if (seg < 0 && span == declSpan)
            {
                seg = names.IndexOf(sym.Name);
            }
        });
        if (seg < 0)
        {
            return (null, false, 0, new RenameRefused(RenameRefusalKind.NoDeclSite,
                $"符号 {target.Value} 不在声明参数表内"));
        }
        return (declSpan, hasRecv, seg, null);
    }

    /// <summary>全程序 FnDecl 走查（span, 是否带 recv, 参数名列表——接收者在首位）。</summary>
    private static void EachDecl(RefactorInput input, Action<Span, bool, List<string>> f)
    {
        AstWalk.Walk(input.Merged,
            s =>
            {
                if (s is Stmt.FnDecl d)
                {
                    var names = new List<string>();
                    if (d.Recv is not null)
                    {
                        names.Add(d.Recv.Name);
                    }
                    names.AddRange(d.Params.Select(p => p.Name));
                    f(d.Span, d.Recv is not null, names);
                }
            }, null, null);
    }

    /// <summary>方法调用点收集：obj.name(args) 的 name 位置。
    ///
    /// 判定链：Field 名相同（AST 结构事实）→ 接收者表达式静态类型 → struct def →
    /// struct 符号 methods 表命中目标 → 改名点。接收者类型缺失（None/Any/Unknown/
    /// Invalid）的同名点存在时拒绝（保守，不半改）。</summary>
    private static RenameRefused? CollectMethodCallSites(
        RefactorInput input, SymbolId target, string oldName, string newName, List<TextEdit> edits)
    {
        Span? untyped = null;
        AstWalk.WalkOwned(input, input.Merged, null, (e, _) =>
        {
            if (e is not Expr.Call call || call.Callee is not Expr.Field field
                || field.Name != oldName)
            {
                return;
            }
            if (Locate.LastIdentSpan(input, field.Span) is not { } nsp)
            {
                return;
            }
            var ty = input.Analysis.TypeOf(field.Obj);
            var owned = ty is not null && MethodOwnerOf(input, ty.Value, oldName) == target;
            if (owned)
            {
                edits.Add(new TextEdit(input.PathOf(nsp), (int)nsp.Start.Offset, (int)nsp.End.Offset, newName));
            }
            else if (untyped is null && IsUnknownType(input, ty))
            {
                untyped = nsp;
            }
        });
        if (untyped is { } sp)
        {
            return new RenameRefused(RenameRefusalKind.UntypedReceiver,
                $"第 {sp.Start.Line} 行存在同名方法调用点，但接收者类型未知，无法确认归属，拒绝 rename");
        }
        return null;
    }

    private static bool IsUnknownType(RefactorInput input, TypeId? ty)
    {
        if (ty is null)
        {
            return true;
        }
        return input.Analysis.Types.Data(ty.Value) is
            TypeData.Any or TypeData.Unknown or TypeData.Invalid;
    }

    /// <summary>接收者类型 + 方法名 → 方法符号（TypeId → struct def → struct 符号 methods 表）。</summary>
    private static SymbolId? MethodOwnerOf(RefactorInput input, TypeId t, string method)
    {
        if (input.Analysis.Types.Data(t) is not TypeData.Struct def)
        {
            return null;
        }
        SymbolId? sid = null;
        foreach (var s in input.Analysis.Symbols.Items)
        {
            if (!s.IsBuiltin && s.Kind == SymbolKind.Struct
                && s.Nominal is NominalRef.Struct sr && sr.Def == def.Def)
            {
                sid = s.Id;
                break;
            }
        }
        if (sid is null)
        {
            return null;
        }
        return input.Analysis.Symbols.Get(sid.Value).Methods.GetValueOrDefault(method);
    }

    /// <summary>organize-imports：单文件 import 块排序 + 去重，注释零丢失（specs §7）。
    ///
    /// 结构事实来自 AST（Stmt.Import 的 path 与 span），非文本搜索；注释经无损 trivia
    /// 层保留。行为口径：去重保首现、码点序排序；行尾注释随行走；区间内的独立注释行
    /// 归拢到块首（原相对序）；已序零编辑；import 区间内夹非 import 代码保守拒绝。</summary>
    public static (WorkspaceEdit? Edit, OrganizeRefused? Refusal) OrganizeImports(string file, string source)
    {
        var text = new SourceText(file, source);
        var parsed = Parser.Parse(text, new SourceId(0));
        if (parsed.Error is not null)
        {
            return (null, new OrganizeRefused(OrganizeRefusalKind.Parse,
                $"第 {parsed.Error.PrimarySpan?.Start.Line ?? 0} 行: {parsed.Error.Message}"));
        }
        var imports = parsed.Program.OfType<Stmt.Import>()
            .Select(im => (im.Path, im.Span)).ToList();

        // 零/单条 import：无序可排、无重可去
        if (imports.Count <= 1)
        {
            return (new WorkspaceEdit([], []), null);
        }

        // 区间：首个 import 的行首 → 末个 import 的行尾（不含换行；行尾 \r 含在区间内）
        var start = LineStart(source, imports.Min(i => i.Span.Start.Offset));
        var end = LineEnd(source, imports.Max(i => i.Span.End.Offset));
        var trivia = TriviaScanner.Scan(text, new SourceId(0));

        // 区间内逐行分类：import 行 / 独立注释行 / 空行 / 其他（拒绝）
        var region = CpSlice(text, start, end);
        var lines = region.Split('\n');
        var importLines = new List<int>();
        var lineStartCp = start;
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var ls = lineStartCp;
            var le = ls + (uint)CpCount(line);
            lineStartCp = le + 1; // 越过换行
            var isImport = imports.Any(i => i.Span.Start.Offset >= ls && i.Span.End.Offset <= le);
            var trimmed = line.Trim();
            if (isImport)
            {
                importLines.Add(li);
            }
            else if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                // 独立注释 / 空行：允许在区间内（归拢保序）
            }
            else
            {
                return (null, new OrganizeRefused(OrganizeRefusalKind.Scattered,
                    $"{file} 第 {text.LineCol((int)ls).Line} 行：import 区间内夹有代码，organize-imports 拒绝处理"));
            }
        }

        // 行尾注释：import span 结束后、同行内的首条注释
        Span? TrailingComment(uint impEnd, uint le)
        {
            foreach (var t in trivia)
            {
                if (t.Kind == TriviaKind.Comment
                    && t.Span.Start.Offset >= impEnd && t.Span.Start.Offset < le)
                {
                    return t.Span;
                }
            }
            return null;
        }

        // 编排事实：去重保首现 + 排序（码点序）
        var seen = new List<string>();
        var kept = new List<(string Path, string? Comment)>();
        foreach (var (path, span) in imports)
        {
            if (seen.Contains(path))
            {
                continue;
            }
            seen.Add(path);
            var le = LineEnd(source, span.End.Offset);
            string? comment = null;
            if (TrailingComment(span.End.Offset, le) is { } csp)
            {
                comment = CpSlice(text, csp.Start.Offset, csp.End.Offset);
            }
            kept.Add((path, comment));
        }
        kept.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        // 新块：区间内的独立注释行归拢块首（原相对序），随后排序后的 import 行
        var block = new List<string>();
        for (var li = 0; li < lines.Length; li++)
        {
            if (importLines.Contains(li))
            {
                continue;
            }
            var trimmed = lines[li].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            block.Add(trimmed);
        }
        foreach (var (path, comment) in kept)
        {
            var lineCp = $"import \"{path}\"";
            if (comment is not null)
            {
                lineCp += " " + comment;
            }
            block.Add(lineCp);
        }
        var newBlock = string.Join("\n", block);
        // 规范化对比：原块行尾 \r 归一（CRLF 源）后与新块比较
        var oldNorm = region.Replace("\r\n", "\n").TrimEnd('\r');
        if (oldNorm == newBlock)
        {
            return (new WorkspaceEdit([], []), null);
        }

        return (new WorkspaceEdit(
            [new TextEdit(file, (int)start, (int)end, newBlock)],
            [new Precondition(file, EditHash.Sha256Hex(source))]), null);
    }

    /// <summary>safe-remove：仅顶层 fn/全局变量、引用计数 0 才给删除编辑（specs §7）。
    ///
    /// 引用事实来自 Analysis.refs（Ident 引用；出现序即证据序）；有引用时返回
    /// <see cref="NotRemovable"/>，refSites 形如 path:line:col。删除区间吞掉声明后的
    /// 单个换行（CRLF 源含 \r），避免残留空行。</summary>
    public static SafeRemoveVerdict SafeRemove(RefactorInput input, SymbolId target)
    {
        if (target.Value >= (uint)input.Analysis.Symbols.Count)
        {
            return new SafeRemoveVerdict(null, null, SafeRemoveErrorKind.Unknown);
        }
        var sym = input.Analysis.Symbols.Get(target);
        if (sym.IsBuiltin)
        {
            return new SafeRemoveVerdict(null, null, SafeRemoveErrorKind.Unknown);
        }
        // 仅顶层 fn 与全局变量（协议 §7）
        NotRemovable? notRemovable;
        switch (sym.Kind)
        {
            case SymbolKind.Fn when sym.IsExtern:
                notRemovable = new NotRemovable("extern 声明不支持 safe remove", []);
                return new SafeRemoveVerdict(null, notRemovable, null);
            case SymbolKind.Fn or SymbolKind.Var:
                break;
            default:
                notRemovable = new NotRemovable(
                    $"safe remove 仅支持顶层 fn 与全局变量（目标 kind={sym.Kind.KindName()}）", []);
                return new SafeRemoveVerdict(null, notRemovable, null);
        }
        // 顶层性：声明语句必须出现在合并程序的顶层切片
        Span? declSpan = null;
        foreach (var s in input.Merged)
        {
            if (input.Analysis.SymbolOfDecl(s) == target)
            {
                declSpan = s.Span;
                break;
            }
        }
        if (declSpan is null)
        {
            notRemovable = new NotRemovable(
                $"safe remove 仅支持顶层 fn 与全局变量（{sym.Name} 非顶层声明）", []);
            return new SafeRemoveVerdict(null, notRemovable, null);
        }

        // 引用计数（定义位之外的全部 Ident 引用）
        var refs = AstWalk.RefsOf(input, target);
        if (refs.Count > 0)
        {
            var sites = refs.Select(e =>
            {
                var sp = e.Span;
                var path = input.PathOf(sp);
                return path.Length == 0
                    ? $"{sp.Start.Line}:{sp.Start.Column}"
                    : $"{path}:{sp.Start.Line}:{sp.Start.Column}";
            }).ToList();
            return new SafeRemoveVerdict(null,
                new NotRemovable($"{refs.Count} 处引用", sites), null);
        }

        // 删除编辑：[声明起点, 行尾 + 换行)；吞掉行尾 \r 与声明后的单个 \n
        var file = input.PathOf(declSpan.Value);
        var text = input.FileOf(declSpan.Value)?.Text.Text ?? "";
        var end = LineEnd(text, declSpan.Value.End.Offset);
        var st = new SourceText(file, text);
        // cp → 该码点首 char（\r/\n 为 BMP 单 char；越界给 \0 不匹配）
        char At(uint cp)
        {
            var ci = st.CpToCharIndex((int)cp);
            return ci < text.Length ? text[ci] : '\0';
        }
        if (At(end) == '\r')
        {
            end++;
        }
        if (At(end) == '\n')
        {
            end++;
        }
        return new SafeRemoveVerdict(
            new WorkspaceEdit(
                [new TextEdit(file, (int)declSpan.Value.Start.Offset, (int)end, "")],
                [new Precondition(file, EditHash.Sha256Hex(text))]),
            null, null);
    }

    /// <summary>应用事务并对结果做闭环验证，成功返回全部文件的新内容
    /// （被编辑文件 = 应用后的文本；未涉及文件 = 原文）。
    ///
    /// 零回归断言：重新 parse + re-analyze 后的诊断码多重集必须与基线完全一致
    /// （基线为空则结果必须零诊断）。C# 与 rust 证据的差异：format 步待
    /// Lang.Formatter 落地后插入（当前编辑产物由 fixture 侧保证规范形）。</summary>
    public static (IReadOnlyDictionary<string, string>? Contents, RefactorError? Error) ApplyAndVerify(
        WorkspaceEdit ws, RefactorInput baseline)
    {
        // 1) apply（内存事务：校验 + 拼接，失败即整体失败）
        var (edited, applyError) = EditTransaction.Apply(baseline.Current(), ws);
        if (applyError is not null)
        {
            return (null, new RefactorError(RefactorErrorKind.Apply, applyError.Message));
        }

        // 2) format（被编辑文件规范化；解析失败 = verify 失败）
        var contents = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (f, t) in baseline.Current())
        {
            contents[f] = t;
        }
        foreach (var (f, t) in edited!)
        {
            var formatted = Fmt.FormatSource(t);
            if (formatted.Error is not null)
            {
                return (null, new RefactorError(RefactorErrorKind.Verify,
                    $"格式化 {f} 失败: {formatted.Error.Message}"));
            }
            contents[f] = formatted.Output;
        }
        var files = contents.Select(kv => (kv.Key, kv.Value)).ToList();
        if (!Rebuild(files, out var got, out var rebuildError) || got is null)
        {
            return (null, new RefactorError(RefactorErrorKind.Verify, rebuildError ?? "重新分析失败"));
        }
        var baseCodes = baseline.BaselineCodes();
        if (!got.SequenceEqual(baseCodes, StringComparer.Ordinal))
        {
            return (null, new RefactorError(RefactorErrorKind.Verify,
                $"诊断零回归断言失败：基线 [{string.Join(", ", baseCodes)}]，编辑后 [{string.Join(", ", got)}]"));
        }
        return (new Dictionary<string, string>(contents), null);
    }

    /// <summary>重建语义视图：逐文件 parse → 合并 → analyze。返回诊断码多重集（升序）。</summary>
    private static bool Rebuild(List<(string Path, string Text)> files,
        out List<string>? codes, out string? error)
    {
        var merged = new List<Stmt>();
        for (var i = 0; i < files.Count; i++)
        {
            var (path, text) = files[i];
            var parsed = Parser.Parse(new SourceText(path, text), new SourceId((uint)i));
            if (parsed.Error is not null)
            {
                codes = null;
                error = $"重新解析 {path} 失败（第 {parsed.Error.PrimarySpan?.Start.Line ?? 0} 行）: {parsed.Error.Message}";
                return false;
            }
            merged.AddRange(parsed.Program);
        }
        var analysis = LangSemantic.Analyze(merged, new SourceId(0));
        codes = analysis.Diags.Select(d => d.Code.CodeText()).Order(StringComparer.Ordinal).ToList();
        error = null;
        return true;
    }

    // —— 码点工具（SourceText 折算底座） ——

    /// <summary>码点区间取切片（UTF-16 安全；越界收敛到文末）。</summary>
    internal static string CpSlice(SourceText text, uint start, uint end)
    {
        var a = text.CpToCharIndex((int)start);
        var b = text.CpToCharIndex((int)Math.Max(start, end));
        return text.Text[a..b];
    }

    /// <summary>串内码点数（astral 码点计 1）。</summary>
    internal static int CpCount(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length;)
        {
            var len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            i += len;
            n++;
        }
        return n;
    }

    /// <summary>cp 之前最近的行首（0 或最近 \n 之后）。</summary>
    private static uint LineStart(string src, uint cp)
    {
        var lineStart = 0u;
        var i = 0u;
        foreach (var ch in CpChars(src))
        {
            if (i >= cp)
            {
                break;
            }
            if (ch == '\n')
            {
                lineStart = i + 1;
            }
            i++;
        }
        return lineStart;
    }

    /// <summary>cp 所在行的行尾（\n 之前；\r 含在行内）。</summary>
    private static uint LineEnd(string src, uint cp)
    {
        var i = 0u;
        foreach (var ch in CpChars(src))
        {
            if (i >= cp && ch == '\n')
            {
                return i;
            }
            i++;
        }
        return i;
    }

    /// <summary>按码点枚举（astral 代理对合为一个）。</summary>
    private static IEnumerable<char> CpChars(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            yield return s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++; // 低代理跳过（合占一个码点位次）
            }
        }
    }
}

/// <summary>冻结 <see cref="IRefactoring"/> 三入口签名（会话形态：绑定一个重构输入）。</summary>
public sealed class RefactoringSession(RefactorInput input) : IRefactoring
{
    /// <summary>底层输入（调用方可自查诊断/符号面）。</summary>
    public RefactorInput Input { get; } = input;

    /// <inheritdoc/>
    public (WorkspaceEdit? Edit, RenameRefusalKind? Refusal) Rename(SymbolId target, string newName)
    {
        var (edit, refusal) = Refactorings.Rename(Input, target, newName);
        return (edit, refusal?.Kind);
    }

    /// <inheritdoc/>
    public (WorkspaceEdit? Edit, OrganizeRefusalKind? Refusal) OrganizeImports(string file, string source)
    {
        var (edit, refusal) = Refactorings.OrganizeImports(file, source);
        return (edit, refusal?.Kind);
    }

    /// <inheritdoc/>
    public (WorkspaceEdit? Applied, NotRemovable? NotRemovable, SafeRemoveErrorKind? Error) SafeRemove(SymbolId target)
    {
        var verdict = Refactorings.SafeRemove(Input, target);
        return (verdict.Applied, verdict.NotRemovable, verdict.Error);
    }
}
