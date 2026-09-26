using Lang.Core;

namespace Lang.Types;

/// <summary>
/// 类型 arena：intern 结构类型 + 名义 def 表（specs/type-system.md）。
/// 预载单例占据 <see cref="TypeId"/> 0..=10（顺序见构造），结构类型 intern 去重；
/// struct/enum def 只增不减。预载面与 A4 冻结对齐（RESULT-PRELUDE-001）：
/// error = StructDefId(0)、Option = EnumDefId(0)（Some=0/None=1）、
/// Result = EnumDefId(1)（Ok=0/Err=1）。
/// </summary>
public sealed class TypeTables
{
    private readonly List<TypeData> _data = [];
    private readonly List<StructDef> _structs = [];
    private readonly List<EnumDef> _enums = [];

    // —— 预载单例（TypeId 即构造的注册序，0..=10 固定）——

    public static readonly TypeId Invalid = new(0);
    public static readonly TypeId Int = new(1);
    public static readonly TypeId Float = new(2);
    public static readonly TypeId Bool = new(3);
    public static readonly TypeId Str = new(4);
    public static readonly TypeId Error = new(5);
    public static readonly TypeId Unit = new(6);
    public static readonly TypeId Range = new(7);
    public static readonly TypeId Any = new(8);
    public static readonly TypeId Unknown = new(9);
    public static readonly TypeId Never = new(10);

    /// <summary>预载 <c>error</c> struct 的 def id（A4 冻结：error=StructDefId(0)）。</summary>
    public static readonly StructDefId ErrorDef = new(0);

    /// <summary>预载 <c>Option</c> enum 的 def id（A4 冻结：Option=EnumDefId(0)）。</summary>
    public static readonly EnumDefId OptionDef = new(0);

    /// <summary>预载 <c>Result</c> enum 的 def id（A4 冻结：Result=EnumDefId(1)）。</summary>
    public static readonly EnumDefId ResultDef = new(1);

    /// <summary>预载 <c>error.msg</c> 字段名。</summary>
    public const string ErrorMsgField = "msg";

    public TypeTables()
    {
        // 原始单例（顺序即 id，0..=10）
        _data.Add(new TypeData.Invalid());
        _data.Add(new TypeData.Int());
        _data.Add(new TypeData.Float());
        _data.Add(new TypeData.Bool());
        _data.Add(new TypeData.Str());
        _data.Add(new TypeData.Error());
        _data.Add(new TypeData.Unit());
        _data.Add(new TypeData.Range());
        _data.Add(new TypeData.Any());
        _data.Add(new TypeData.Unknown());
        _data.Add(new TypeData.Never());

        // 预载面（RESULT-PRELUDE-001）：id 与 A4 DefTables 冻结一致
        _structs.Add(new StructDef
        {
            Name = "error",
            Fields = [new FieldDef { Name = ErrorMsgField, Ty = Str }],
        });
        _enums.Add(new EnumDef
        {
            Name = "Option",
            Cases =
            [
                // 载荷类型随实例化变化：登记 Unknown 占位（与 A4 prelude 口径一致）
                new EnumCaseDef { Name = "Some", Payloads = [Unknown] },
                new EnumCaseDef { Name = "None", Payloads = [] },
            ],
        });
        _enums.Add(new EnumDef
        {
            Name = "Result",
            Cases =
            [
                new EnumCaseDef { Name = "Ok", Payloads = [Unknown] },
                new EnumCaseDef { Name = "Err", Payloads = [Unknown] },
            ],
        });
    }

    /// <summary>类型结构（arena 只增不减，id 恒有效）。</summary>
    public TypeData Data(TypeId id) => _data[(int)id.Value];

    /// <summary>intern 一个结构类型（结构相同 → 同一 TypeId；单例直接命中常量位）。</summary>
    public TypeId Intern(TypeData d)
    {
        TypeId? simple = d switch
        {
            TypeData.Invalid => Invalid,
            TypeData.Int => Int,
            TypeData.Float => Float,
            TypeData.Bool => Bool,
            TypeData.Str => Str,
            TypeData.Error => Error,
            TypeData.Unit => Unit,
            TypeData.Range => Range,
            TypeData.Any => Any,
            TypeData.Unknown => Unknown,
            TypeData.Never => Never,
            _ => null,
        };
        if (simple is not null) return simple.Value;
        var pos = _data.FindIndex(x => x == d);
        if (pos >= 0) return new TypeId((uint)pos);
        _data.Add(d);
        return new TypeId((uint)(_data.Count - 1));
    }

    // —— 名义 def 表（只增不减；注册返回稳定 def id）——

    public StructDefId RegisterStruct(StructDef def)
    {
        var id = new StructDefId((uint)_structs.Count);
        _structs.Add(def);
        return id;
    }

    public EnumDefId RegisterEnum(EnumDef def)
    {
        var id = new EnumDefId((uint)_enums.Count);
        _enums.Add(def);
        return id;
    }

    /// <summary>struct 定义（def 是可变类：字段表注册时先占位、注解解析后回填）。</summary>
    public StructDef GetStruct(StructDefId id) => _structs[(int)id.Value];

    /// <summary>enum 定义（case 载荷表同上可回填）。</summary>
    public EnumDef GetEnum(EnumDefId id) => _enums[(int)id.Value];
}
