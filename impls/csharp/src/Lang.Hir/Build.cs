using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;
using AstAssignOp = Lang.Syntax.AssignOp;
using AstBinaryOp = Lang.Syntax.BinaryOp;
using AstUnaryOp = Lang.Syntax.UnaryOp;
using SemMatchKind = Lang.Semantic.MatchKind;

namespace Lang.Hir;

/// <summary>HIR 构建失败（内部不变量破坏：语义已通过却出现不可能的 AST 形状）。</summary>
public sealed record BuildError(string Message)
{
    public override string ToString() => $"HIR 构建内部错误: {Message}";
}

/// <summary>AST + 语义产物 → Typed HIR 的静态门面（任务书入口名 BuildHir）。</summary>
public static class HirBuild
{
    /// <summary>
    /// 下降整个程序。前置条件：<paramref name="analysis"/> 由同一棵 <paramref name="stmts"/>
    /// 分析而来且无诊断（跨树查询返回 null、带诊断时有事实缺口——两者都只产生占位
    /// HIR，不做补救）。构建失败 = 内部不变量破坏，以受控错误返回（不 throw 表达）。
    /// </summary>
    public static LowerResult<HirProg> BuildHir(IReadOnlyList<Stmt> stmts, Analysis analysis) =>
        new HirBuilder(analysis).Lower(stmts);
}

/// <summary><see cref="ILowerHir{TAst}"/> 的语义驱动实现：<c>TAst = 合并后语句序列</code>。</summary>
public sealed class HirBuilder(Analysis analysis) : ILowerHir<IReadOnlyList<Stmt>>
{
    private readonly Analysis _a = analysis;
    private uint _next;
    /// <summary>顶层绑定名（fnlit 捕获排除全局——闭包体经全局槽访问）。</summary>
    private readonly HashSet<string> _globalNames = new(StringComparer.Ordinal);
    /// <summary>当前函数（fn/fnlit）内的绑定名作用域栈。</summary>
    private List<List<string>> _scopes = [[]];
    /// <summary>当前函数体内收集的自由名（fnlit 捕获；fn/全局层收集后丢弃）。</summary>
    private readonly List<string> _free = [];

    /// <summary>下降整个程序。要求确定性：同输入同输出（逐字段相等）。</summary>
    public LowerResult<HirProg> Lower(IReadOnlyList<Stmt> ast)
    {
        try
        {
            return LowerResult<HirProg>.Ok(Build(ast));
        }
        catch (BuildAbort e)
        {
            return LowerResult<HirProg>.Fail(e.Message);
        }
    }

    /// <summary>切片入口：与 <see cref="Lower"/> 同一构建管线（compile 便利入口消费）。</summary>
    public LowerResult<HirProg> LowerSlice(IReadOnlyList<Stmt> stmts) => Lower(stmts);

    /// <summary>内部控制流短路（公开面不 throw，边界转受控错误）。</summary>
    private sealed class BuildAbort(string message) : Exception(message);

    private BuildAbort Abort(string message) => throw new BuildAbort(message);

    private BuildAbort Abort(string message, Span span) =>
        throw new BuildAbort($"{message}（span {span.Start.Line}:{span.Start.Column}）");

    private NodeInfo Node(Span span)
    {
        _next++;
        return NodeInfo.New(new NodeId(_next), span);
    }

