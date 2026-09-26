using Lang.Core;
using Lang.Edit;

namespace Lang.Refactor;

/// <summary>
/// lang-refactor —— 语义重构三入口（specs/agent-protocol.md §7）。
/// <b>第二波已实现</b>（行为证据：impls/rust/crates/lang-refactor/src/）。
///
/// <para>纪律：基于 SymbolId + <see cref="Analysis"/> 语义引用面，<b>禁 Regex Rename</b>；
/// 默认 dry-run 只产 <c>WorkspaceEdit</c>（Lang.Edit），--apply 落盘并复用
/// precondition → apply → parse → resolve → typecheck 闭环（失败输出 stage: apply|verify）。</para>
/// </summary>

/// <summary>rename 拒绝语义（冻结七类，specs §7：fn/method/变量/参数之外显式拒绝；P1 = struct/enum/ctor/字段）。</summary>
public enum RenameRefusalKind
{
    /// <summary>目标符号不存在。</summary>
    Unknown,

    /// <summary>目标种类不受支持（struct/enum/构造器/字段）。</summary>
    UnsupportedKind,

    /// <summary>新名非法（保留字/形态）。</summary>
    InvalidName,

    /// <summary>合并程序内存在同名目标（保守冲突检测）。</summary>
    Conflict,

    /// <summary>解析有诊断（脏基线不重构）。</summary>
    Dirty,

    /// <summary>找不到定义位。</summary>
    NoDeclSite,

    /// <summary>方法调用接收者类型未知（保守拒绝，不半改）。</summary>
    UntypedReceiver,
}

/// <summary>organize-imports 拒绝语义：import 区间夹代码时保守拒绝（不猜测切割）；
/// 解析失败拒绝（OrganizeRefusalKind.Parse 为实现波次对齐 rust OrganizeRefusal 补登的第二类）。</summary>
public enum OrganizeRefusalKind
{
    /// <summary>import 与其他语句交错（非连续头部区块）。</summary>
    Scattered,

    /// <summary>源码解析失败。</summary>
    Parse,
}

/// <summary>safe-remove 不可删证据：引用计数 &gt; 0 时给出原因与 refSites（path:line:col）。</summary>
public sealed record NotRemovable(string Reason, IReadOnlyList<string> RefSites);

/// <summary>safe-remove 顶层错误（目标种类不受支持/不存在）。</summary>
public enum SafeRemoveErrorKind { Unknown, UnsupportedKind }

/// <summary>重构三入口签名（本波登记，不实现）。实现义务见各方法注释。</summary>
public interface IRefactoring
{
    /// <summary>rename：fn/method/变量/参数；定义位 + 引用位（refs + StructLit 类型引用 + 方法调用点
    /// 经接收者静态类型）全体替换。任一 <see cref="RenameRefusalKind"/> 命中即整体拒绝（不半改）。</summary>
    (WorkspaceEdit? Edit, RenameRefusalKind? Refusal) Rename(SymbolId target, string newName);

    /// <summary>organize-imports：单文件 import 排序 + 去重（保首现）；已序零编辑；区间夹代码拒绝。</summary>
    (WorkspaceEdit? Edit, OrganizeRefusalKind? Refusal) OrganizeImports(string file, string source);

    /// <summary>safe-remove：仅顶层 fn/全局变量、引用计数 0 才给删除编辑；有引用返回证据。</summary>
    (WorkspaceEdit? Applied, NotRemovable? NotRemovable, SafeRemoveErrorKind? Error) SafeRemove(SymbolId target);
}
