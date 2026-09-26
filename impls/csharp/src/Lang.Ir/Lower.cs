using Lang.Hir;
using Lang.Core;

namespace Lang.Ir;

/// <summary>程序入口（init/main）解析结果：执行器按此启动（有 init 先执行一次，再执行 main）。</summary>
public readonly record struct EntryPoints(FunctionId? Init, FunctionId? Main);

/// <summary>HIR → IR lowering 失败形态（内部不变量破坏；非用户诊断）。</summary>
public sealed record IrLowerError(string Message)
{
    public override string ToString() => $"IR lowering 内部错误: {Message}";
}

/// <summary>
/// Typed HIR → Core IR（本波只冻结签名，实现等语义核就绪后下一波交付）。
/// 降形纪律（冻结，lang-rs 同构）：块模型两铁律——①多操作数构造一律先落临时槽再统一压栈；
/// ②跨块存活的值一律驻局部槽。地址组件保 place 身份（见 Model.cs 的 Place 溯源契约）。
/// 产物必须通过 <see cref="Validator.Validate"/>。
/// </summary>
public interface ILowerIr
{
    /// <summary>下降整个程序。要求确定性：同输入同输出（逐字段相等）。</summary>
    LowerResult<IrProgram> Lower(HirProg hir);
}
