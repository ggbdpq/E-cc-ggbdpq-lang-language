namespace Lang.Hir;

/// <summary>
/// 结果形态：值或受控错误二选一（契约 §5：内部不变量破坏不 throw 表达，
/// 以受控错误类型返回；用户源码错误不经过本层——那在 semantic 期已拒绝）。
/// </summary>
public readonly record struct LowerResult<T>(T? Value, string? Error)
{
    public static LowerResult<T> Ok(T value) => new(value, null);

    public static LowerResult<T> Fail(string error) => new(default, error);

    public bool IsOk => Error is null;
}

/// <summary>
/// AST 程序 → Typed HIR（本波只冻结签名，实现等 A1 语法面 + A2 语义核就绪后下一波交付）。
/// 冻结约定：
/// - NodeId 分配为<b>先序递增</b>（与 Go/TS/Rust 参考实现一致），同一输入两次 lowering
///   必须产出逐字段相等的 <see cref="HirProg"/>（确定性契约）。
/// - Span 在此层盖章（列级）；符号与类型结论来自 resolver/checker，lowering 只搬运。
/// </summary>
/// <typeparam name="TAst">A1 语法面的程序节点类型（实现方绑定，如 Lang.Syntax 的程序节点），
/// 避免本层反向锁死 A1 的具体形状。</typeparam>
public interface ILowerHir<TAst>
{
    /// <summary>下降整个程序。要求确定性：同输入同输出（逐字段相等）。</summary>
    LowerResult<HirProg> Lower(TAst ast);
}
