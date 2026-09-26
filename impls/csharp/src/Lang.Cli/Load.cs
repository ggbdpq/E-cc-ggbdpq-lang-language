using Lang.Contract;
using Lang.Core;
using Lang.Ir;
using Lang.Loader;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Cli;

/// <summary>CLI 统一装载面：Lang.Loader 装载 → 逐文件 parse（SourceId 按装载序分配）→
/// 合并语句序列（剥除 import 语句）→ <see cref="LangSemantic.Analyze"/> 语义分析。</summary>
///
/// <para>SourceId 约定（与 lang-rs lang-cli load.rs 同一口径）：每个文件按<b>装载序</b>
/// 分配 <c>SourceId(i)</c>（span 随 AST 节点保留原 id，DTO 反查经
/// <see cref="CliModule.Names"/>）；合并序列用 <c>SourceId(0)</c> 作分析锚
/// （无锚诊断落在首文件）。</para>
internal static class Loader
{
    /// <summary>装载结果三态（值/受控错误二选一，不 throw）。</summary>
    internal abstract record LoadResult
    {
        /// <summary>装载并分析成功（可能带语义诊断，见 <see cref="CliModule"/>）。</summary>
        internal sealed record Ok(CliModule Module) : LoadResult;

        /// <summary>装载器错误（文件缺失/import 环等；消息文本已含位置前缀；非 Diagnostic）。</summary>
        internal sealed record LoadError(string Message) : LoadResult;

        /// <summary>任一文件解析失败：报告全部解析诊断 + 反查表（不做语义分析，
        /// 避免缺文件的连锁噪声）。</summary>
        internal sealed record ParseError(IReadOnlyList<Diagnostic> Diagnostics, SourceNames Names) : LoadResult;
    }

    /// <summary>已装载并分析的一个模块。</summary>
    internal sealed class CliModule
    {
        /// <summary>装载文件（SourceId 装载序；inspect type 的源码定位用）。</summary>
        internal sealed record CliFile(string RelPath, string Text, SourceId Sid);

        /// <summary>合并语句序列（NAME-MERGE-010；<c>Analysis</c> 消费的同一份缓冲）。</summary>
        public required IReadOnlyList<Stmt> Merged { get; init; }

        public required Analysis Analysis { get; init; }

        /// <summary>SourceId → 文件相对路径（DTO <c>file</c> 反查）。</summary>
        public required SourceNames Names { get; init; }

        /// <summary>测试文件相对路径（装载序；test 命令用）。</summary>
        public required IReadOnlyList<string> TestFiles { get; init; }

        /// <summary>装载文件集（源文本；inspect type 定位用）。</summary>
        public required IReadOnlyList<CliFile> Files { get; init; }
    }

    /// <summary>装载 <paramref name="dir"/> 为一个模块。<paramref name="includeTests"/> 时
    /// 追加根目录 <c>*_test.lang-go</c>（run/check 传 false，test 传 true——MODULE-TEST-007）。</summary>
    internal static LoadResult LoadModule(string dir, bool includeTests)
    {
        if (!ModuleLoader.TryLoadProgram(dir, includeTests, ParseImports, out var program, out var error))
        {
            return new LoadResult.LoadError(error!.Message);
        }

        var names = new SourceNames();
        var merged = new List<Stmt>();
        var parseDiags = new List<Diagnostic>();
        var loaded = program!;
        var fileStmts = new List<IReadOnlyList<Stmt>>(loaded.Files.Count);
        var files = new List<CliModule.CliFile>(loaded.Files.Count);
        for (int i = 0; i < loaded.Files.Count; i++)
        {
            var f = loaded.Files[i];
            var sid = new SourceId((uint)i);
            var text = new SourceText(f.RelPath, f.Source);
            files.Add(new CliModule.CliFile(f.RelPath, f.Source, sid));
            var parsed = Parser.Parse(text, sid);
            if (parsed.Error is not null)
            {
                parseDiags.Add(parsed.Error);
                fileStmts.Add([]);
            }
            else
            {
                fileStmts.Add(parsed.Program);
                // import 语句剥除（装载器的文件级合并已完成依赖先序；语句流上
                // import 是空操作标记——解释器拒绝执行 Import，参考实现
                // expand 在语句位替换后同样不保留 import 语句）
                foreach (var s in parsed.Program)
                {
                    if (s is not Stmt.Import)
                    {
                        merged.Add(s);
                    }
                }
            }

            names.Add(sid, f.RelPath);
        }

        if (parseDiags.Count > 0)
        {
            return new LoadResult.ParseError(parseDiags, names);
        }

        var analysis = LangSemantic.Analyze(merged, new SourceId(0));
        return new LoadResult.Ok(new CliModule
        {
            Merged = merged,
            Analysis = analysis,
            Names = names,
            TestFiles = loaded.TestFiles,
            Files = files,
        });
    }

    /// <summary>importsOf 的解析器接线真实现：Lang.Syntax parse 抽顶层 Import，
    /// 解析失败返回空列表（规格同 lang-rs parse_imports，单一事实源收拢在 CLI 内部
    /// ——Lang.Loader 本波冻结不动）。</summary>
    internal static IReadOnlyList<ImportRef> ParseImports(string relPath, string source)
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
}
