using Lang.Hir;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Ir;

/// <summary>
/// 便利入口：语义分析后的语句序列 → <see cref="IrProgram"/>（「源码 → IR」全链）。
/// 前置：analysis 由<b>同一棵</b> stmts 分析而来且无诊断——用户诊断已由 Lang.Semantic
/// 发射，本入口只可能失败于内部不变量破坏（错误消息分 Build/Lower 两段，均非用户诊断面）。
/// </summary>
public static class Compiler
{
    /// <summary>语句序列 + 语义产物 → IrProgram（AST→HIR→IR，产物已过 validate）。</summary>
    public static LowerResult<IrProgram> Compile(IReadOnlyList<Stmt> stmts, Analysis analysis)
    {
        var hir = new HirBuilder(analysis).Lower(stmts);
        if (!hir.IsOk)
        {
            return LowerResult<IrProgram>.Fail($"HIR 构建: {hir.Error}");
        }
        var lowered = new IrLowerer(analysis.Types, analysis.Symbols).Lower(hir.Value!);
        return lowered.IsOk
            ? lowered
            : LowerResult<IrProgram>.Fail($"IR lowering: {lowered.Error}");
    }
}