    private HirProg Build(IReadOnlyList<Stmt> prog)
    {
        foreach (var s in prog)
        {
            if (s is Stmt.Let { Name: not "_" } let0)
            {
                _globalNames.Add(let0.Name);
            }
        }
        // 1. struct/enum（声明序交错，与 TS collectDecls 同一循环）。
        var structs = new List<HirStructDecl>();
        var enums = new List<HirEnumDecl>();
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.StructDecl d: structs.Add(StructDecl(d)); break;
                case Stmt.EnumDecl d: enums.Add(EnumDecl(d)); break;
            }
        }
        // 2. 顶层绑定。
        var globals = new List<HirLet>();
        foreach (var s in prog)
        {
            if (s is Stmt.Let)
            {
                globals.Add(LetStmt(s));
            }
        }
        // 3. 顶层 fn（首个 main）+ extern。
        var fns = new List<HirFn>();
        HirFn? main = null;
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.FnDecl { Recv: null } d:
                    {
                        var f = FnDecl(d);
                        if (f.Name == "main" && main is null)
                        {
                            main = f;
                        }
                        fns.Add(f);
                        break;
                    }
                case Stmt.Extern ext:
                    {
                        foreach (var d in ext.Fns)
                        {
                            var f = FnDecl(d) with { IsExtern = true, ExternHost = ext.Host, Body = [] };
                            // resolver 对 extern 内层 FnDecl 不入 decl_syms（仅顶层作用域持名）——
                            // 按名回查 Fn 符号，保住「HIR 携带符号结论」契约（extern fn 名顶层
                            // 唯一，首个命中确定；A2 记录缺口的搬运面补全，非新推断）。
                            if (f.Meta.Sym is null)
                            {
                                foreach (var sym in _a.Symbols.Items)
                                {
                                    if (sym.Kind == SymbolKind.Fn && sym.Name == f.Name)
                                    {
                                        f = f with { Meta = f.Meta.WithSym(sym.Id) };
                                        break;
                                    }
                                }
                            }
                            fns.Add(f);
                        }
                        break;
                    }
            }
        }
        // 4. 方法。
        foreach (var s in prog)
        {
            if (s is Stmt.FnDecl { Recv: not null } d)
            {
                fns.Add(FnDecl(d));
            }
        }
        return new HirProg(structs, enums, globals, fns, main);
    }

    // ———— 查表助手 ————

    private TypeId? Anno(TypeEx? t, IReadOnlyList<string> tparams) =>
        t is null ? null : LangSemantic.ResolveAnnotation(_a, t, tparams);

    // ———— 声明 ————

    private HirStructDecl StructDecl(Stmt s)
    {
        if (s is not Stmt.StructDecl d)
        {
            throw Abort("struct_decl 期望", s.Span);
        }
        var sym = _a.Symbols.FirstTypeNamed(d.Name);
        var meta = Node(d.Span);
        if (sym is { } sid)
        {
            meta = meta.WithSym(sid);
        }
        // 字段类型取 def 表（resolver 已按声明序静默解析）。
        List<(string Name, TypeId? Ty)> defFields = [];
        if (sym is { } sid2 && _a.Symbols.Get(sid2).Nominal is NominalRef.Struct def)
        {
            foreach (var f in _a.Types.GetStruct(def.Def).Fields)
            {
                defFields.Add((f.Name, f.Ty));
            }
        }
        var fdecls = new List<HirFieldDecl>(d.Fields.Count);
        for (var i = 0; i < d.Fields.Count; i++)
        {
            var f = d.Fields[i];
            var ty = i < defFields.Count ? defFields[i].Ty : null;
            fdecls.Add(new HirFieldDecl(f.Name, ty ?? Anno(f.Typ, d.TypeParams)));
        }
        return new HirStructDecl(meta, d.Name, d.TypeParams, fdecls);
    }

    private HirEnumDecl EnumDecl(Stmt s)
    {
        if (s is not Stmt.EnumDecl d)
        {
            throw Abort("enum_decl 期望", s.Span);
        }
        var sym = _a.Symbols.FirstTypeNamed(d.Name);
        var meta = Node(d.Span);
        if (sym is { } sid)
        {
            meta = meta.WithSym(sid);
        }
        // case 载荷类型取 def 表；case 符号取同 owner 同名构造器（末个命中，与 TS 一致）。
        List<IReadOnlyList<TypeId>> defCases = [];
        if (sym is { } sid2 && _a.Symbols.Get(sid2).Nominal is NominalRef.Enum def)
        {
            foreach (var c in _a.Types.GetEnum(def.Def).Cases)
            {
                defCases.Add(c.Payloads);
            }
        }
        var cdecls = new List<HirEnumCase>(d.Cases.Count);
        for (var i = 0; i < d.Cases.Count; i++)
        {
            var c = d.Cases[i];
            SymbolId? csym = null;
            foreach (var x in _a.Symbols.Items)
            {
                if (x.Kind == SymbolKind.Ctor && x.OwnerName == d.Name && x.Name == c.Name)
                {
                    csym = x.Id; // 末个命中
                }
            }
            IReadOnlyList<TypeId?> payloads;
            if (i < defCases.Count)
            {
                var ps = new List<TypeId?>(defCases[i].Count);
                foreach (var t in defCases[i])
                {
                    ps.Add(t);
                }
                payloads = ps;
            }
            else
            {
                payloads = c.Payloads.Select(p => (TypeId?)Anno(p, d.TypeParams)).ToList();
            }
            cdecls.Add(new HirEnumCase(csym, c.Name, payloads));
        }
        return new HirEnumDecl(meta, d.Name, d.TypeParams, cdecls);
    }

    private HirLet LetStmt(Stmt s)
    {
        if (s is not Stmt.Let let)
        {
            throw Abort("let 期望", s.Span);
        }
        var sym = _a.SymbolOfDecl(s);
        var meta = Node(let.Span);
        if (sym is { } sid)
        {
            meta = meta.WithSym(sid);
            if (_a.SymbolType(sid) is { } t)
            {
                meta = meta.WithType(t);
            }
        }
        return new HirLet(meta, let.Name, let.Mutable, LowerExpr(let.Init));
    }

    private HirFn FnDecl(Stmt s)
    {
        if (s is not Stmt.FnDecl d)
        {
            throw Abort("fn_decl 期望", s.Span);
        }
        var sym = _a.SymbolOfDecl(s);
        var retTy = Anno(d.Ret, d.TypeParams);
        var meta = Node(d.Span);
        if (sym is { } sid)
        {
            meta = meta.WithSym(sid);
        }
        if (retTy is { } rt)
        {
            meta = meta.WithType(rt);
        }
        // 函数边界：新作用域栈，自由名丢弃（具名 fn 不捕获）。
        var savedScopes = _scopes;
        _scopes = [[]];
        var savedFree = new List<string>(_free);
        _free.Clear();
        HirParam? recvParam = d.Recv is null ? null : LowerParam(d.Recv, []);
        if (recvParam is not null)
        {
            BindName(recvParam.Name);
        }
        var fparams = new List<HirParam>(d.Params.Count);
        foreach (var p in d.Params)
        {
            var hp = LowerParam(p, d.TypeParams);
            BindName(hp.Name);
            fparams.Add(hp);
        }
        var hbody = new List<HirStmt>(d.Body.Count);
        foreach (var st in d.Body)
        {
            hbody.Add(StmtLower(st));
        }
        _scopes = savedScopes;
        _free.Clear();
        _free.AddRange(savedFree);
        return new HirFn(meta, d.Name, recvParam, d.TypeParams, fparams, retTy, hbody,
            IsExtern: false, ExternHost: string.Empty);
    }

    private HirParam LowerParam(Param p, IReadOnlyList<string> tparams) =>
        new(p.Name, Anno(p.Typ, tparams));

    // ———— 语句 ————

    private HirStmt StmtLower(Stmt s)
    {
        switch (s)
        {
            case Stmt.Let:
                return new HirStmt.Let(LetStmt(s));
            case Stmt.Assign a:
                {
                    var meta = Node(a.Span);
                    var t = AssignTarget(a.Target);
                    var rhs = LowerExpr(a.Value);
                    return new HirStmt.Assign(new HirAssign(meta, t, MapAssignOp(a.Op), rhs));
                }
            case Stmt.ExprStmt e:
                {
                    var x = LowerExpr(e.Expr);
                    var meta = Node(e.Span);
                    return new HirStmt.Eval(meta, x);
                }
            case Stmt.Return ret:
                {
                    // 与 TS/Go 证据一致：node 先于 val。
                    var meta = Node(ret.Span);
                    var val = ret.Value is null ? null : LowerExpr(ret.Value);
                    return new HirStmt.Return(meta, val);
                }
            case Stmt.If ifStmt:
                {
                    var meta = Node(ifStmt.Span);
                    var c = LowerExpr(ifStmt.Cond);
                    var then = Block(ifStmt.ThenBody);
                    IReadOnlyList<HirStmt> els = ifStmt.ElseBody is null ? [] : Block(ifStmt.ElseBody);
                    return new HirStmt.If(meta, c, then, els);
                }
            case Stmt.While w:
                {
                    var meta = Node(w.Span);
                    var c = LowerExpr(w.Cond);
                    var b = Block(w.Body);
                    return new HirStmt.While(meta, c, b);
                }
            case Stmt.ForIn forIn:
                {
                    var sym = _a.SymbolOfDecl(s);
                    var meta = Node(forIn.Span);
                    if (sym is { } sid)
                    {
                        meta = meta.WithSym(sid);
                        if (_a.SymbolType(sid) is { } t)
                        {
                            meta = meta.WithType(t);
                        }
                    }
                    var it = LowerExpr(forIn.Iter);
                    _scopes.Add([]);
                    if (forIn.Variable != "_")
                    {
                        BindName(forIn.Variable);
                    }
                    var b = Stmts(forIn.Body);
                    _scopes.RemoveAt(_scopes.Count - 1);
                    return new HirStmt.ForIn(meta, forIn.Variable, meta.TypeId, it, b);
                }
            case Stmt.Break br:
                return new HirStmt.Break(Node(br.Span));
            case Stmt.Continue cont:
                return new HirStmt.Continue(Node(cont.Span));
            case Stmt.Go go:
                {
                    var meta = Node(go.Span);
                    if (go.Call is not Expr.Call call)
                    {
                        throw Abort("go 目标须为调用（resolver 已约束）", go.Span);
                    }
                    var func = call.Callee is Expr.Ident ? _a.SymbolOfRef(call.Callee) : null;
                    var hargs = new List<HirExpr>(call.Args.Count);
                    foreach (var a in call.Args)
                    {
                        hargs.Add(LowerExpr(a));
                    }
                    return new HirStmt.Spawn(meta, func, hargs);
                }
            case Stmt.FnDecl:
                {
                    var meta = Node(s.Span);
                    var f = FnDecl(s);
                    return new HirStmt.FnStmt(meta, f);
                }
            // 不可达：结构/枚举/extern/import 由顶层收集处理（函数体内出现已由
            // resolver E2010 拒绝），构建不消费带诊断的程序。
            default:
                throw Abort("函数体内出现声明语句——语义期应已拒绝", s.Span);
        }
    }

    private IReadOnlyList<HirStmt> Stmts(IReadOnlyList<Stmt> list)
    {
        var outp = new List<HirStmt>(list.Count);
        foreach (var s in list)
        {
            outp.Add(StmtLower(s));
        }
        return outp;
    }

    private IReadOnlyList<HirStmt> Block(IReadOnlyList<Stmt> list)
    {
        _scopes.Add([]);
        var outp = Stmts(list);
        _scopes.RemoveAt(_scopes.Count - 1);
        return outp;
    }

    private HirExpr AssignTarget(Expr t)
    {
        switch (t)
        {
            case Expr.Ident ident:
                {
                    var sym = _a.SymbolOfRef(t);
                    var meta = Node(ident.Span);
                    if (sym is { } sid)
                    {
                        meta = meta.WithSym(sid);
                        if (_a.SymbolType(sid) is { } ty)
                        {
                            meta = meta.WithType(ty);
                        }
                    }
                    NoteName(ident.Name, sym);
                    return new HirExpr.LocalRef(meta, ident.Name);
                }
            case Expr.Field field:
                {
                    var meta = Node(field.Span);
                    var o = LowerExpr(field.Obj);
                    return new HirExpr.Field(meta, o, field.Name);
                }
            case Expr.Index index:
                {
                    var meta = Node(index.Span);
                    var o = LowerExpr(index.Obj);
                    var i = LowerExpr(index.Idx);
                    return new HirExpr.Index(meta, o, i);
                }
            default:
                throw Abort("未预期的赋值目标（解析器已约束为 ident/field/index）", t.Span);
        }
    }

    // ———— 表达式 ————

    private HirExpr LowerExpr(Expr e)
    {
        var ty = _a.TypeOf(e);
        switch (e)
        {
            case Expr.IntLit lit:
                return new HirExpr.Lit(Lit(Node(lit.Span), ty, LitKind.Int, lit.Value, 0, "", false));
            case Expr.FloatLit lit:
                return new HirExpr.Lit(Lit(Node(lit.Span), ty, LitKind.Float, 0, lit.Value, "", false));
            case Expr.StrLit lit:
                return new HirExpr.Lit(Lit(Node(lit.Span), ty, LitKind.Str, 0, 0, lit.Value, false));
            case Expr.BoolLit lit:
                return new HirExpr.Lit(Lit(Node(lit.Span), ty, LitKind.Bool, 0, 0, "", lit.Value));

            case Expr.Ident ident:
                {
                    var meta = WithTy(Node(ident.Span), ty);
                    var sym = _a.SymbolOfRef(e);
                    if (sym is { } sid)
                    {
                        meta = meta.WithSym(sid);
                        switch (_a.Symbols.Get(sid).Kind)
                        {
                            case SymbolKind.Fn:
                                return new HirExpr.FnRef(meta, ident.Name);
                            case SymbolKind.Ctor:
                                return new HirExpr.Ctor(meta, sid, []);
                        }
                    }
                    else if (Builtin.IsBuiltinFn(ident.Name))
                    {
                        return new HirExpr.BuiltinRef(meta, ident.Name);
                    }
                    // Var/Param → 局部引用；未定义名（checker 已报）→ LocalRef 占位。
                    NoteName(ident.Name, sym);
                    return new HirExpr.LocalRef(meta, ident.Name);
                }

            case Expr.Unary un:
                {
                    var meta = WithTy(Node(un.Span), ty);
                    var x = LowerExpr(un.Operand);
                    return new HirExpr.Unary(meta, MapUnaryOp(un.Op), x);
                }

            case Expr.Binary bin:
                {
                    var meta = Node(bin.Span);
                    var l = LowerExpr(bin.Lhs);
                    var r = LowerExpr(bin.Rhs);
                    switch (bin.Op)
                    {
                        case AstBinaryOp.AndAnd:
                            return new HirExpr.AndThen(WithTy(meta, TypeTables.Bool), l, r);
                        case AstBinaryOp.OrOr:
                            return new HirExpr.OrElse(WithTy(meta, TypeTables.Bool), l, r);
                        default:
                            return new HirExpr.Binary(WithTy(meta, ty), MapBinaryOp(bin.Op), l, r);
                    }
                }

            case Expr.Range range:
                {
                    var meta = WithTy(Node(range.Span), ty);
                    var l = LowerExpr(range.Lo);
                    var h = LowerExpr(range.Hi);
                    return new HirExpr.Range(meta, l, h);
                }

            case Expr.Call call:
                return Call(e, call.Span, call.Callee, call.Args, ty);

            case Expr.Index index:
                {
                    var meta = WithTy(Node(index.Span), ty);
                    var o = LowerExpr(index.Obj);
                    var i = LowerExpr(index.Idx);
                    return new HirExpr.Index(meta, o, i);
                }

            case Expr.Slice slice:
                {
                    var meta = WithTy(Node(slice.Span), ty);
                    var o = LowerExpr(slice.Obj);
                    var l = LowerExpr(slice.Lo);
                    var h = LowerExpr(slice.Hi);
                    return new HirExpr.Slice(meta, o, l, h);
                }

            case Expr.Field field:
                {
                    var meta = WithTy(Node(field.Span), ty);
                    var o = LowerExpr(field.Obj);
                    return new HirExpr.Field(meta, o, field.Name);
                }

            case Expr.Try tryE:
                {
                    // 类型事实恢复：checker 对 let/return/赋值顶层的 `?` 走 infer_try
                    // 直呼（绕过 expr_ty 记录），Try 节点本身无类型结论。按
                    // infer_try_operand 的同一条规则从操作数的已记录类型恢复
                    // Result 的 ok 侧（A2 记录缺口的搬运面补全，非新推断）。
                    if (ty is null)
                    {
                        var opTy = _a.TypeOf(tryE.Operand);
                        if (opTy is { } ot && _a.Types.Data(ot) is TypeData.Result r)
                        {
                            ty = r.Ok;
                        }
                    }
                    var meta = WithTy(Node(tryE.Span), ty);
                    var x = LowerExpr(tryE.Operand);
                    return new HirExpr.Try(meta, x);
                }

            case Expr.StructLit lit:
                {
                    var meta = WithTy(Node(lit.Span), ty);
                    if (_a.SymbolOfRef(e) is { } sid)
                    {
                        meta = meta.WithSym(sid);
                    }
                    var fitems = new List<HirStructField>(lit.Fields.Count);
                    foreach (var f in lit.Fields)
                    {
                        var v = LowerExpr(f.Value);
                        fitems.Add(new HirStructField(f.Name, v));
                    }
                    return new HirExpr.StructLit(meta, meta.Sym, fitems);
                }

            case Expr.MapLit lit:
                {
                    var meta = WithTy(Node(lit.Span), ty);
                    var outp = new List<HirMapItem>(lit.Items.Count);
                    foreach (var it in lit.Items)
                    {
                        // 逐对 键先值后（EVAL-LITERAL-004）。
                        var k = LowerExpr(it.Key);
                        var v = LowerExpr(it.Value);
                        outp.Add(new HirMapItem(k, v));
                    }
                    return new HirExpr.MapLit(meta, outp);
                }

            case Expr.ArrLit lit:
                {
                    var meta = WithTy(Node(lit.Span), ty);
                    var elems = new List<HirExpr>(lit.Items.Count);
                    foreach (var el in lit.Items)
                    {
                        elems.Add(LowerExpr(el));
                    }
                    return new HirExpr.ArrayLit(meta, elems);
                }

            case Expr.Match match:
                {
                    var meta = WithTy(Node(match.Span), ty);
                    var scrut = LowerExpr(match.Scrutinee);
                    var kind = _a.MatchKindOf(e) ?? SemMatchKind.Literal;
                    var hkind = kind switch
                    {
                        SemMatchKind.Literal => MatchKind.Literal,
                        SemMatchKind.Enum => MatchKind.Enum,
                        SemMatchKind.Result => MatchKind.Result,
                        SemMatchKind.Option => MatchKind.Option,
                        _ => throw Abort("未登记的 MatchKind"),
                    };
                    var harms = new List<HirMatchArm>(match.Arms.Count);
                    foreach (var arm in match.Arms)
                    {
                        harms.Add(new HirMatchArm(LowerPat(arm.Pat), LowerExpr(arm.Body)));
                    }
                    return new HirExpr.Match(meta, scrut, hkind, harms);
                }

            case Expr.FnLit fn:
                {
                    var meta = WithTy(Node(fn.Span), ty);
                    // fnlit 是捕获边界：新作用域栈；自由名即捕获集。
                    var savedScopes = _scopes;
                    _scopes = [[]];
                    var savedFree = new List<string>(_free);
                    _free.Clear();
                    var hparams = new List<HirParam>(fn.Params.Count);
                    foreach (var p in fn.Params)
                    {
                        var hp = LowerParam(p, fn.TypeParams);
                        BindName(hp.Name);
                        hparams.Add(hp);
                    }
                    var hbody = Stmts(fn.Body);
                    var captures = new List<string>(_free);
                    _free.Clear();
                    _scopes = savedScopes;
                    _free.AddRange(savedFree);
                    // 传递捕获：嵌套 fnlit 需要的名字若不在本层绑定，则本层也要捕获。
                    foreach (var n in captures)
                    {
                        if (!Bound(n) && !_globalNames.Contains(n) && !_free.Contains(n))
                        {
                            _free.Add(n);
                        }
                    }
                    var hret = Anno(fn.Ret, fn.TypeParams);
                    return new HirExpr.FnLit(meta, fn.TypeParams, hparams, hret, hbody, captures);
                }

            default:
                throw Abort("未登记的 Expr 变体", e.Span);
        }
    }

    private static HirLit Lit(NodeInfo meta, TypeId? ty, LitKind kind, long i64, double f64, string s, bool b)
    {
        if (ty is { } t)
        {
            meta = meta.WithType(t);
        }
        return new HirLit(meta, kind, i64, f64, s, b);
    }

    private static NodeInfo WithTy(NodeInfo meta, TypeId? ty) =>
        ty is { } t ? meta.WithType(t) : meta;

    /// <summary>调用脱糖（TS call() 同构）：构造器 / 内建 / 用户 fn / 方法 / 函数值。</summary>
    private HirExpr Call(Expr _e, Span span, Expr callee, IReadOnlyList<Expr> args, TypeId? ty)
    {
        if (callee is Expr.Ident { Name: var name } ident)
        {
            var sym = _a.SymbolOfRef(callee);
            if (sym is { } sid)
            {
                var kind = _a.Symbols.Get(sid).Kind;
                if (kind == SymbolKind.Ctor)
                {
                    var meta = Node(span);
                    if (ty is { } t1)
                    {
                        meta = meta.WithSym(sid).WithType(t1);
                    }
                    else
                    {
                        meta = meta.WithSym(sid);
                    }
                    var hargs = new List<HirExpr>(args.Count);
                    foreach (var a in args)
                    {
                        hargs.Add(LowerExpr(a));
                    }
                    return new HirExpr.Ctor(meta, sid, hargs);
                }
                if (kind == SymbolKind.Fn)
                {
                    var meta = Node(span);
                    if (ty is { } t2)
                    {
                        meta = meta.WithSym(sid).WithType(t2);
                    }
                    else
                    {
                        meta = meta.WithSym(sid);
                    }
                    var hargs = new List<HirExpr>(args.Count);
                    foreach (var a in args)
                    {
                        hargs.Add(LowerExpr(a));
                    }
                    return new HirExpr.Call(meta, sid, null, null, hargs);
                }
            }
            if (sym is null && Builtin.IsBuiltinFn(name))
            {
                var meta = WithTy(Node(span), ty);
                var hargs = new List<HirExpr>(args.Count);
                foreach (var a in args)
                {
                    hargs.Add(LowerExpr(a));
                }
                return new HirExpr.Call(meta, null, name, null, hargs);
            }
        }
        if (callee is Expr.Field fe)
        {
            var recv = LowerExpr(fe.Obj);
            var recvTy = HirFacts.TypeOf(recv);
            var derefed = recvTy is { } rt ? Ops.Deref(_a.Types, rt) : TypeTables.Unknown;
            SymbolId? method = null;
            if (_a.Types.Data(derefed) is TypeData.Struct def)
            {
                // struct def → 首个声明符号（arena 创建序，与 checker 同构）。
                SymbolId? st = null;
                foreach (var x in _a.Symbols.Items)
                {
                    if (x.Nominal is NominalRef.Struct sd && sd.Def == def.Def)
                    {
                        st = x.Id;
                        break;
                    }
                }
                if (st is { } stid && _a.Symbols.Get(stid).Methods.TryGetValue(fe.Name, out var m))
                {
                    method = m;
                }
            }
            var meta = Node(span);
            if (method is { } mid)
            {
                meta = meta.WithSym(mid);
            }
            if (ty is { } t)
            {
                meta = meta.WithType(t);
            }
            var hargs = new List<HirExpr>(args.Count);
            foreach (var a in args)
            {
                hargs.Add(LowerExpr(a));
            }
            return new HirExpr.MethodCall(meta, recv, method, method is null ? fe.Name : null, hargs,
                MethodNameSpan(fe.Span, fe.Name));
        }
        // 表达式 callee（函数值）。
        var cmeta = WithTy(Node(span), ty);
        var c = LowerExpr(callee);
        var cargs = new List<HirExpr>(args.Count);
        foreach (var a in args)
        {
            cargs.Add(LowerExpr(a));
        }
        return new HirExpr.Call(cmeta, null, null, c, cargs);
    }

    // ———— 模式 ————

    private HirPat LowerPat(Pat p)
    {
        switch (p)
        {
            case Pat.Wild:
                return new HirPat.Wild();
            case Pat.Bind bind:
                return new HirPat.Bind(bind.Name, _a.PatType(p));
            case Pat.Lit lit:
                {
                    var x = LowerExpr(lit.Value);
                    return x is HirExpr.Lit l
                        ? new HirPat.Lit(l.Value)
                        : throw Abort("字面量模式期望字面量（解析器已约束）", lit.Span);
                }
            case Pat.Case case_:
                {
                    var ctorSym = _a.SymbolOfPat(p);
                    var hsubs = new List<HirPat>(case_.Subs.Count);
                    foreach (var sp in case_.Subs)
                    {
                        hsubs.Add(LowerPat(sp));
                    }
                    return new HirPat.Ctor(ctorSym, hsubs);
                }
            default:
                throw Abort("未登记的 Pat 变体", p.Span);
        }
    }

    // ———— 捕获簿记 ————

    private bool Bound(string name)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Contains(name))
            {
                return true;
            }
        }
        return false;
    }

    private void BindName(string name) => _scopes[^1].Add(name);

    /// <summary>自由名登记：解析到外层函数局部（Var/Param）且不在当前函数作用域、
    /// 非全局 → 记入当前 fnlit 捕获集（首次出现序）。</summary>
    private void NoteName(string name, SymbolId? sym)
    {
        var localLike = sym is { } sid
            && _a.Symbols.Get(sid).Kind is SymbolKind.Var or SymbolKind.Param;
        if (!localLike || Bound(name) || _globalNames.Contains(name))
        {
            return;
        }
        if (!_free.Contains(name))
        {
            _free.Add(name);
        }
    }

    // ———— 算符映射 ————

    private static UnaryOp MapUnaryOp(AstUnaryOp op) => op switch
    {
        AstUnaryOp.Neg => UnaryOp.Neg,
        AstUnaryOp.Not => UnaryOp.Not,
        AstUnaryOp.Ref => UnaryOp.Ref,
        _ => throw new InvalidOperationException($"未登记的 UnaryOp: {op}"),
    };

    private static BinaryOp MapBinaryOp(AstBinaryOp op) => op switch
    {
        AstBinaryOp.Eq => BinaryOp.Eq,
        AstBinaryOp.Ne => BinaryOp.Ne,
        AstBinaryOp.Lt => BinaryOp.Lt,
        AstBinaryOp.Le => BinaryOp.Le,
        AstBinaryOp.Gt => BinaryOp.Gt,
        AstBinaryOp.Ge => BinaryOp.Ge,
        AstBinaryOp.Add => BinaryOp.Add,
        AstBinaryOp.Sub => BinaryOp.Sub,
        AstBinaryOp.Mul => BinaryOp.Mul,
        AstBinaryOp.Div => BinaryOp.Div,
        AstBinaryOp.Rem => BinaryOp.Rem,
        AstBinaryOp.BitAnd => BinaryOp.BitAnd,
        AstBinaryOp.BitOr => BinaryOp.BitOr,
        AstBinaryOp.BitXor => BinaryOp.BitXor,
        AstBinaryOp.Shl => BinaryOp.Shl,
        AstBinaryOp.Shr => BinaryOp.Shr,
        AstBinaryOp.AndAnd or AstBinaryOp.OrOr =>
            throw new InvalidOperationException("AndAnd/OrOr 已在调用点降为 AndThen/OrElse"),
        _ => throw new InvalidOperationException($"未登记的 BinaryOp: {op}"),
    };

    private static AssignOp MapAssignOp(AstAssignOp op) => op switch
    {
        AstAssignOp.Assign => AssignOp.Assign,
        AstAssignOp.AddAssign => AssignOp.AddAssign,
        AstAssignOp.SubAssign => AssignOp.SubAssign,
        AstAssignOp.MulAssign => AssignOp.MulAssign,
        AstAssignOp.DivAssign => AssignOp.DivAssign,
        _ => throw new InvalidOperationException($"未登记的 AssignOp: {op}"),
    };

    /// <summary>方法名的 token 区间（fe.span 终点回退名字码点数；rename 消费）。</summary>
    private static Span MethodNameSpan(Span feSpan, string name)
    {
        var n = (uint)name.CharCount();
        var end = feSpan.End;
        var start = new Position(
            end.Offset - Math.Min(end.Offset, n),
            end.Line,
            end.Column - Math.Min(end.Column, n));
        return new Span(feSpan.Source, start, end);
    }
}

file static class CharCountExt
{
    public static int CharCount(this string s)
    {
        var n = 0;
        foreach (var _ in s)
        {
            n++;
        }
        return n;
    }
}
