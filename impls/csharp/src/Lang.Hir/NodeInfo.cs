using Lang.Core;

namespace Lang.Hir;

/// <summary>
/// 节点元数据：所有重要 HIR 节点内嵌一份（语义事实层，第三层）。
/// HIR 表达程序的语义事实；后端与 IR 消费结论，<b>禁止重新推断</b>，不再回读 AST。
/// </summary>
/// <param name="Id">编译内稳定节点编号（先序递增，由 lower 分配）。</param>
/// <param name="Span">列级源码区间（继承自 AST 盖章；IR 不携带 span，契约层从这里取）。</param>
/// <param name="Sym">resolver 结论：绑定的符号（无符号节点为 null）。</param>
/// <param name="TypeId">checker 结论：节点语义类型（void/未标注为 null）。</param>
public sealed record NodeInfo(NodeId Id, Span Span, SymbolId? Sym, TypeId? TypeId)
{
    public static NodeInfo New(NodeId id, Span span) => new(id, span, null, null);

    public NodeInfo WithSym(SymbolId sym) => this with { Sym = sym };

    public NodeInfo WithType(TypeId typeId) => this with { TypeId = typeId };
}
