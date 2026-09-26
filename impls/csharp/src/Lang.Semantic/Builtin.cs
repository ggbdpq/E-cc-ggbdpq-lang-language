using Lang.Core;
using Lang.Types;

namespace Lang.Semantic;

/// <summary>
/// 内建面：函数名单（resolver 预载）+ 返回类型特化（checker 消费）。
/// 名单与 A4 冻结的 20 个内建一致（docs/lang-rs/status/A4-runtime.md）；
/// TS/Go 证据里的 http 服务值不在 A4 冻结面，本实现不预载。
/// </summary>
public static class Builtin
{
    /// <summary>内建函数名单（与 interp/gen 三方证据一致；顺序即注册序）。</summary>
    public static readonly string[] BuiltinFns =
    [
        "println",
        "print",
        "len",
        "str",
        "int",
        "float",
        "keys",
        "values",
        "has",
        "push",
        "assert",
        "err",
        "bytes",
        "char",
        "jsonParse",
        "jsonStringify",
        "isNone",
        "makeChan",
        "send",
        "recv",
    ];

    /// <summary>预载 struct 名（RESULT-PRELUDE-001 的 error；HttpResp 属宿主服务面不预载）。</summary>
    public static readonly string[] PreludeStructs = ["error"];

    /// <summary>预载构造器（与 A4 prelude case 序号一致：名字/所属/case 序号/载荷数）。</summary>
    public static readonly (string Name, string Owner, uint CaseIndex, int Arity)[] PreludeCtors =
    [
        ("Some", "Option", 0, 1),
        ("None", "Option", 1, 0),
        ("Ok", "Result", 0, 1),
        ("Err", "Result", 1, 1),
    ];

    /// <summary>内建调用返回类型（参数不检查该位；返回类型依实参特化）。</summary>
    public static TypeId BuiltinReturn(TypeTables t, string name, IReadOnlyList<TypeId> args) => name switch
    {
        "println" or "print" or "assert" or "send" => TypeTables.Unit,
        "len" or "int" => TypeTables.Int,
        "float" => TypeTables.Float,
        "str" or "char" or "jsonStringify" => TypeTables.Str,
        "keys" or "values" => t.Intern(new TypeData.Slice(TypeTables.Unknown)),
        "has" or "isNone" => TypeTables.Bool,
        "push" => args.Count > 0 ? args[0] : TypeTables.Unknown,
        "err" => TypeTables.Error,
        "bytes" => t.Intern(new TypeData.Slice(TypeTables.Int)),
        "jsonParse" => TypeTables.Any,
        "makeChan" => t.Intern(new TypeData.Chan(TypeTables.Unknown)),
        "recv" => t.Intern(new TypeData.Result(
            t.Data(args[0]) is TypeData.Chan c ? c.Elem : TypeTables.Unknown,
            TypeTables.Error)),
        _ => TypeTables.Unknown,
    };

    /// <summary>是否内建函数名。</summary>
    public static bool IsBuiltinFn(string name) => Array.IndexOf(BuiltinFns, name) >= 0;
}
