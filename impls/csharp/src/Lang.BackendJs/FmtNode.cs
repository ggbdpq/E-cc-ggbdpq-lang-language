namespace Lang.BackendJs;

/// <summary>
/// 抽象打印值树：fmt 层自有形状（与 Lang.BackendGo 的 <c>FmtNode</c> 同构；
/// 两后端各自持有、不共享程序集——沿 lang-rs 同构不共享的决策）。
/// 映射打印保持<b>插入序</b>（列表承载）。私有构造封闭层级。
/// </summary>
public abstract record FmtNode
{
    private FmtNode()
    {
    }

    /// <summary>unit 值，打印 <c>()</c>。</summary>
    public sealed record Unit : FmtNode
    {
        public static readonly Unit Instance = new();
        private Unit()
        {
        }
    }

    /// <summary>int64（全域十进制打印，含 long.MinValue）。</summary>
    public sealed record Int(long Value) : FmtNode;

    /// <summary>float64（Go strconv.FormatFloat('g',-1,64) 等价打印，非 C# 默认形）。</summary>
    public sealed record Float(double Value) : FmtNode;

    public sealed record Bool(bool Value) : FmtNode;

    /// <summary>字符串：顶层原样、嵌套带引号（→JS 用 JSON.stringify 语义）。</summary>
    public sealed record Str(string Value) : FmtNode;

    /// <summary>用户/匿名函数；<c>null</c> = 匿名。</summary>
    public sealed record Fn(string? Name) : FmtNode;

    /// <summary>半开区间 lo..hi。</summary>
    public sealed record Range(long Lo, long Hi) : FmtNode;

    /// <summary>引用（&amp;v），打印为 <c>&amp;嵌套形</c>。</summary>
    public sealed record Ptr(FmtNode Inner) : FmtNode;

    /// <summary>数组，打印 <c>[a, b]</c>（元素嵌套形）。</summary>
    public sealed record Arr(IReadOnlyList<FmtNode> Items) : FmtNode;

    /// <summary>映射：键值对按插入序；键只能是 int/string/bool。</summary>
    public sealed record Map(IReadOnlyList<(FmtNode Key, FmtNode Value)> Entries) : FmtNode;

    /// <summary>struct：字段按声明序，打印 <c>Name{f: v, ...}</c>。</summary>
    public sealed record Struct(string Name, IReadOnlyList<(string Field, FmtNode Value)> Fields) : FmtNode;

    /// <summary>用户 enum：case 名 + 载荷，打印 <c>Tag(p1, p2)</c> / <c>Tag</c>。</summary>
    public sealed record EnumValue(string Tag, IReadOnlyList<FmtNode> Payloads) : FmtNode;

    /// <summary>Option[T]：Inner 为 null 表示 None。</summary>
    public sealed record Option(FmtNode? Inner) : FmtNode;

    /// <summary>Result[T, error] 的 Ok(v)。</summary>
    public sealed record Ok(FmtNode Value) : FmtNode;

    /// <summary>Result[T, error] 的 Err(e)。</summary>
    public sealed record Err(FmtNode Value) : FmtNode;

    /// <summary>struct error { msg: string }，打印 <c>error{msg: "&lt;JSON 引号形&gt;"}</c>。</summary>
    public sealed record Error(string Msg) : FmtNode;
}
