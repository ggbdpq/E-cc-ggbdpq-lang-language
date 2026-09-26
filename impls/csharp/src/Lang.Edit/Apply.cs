using Lang.Core;

namespace Lang.Edit;

/// <summary>
/// 编辑事务应用（specs/agent-protocol.md §6）。行为证据：impls/rust/crates/lang-edit/src/apply.rs。
///
/// <para>管线：前置校验（sha256）→ 区间合法 → 重叠拒绝 → 码点切片拼接。
/// 原子性：全部文件的校验与拼接<b>全部完成后</b>才进入写入；任一校验失败不产生
/// 任何写入（全成或全不成）。偏移一律 0 基 Unicode 码点；C# 切片按 char，
/// 必须经 <see cref="SourceText.CpToCharIndex(int)"/> 折算后再动 string。</para>
///
/// <para>同文件多编辑按 start 降序（稳定：同 start 保持编辑表序）逐个套用——高偏移
/// 编辑不影响低偏移编辑的区间；同点插入按编辑表序倒序落位（先落者被后落者顶右），
/// 同一 WorkspaceEdit 重复应用结果逐字节一致。文件处理序 = 路径字典序（Ordinal）。</para>
/// </summary>
public static class EditTransaction
{
    /// <summary>内存事务（富错误面）：current 为「分析时」的文件快照（相对路径 → 全文），
    /// 返回被编辑文件的新内容（只含被编辑的文件）；任何校验失败返回 null + 错误，
    /// 不产生部分结果。等价于冻结 <see cref="IEditApplier.ApplyInMemory"/> 的富形态。</summary>
    public static (IReadOnlyDictionary<string, string>? Applied, ApplyError? Error) Apply(
        IReadOnlyDictionary<string, string> current, WorkspaceEdit ws) =>
        Compute(ws, file => current.TryGetValue(file, out var text) ? text : null);

