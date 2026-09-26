using Lang.Core;

namespace Lang.Types;

/// <summary>字段查找结论（TYPE-MEMBER-007；诊断由检查器按类别发射）。</summary>
public abstract record FieldLookup
{
    /// <summary>命中字段，携带字段类型。</summary>
    public sealed record Found(TypeId Ty) : FieldLookup;

    /// <summary>struct/error 无此字段（E3004，struct 名或 error 专名消息）。</summary>
    public sealed record Missing : FieldLookup;

    /// <summary>接收者是非 struct 的具体类型（E3004，通用消息）。</summary>
    public sealed record NotStruct : FieldLookup;

    /// <summary>接收者宽松（any/unknown/invalid）→ 保守 Unknown，不报错。</summary>
    public sealed record Lax(TypeId Ty) : FieldLookup;
}

/// <summary>调用兼容结论（CALL-ARG-002/E3003/E3001 的类型侧判定）。</summary>
public abstract record CallCompat
{
    /// <summary>个数与已注解参数全部兼容。</summary>
    public sealed record Compatible : CallCompat;

    /// <summary>个数不符。</summary>
    public sealed record Arity(int Want, int Got) : CallCompat;

    /// <summary>第 Index 个实参（0 基）与形参类型不兼容。</summary>
    public sealed record ArgMismatch(int Index) : CallCompat;

    /// <summary>callee 非 fn 类型且不宽松。</summary>
    public sealed record NotCallable : CallCompat;
}

/// <summary>
/// 类型运算：结构同一、可赋值性、诊断名、成员查找、调用兼容、泛型替换、迭代协议。
/// 宽松铁律：任一侧为 any/Unknown/Invalid/TypeParam 时 assignable 双向放行
/// （TYPE-ANY-002 的保守面）；容器与代数类型按元素协变、fn 参数逆协变；
/// struct/enum 名义同一（TYPE-STRUCT-003）；type_param 彼此同一（GEN-DECL-001 擦除）。
/// </summary>
public static class Ops
{
    /// <summary>是否数值类型（int/float）。</summary>
    public static bool IsNumeric(TypeTables t, TypeId id) => t.Data(id) is TypeData.Int or TypeData.Float;

    /// <summary>是否具体类型（非 any/unknown/invalid 参与判断的宽松位）。</summary>
    public static bool IsConcrete(TypeTables t, TypeId id) =>
        t.Data(id) is not (TypeData.Any or TypeData.Unknown or TypeData.Invalid);

    /// <summary>宽松类型：any/unknown/invalid/type_param —— 参与即放行。</summary>
    public static bool IsLax(TypeTables t, TypeId id) =>
        t.Data(id) is TypeData.Any or TypeData.Unknown or TypeData.Invalid or TypeData.TypeParam;

    /// <summary>Result 的 T 侧（非 result → Unknown）。</summary>
    public static TypeId ResultOk(TypeTables t, TypeId id) =>
        t.Data(id) is TypeData.Result r ? r.Ok : TypeTables.Unknown;

    /// <summary>Result 的 E 侧（非 result → Unknown）。</summary>
    public static TypeId ResultErr(TypeTables t, TypeId id) =>
        t.Data(id) is TypeData.Result r ? r.Err : TypeTables.Unknown;

    /// <summary>Option 的 T 侧（非 option → Unknown）。</summary>
    public static TypeId OptionElem(TypeTables t, TypeId id) =>
        t.Data(id) is TypeData.Option o ? o.Elem : TypeTables.Unknown;

    /// <summary>穿透 <c>&amp;T</c> 引用链。</summary>
    public static TypeId Deref(TypeTables t, TypeId id)
    {
        var cur = id;
        while (t.Data(cur) is TypeData.Ref rf) cur = rf.Elem;
        return cur;
    }

