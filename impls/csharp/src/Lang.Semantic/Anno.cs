using Lang.Core;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Semantic;

/// <summary>注解解析上下文（Resolver / Checker / 公共静默入口各自实现）。</summary>
internal interface ITypeCtx
{
    TypeTables Tables { get; }

    SymbolArena Arena { get; }

    /// <summary>具名类型查名（预载 error → 顶层 → arena 首个同名）。</summary>
    SymbolId? LookupNamed(string name);

    /// <summary>无节点锚诊断（line 0）。</summary>
    void Emit(DiagnosticCode code, string msg);
}

/// <summary>
/// 源码注解 → 语义类型（TypeEx 5 变体 → TypeId）。
/// 双通道，与证据保持一致：
/// - <see cref="ResolveAnnoQuiet"/>：声明收集期的静默解析（struct 字段、enum 载荷、
///   HIR 侧复用）——不发射诊断，坏 Option/Result arity 保守回退；
/// - <see cref="ResolveAnnoEmit"/>：检查器路径（参数/返回/let 注解）——Option arity≠1 →
///   E3001、Result arity≠2 → E3001、Result 第二参数非 error → E3009（均 line 0，
///   与 case.yaml 期望的锚定一致）；E3009 后仍返回 Result 形，避免下游把
///   Result 函数误判为普通函数（E3006 连锁，证据同款处理）。
/// 泛型：tps 中的名字解析为擦除占位（TypeData.TypeParam）。
/// </summary>
internal static class Anno
{
    private static bool TpsHas(IReadOnlyList<string> tps, string name)
    {
        foreach (var t in tps)
        {
            if (t == name) return true;
        }
        return false;
    }

    private static TypeId NamedToType(ITypeCtx ctx, SymbolId sid)
    {
        var nominal = ctx.Arena.Get(sid).Nominal;
        return nominal switch
        {
            NominalRef.Struct s => ctx.Tables.Intern(new TypeData.Struct(s.Def)),
            NominalRef.Enum e => ctx.Tables.Intern(new TypeData.Enum(e.Def)),
            _ => TypeTables.Unknown,
        };
    }

    private static TypeId FirstArgOr(TypeEx.Named n, ITypeCtx ctx, IReadOnlyList<string> tps, TypeId fallback) =>
        n.Args.Count > 0 ? ResolveAnnoQuiet(ctx, n.Args[0], tps) : fallback;

    // —— 静默通道（声明收集期）——

    public static TypeId ResolveAnnoQuiet(ITypeCtx ctx, TypeEx t, IReadOnlyList<string> tps)
    {
        switch (t)
        {
            case TypeEx.Named n:
                switch (n.Name)
                {
                    case "int": return TypeTables.Int;
                    case "float": return TypeTables.Float;
                    case "string": return TypeTables.Str;
                    case "bool": return TypeTables.Bool;
                    case "error": return TypeTables.Error;
                    case "any": return TypeTables.Any;
                    case "chan":
                        return ctx.Tables.Intern(new TypeData.Chan(FirstArgOr(n, ctx, tps, TypeTables.Unknown)));
                    case "Option":
                        return ctx.Tables.Intern(new TypeData.Option(FirstArgOr(n, ctx, tps, TypeTables.Unknown)));
                    case "Result":
                        var ok = FirstArgOr(n, ctx, tps, TypeTables.Unknown);
                        return ctx.Tables.Intern(new TypeData.Result(ok, TypeTables.Error));
                    default:
                        if (TpsHas(tps, n.Name))
                        {
                            return ctx.Tables.Intern(new TypeData.TypeParam(n.Name));
                        }
                        return ctx.LookupNamed(n.Name) is { } sid ? NamedToType(ctx, sid) : TypeTables.Unknown;
                }
            case TypeEx.Arr a:
                return ctx.Tables.Intern(new TypeData.Slice(ResolveAnnoQuiet(ctx, a.Elem, tps)));
            case TypeEx.Map m:
                return ctx.Tables.Intern(new TypeData.Map(
                    ResolveAnnoQuiet(ctx, m.Key, tps),
                    ResolveAnnoQuiet(ctx, m.Val, tps)));
            case TypeEx.Ptr p:
                return ctx.Tables.Intern(new TypeData.Ref(ResolveAnnoQuiet(ctx, p.Elem, tps)));
            case TypeEx.Fn f:
                {
                    var ps = f.Params.Select(p => ResolveAnnoQuiet(ctx, p, tps)).ToList();
                    var r = f.Ret is not null ? ResolveAnnoQuiet(ctx, f.Ret, tps) : TypeTables.Unknown;
                    return ctx.Tables.Intern(new TypeData.Fn(ps, r));
                }
            default:
                throw new InvalidOperationException("未登记的 TypeEx 变体");
        }
    }

