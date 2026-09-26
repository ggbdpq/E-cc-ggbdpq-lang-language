namespace Lang.Core;

/// <summary>身份 newtype 家族（计划 §R1）：对外身份禁止裸 int/string。</summary>
public readonly record struct SourceId(uint Value);
public readonly record struct NodeId(uint Value);
public readonly record struct SymbolId(uint Value);
public readonly record struct TypeId(uint Value);
public readonly record struct FunctionId(uint Value);
public readonly record struct LocalId(uint Value);
