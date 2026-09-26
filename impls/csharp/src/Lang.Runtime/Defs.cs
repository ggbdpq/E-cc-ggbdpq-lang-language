namespace Lang.Runtime;

/// <summary>struct 定义身份。预载 error 结构固定为 0 号。</summary>
public readonly record struct StructDefId(uint Value);

/// <summary>enum 定义身份。预载 Option 固定为 0 号、Result 固定为 1 号；身份即语义（相等要求 def 一致），不做名字比较。</summary>
public readonly record struct EnumDefId(uint Value);

/// <summary>struct 定义：名字 + 字段名表（输出 Name{field: value} 用）。</summary>
public sealed record StructDef(string Name, IReadOnlyList<string> Fields);

/// <summary>enum 定义：名字 + case 名表（输出 Tag(载荷) 用）。</summary>
public sealed record EnumDef(string Name, IReadOnlyList<string> Cases);

/// <summary>
/// struct/enum 定义表：运行时只消费「输出与相等判定」所需的最小定义面。
/// 方法分派在静态期完成（CALL-RESOLVE-001），故运行时定义表不含方法；
/// 定义登记由前级（语义/IR 层）在装载时完成，id 顺序稳定。
/// </summary>
public sealed class DefTables
{
    private readonly List<StructDef> _structs = new();
    private readonly List<EnumDef> _enums = new();
    private readonly StructDefId _errorDef;
    private readonly EnumDefId _optionDef;
    private readonly EnumDefId _resultDef;

    private DefTables()
    {
        _errorDef = RegisterStruct("error", new[] { "msg" });
        _optionDef = RegisterEnum("Option", new[] { "Some", "None" });
        _resultDef = RegisterEnum("Result", new[] { "Ok", "Err" });
    }

    /// <summary>空表 + 预载定义。id 分配顺序为契约的一部分：error → StructDefId(0)、Option → EnumDefId(0)、Result → EnumDefId(1)。</summary>
    public static DefTables WithPrelude() => new();

    /// <summary>登记 struct 定义（重复定义的静态拒绝在语义层，运行时只登记）。</summary>
    public StructDefId RegisterStruct(string name, IReadOnlyList<string> fields)
    {
        var id = new StructDefId((uint)_structs.Count);
        _structs.Add(new StructDef(name, fields.ToArray()));
        return id;
    }

    /// <summary>登记 enum 定义。</summary>
    public EnumDefId RegisterEnum(string name, IReadOnlyList<string> cases)
    {
        var id = new EnumDefId((uint)_enums.Count);
        _enums.Add(new EnumDef(name, cases.ToArray()));
        return id;
    }

    /// <summary>读取 struct 定义。越界 = 内部不变量破坏（id 只能由 RegisterStruct 产生）。</summary>
    public RtResult<StructDef> StructDef(StructDefId id) =>
        id.Value < (uint)_structs.Count
            ? RtResult<StructDef>.Ok(_structs[(int)id.Value])
            : RtResult<StructDef>.Fail(RtError.Internal($"StructDefId({id.Value}) 越界"));

    /// <summary>读取 enum 定义。越界 = 内部不变量破坏。</summary>
    public RtResult<EnumDef> EnumDef(EnumDefId id) =>
        id.Value < (uint)_enums.Count
            ? RtResult<EnumDef>.Ok(_enums[(int)id.Value])
            : RtResult<EnumDef>.Fail(RtError.Internal($"EnumDefId({id.Value}) 越界"));

    /// <summary>预载 error struct（err(msg) 构造用）。</summary>
    public StructDefId ErrorDef() => _errorDef;

    /// <summary>预载 Option enum。</summary>
    public EnumDefId OptionDef() => _optionDef;

    /// <summary>预载 Result enum。</summary>
    public EnumDefId ResultDef() => _resultDef;
}
