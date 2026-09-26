using Lang.Core;

namespace Lang.Edit;

/// <summary>
/// lang-edit —— Safe Editing 编辑事务（specs/agent-protocol.md §6）。
///
/// <para><b>offset 契约（决定性决策，实现时不得违背）</b>：<see cref="TextEdit.StartOffset"/>/
/// <see cref="TextEdit.EndOffset"/> 一律 0 基 <b>Unicode 码点</b>偏移（与 SourceSpan 同度量，
/// specs/source-span.md）——C# string 是 UTF-16 code unit 序列，astral 码点（🦀𝕏 类）占 2 个
/// char；把 char index 当 offset 在 astral 输入上静默错位。实现义务：切片前经
/// <see cref="SourceText.CpToCharIndex(int)"/> 折算，字节偏移止步 parser 内部。</para>
///
/// <para>应用管线（<see cref="EditTransaction"/>）：前置校验（sha256）→ 区间合法 →
/// 重叠拒绝 → 码点切片拼接。原子性：全部文件的校验与拼接<b>全部完成后</b>才进入写入；
/// 任一校验失败不产生任何写入（全成或全不成）。同文件多编辑按 start 降序（稳定）逐个
/// splice——高偏移编辑不影响低偏移编辑的区间；同点插入按编辑表序倒序落位（确定性）。</para>
/// </summary>

/// <summary>单点文本替换：[StartOffset, EndOffset) 区间替换为 <see cref="Replacement"/>（码点度量）。</summary>
public sealed record TextEdit(string File, int StartOffset, int EndOffset, string Replacement);

/// <summary>文件前置校验：FileHash = sha256 hex（调用方计算；编辑写入前必须逐文件核验）。</summary>
public sealed record Precondition(string File, string FileHash);

/// <summary>编辑事务载体：edits 与 preconditions 都是跨文件集合；每涉及的文件各带独立前置校验。</summary>
public sealed record WorkspaceEdit(
    IReadOnlyList<TextEdit> Edits,
    IReadOnlyList<Precondition> Preconditions);

/// <summary>编辑事务的受控拒绝语义（specs/agent-protocol.md §6：前置校验失败/缺前置/重叠区间一律拒绝写入）。
/// 枚举数值序 = 声明序（冻结面；Io 为实现波次对齐 rust ApplyError 补登的第五类）。</summary>
public enum ApplyErrorKind
{
    /// <summary>涉及文件缺 precondition——拒绝。</summary>
    MissingPrecondition,

    /// <summary>fileHash 与盘上内容不符（源码已变化）——拒绝。</summary>
    PreconditionFailed,

    /// <summary>edits 区间重叠——拒绝。</summary>
    Overlap,

    /// <summary>offset 越界或倒序（end &lt; start）——拒绝。</summary>
    OutOfRange,

    /// <summary>文件不存在或读/写 IO 失败（内存快照缺文件 / 盘上事务 IO）。</summary>
    Io,
}

/// <summary>拒绝详情（Kind + 人类可读原因；负载体，不替代 <see cref="ApplyErrorKind"/> 契约面）。</summary>
public sealed record ApplyError(ApplyErrorKind Kind, string Message)
{
    public override string ToString() => Message;
}

/// <summary>编辑事务入口签名（本波登记，不实现）。实现义务：map 进 map 出的多文件事务，
/// 校验阶段零写入、全成或全不成；任一 <see cref="ApplyErrorKind"/> 命中即受控拒绝，
/// 不抛异常表达用户态错误。</summary>
public interface IEditApplier
{
    /// <summary>内存事务：files（路径 → 全文）进、编辑后全文出；拒绝时返回 false 并给出 kind。</summary>
    bool ApplyInMemory(
        IReadOnlyDictionary<string, string> files,
        WorkspaceEdit edit,
        out IReadOnlyDictionary<string, string>? applied,
        out ApplyErrorKind error);
}