    /// <summary>结构同一（TYPE-FN-004 fn 结构兼容、TYPE-STRUCT-003 名义同一、擦除 type_param）。</summary>
    public static bool Equal(TypeTables t, TypeId a, TypeId b)
    {
        if (a == b) return true;
        return (t.Data(a), t.Data(b)) switch
        {
            (TypeData.Slice x, TypeData.Slice y) => Equal(t, x.Elem, y.Elem),
            (TypeData.Ref x, TypeData.Ref y) => Equal(t, x.Elem, y.Elem),
            (TypeData.Chan x, TypeData.Chan y) => Equal(t, x.Elem, y.Elem),
            (TypeData.Option x, TypeData.Option y) => Equal(t, x.Elem, y.Elem),
            (TypeData.Map x, TypeData.Map y) => Equal(t, x.Key, y.Key) && Equal(t, x.Val, y.Val),
            (TypeData.Fn x, TypeData.Fn y) => x.Params.Count == y.Params.Count
                && EqualParams(t, x.Params, y.Params)
                && Equal(t, x.Ret, y.Ret),
            (TypeData.Result x, TypeData.Result y) => Equal(t, x.Ok, y.Ok) && Equal(t, x.Err, y.Err),
            (TypeData.Struct x, TypeData.Struct y) => x.Def == y.Def,
            (TypeData.Enum x, TypeData.Enum y) => x.Def == y.Def,
            (TypeData.TypeParam, TypeData.TypeParam) => true, // 擦除同一
            _ => false,
        };
    }

    private static bool EqualParams(TypeTables t, IReadOnlyList<TypeId> xs, IReadOnlyList<TypeId> ys)
    {
        for (var i = 0; i < xs.Count; i++)
        {
            if (!Equal(t, xs[i], ys[i])) return false;
        }
        return true;
    }

    /// <summary>可赋值性（TYPE-ASSIGN-001）：同一类型 / 目标 any / 源 Unknown（及宽松位）。
    /// 无隐式数值转换：int 与 float 互不相容（E3002 由检查器按运算分别发射）。</summary>
    public static bool Assignable(TypeTables t, TypeId to, TypeId from)
    {
        if (IsLax(t, to) || IsLax(t, from)) return true;
        return (t.Data(to), t.Data(from)) switch
        {
            (TypeData.Slice x, TypeData.Slice y) => Assignable(t, x.Elem, y.Elem),
            (TypeData.Ref x, TypeData.Ref y) => Assignable(t, x.Elem, y.Elem),
            (TypeData.Chan x, TypeData.Chan y) => Assignable(t, x.Elem, y.Elem),
            (TypeData.Option x, TypeData.Option y) => Assignable(t, x.Elem, y.Elem),
            (TypeData.Map x, TypeData.Map y) => Assignable(t, x.Key, y.Key) && Assignable(t, x.Val, y.Val),
            (TypeData.Result x, TypeData.Result y) => Assignable(t, x.Ok, y.Ok) && Assignable(t, x.Err, y.Err),
            (TypeData.Fn x, TypeData.Fn y) => x.Params.Count == y.Params.Count
                && ContravariantParams(t, x.Params, y.Params)
                && Assignable(t, x.Ret, y.Ret), // 返回协变；参数逆协变（具体类型间退化为一致）
            _ => Equal(t, to, from),
        };
    }

    private static bool ContravariantParams(TypeTables t, IReadOnlyList<TypeId> xs, IReadOnlyList<TypeId> ys)
    {
        for (var i = 0; i < xs.Count; i++)
        {
            if (!Assignable(t, ys[i], xs[i])) return false;
        }
        return true;
    }

    /// <summary>诊断与 dump 用类型名（贴近源码写法，与证据 typeStr 逐字一致）。</summary>
    public static string TypeStr(TypeTables t, TypeId id) => t.Data(id) switch
    {
        TypeData.Invalid => "invalid",
        TypeData.Int => "int",
        TypeData.Float => "float",
        TypeData.Bool => "bool",
        TypeData.Str => "string",
        TypeData.Error => "error",
        TypeData.Unit => "()",
        TypeData.Range => "range",
        TypeData.Any => "any",
        TypeData.Unknown => "unknown",
        TypeData.Never => "never",
        TypeData.Slice e => $"[{TypeStr(t, e.Elem)}]",
        TypeData.Map m => $"map[{TypeStr(t, m.Key)}]{TypeStr(t, m.Val)}",
        TypeData.Ref r => $"&{TypeStr(t, r.Elem)}",
        TypeData.Chan c => $"chan[{TypeStr(t, c.Elem)}]",
        TypeData.Fn f => FormatFn(t, f),
        TypeData.Struct s => t.GetStruct(s.Def).Name,
        TypeData.Enum e => t.GetEnum(e.Def).Name,
        TypeData.Option o => $"Option[{TypeStr(t, o.Elem)}]",
        TypeData.Result r => $"Result[{TypeStr(t, r.Ok)}, {TypeStr(t, r.Err)}]",
        TypeData.TypeParam tp => tp.Name,
        _ => throw new InvalidOperationException("未登记的 TypeData 变体"),
    };

