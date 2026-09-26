using Lang.Core;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Semantic;

/// <summary>match 的语义分类（checker 判定，HIR/IR/后端直接消费结论；MATCH-KIND-001）。</summary>
public enum MatchKind
{
    /// <summary>字面量 match（int/string/bool）。</summary>
    Literal,
    /// <summary>enum match（编译期穷尽检查）。</summary>
    Enum,
    /// <summary>Result match（两臂齐全检查）。</summary>
    Result,
    /// <summary>Option match（两臂齐全检查）。</summary>
    Option,
}

/// <summary>检查产物（对外经 Analysis 查询视图暴露）。</summary>
internal sealed class CheckOutput
{
    public required NodeMap<TypeId> ExprTy { get; init; }
    public required NodeMap<TypeId> PatTy { get; init; }
    public required Dictionary<SymbolId, TypeId> SymTy { get; init; }
    public required Dictionary<SymbolId, TypeId> FnTy { get; init; }
    public required NodeMap<MatchKind> MatchKinds { get; init; }
    public required TypeTables Tables { get; init; }
    public required List<Diagnostic> Diags { get; init; }
}

/// <summary>
/// 类型检查器：对 AST 做语义类型检查，产出每表达式/绑定的类型（HIR/IR/后端共用）。
/// 遍历结构与 resolver 同构（顶层 let → fn 体 → 方法体 → 游离语句）；名字解析结果
/// 复用 <see cref="ResolveOutput"/>，名义 def 表由 resolver 移交本检查器续用（单一实例）。
/// 本检查器收编转译器的全部前置约束：参数注解（E3007）、`?` 位置与约束
/// （E3005/E3008）、Result 函数 return 形态（E3006/E3001）、match 分类与穷尽性
/// （E4001/E4002/E4003）。
/// 与证据的两处刻意分歧（均按 spec/ADR 修正，登记 status）：
/// 1. 赋值顶层 `?` 走 InferTry（RESULT-TRY-003 + ADR-0005；证据实现误报 E3005）；
/// 2. 块内 let/循环变量/臂绑定写入当前块作用域而非全局层
///    （NAME-SCOPE-001/MATCH-BIND-003 的静态作用域语义；证据写全局层属历史行为）。
/// </summary>
internal sealed class Checker : ITypeCtx
{
    private readonly ResolveOutput _res;
    private readonly NodeMap<TypeId> _exprTy = new();
    private readonly NodeMap<TypeId> _patTy = new();
    private readonly Dictionary<SymbolId, TypeId> _symTy = [];
    private readonly Dictionary<SymbolId, TypeId> _fnTy = [];
    private readonly NodeMap<MatchKind> _matchKind = new();
    private readonly List<Diagnostic> _diags = [];

    /// <summary>绑定类型作用域栈（名字 → 类型）。</summary>
    private List<Dictionary<string, TypeId>> _scopes = [];

    /// <summary>当前函数返回注解（null = 无注解）。</summary>
    private TypeId? _fnRet;

    /// <summary>当前函数的泛型参数名集（注解解析用）。</summary>
    private IReadOnlyList<string> _tps = [];

    private bool _inFn;

    public Checker(ResolveOutput res)
    {
        _res = res;
        // 接管 resolver 的 def 表（名义身份的单一实例随 Checker 流转到产物）
        Tables = res.Tables;
        res.Tables = new TypeTables();
    }

    public TypeTables Tables { get; private set; }

    SymbolArena ITypeCtx.Arena => _res.Arena;

    void ITypeCtx.Emit(DiagnosticCode code, string msg) => _diags.Add(Diagnostic.New(code, msg));

    SymbolId? ITypeCtx.LookupNamed(string name) => LookupTypeSym(name);

    private void Err(Span span, DiagnosticCode code, string msg)
    {
        _diags.Add(Diagnostic.New(code, msg, Diagnostic.AtLine(span.Source, span.Start.Line)));
    }

    private SymbolId? LookupTypeSym(string name)
    {
        if (_res.BuiltinMap.TryGetValue(name, out var bid) && _res.Arena.Get(bid).Kind == SymbolKind.Struct)
        {
            return bid;
        }
        if (_res.TopTypes.TryGetValue(name, out var tid)) return tid;
        return _res.Arena.FirstTypeNamed(name);
    }

    private void PushScope() => _scopes.Add([]);

    private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    private void DefineType(string name, TypeId ty) => _scopes[^1][name] = ty;