    // —— 检查器通道（Option/Result arity 与第二参数错误在此发射）——

    public static TypeId ResolveAnnoEmit(ITypeCtx ctx, TypeEx t, IReadOnlyList<string> tps)
    {
        if (t is TypeEx.Named n)
        {
            switch (n.Name)
            {
                case "Option" when n.Args.Count == 1:
                    return ctx.Tables.Intern(new TypeData.Option(ResolveAnnoEmit(ctx, n.Args[0], tps)));
                case "Option":
                    ctx.Emit(DiagnosticCode.E3001, "Option 需要一个类型参数");
                    return TypeTables.Invalid;
                case "Result" when n.Args.Count == 2:
                    {
                        var ok = ResolveAnnoEmit(ctx, n.Args[0], tps);
                        var errOk = n.Args[1] is TypeEx.Named e && e.Name == "error";
                        if (!errOk)
                        {
                            ctx.Emit(DiagnosticCode.E3009, "Result 第二类型参数须为 error");
                        }
                        // E3009 后仍返回 Result 形，防止 E3006 连锁（证据同款）
                        return ctx.Tables.Intern(new TypeData.Result(ok, TypeTables.Error));
                    }
                case "Result":
                    ctx.Emit(DiagnosticCode.E3001, "Result 需要两个类型参数");
                    return TypeTables.Invalid;
            }
        }
        switch (t)
        {
            case TypeEx.Named named:
                switch (named.Name)
                {
                    case "int": return TypeTables.Int;
                    case "float": return TypeTables.Float;
                    case "string": return TypeTables.Str;
                    case "bool": return TypeTables.Bool;
                    case "error": return TypeTables.Error;
                    case "any": return TypeTables.Any;
                    case "chan":
                        return ctx.Tables.Intern(new TypeData.Chan(
                            named.Args.Count > 0 ? ResolveAnnoEmit(ctx, named.Args[0], tps) : TypeTables.Unknown));
                    default:
                        if (TpsHas(tps, named.Name))
                        {
                            return ctx.Tables.Intern(new TypeData.TypeParam(named.Name));
                        }
                        return ctx.LookupNamed(named.Name) is { } sid ? NamedToType(ctx, sid) : TypeTables.Unknown;
                }
            case TypeEx.Arr a:
                return ctx.Tables.Intern(new TypeData.Slice(ResolveAnnoEmit(ctx, a.Elem, tps)));
            case TypeEx.Map m:
                return ctx.Tables.Intern(new TypeData.Map(
                    ResolveAnnoEmit(ctx, m.Key, tps),
                    ResolveAnnoEmit(ctx, m.Val, tps)));
            case TypeEx.Ptr p:
                return ctx.Tables.Intern(new TypeData.Ref(ResolveAnnoEmit(ctx, p.Elem, tps)));
            case TypeEx.Fn f:
                {
                    var ps = f.Params.Select(p => ResolveAnnoEmit(ctx, p, tps)).ToList();
                    var r = f.Ret is not null ? ResolveAnnoEmit(ctx, f.Ret, tps) : TypeTables.Unknown;
                    return ctx.Tables.Intern(new TypeData.Fn(ps, r));
                }
            default:
                throw new InvalidOperationException("未登记的 TypeEx 变体");
        }
    }
}