    /// <summary>盘上事务：root 为模块根目录，<see cref="TextEdit.File"/> 为根相对路径。
    /// 全部读取与校验完成后才逐文件写入（临时文件 + 改名，避免半截文件）；
    /// 校验阶段的任何失败不触碰文件系统。写入阶段 IO 失败以 <see cref="ApplyErrorKind.Io"/>
    /// 报告（校验原子，跨文件写入尽力而为——与 rust 证据同口径）。</summary>
    public static ApplyError? ApplyToDirectory(WorkspaceEdit ws, string root) =>
        ComputeAndWrite(ws, root, (file, text) =>
        {
            var target = Path.Combine(root, file);
            var tmp = Path.Combine(root, $".{file}.lang-edit-tmp");
            try
            {
                File.WriteAllText(tmp, text);
                File.Move(tmp, target, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryCleanup(tmp);
                return new ApplyError(ApplyErrorKind.Io, $"edit: 写 {file}: {e.Message}");
            }
            return null;
        });

    private static void TryCleanup(string tmp)
    {
        try
        {
            File.Delete(tmp);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>事务核心（读委托注入）：校验全部文件并计算各自新内容；任何失败即整体
    /// 失败（零写入语义由此保证——调用方拿到结果才允许落盘）。文件按路径字典序处理。</summary>
    private static (IReadOnlyDictionary<string, string>? Applied, ApplyError? Error) Compute(
        WorkspaceEdit ws, Func<string, string?> read)
    {
        if (ws.Edits.Count == 0)
        {
            return (new Dictionary<string, string>(), null);
        }
        var byFile = GroupByFile(ws);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, edits) in byFile)
        {
            var hash = ws.Preconditions.LastOrDefault(p => p.File == file)?.FileHash;
            if (hash is null)
            {
                return (null, new ApplyError(ApplyErrorKind.MissingPrecondition,
                    $"edit: 文件 {file} 缺少前置校验（fileHash）"));
            }
            var source = read(file);
            if (source is null)
            {
                return (null, new ApplyError(ApplyErrorKind.Io,
                    $"edit: 读 {file}: 文件不存在或不可读"));
            }
            var (text, err) = ApplyOneFile(file, source, edits, hash);
            if (err is not null)
            {
                return (null, err);
            }
            results[file] = text!;
        }
        return (results, null);
    }

    private static ApplyError? ComputeAndWrite(
        WorkspaceEdit ws, string root, Func<string, string, ApplyError?> write)
    {
        var (applied, error) = Compute(ws, file =>
        {
            var path = Path.Combine(root, file);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
        if (error is not null)
        {
            return error;
        }
        foreach (var (file, text) in applied!)
        {
            if (write(file, text) is { } writeErr)
            {
                return writeErr;
            }
        }
        return null;
    }

    /// <summary>edits 按文件分组（键序 = 路径字典序；组内保持编辑表序）。</summary>
    internal static SortedDictionary<string, List<TextEdit>> GroupByFile(WorkspaceEdit ws)
    {
        var byFile = new SortedDictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        foreach (var e in ws.Edits)
        {
            if (!byFile.TryGetValue(e.File, out var list))
            {
                list = [];
                byFile[e.File] = list;
            }
            list.Add(e);
        }
        return byFile;
    }

    /// <summary>单文件：哈希核验 → 区间/重叠校验（降序稳定序）→ 码点切片拼接。</summary>
    private static (string? Text, ApplyError? Error) ApplyOneFile(
        string file, string source, IReadOnlyList<TextEdit> edits, string expectHash)
    {
        var actual = EditHash.Sha256Hex(source);
        if (actual != expectHash)
        {
            return (null, new ApplyError(ApplyErrorKind.PreconditionFailed,
                $"edit: {file} 自分析后已变化（哈希不匹配），拒绝写入"));
        }
        var text = new SourceText(file, source);

        // start 降序稳定排序（同 start 保持编辑表序；LINQ OrderBy 文档保证稳定）
        var sorted = edits
            .Select((e, i) => (Edit: e, Seq: i))
            .OrderByDescending(x => x.Edit.StartOffset)
            .ThenBy(x => x.Seq)
            .Select(x => x.Edit)
            .ToList();

        // 校验（码点度量）：逐编辑查越界/倒序，再查与次一编辑的重叠
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (e.StartOffset > e.EndOffset || e.EndOffset > (uint)text.LengthCp)
            {
                return (null, new ApplyError(ApplyErrorKind.OutOfRange,
                    $"edit: {file} 编辑区间越界 [{e.StartOffset}, {e.EndOffset})（长度 {text.LengthCp}）"));
            }
            if (i + 1 < sorted.Count && sorted[i + 1].EndOffset > e.StartOffset)
            {
                var next = sorted[i + 1];
                return (null, new ApplyError(ApplyErrorKind.Overlap,
                    $"edit: {file} 存在重叠编辑区间 [{next.StartOffset}, {next.EndOffset}) 与 [{e.StartOffset}, {e.EndOffset})"));
            }
        }

        // 折算 char 区间（一次性，基于原文；降序套用互不干扰）后拼接
        var sb = new System.Text.StringBuilder(source);
        foreach (var e in sorted)
        {
            var startChar = text.CpToCharIndex((int)e.StartOffset);
            var endChar = text.CpToCharIndex((int)e.EndOffset);
            sb.Remove(startChar, endChar - startChar);
            sb.Insert(startChar, e.Replacement);
        }
        return (sb.ToString(), null);
    }
}

/// <summary>冻结 <see cref="IEditApplier"/> 的实现（out 形态适配富错误面）。</summary>
public sealed class EditApplier : IEditApplier
{
    public bool ApplyInMemory(
        IReadOnlyDictionary<string, string> files,
        WorkspaceEdit edit,
        out IReadOnlyDictionary<string, string>? applied,
        out ApplyErrorKind error)
    {
        var (result, err) = EditTransaction.Apply(files, edit);
        applied = result;
        error = err?.Kind ?? default;
        return err is null;
    }
}