    private TypeId? LookupVar(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var t)) return t;
        }
        return null;
    }

    // ———— 主驱动（与 resolver 同构的遍历）————

    public CheckOutput Check(IReadOnlyList<Stmt> prog)
    {
        Walk(prog);
        return new CheckOutput
        {
            ExprTy = _exprTy,
            PatTy = _patTy,
            SymTy = _symTy,
            FnTy = _fnTy,
            MatchKinds = _matchKind,
            Tables = Tables,
            Diags = _diags,
        };
    }

    private void Walk(IReadOnlyList<Stmt> prog)
    {
        PushScope();
        // 1. 顶层绑定依序（前向引用已被 resolver 拦）
        foreach (var s in prog)
        {
            if (s is Stmt.Let)
            {
                LetStmt(s);
            }
        }
        // 2. fn 体
        foreach (var s in prog)
        {
            if (s is Stmt.FnDecl { Recv: null } d)
            {
                SymbolId? sym = _res.DeclSyms.Get(s);
                FnBody(d, sym, null);
            }
        }
        // 3. 方法体
        foreach (var s in prog)
        {
            if (s is Stmt.FnDecl { Recv: not null } d)
            {
                var sym = MethodSym(d);
                var recvTy = MethodStructType(d);
                FnBody(d, sym, recvTy);
            }
        }
        // 4. 顶层游离语句（脚本模式；interp 可执行，转译不支持——只做类型走查）
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.Let or Stmt.FnDecl or Stmt.StructDecl or Stmt.EnumDecl or Stmt.Extern or Stmt.Import:
                    break;
                default:
                    WalkStmt(s);
                    break;
            }
        }
        PopScope();
    }

    private SymbolId? MethodSym(Stmt.FnDecl d)
    {
        if (d.Recv?.Typ is not TypeEx.Named rt) return null;
        var st = LookupTypeSym(rt.Name);
        return st is null ? null : _res.Arena.Get(st.Value).Methods.TryGetValue(d.Name, out var m) ? m : null;
    }

    private TypeId MethodStructType(Stmt.FnDecl d)
    {
        if (d.Recv?.Typ is not TypeEx.Named rt) return TypeTables.Unknown;
        var st = LookupTypeSym(rt.Name);
        if (st is null) return TypeTables.Unknown;
        return _res.Arena.Get(st.Value).Nominal is NominalRef.Struct def
            ? Tables.Intern(new TypeData.Struct(def.Def))
            : TypeTables.Unknown;
    }

    /// <summary>函数体检查：建立返回上下文；参数注解缺失 → E3007。</summary>
    private void FnBody(Stmt.FnDecl d, SymbolId? sym, TypeId? recvTy)
    {
        var savedScopes = _scopes;
        _scopes = [[]];
        var savedTps = _tps;
        _tps = d.TypeParams;
        var savedRet = _fnRet;
        var savedInFn = _inFn;
        var sig = FnSignatureOfDecl(d);
        var sigRet = Tables.Data(sig) is TypeData.Fn f ? f.Ret : TypeTables.Unknown;
        _fnRet = d.Ret is not null ? sigRet : null; // 注解只解析一次，错误不重复报
        _inFn = true;

        if (d.Recv is not null)
        {
            DefineType(d.Recv.Name, recvTy ?? TypeTables.Unknown);
        }
        DeclareParams(d, sym, recvTy);
        if (sym is not null)
        {
            _fnTy[sym.Value] = sig;
        }
        foreach (var s in d.Body)
        {
            WalkStmt(s);
        }
        _scopes = savedScopes;
        _tps = savedTps;
        _fnRet = savedRet;
        _inFn = savedInFn;
    }

    /// <summary>解析参数注解（缺注解 → E3007 + Unknown）入作用域，并把类型按序落到参数符号。</summary>
    private void DeclareParams(Stmt.FnDecl d, SymbolId? sym, TypeId? recvTy)
    {
        var psyms = sym is not null && _res.FnParams.TryGetValue(sym.Value, out var ps)
            ? ps
            : [];
        var pi = 0;
        if (d.Recv is not null && pi < psyms.Count)
        {
            _symTy[psyms[pi]] = recvTy ?? TypeTables.Unknown;
            pi++;
        }
        foreach (var p in d.Params)
        {
            TypeId pt;
            if (p.Typ is null)
            {
                Err(d.Span, DiagnosticCode.E3007, $"参数 {p.Name} 缺少类型注解");
                pt = TypeTables.Unknown;
            }
            else
            {
                pt = Anno.ResolveAnnoEmit(this, p.Typ, d.TypeParams);
            }
            DefineType(p.Name, pt);
            if (p.Name != "_" && pi < psyms.Count)
            {
                _symTy[psyms[pi]] = pt;
                pi++;
            }
        }
    }

    /// <summary>fn/方法的函数类型（参数注解缺失 → Unknown 占位）。</summary>
    private TypeId FnSignatureOfDecl(Stmt.FnDecl d)
    {
        var ps = d.Params.Select(p => p.Typ is null
            ? TypeTables.Unknown
            : Anno.ResolveAnnoEmit(this, p.Typ, d.TypeParams)).ToList();
        var r = d.Ret is null ? TypeTables.Unknown : Anno.ResolveAnnoEmit(this, d.Ret, d.TypeParams);
        return Tables.Intern(new TypeData.Fn(ps, r));
    }

    /// <summary>符号侧的函数类型（extern/未走查体的 fn 用）。</summary>
    private TypeId FnSignatureOfSymbol(SymbolId sym)
    {
        var s = _res.Arena.Get(sym);
        var ret = s.TypeEx;
        var tps = s.TypeParams;
        var ps = s.ParamAnnos.Select(a => a is null
            ? TypeTables.Unknown
            : Anno.ResolveAnnoEmit(this, a, tps)).ToList();
        var r = ret is null ? TypeTables.Unknown : Anno.ResolveAnnoEmit(this, ret, tps);
        return Tables.Intern(new TypeData.Fn(ps, r));
    }

    // ———— 语句———

    private void WalkStmt(Stmt s)
    {
        switch (s)
        {
            case Stmt.Let:
                LetStmt(s);
                break;

            case Stmt.Assign:
                Assign(s);
                break;

            case Stmt.ExprStmt e:
                // ADR-0005 扩展（2026-09-27）：表达式语句位置的顶层 ? 合法，
                // 走与 let/return/赋值相同的传播检查
                if (e.Expr is Expr.Try tryEx)
                {
                    InferTry(tryEx);
                }
                else
                {
                    Infer(e.Expr);
                }
                break;

            case Stmt.Return:
                ReturnStmt(s);
                break;

            case Stmt.If ifStmt:
                {
                    RequireBool(ifStmt.Cond, "if 条件");
                    Block(ifStmt.ThenBody);
                    if (ifStmt.ElseBody is not null)
                    {
                        Block(ifStmt.ElseBody);
                    }
                    break;
                }

            case Stmt.While w:
                RequireBool(w.Cond, "while 条件");
                Block(w.Body);
                break;

            case Stmt.ForIn forIn:
                {
                    var ty = Infer(forIn.Iter);
                    var elem = ForInElem(ty, forIn.Span);
                    var sym = _res.DeclSyms.Get(s);
                    if (sym is not null)
                    {
                        _symTy[sym.Value] = elem;
                    }
                    PushScope();
                    DefineType(forIn.Variable, elem);
                    foreach (var b in forIn.Body)
                    {
                        WalkStmt(b);
                    }
                    PopScope();
                    break;
                }

            case Stmt.Go go:
                InferCall(go.Call);
                break;

            case Stmt.FnDecl { Recv: null } d:
                {
                    var sym = _res.DeclSyms.Get(s);
                    FnBody(d, sym, null);
                    break;
                }

            // 嵌套类型声明由 resolver 报 E2010；break/continue 位置由 resolver 管
            case Stmt.FnDecl { Recv: not null }
                or Stmt.StructDecl or Stmt.EnumDecl or Stmt.Break or Stmt.Continue
                or Stmt.Import or Stmt.Extern:
                break;
        }
    }

    /// <summary>for-in 迭代协议（CTRL-FORIN-003）：不可迭代 → E3010。</summary>
    private TypeId ForInElem(TypeId ty, Span span)
    {
        switch (Tables.Data(ty))
        {
            case TypeData.Slice s: return s.Elem;
            case TypeData.Map m: return m.Key;
            case TypeData.Str: return TypeTables.Str;
            case TypeData.Range: return TypeTables.Int;
            default:
                if (!Ops.IsConcrete(Tables, ty)) return TypeTables.Unknown;
                var ts = Ops.TypeStr(Tables, ty);
                Err(span, DiagnosticCode.E3010, $"{ts} 不可迭代");
                return TypeTables.Unknown;
        }
    }

    private void Block(IReadOnlyList<Stmt> list)
    {
        PushScope();
        foreach (var s in list)
        {
            WalkStmt(s);
        }
        PopScope();
    }

    private void LetStmt(Stmt s)
    {
        if (s is not Stmt.Let let) return;
        var ty = let.Init is Expr.Try
            ? InferTry(let.Init)
            : Infer(let.Init);
        if (let.Typ is not null)
        {
            var anno = Anno.ResolveAnnoEmit(this, let.Typ, _tps);
            if (!Ops.Assignable(Tables, anno, ty))
            {
                var vt = Ops.TypeStr(Tables, ty);
                var at = Ops.TypeStr(Tables, anno);
                Err(let.Span, DiagnosticCode.E3001, $"不能把 {vt} 绑定为注解类型 {at}");
            }
            ty = anno;
        }
        var sym = _res.DeclSyms.Get(s);
        if (sym is not null)
        {
            _symTy[sym.Value] = ty;
        }
        DefineType(let.Name, ty);
    }

    private void Assign(Stmt s)
    {
        if (s is not Stmt.Assign assign) return;
        // SPEC 修正（RESULT-TRY-003 + ADR-0005）：赋值顶层 `?` 合法（证据误报 E3005）
        var valTy = assign.Value is Expr.Try
            ? InferTry(assign.Value)
            : Infer(assign.Value);
        switch (assign.Target)
        {
            case Expr.Ident ident:
                {
                    var tgtTy = LookupVar(ident.Name) ?? TypeTables.Unknown;
                    if (assign.Op != AssignOp.Assign)
                    {
                        CheckCompound(assign.Span, assign.Op, tgtTy, valTy);
                        return;
                    }
                    if (!Ops.Assignable(Tables, tgtTy, valTy))
                    {
                        var vt = Ops.TypeStr(Tables, valTy);
                        var tt = Ops.TypeStr(Tables, tgtTy);
                        Err(assign.Span, DiagnosticCode.E3001, $"不能把 {vt} 赋给 {tt}");
                    }
                    break;
                }
            case Expr.Field field:
                {
                    var objTy = Infer(field.Obj);
                    var fTy = FieldType(objTy, field.Name, assign.Target.Span);
                    if (assign.Op != AssignOp.Assign)
                    {
                        CheckCompound(assign.Span, assign.Op, fTy, valTy);
                        return;
                    }
                    if (!Ops.Assignable(Tables, fTy, valTy))
                    {
                        var vt = Ops.TypeStr(Tables, valTy);
                        var ft = Ops.TypeStr(Tables, fTy);
                        Err(assign.Span, DiagnosticCode.E3001, $"不能把 {vt} 赋给字段 {field.Name}（{ft}）");
                    }
                    break;
                }
            case Expr.Index index:
                {
                    var objTy = Infer(index.Obj);
                    var idxTy = Infer(index.Idx);
                    CheckIndexKey(objTy, idxTy, assign.Target.Span);
                    var elemTy = IndexElemTy(objTy, assign.Target.Span);
                    if (assign.Op != AssignOp.Assign)
                    {
                        CheckCompound(assign.Span, assign.Op, elemTy, valTy);
                        return;
                    }
                    if (!Ops.Assignable(Tables, elemTy, valTy))
                    {
                        var vt = Ops.TypeStr(Tables, valTy);
                        var et = Ops.TypeStr(Tables, elemTy);
                        Err(assign.Span, DiagnosticCode.E3001, $"不能把 {vt} 赋给容器元素（{et}）");
                    }
                    break;
                }
        }
    }

    /// <summary>复合赋值（+= -= *= /=）：同型数值/字符串；数组 += 数组。</summary>
    private void CheckCompound(Span span, AssignOp op, TypeId tgtTy, TypeId valTy)
    {
        if (!Ops.IsConcrete(Tables, tgtTy) || !Ops.IsConcrete(Tables, valTy))
        {
            return;
        }
        var opS = op switch
        {
            AssignOp.Assign => "=",
            AssignOp.AddAssign => "+=",
            AssignOp.SubAssign => "-=",
            AssignOp.MulAssign => "*=",
            AssignOp.DivAssign => "/=",
            _ => throw new InvalidOperationException($"未登记的 AssignOp: {op}"),
        };
        if (Tables.Data(tgtTy) is TypeData.Slice)
        {
            if (op == AssignOp.AddAssign && Tables.Data(valTy) is TypeData.Slice)
            {
                return;
            }
            var vt = Ops.TypeStr(Tables, valTy);
            Err(span, DiagnosticCode.E3001, $"数组 {opS} 需要数组右值，得到 {vt}");
            return;
        }
        var sameKind = Tables.Data(tgtTy).GetType() == Tables.Data(valTy).GetType();
        if (sameKind
            && (Ops.IsNumeric(Tables, tgtTy) || Tables.Data(tgtTy) is TypeData.Str))
        {
            return;
        }
        var tt = Ops.TypeStr(Tables, tgtTy);
        var vt2 = Ops.TypeStr(Tables, valTy);
        Err(span, DiagnosticCode.E3002, $"{tt} 与 {vt2} 不可做 {OpName(opS)}（无隐式转换）");
    }

    /// <summary>复合赋值/二元运算的诊断名（证据 opName：仅认识赋值系，其余一律 "?"）。</summary>
    private static string OpName(string op) => op is "+=" or "-=" or "*=" or "/=" or "=" ? op : "?";

    private void ReturnStmt(Stmt s)
    {
        if (s is not Stmt.Return ret) return;
        if (!_inFn)
        {
            return; // resolver 已报 E2008
        }
        if (ret.Value is null)
        {
            if (_fnRet is { } r0 && Tables.Data(r0) is TypeData.Result)
            {
                Err(ret.Span, DiagnosticCode.E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)");
            }
            else if (_fnRet is { } r1 && Ops.IsConcrete(Tables, r1))
            {
                var ts = Ops.TypeStr(Tables, r1);
                Err(ret.Span, DiagnosticCode.E3001, $"注解为 {ts} 的函数不能裸 return");
            }
            return;
        }
        if (ret.Value is Expr.Try t)
        {
            InferTry(t);
            return;
        }
        var fnRet = _fnRet;
        if (fnRet is { } r && Tables.Data(r) is TypeData.Result)
        {
            InferResultReturn(ret.Span, ret.Value, r);
            return;
        }
        var valTy = Infer(ret.Value);
        if (fnRet is { } r2 && Ops.IsConcrete(Tables, r2) && !Ops.Assignable(Tables, r2, valTy))
        {
            var vt = Ops.TypeStr(Tables, valTy);
            var rt = Ops.TypeStr(Tables, r2);
            Err(ret.Span, DiagnosticCode.E3001, $"返回 {vt} 与注解 {rt} 不匹配");
        }
    }

    private void InferResultReturn(Span span, Expr call, TypeId ret)
    {
        if (call is not Expr.Call c || c.Callee is not Expr.Ident ident
            || (ident.Name != "Ok" && ident.Name != "Err") || c.Args.Count != 1)
        {
            Err(span, DiagnosticCode.E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)");
            Infer(call); // 证据同款：非 Call 值仍走推断（内嵌 ? 的 E3005 在此发出）
            return;
        }
        var argTy = Infer(c.Args[0]);
        if (ident.Name == "Ok")
        {
            if (!Ops.Assignable(Tables, Ops.ResultOk(Tables, ret), argTy))
            {
                var at = Ops.TypeStr(Tables, argTy);
                var rt = Ops.TypeStr(Tables, ret);
                Err(span, DiagnosticCode.E3001, $"Ok({at}) 与返回类型 {rt} 不匹配");
            }
        }
        else if (!Ops.Assignable(Tables, Ops.ResultErr(Tables, ret), argTy))
        {
            var at = Ops.TypeStr(Tables, argTy);
            var rt = Ops.TypeStr(Tables, ret);
            Err(span, DiagnosticCode.E3001, $"Err({at}) 与返回类型 {rt} 不匹配");
        }
        _exprTy.Set(call, ret);
    }

    // ———— 推断主入口———

    /// <summary>推断并记录表达式类型（memo 化；同节点诊断不重复发射）。</summary>
    private TypeId Infer(Expr e)
    {
        if (_exprTy.Get(e) is { } cached)
        {
            return cached;
        }
        var t = Compute(e);
        _exprTy.Set(e, t);
        return t;
    }

    private TypeId Compute(Expr e)
    {
        switch (e)
        {
            case Expr.IntLit: return TypeTables.Int;
            case Expr.FloatLit: return TypeTables.Float;
            case Expr.StrLit: return TypeTables.Str;
            case Expr.BoolLit: return TypeTables.Bool;
            case Expr.Ident: return IdentType(e);
            case Expr.Unary: return InferUnary(e);
            case Expr.Binary: return InferBinary(e);
            case Expr.Range range:
                {
                    var loTy = Infer(range.Lo);
                    var hiTy = Infer(range.Hi);
                    var bad = (Ops.IsConcrete(Tables, loTy) && Tables.Data(loTy) is not TypeData.Int)
                        || (Ops.IsConcrete(Tables, hiTy) && Tables.Data(hiTy) is not TypeData.Int);
                    if (bad)
                    {
                        var ls = Ops.TypeStr(Tables, loTy);
                        var hs = Ops.TypeStr(Tables, hiTy);
                        Err(range.Span, DiagnosticCode.E3001, $"区间两端须为 int，得到 {ls}..{hs}");
                        return TypeTables.Invalid;
                    }
                    return TypeTables.Range;
                }
            case Expr.Call: return InferCall(e);
            case Expr.Index index:
                {
                    var objTy = Infer(index.Obj);
                    var idxTy = Infer(index.Idx);
                    CheckIndexKey(objTy, idxTy, index.Span);
                    return IndexElemTy(objTy, index.Span);
                }
            case Expr.Slice slice:
                {
                    var rawObj = Infer(slice.Obj);
                    var objTy = Ops.Deref(Tables, rawObj);
                    var loTy = Infer(slice.Lo);
                    var hiTy = Infer(slice.Hi);
                    foreach (var t in new[] { loTy, hiTy })
                    {
                        if (Ops.IsConcrete(Tables, t) && Tables.Data(t) is not TypeData.Int)
                        {
                            var ts = Ops.TypeStr(Tables, t);
                            Err(slice.Span, DiagnosticCode.E3013, $"切片端点须为 int，得到 {ts}");
                        }
                    }
                    switch (Tables.Data(objTy))
                    {
                        case TypeData.Slice: return objTy;
                        case TypeData.Str: return TypeTables.Str;
                        case TypeData.Any or TypeData.Unknown or TypeData.Invalid: return TypeTables.Unknown;
                        default:
                            {
                                var ts = Ops.TypeStr(Tables, objTy);
                                Err(slice.Span, DiagnosticCode.E3001, $"{ts} 不可切片");
                                return TypeTables.Invalid;
                            }
                    }
                }
            case Expr.Field field:
                {
                    var objTy = Infer(field.Obj);
                    return FieldType(objTy, field.Name, e.Span);
                }
            case Expr.StructLit: return InferStructLit(e);
            case Expr.MapLit: return InferMapLit(e);
            case Expr.ArrLit: return InferArrLit(e);
            case Expr.Match: return InferMatch(e);
            case Expr.FnLit: return InferFnLit(e);
            case Expr.Try t:
                {
                    // 非语句顶层位置（let/return/赋值/表达式语句已在语句层处理）
                    Err(t.Span, DiagnosticCode.E3005, "? 只能出现在 let/return/赋值/表达式语句的顶层");
                    return InferTryOperand(t);
                }
            default:
                throw new InvalidOperationException("未登记的 Expr 变体");
        }
    }

    /// <summary>Ident 类型：作用域栈（参数/绑定/臂捕获）优先，再走符号路径。</summary>
    private TypeId IdentType(Expr e)
    {
        if (e is not Expr.Ident ident) return TypeTables.Unknown;
        if (LookupVar(ident.Name) is { } vt && vt != TypeTables.Invalid)
        {
            return vt;
        }
        if (_res.Refs.Get(e) is { } sym)
        {
            switch (_res.Arena.Get(sym).Kind)
            {
                case SymbolKind.Fn:
                    {
                        if (_fnTy.TryGetValue(sym, out var ft)) return ft;
                        var t = FnSignatureOfSymbol(sym);
                        _fnTy[sym] = t;
                        return t;
                    }
                case SymbolKind.Ctor:
                    return CtorValueType(sym, ident.Span);
                default:
                    return _symTy.TryGetValue(sym, out var st) ? st : TypeTables.Unknown;
            }
        }
        return TypeTables.Unknown; // 内建裸引用 / 未定义名（已报）→ 保守
    }

    /// <summary>裸构造器引用（零参才合法，如 None）。</summary>
    private TypeId CtorValueType(SymbolId sym, Span span)
    {
        var s = _res.Arena.Get(sym);
        if (s.Arity != 0)
        {
            Err(span, DiagnosticCode.E3003, $"构造器 {s.Name} 须直接调用");
            return TypeTables.Invalid;
        }
        return CtorResultType(sym, []);
    }

    /// <summary>构造器调用的结果类型。</summary>
    private TypeId CtorResultType(SymbolId sym, IReadOnlyList<TypeId> args)
    {
        var owner = _res.Arena.Get(sym).OwnerName;
        switch (owner)
        {
            case "Option":
                return Tables.Intern(new TypeData.Option(args.Count > 0 ? args[0] : TypeTables.Unknown));
            case "Result":
                return Tables.Intern(new TypeData.Result(
                    args.Count > 0 ? args[0] : TypeTables.Unknown,
                    TypeTables.Error));
            default:
                return LookupTypeSym(owner) is { } sid
                    && _res.Arena.Get(sid).Nominal is NominalRef.Enum def
                    ? Tables.Intern(new TypeData.Enum(def.Def))
                    : TypeTables.Unknown;
        }
    }

    private TypeId InferUnary(Expr e)
    {
        if (e is not Expr.Unary un) return TypeTables.Unknown;
        var t = Infer(un.Operand);
        switch (un.Op)
        {
            case UnaryOp.Neg:
                {
                    if (!Ops.IsConcrete(Tables, t)) return t;
                    if (Ops.IsNumeric(Tables, t)) return t;
                    var ts = Ops.TypeStr(Tables, t);
                    Err(un.Span, DiagnosticCode.E3011, $"- 不能用于 {ts}");
                    return TypeTables.Invalid;
                }
            case UnaryOp.Not:
                {
                    if (Ops.IsConcrete(Tables, t) && Tables.Data(t) is not TypeData.Bool)
                    {
                        var ts = Ops.TypeStr(Tables, t);
                        Err(un.Span, DiagnosticCode.E3011, $"! 不能用于 {ts}（须为 bool）");
                        return TypeTables.Invalid;
                    }
                    return TypeTables.Bool;
                }
            case UnaryOp.Ref:
                {
                    if (un.Operand is not Expr.Ident)
                    {
                        Err(un.Span, DiagnosticCode.E3012, "v0 只能对变量取引用");
                        return TypeTables.Invalid;
                    }
                    return Tables.Intern(new TypeData.Ref(t));
                }
            default:
                throw new InvalidOperationException($"未登记的 UnaryOp: {un.Op}");
        }
    }

    private TypeId InferBinary(Expr e)
    {
        if (e is not Expr.Binary bin) return TypeTables.Unknown;
        // 短路逻辑：操作数须 bool（与 interp truthy 一致）
        if (bin.Op is BinaryOp.AndAnd or BinaryOp.OrOr)
        {
            RequireBool(bin.Lhs, "逻辑运算");
            RequireBool(bin.Rhs, "逻辑运算");
            return TypeTables.Bool;
        }
        var l = Infer(bin.Lhs);
        var r = Infer(bin.Rhs);

        switch (bin.Op)
        {
            case BinaryOp.Eq or BinaryOp.Ne:
                {
                    if (Ops.IsConcrete(Tables, l) && Ops.IsConcrete(Tables, r))
                    {
                        var sameScalar = Tables.Data(l).GetType() == Tables.Data(r).GetType()
                            && (Ops.IsNumeric(Tables, l)
                                || Tables.Data(l) is TypeData.Str or TypeData.Bool);
                        var sameEnum = Tables.Data(l) is TypeData.Enum le
                            && Tables.Data(r) is TypeData.Enum re
                            && le.Def == re.Def;
                        if (!sameScalar && !sameEnum)
                        {
                            var ls = Ops.TypeStr(Tables, l);
                            var rs = Ops.TypeStr(Tables, r);
                            Err(bin.Span, DiagnosticCode.E3001, $"{ls} 与 {rs} 不可用 == 比较");
                            return TypeTables.Invalid;
                        }
                    }
                    return TypeTables.Bool;
                }
            case BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge:
                {
                    if (Ops.IsConcrete(Tables, l) && Ops.IsConcrete(Tables, r))
                    {
                        var ok = Tables.Data(l).GetType() == Tables.Data(r).GetType()
                            && (Ops.IsNumeric(Tables, l) || Tables.Data(l) is TypeData.Str);
                        if (!ok)
                        {
                            var ls = Ops.TypeStr(Tables, l);
                            var rs = Ops.TypeStr(Tables, r);
                            Err(bin.Span, DiagnosticCode.E3001, $"{ls} 与 {rs} 不可比较大小（无隐式转换）");
                            return TypeTables.Invalid;
                        }
                    }
                    return TypeTables.Bool;
                }
            case BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor or BinaryOp.Shl or BinaryOp.Shr:
                {
                    if (Ops.IsConcrete(Tables, l) && Ops.IsConcrete(Tables, r))
                    {
                        if (Tables.Data(l) is not TypeData.Int || Tables.Data(r) is not TypeData.Int)
                        {
                            var ls = Ops.TypeStr(Tables, l);
                            var rs = Ops.TypeStr(Tables, r);
                            Err(bin.Span, DiagnosticCode.E3001, $"{ls} 与 {rs} 不可位运算（须为 int）");
                            return TypeTables.Invalid;
                        }
                        return TypeTables.Int;
                    }
                    return TypeTables.Any;
                }

            // 算术
            case BinaryOp.Add:
                {
                    if (Ops.IsConcrete(Tables, l)
                        && Tables.Data(l) is TypeData.Str
                        && Ops.IsConcrete(Tables, r)
                        && Tables.Data(r) is TypeData.Str)
                    {
                        return TypeTables.Str;
                    }
                    return ArithType(bin.Span, l, r);
                }
            case BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div:
                return ArithType(bin.Span, l, r);
            case BinaryOp.Rem:
                {
                    if (Ops.IsConcrete(Tables, l) && Tables.Data(l) is TypeData.Float)
                    {
                        Err(bin.Span, DiagnosticCode.E3001, "% 不能用于 float");
                        return TypeTables.Invalid;
                    }
                    return ArithType(bin.Span, l, r);
                }
            // AndAnd/OrOr 已在入口短路处理
            case BinaryOp.AndAnd or BinaryOp.OrOr:
                return TypeTables.Bool;
            default:
                throw new InvalidOperationException($"未登记的 BinaryOp: {bin.Op}");
        }
    }

    /// <summary>数值运算类型：int⊗int→int、float⊗float→float、混用→E3002、动态→Any。</summary>
    private TypeId ArithType(Span span, TypeId l, TypeId r)
    {
        if (!Ops.IsConcrete(Tables, l) || !Ops.IsConcrete(Tables, r))
        {
            return TypeTables.Any;
        }
        if (Tables.Data(l).GetType() == Tables.Data(r).GetType() && Ops.IsNumeric(Tables, l))
        {
            return l;
        }
        var ls = Ops.TypeStr(Tables, l);
        var rs = Ops.TypeStr(Tables, r);
        Err(span, DiagnosticCode.E3002, $"{ls} 与 {rs} 不可做 {OpName("?")}（无隐式转换）");
        return TypeTables.Invalid;
    }

    private void RequireBool(Expr e, string what)
    {
        var t = Infer(e);
        if (Ops.IsConcrete(Tables, t) && Tables.Data(t) is not TypeData.Bool)
        {
            var ts = Ops.TypeStr(Tables, t);
            Err(e.Span, DiagnosticCode.E3011, $"{what}须为 bool，得到 {ts}");
        }
    }

    // ———— 调用———

    private TypeId InferCall(Expr e)
    {
        if (e is not Expr.Call call) return TypeTables.Unknown;
        var callee = call.Callee;
        // 构造器 / 内建 / 用户 fn 的 Ident 调用
        if (callee is Expr.Ident { Name: var name } ident)
        {
            var refs = _res.Refs.Get(ident);
            if (refs is { } sym)
            {
                if (_res.Arena.Get(sym).Kind == SymbolKind.Ctor)
                {
                    var argTys = call.Args.Select(a => Infer(a)).ToList();
                    var arity = _res.Arena.Get(sym).Arity;
                    if (argTys.Count != arity)
                    {
                        Err(e.Span, DiagnosticCode.E3003, $"{name} 需要 {arity} 个参数，得到 {argTys.Count} 个");
                        return TypeTables.Invalid;
                    }
                    if (name is "Ok" or "Err")
                    {
                        // Ok/Err 只能出现在 Result 函数的 return（RESULT-RETURN-004）
                        Err(e.Span, DiagnosticCode.E3006, "Ok/Err 只能出现在 Result 函数的 return");
                    }
                    return CtorResultType(sym, argTys);
                }
                // 用户 fn
                if (_res.Arena.Get(sym).Kind == SymbolKind.Fn)
                {
                    CheckArgsVsSymbol(sym, call.Args, e.Span);
                    if (_fnTy.TryGetValue(sym, out var t)
                        && Tables.Data(t) is TypeData.Fn f)
                    {
                        return f.Ret;
                    }
                    return TypeTables.Unknown;
                }
                // var/param 等持有函数值的 callee：落到下方表达式 callee 路径
            }
            else if (_res.BuiltinMap.ContainsKey(name) && Builtin.IsBuiltinFn(name))
            {
                // 内建调用（内建不进 refs，裸引用已由 resolver 放行给调用路径）
                var argTys = call.Args.Select(a => Infer(a)).ToList();
                return Builtin.BuiltinReturn(Tables, name, argTys);
            }
        }

        // 方法/字段函数值调用：obj.name(args)
        if (callee is Expr.Field)
        {
            return InferMethodCall(callee, e);
        }

        // 表达式 callee（函数字面量/索引）：按函数类型检查
        var calleeTy = Infer(callee);
        var args2 = call.Args.Select(a => Infer(a)).ToList();
        if (Tables.Data(calleeTy) is TypeData.Fn cf)
        {
            CheckArgsVsFn(calleeTy, args2, e.Span);
            return cf.Ret;
        }
        return TypeTables.Unknown;
    }

    /// <summary>obj.name(args)：用户方法 / 字段函数值。</summary>
    private TypeId InferMethodCall(Expr fe, Expr call)
    {
        if (fe is not Expr.Field field || call is not Expr.Call c) return TypeTables.Unknown;
        var rawObj = Infer(field.Obj);
        var objTy = Ops.Deref(Tables, rawObj);
        if (Tables.Data(objTy) is TypeData.Struct def)
        {
            // 用户方法（值接收者）
            var m = StructMethodSym(def.Def, field.Name);
            if (m is { } msym)
            {
                CheckArgsVsSymbol(msym, c.Args, call.Span);
                var s = _res.Arena.Get(msym);
                return s.TypeEx is not null
                    ? Anno.ResolveAnnoEmit(this, s.TypeEx, s.TypeParams)
                    : TypeTables.Unknown;
            }
            // 字段持有的函数值
            var fTy = FieldType(objTy, field.Name, field.Span);
            var argTys = c.Args.Select(a => Infer(a)).ToList();
            if (Tables.Data(fTy) is TypeData.Fn ff)
            {
                CheckArgsVsFn(fTy, argTys, call.Span);
                return ff.Ret;
            }
            if (!Ops.IsConcrete(Tables, fTy) || fTy == TypeTables.Invalid)
            {
                return TypeTables.Unknown;
            }
            Err(call.Span, DiagnosticCode.E3004, $"{field.Name} 不是方法也不是函数值字段");
            return TypeTables.Invalid;
        }
        if (!Ops.IsConcrete(Tables, objTy))
        {
            foreach (var a in c.Args)
            {
                Infer(a);
            }
            return TypeTables.Unknown;
        }
        var ts = Ops.TypeStr(Tables, objTy);
        Err(call.Span, DiagnosticCode.E3004, $"{ts} 没有方法 {field.Name}");
        return TypeTables.Invalid;
    }

    /// <summary>struct def → 首个声明符号（arena 创建序；顶层先于嵌套）。</summary>
    private SymbolId? StructSymbolFor(StructDefId def)
    {
        foreach (var s in _res.Arena.Items)
        {
            if (s.Nominal is NominalRef.Struct st && st.Def == def)
            {
                return s.Id;
            }
        }
        return null;
    }

    private SymbolId? StructMethodSym(StructDefId def, string name)
    {
        var sid = StructSymbolFor(def);
        return sid is null ? null : _res.Arena.Get(sid.Value).Methods.TryGetValue(name, out var m) ? m : null;
    }

    /// <summary>实参 ↔ 声明参数（个数 + 已注解参数的兼容性）。</summary>
    private void CheckArgsVsSymbol(SymbolId sym, IReadOnlyList<Expr> args, Span span)
    {
        var s = _res.Arena.Get(sym);
        var fname = s.Name;
        var arity = s.Arity;
        var annos = s.ParamAnnos;
        var names = s.ParamNames;
        var tps = s.TypeParams;
        if (args.Count != arity)
        {
            Err(span, DiagnosticCode.E3003, $"{fname} 需要 {arity} 个参数，得到 {args.Count} 个");
            foreach (var a in args)
            {
                Infer(a);
            }
            return;
        }
        for (var i = 0; i < args.Count; i++)
        {
            var at = Infer(args[i]);
            if (i >= annos.Count || annos[i] is not { } anno) continue;
            var pt = Anno.ResolveAnnoEmit(this, anno, tps);
            if (!Ops.Assignable(Tables, pt, at))
            {
                var pname = i < names.Count ? names[i] : "";
                var pts = Ops.TypeStr(Tables, pt);
                var ats = Ops.TypeStr(Tables, at);
                Err(args[i].Span, DiagnosticCode.E3001, $"参数 {pname} 需要 {pts}，得到 {ats}");
            }
        }
    }

    private void CheckArgsVsFn(TypeId fnTy, IReadOnlyList<TypeId> args, Span span)
    {
        if (Tables.Data(fnTy) is not TypeData.Fn f) return;
        if (args.Count != f.Params.Count)
        {
            Err(span, DiagnosticCode.E3003, $"需要 {f.Params.Count} 个参数，得到 {args.Count} 个");
            return;
        }
        for (var i = 0; i < f.Params.Count; i++)
        {
            if (!Ops.Assignable(Tables, f.Params[i], args[i]))
            {
                var pts = Ops.TypeStr(Tables, f.Params[i]);
                var ats = Ops.TypeStr(Tables, args[i]);
                Err(span, DiagnosticCode.E3001, $"第 {i + 1} 个参数需要 {pts}，得到 {ats}");
            }
        }
    }

    // ———— 字面量与容器———

    private TypeId InferStructLit(Expr e)
    {
        if (e is not Expr.StructLit lit) return TypeTables.Unknown;
        var refs = _res.Refs.Get(e);
        if (refs is not { } sym)
        {
            // resolver 已报未知 struct；只走子表达式
            foreach (var f in lit.Fields)
            {
                Infer(f.Value);
            }
            return TypeTables.Unknown;
        }
        var s = _res.Arena.Get(sym);
        if (lit.Fields.Count != s.Fields.Count)
        {
            Err(lit.Span, DiagnosticCode.E3001,
                $"{s.Name} 需要 {s.Fields.Count} 个字段，得到 {lit.Fields.Count} 个");
        }
        for (var i = 0; i < lit.Fields.Count; i++)
        {
            var f = lit.Fields[i];
            var ft = Infer(f.Value);
            if (i >= s.Fields.Count) continue;
            if (f.Name != s.Fields[i])
            {
                Err(lit.Span, DiagnosticCode.E3001,
                    $"{s.Name} 第 {i + 1} 个字段应为 {s.Fields[i]}，得到 {f.Name}");
            }
            if (i < s.FieldAnnos.Count && s.FieldAnnos[i] is { } anno)
            {
                var at = Anno.ResolveAnnoQuiet(this, anno, s.TypeParams);
                if (!Ops.Assignable(Tables, at, ft))
                {
                    var fts = Ops.TypeStr(Tables, ft);
                    var ats = Ops.TypeStr(Tables, at);
                    Err(f.Value.Span, DiagnosticCode.E3001, $"字段 {f.Name} 需要 {ats}，得到 {fts}");
                }
            }
        }
        return s.Nominal is NominalRef.Struct def
            ? Tables.Intern(new TypeData.Struct(def.Def))
            : TypeTables.Unknown;
    }

    private TypeId InferMapLit(Expr e)
    {
        if (e is not Expr.MapLit lit) return TypeTables.Unknown;
        TypeId? keyT = null;
        TypeId? valT = null;
        foreach (var item in lit.Items)
        {
            var kt = Infer(item.Key);
            var vt = Infer(item.Value);
            keyT = MixConcrete(keyT, kt);
            valT = MixConcrete(valT, vt);
        }
        var key = keyT ?? TypeTables.Unknown;
        var val = valT ?? TypeTables.Unknown;
        var keyBad = Ops.IsConcrete(Tables, key)
            && Tables.Data(key) is not (TypeData.Int or TypeData.Str or TypeData.Bool);
        if (keyBad)
        {
            var ks = Ops.TypeStr(Tables, key);
            Err(lit.Span, DiagnosticCode.E3014, $"映射键须为 int/string/bool，得到 {ks}");
            return Tables.Intern(new TypeData.Map(TypeTables.Unknown, val));
        }
        return Tables.Intern(new TypeData.Map(key, val));
    }

    /// <summary>混型统一：同型保持；一侧宽松取另一侧；两侧具体异型 → 动态（interp 合法）。</summary>
    private TypeId? MixConcrete(TypeId? cur, TypeId next)
    {
        if (cur is null) return next;
        var c = cur.Value;
        if (Ops.Equal(Tables, c, next) || !Ops.IsConcrete(Tables, next))
        {
            return c;
        }
        if (!Ops.IsConcrete(Tables, c))
        {
            return next;
        }
        return TypeTables.Unknown;
    }

    private TypeId InferArrLit(Expr e)
    {
        if (e is not Expr.ArrLit lit) return TypeTables.Unknown;
        TypeId? elem = null;
        foreach (var el in lit.Items)
        {
            var t = Infer(el);
            if (elem is null)
            {
                elem = t;
            }
            else
            {
                var cur = elem.Value;
                if (Ops.Equal(Tables, cur, t)) continue;
                if (!Ops.IsConcrete(Tables, t)) continue;
                if (!Ops.IsConcrete(Tables, cur))
                {
                    elem = t;
                    continue;
                }
                elem = TypeTables.Unknown; // 混型数组：interp 合法
            }
        }
        return Tables.Intern(new TypeData.Slice(elem ?? TypeTables.Unknown));
    }

    // ———— match———

    private TypeId InferMatch(Expr e)
    {
        if (e is not Expr.Match match) return TypeTables.Unknown;
        var scrut = Infer(match.Scrutinee);
        var (kind, ok) = ClassifyMatch(e);
        _matchKind.Set(e, kind);
        if (!ok)
        {
            return TypeTables.Unknown; // 分类已报错（混用/字面量臂），不再叠加穷尽诊断
        }

        if (kind == MatchKind.Enum)
        {
            CheckEnumExhaustive(e, match.Span);
        }
        else if (kind is MatchKind.Result or MatchKind.Option)
        {
            CheckAlgebraicExhaustive(e, match.Span);
        }

        // 臂绑定类型与臂体类型（绑定限本臂作用域，MATCH-BIND-003）
        TypeId? body = null;
        foreach (var arm in match.Arms)
        {
            var bindTypes = ArmBindTypes(arm.Pat, scrut);
            PushScope();
            foreach (var (name, t) in bindTypes)
            {
                DefineType(name, t);
            }
            var bt = Infer(arm.Body);
            PopScope();
            if (body is null)
            {
                body = bt;
            }
            else
            {
                var cur = body.Value;
                if (Ops.Equal(Tables, cur, bt))
                {
                    // 同型
                }
                else if (Ops.Assignable(Tables, bt, cur))
                {
                    // 臂体可归入已定类型 → 保持（如 [] 归入 [Ref]）
                }
                else if (Ops.Assignable(Tables, cur, bt))
                {
                    body = bt;
                }
                else if (Ops.IsConcrete(Tables, cur) && Ops.IsConcrete(Tables, bt))
                {
                    var cs = Ops.TypeStr(Tables, cur);
                    var bs = Ops.TypeStr(Tables, bt);
                    Err(arm.Span, DiagnosticCode.E3001, $"match 臂类型不一致：{cs} 与 {bs}");
                }
                else if (!Ops.IsConcrete(Tables, cur))
                {
                    body = bt;
                }
            }
        }
        return body ?? TypeTables.Unknown;
    }

    /// <summary>模式绑定的捕获类型。</summary>
    private List<(string Name, TypeId Ty)> ArmBindTypes(Pat p, TypeId scrut)
    {
        var out_ = new List<(string, TypeId)>();
        WalkBind(p, scrut, out_);
        return out_;
    }

    private void WalkBind(Pat p, TypeId scrut, List<(string Name, TypeId Ty)> out_)
    {
        switch (p)
        {
            case Pat.Bind bind:
                out_.Add((bind.Name, scrut));
                _patTy.Set(p, scrut);
                break;
            case Pat.Case case_:
                {
                    var refs = _res.PatRefs.Get(p);
                    if (refs is not { } sym) return;
                    var s = _res.Arena.Get(sym);
                    var (owner, cname, caseIndex) = (s.OwnerName, s.Name, s.CaseIndex);
                    for (var i = 0; i < case_.Subs.Count; i++)
                    {
                        TypeId subT;
                        if (owner == "Result")
                        {
                            subT = cname == "Ok" ? Ops.ResultOk(Tables, scrut) : TypeTables.Error;
                        }
                        else if (owner == "Option")
                        {
                            subT = Ops.OptionElem(Tables, scrut);
                        }
                        else
                        {
                            subT = CasePayloadType(owner, caseIndex, i);
                        }
                        WalkBind(case_.Subs[i], subT, out_);
                    }
                    break;
                }
            case Pat.Lit or Pat.Wild:
                break;
        }
    }

    /// <summary>enum case 载荷类型（def 表已静默解析）。</summary>
    private TypeId CasePayloadType(string owner, uint caseIndex, int payloadIdx)
    {
        if (LookupTypeSym(owner) is not { } sid) return TypeTables.Unknown;
        if (_res.Arena.Get(sid).Nominal is not NominalRef.Enum def) return TypeTables.Unknown;
        var cases = Tables.GetEnum(def.Def).Cases;
        if (caseIndex >= (uint)cases.Count) return TypeTables.Unknown;
        var c = cases[(int)caseIndex];
        return payloadIdx < c.Payloads.Count ? c.Payloads[payloadIdx] : TypeTables.Unknown;
    }

    /// <summary>match 分类（唯一判定点）。第二返回值 false 表示分类阶段已报错。</summary>
    private (MatchKind Kind, bool Ok) ClassifyMatch(Expr e)
    {
        if (e is not Expr.Match match) return (MatchKind.Literal, true);
        var hasCase = false;
        var hasLit = false;
        var resultCtor = false;
        var optionCtor = false;
        var enumName = "";
        var mixedEnum = false;
        foreach (var arm in match.Arms)
        {
            switch (arm.Pat)
            {
                case Pat.Lit:
                    hasLit = true;
                    break;
                case Pat.Case:
                    {
                        hasCase = true;
                        if (_res.PatRefs.Get(arm.Pat) is not { } sym) continue;
                        var owner = _res.Arena.Get(sym).OwnerName;
                        if (owner == "Result")
                        {
                            resultCtor = true;
                        }
                        else if (owner == "Option")
                        {
                            optionCtor = true;
                        }
                        else if (enumName.Length == 0)
                        {
                            enumName = owner;
                        }
                        else if (enumName != owner)
                        {
                            mixedEnum = true;
                        }
                        break;
                    }
                case Pat.Bind or Pat.Wild:
                    break;
            }
        }
        if (resultCtor && optionCtor)
        {
            Err(match.Span, DiagnosticCode.E4003, "Result 与 Option 的模式混用");
            return (MatchKind.Literal, false);
        }
        if (resultCtor)
        {
            if (hasLit)
            {
                Err(match.Span, DiagnosticCode.E4002, "代数类型的 match 不能混入字面量模式");
                return (MatchKind.Result, false);
            }
            return (MatchKind.Result, true);
        }
        if (optionCtor)
        {
            if (hasLit)
            {
                Err(match.Span, DiagnosticCode.E4002, "代数类型的 match 不能混入字面量模式");
                return (MatchKind.Option, false);
            }
            return (MatchKind.Option, true);
        }
        if (hasCase)
        {
            if (hasLit)
            {
                Err(match.Span, DiagnosticCode.E4002, "enum 的 match 不能混入字面量模式");
                return (MatchKind.Enum, false);
            }
            if (mixedEnum)
            {
                Err(match.Span, DiagnosticCode.E4003, "match 混入了不同 enum 的构造器");
                return (MatchKind.Enum, false);
            }
            return (MatchKind.Enum, true);
        }
        return (MatchKind.Literal, true);
    }

    /// <summary>enum match 穷尽性（编译期）。</summary>
    private void CheckEnumExhaustive(Expr e, Span span)
    {
        if (e is not Expr.Match match) return;
        var covered = new List<string>();
        var hasWild = false;
        SymbolId? enumSym = null;
        foreach (var arm in match.Arms)
        {
            switch (arm.Pat)
            {
                case Pat.Case case_:
                    {
                        if (_res.PatRefs.Get(arm.Pat) is not { } sym) continue;
                        if (enumSym is null)
                        {
                            var owner = _res.Arena.Get(sym).OwnerName;
                            enumSym = LookupTypeSym(owner);
                        }
                        covered.Add(case_.Name);
                        break;
                    }
                case Pat.Wild or Pat.Bind:
                    hasWild = true;
                    break;
                case Pat.Lit:
                    break;
            }
        }
        if (hasWild) return;
        if (enumSym is not { } es) return;
        if (_res.Arena.Get(es).Nominal is not NominalRef.Enum def) return;
        var missing = Tables.GetEnum(def.Def).Cases
            .Where(c => !covered.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();
        if (missing.Count > 0)
        {
            Err(span, DiagnosticCode.E4001, $"match 不穷尽，缺少: {string.Join(", ", missing)}");
        }
    }

    /// <summary>Result/Option match 须两臂齐全。</summary>
    private void CheckAlgebraicExhaustive(Expr e, Span span)
    {
        if (e is not Expr.Match match) return;
        var pos = false;
        var neg = false;
        foreach (var arm in match.Arms)
        {
            switch (arm.Pat)
            {
                case Pat.Case case_:
                    if (case_.Name is "Ok" or "Some")
                    {
                        pos = true;
                    }
                    else if (case_.Name is "Err" or "None")
                    {
                        neg = true;
                    }
                    break;
                case Pat.Wild:
                    pos = true;
                    neg = true;
                    break;
                default:
                    break;
            }
        }
        if (!pos || !neg)
        {
            Err(span, DiagnosticCode.E4001, "match 不穷尽：Result/Option 须覆盖两臂");
        }
    }

    // ———— ? 传播———

    /// <summary>语句顶层 ? 的检查：所在函数须注解 Result，操作数须 Result。</summary>
    private TypeId InferTry(Expr t)
    {
        var retIsResult = _fnRet is { } r && Tables.Data(r) is TypeData.Result;
        if (!_inFn || !retIsResult)
        {
            Err(t.Span, DiagnosticCode.E3005, "带 ? 的函数须注解为 Result[T, error]");
        }
        return InferTryOperand(t);
    }

    private TypeId InferTryOperand(Expr t)
    {
        if (t is not Expr.Try tryE) return TypeTables.Unknown;
        var op = Infer(tryE.Operand);
        switch (Tables.Data(op))
        {
            case TypeData.Result r:
                return r.Ok;
            case TypeData.Any or TypeData.Unknown or TypeData.Invalid:
                return TypeTables.Unknown;
            case TypeData.Option:
                {
                    var ts = Ops.TypeStr(Tables, op);
                    Err(tryE.Span, DiagnosticCode.E3008,
                        $"? 的操作数须为 Result，得到 {ts}（Option 不可用 ?）");
                    return TypeTables.Invalid;
                }
            default:
                {
                    var ts = Ops.TypeStr(Tables, op);
                    Err(tryE.Span, DiagnosticCode.E3008, $"? 的操作数须为 Result，得到 {ts}");
                    return TypeTables.Invalid;
                }
        }
    }

    // ———— 字段与索引———

    /// <summary>字段访问类型检查（`&amp;T` 自动解引用，与 interp PtrVal 语义一致）。</summary>
    private TypeId FieldType(TypeId objTy, string name, Span span)
    {
        var ty = Ops.Deref(Tables, objTy);
        switch (Tables.Data(ty))
        {
            case TypeData.Struct def:
                {
                    var found = Tables.GetStruct(def.Def).Fields.FirstOrDefault(f => f.Name == name);
                    if (found is not null)
                    {
                        return found.Ty;
                    }
                    var sname = Tables.GetStruct(def.Def).Name;
                    Err(span, DiagnosticCode.E3004, $"{sname} 没有字段 {name}");
                    return TypeTables.Invalid;
                }
            case TypeData.Error:
                if (name == TypeTables.ErrorMsgField)
                {
                    return TypeTables.Str;
                }
                Err(span, DiagnosticCode.E3004, "error 只有 msg 字段");
                return TypeTables.Invalid;
            default:
                if (!Ops.IsConcrete(Tables, ty))
                {
                    return TypeTables.Unknown;
                }
                var ts = Ops.TypeStr(Tables, ty);
                Err(span, DiagnosticCode.E3004, $"{ts} 没有字段 {name}");
                return TypeTables.Invalid;
        }
    }

    /// <summary>索引读取的元素类型。</summary>
    private TypeId IndexElemTy(TypeId objTy, Span span)
    {
        var ty = Ops.Deref(Tables, objTy);
        switch (Tables.Data(ty))
        {
            case TypeData.Slice s: return s.Elem;
            case TypeData.Map m: return m.Val;
            case TypeData.Str: return TypeTables.Str;
            case TypeData.Any or TypeData.Unknown or TypeData.Invalid: return TypeTables.Unknown;
            default:
                {
                    var ts = Ops.TypeStr(Tables, ty);
                    Err(span, DiagnosticCode.E3001, $"{ts} 不可索引");
                    return TypeTables.Invalid;
                }
        }
    }

    /// <summary>索引类型检查：容器须 int 下标；映射键限 int/string/bool。</summary>
    private void CheckIndexKey(TypeId objTy, TypeId idxTy, Span span)
    {
        if (!Ops.IsConcrete(Tables, idxTy)) return;
        var ty = Ops.Deref(Tables, objTy);
        var idxBad = Tables.Data(idxTy) is not (TypeData.Int or TypeData.Str or TypeData.Bool);
        switch (Tables.Data(ty))
        {
            case TypeData.Slice or TypeData.Str:
                if (Tables.Data(idxTy) is not TypeData.Int)
                {
                    var is_ = Ops.TypeStr(Tables, idxTy);
                    Err(span, DiagnosticCode.E3013, $"索引须为 int，得到 {is_}");
                }
                break;
            case TypeData.Map when idxBad:
                {
                    var is_ = Ops.TypeStr(Tables, idxTy);
                    Err(span, DiagnosticCode.E3014, $"映射键须为 int/string/bool，得到 {is_}");
                    break;
                }
        }
    }

    // ———— 函数字面量———

    private TypeId InferFnLit(Expr e)
    {
        if (e is not Expr.FnLit fn) return TypeTables.Unknown;
        var savedScopes = _scopes;
        _scopes = [[]];
        var savedRet = _fnRet;
        var savedInFn = _inFn;
        var tps = fn.TypeParams;
        _fnRet = fn.Ret is not null ? Anno.ResolveAnnoEmit(this, fn.Ret, tps) : null;
        _inFn = true;
        var ps = new List<TypeId>();
        foreach (var p in fn.Params)
        {
            if (p.Typ is null)
            {
                Err(fn.Span, DiagnosticCode.E3007, $"函数字面量参数 {p.Name} 缺少类型注解");
                ps.Add(TypeTables.Unknown);
                DefineType(p.Name, TypeTables.Unknown);
            }
            else
            {
                var pt = Anno.ResolveAnnoEmit(this, p.Typ, tps);
                ps.Add(pt);
                DefineType(p.Name, pt);
            }
        }
        foreach (var s in fn.Body)
        {
            WalkStmt(s);
        }
        var r = fn.Ret is null ? TypeTables.Unknown : Anno.ResolveAnnoEmit(this, fn.Ret, tps);
        _scopes = savedScopes;
        _fnRet = savedRet;
        _inFn = savedInFn;
        return Tables.Intern(new TypeData.Fn(ps, r));
    }
}