    private static string FormatFn(TypeTables t, TypeData.Fn f)
    {
        var ps = string.Join(", ", f.Params.Select(p => TypeStr(t, p)));
        var s = $"fn({ps})";
        if (t.Data(f.Ret) is not TypeData.Unit)
        {
            s += $": {TypeStr(t, f.Ret)}";
        }
        return s;
    }

    /// <summary>成员查找：struct 字段与 error.msg（<c>&amp;T</c> 自动解引用，与 interp PtrVal 语义一致）。</summary>
    public static FieldLookup LookupField(TypeTables t, TypeId obj, string name)
    {
        var ty = Deref(t, obj);
        switch (t.Data(ty))
        {
            case TypeData.Struct def:
                foreach (var f in t.GetStruct(def.Def).Fields)
                {
                    if (f.Name == name) return new FieldLookup.Found(f.Ty);
                }
                return new FieldLookup.Missing();
            case TypeData.Error:
                return name == TypeTables.ErrorMsgField
                    ? new FieldLookup.Found(TypeTables.Str)
                    : new FieldLookup.Missing();
            default:
                return !IsConcrete(t, ty)
                    ? new FieldLookup.Lax(TypeTables.Unknown)
                    : new FieldLookup.NotStruct();
        }
    }

    /// <summary>按函数类型检查调用（结构兼容；具体实参 span 由检查器携带）。</summary>
    public static CallCompat CheckCall(TypeTables t, TypeId callee, IReadOnlyList<TypeId> args)
    {
        if (t.Data(callee) is TypeData.Fn f)
        {
            if (f.Params.Count != args.Count)
            {
                return new CallCompat.Arity(f.Params.Count, args.Count);
            }
            for (var i = 0; i < f.Params.Count; i++)
            {
                if (!Assignable(t, f.Params[i], args[i])) return new CallCompat.ArgMismatch(i);
            }
            return new CallCompat.Compatible();
        }
        // 宽松 callee 保守放行
        return !IsConcrete(t, callee) ? new CallCompat.Compatible() : new CallCompat.NotCallable();
    }

    /// <summary>泛型替换（GEN-DECL-001 擦除语义的类型侧）：把 id 中名为 name 的
    /// TypeParam 替换为 to。名义 struct/enum 原样保留（成员类型不递归替换）。</summary>
    public static TypeId Substitute(TypeTables t, TypeId id, string name, TypeId to)
    {
        switch (t.Data(id))
        {
            case TypeData.TypeParam tp when tp.Name == name:
                return to;
            case TypeData.Slice s:
                return t.Intern(new TypeData.Slice(Substitute(t, s.Elem, name, to)));
            case TypeData.Map m:
                return t.Intern(new TypeData.Map(
                    Substitute(t, m.Key, name, to),
                    Substitute(t, m.Val, name, to)));
            case TypeData.Ref r:
                return t.Intern(new TypeData.Ref(Substitute(t, r.Elem, name, to)));
            case TypeData.Chan c:
                return t.Intern(new TypeData.Chan(Substitute(t, c.Elem, name, to)));
            case TypeData.Option o:
                return t.Intern(new TypeData.Option(Substitute(t, o.Elem, name, to)));
            case TypeData.Result r:
                return t.Intern(new TypeData.Result(
                    Substitute(t, r.Ok, name, to),
                    Substitute(t, r.Err, name, to)));
            case TypeData.Fn f:
                {
                    var ps = f.Params.Select(p => Substitute(t, p, name, to)).ToList();
                    return t.Intern(new TypeData.Fn(ps, Substitute(t, f.Ret, name, to)));
                }
            default:
                return id;
        }
    }

    /// <summary>迭代协议（CTRL-FORIN-003）：null = 不可迭代（E3010）；否则为循环变量类型。</summary>
    public static TypeId? IterableElem(TypeTables t, TypeId id)
    {
        var d = Deref(t, id);
        switch (t.Data(d))
        {
            case TypeData.Slice s: return s.Elem;
            case TypeData.Map m: return m.Key; // 映射迭代产出键
            case TypeData.Str: return TypeTables.Str; // 单字符字符串
            case TypeData.Range: return TypeTables.Int;
            default:
                return !IsConcrete(t, d) ? TypeTables.Unknown : null;
        }
    }
}
