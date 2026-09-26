using Lang.Core;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Semantic;

/// <summary>解析产物（checker 消费；对外经 Analysis 查询视图暴露）。</summary>
internal sealed class ResolveOutput
{
    public required SymbolArena Arena { get; init; }
    public required NodeMap<SymbolId> Refs { get; init; }
    public required NodeMap<SymbolId> PatRefs { get; init; }
    public required NodeMap<SymbolId> DeclSyms { get; init; }
    public required Dictionary<SymbolId, List<SymbolId>> FnParams { get; init; }
    public required Dictionary<string, SymbolId> BuiltinMap { get; init; }
    public required Dictionary<string, SymbolId> TopTypes { get; init; }
    public required TypeTables Tables { get; set; }
    public required List<Diagnostic> Diags { get; init; }
}

/// <summary>
/// 名字解析（specs/name-resolution.md NAME-SCOPE/SHADOW/DUP/UNDEF/CTOR/RECV/MUT/PLACE/NEST/MERGE）。
/// 多遍扫描，结构与 TS/Go 证据同构：预载 → 收集 struct/enum 符号 → 顶层类型 def 表
/// 注册（字段/载荷注解静默解析，前向类型引用可见）→ 收集 fn/extern/method 符号 →
/// 顶层绑定依序解析（与 interp 顺序执行一致，前向引用全局即 E2002）→ 函数体最后走
/// （函数体对全局的可见性与声明顺序无关）。
/// 命名空间三分：值（fn/绑定/参数，作用域栈）、类型（struct/enum，作用域栈）、
/// 构造器（全局唯一，含预载 Some/None/Ok/Err）。
/// 查名顺序与解释器运行时一致：作用域栈 → 构造器 → 内建。
/// </summary>
internal sealed class Resolver : ITypeCtx
{
    public readonly SymbolArena Arena = new();
    public readonly List<Diagnostic> Diags = [];
    public readonly Dictionary<string, SymbolId> BuiltinMap = [];
    public Dictionary<string, SymbolId> TopTypes = [];
    public TypeTables Tables { get; } = new();

    private readonly NodeMap<SymbolId> _refs = new();
    private readonly NodeMap<SymbolId> _patRefs = new();
    private readonly NodeMap<SymbolId> _declSyms = new();
    private readonly Dictionary<SymbolId, List<SymbolId>> _fnParams = [];

    /// <summary>值命名空间：fn/绑定/参数。</summary>
    private List<Dictionary<string, SymbolId>> _scopes = [];

    /// <summary>类型命名空间：struct/enum。</summary>
    private List<Dictionary<string, SymbolId>> _typeScopes = [];

    /// <summary>构造器全局命名空间（含预载）。</summary>
    private readonly Dictionary<string, SymbolId> _ctorScope = [];

    private uint _loopDepth;
    private uint _fnDepth;

    internal NodeMap<SymbolId> Refs => _refs;

    internal NodeMap<SymbolId> PatRefs => _patRefs;

    internal NodeMap<SymbolId> DeclSyms => _declSyms;

    internal Dictionary<SymbolId, List<SymbolId>> FnParams => _fnParams;

    /// <summary>初始化根作用域（预载完成后、驱动前调用）。</summary>
    internal void ResetRootScopes()
    {
        _scopes = [[]];
        _typeScopes = [[]];
    }

    internal void Run(IReadOnlyList<Stmt> prog)
    {
        CollectTypes(prog);
        RegisterTopDefs(prog);
        CollectFns(prog);
        BindGlobals(prog);
        BindFns(prog);
    }

    /// <summary>顶层值命名空间快照（fns + 全局绑定；与证据 top_types 克隆口径一致）。</summary>
    internal Dictionary<string, SymbolId> TopTypesSnapshot() => new(_scopes[0]);

    SymbolArena ITypeCtx.Arena => Arena;

    void ITypeCtx.Emit(DiagnosticCode code, string msg)
    {
        // 注解解析的诊断无节点锚（line 0）
        Diags.Add(Diagnostic.New(code, msg));
    }

    SymbolId? ITypeCtx.LookupNamed(string name) => LookupTypeSym(name);

    private void Err(Span span, DiagnosticCode code, string msg)
    {
        // v0.4 行级锚：line 取节点首行，column 恒 1
        Diags.Add(Diagnostic.New(code, msg, Diagnostic.AtLine(span.Source, span.Start.Line)));
    }

    private void PushScope()
    {
        _scopes.Add([]);
        _typeScopes.Add([]);
    }

    private void PopScope()
    {
        _scopes.RemoveAt(_scopes.Count - 1);
        _typeScopes.RemoveAt(_typeScopes.Count - 1);
    }

    /// <summary>当前作用域定义；重名即 E2001 且保留先者（遮蔽跨作用域合法，不在此列）。</summary>
    private void Define(string name, Span span, SymbolKind kind, SymbolId id)
    {
        var top = _scopes[^1];
        if (top.ContainsKey(name))
        {
            Err(span, DiagnosticCode.E2001, $"{kind.KindName()} {name} 在此作用域重复定义");
            return;
        }
        top.Add(name, id);
    }

    /// <summary>构造器全局命名空间定义；重名即 E2003（保留先者）。</summary>
    private void DefineCtor(string name, Span span, SymbolId id)
    {
        if (_ctorScope.TryGetValue(name, out var old))
        {
            var oldSym = Arena.Get(old);
            var owner = oldSym.IsBuiltin ? "内建" : oldSym.OwnerName;
            Err(span, DiagnosticCode.E2003, $"构造器 {name} 重名（已被 {owner} 使用）");
            return;
        }
        _ctorScope.Add(name, id);
    }

    private SymbolId? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var id)) return id;
        }
        return null;
    }

    private SymbolId? LookupCtor(string name) => _ctorScope.TryGetValue(name, out var id) ? id : null;

    private SymbolId? LookupTop(string name) =>
        _scopes.Count > 0 && _scopes[0].TryGetValue(name, out var id) ? id : null;

    /// <summary>类型命名空间查名（struct/enum；struct_lit 引用用，最内作用域优先）。</summary>
    private SymbolId? LookupType(string name)
    {
        for (var i = _typeScopes.Count - 1; i >= 0; i--)
        {
            if (_typeScopes[i].TryGetValue(name, out var id)) return id;
        }
        return null;
    }

    /// <summary>注解解析用的类型查名：预载 error → 顶层 → arena 首个同名（嵌套兜底）。</summary>
    private SymbolId? LookupTypeSym(string name)
    {
        if (BuiltinMap.TryGetValue(name, out var bid) && Arena.Get(bid).Kind == SymbolKind.Struct)
        {
            return bid;
        }
        if (TopTypes.TryGetValue(name, out var tid)) return tid;
        return Arena.FirstTypeNamed(name);
    }

    // ———— 预载 ————

    private static Span BuiltinSpan() => Span.Point(new SourceId(0), new Position(0, 0, 0));

    internal void Prelude()
    {
        foreach (var n in Builtin.BuiltinFns)
        {
            var id = Arena.Push(new Symbol
            {
                Kind = SymbolKind.Fn,
                Name = n,
                Span = BuiltinSpan(),
                IsBuiltin = true,
            });
            BuiltinMap.Add(n, id);
        }
        foreach (var n in Builtin.PreludeStructs)
        {
            var id = Arena.Push(new Symbol
            {
                Kind = SymbolKind.Struct,
                Name = n,
                Span = BuiltinSpan(),
                Fields = ["msg"],
                Nominal = new NominalRef.Struct(TypeTables.ErrorDef),
                IsBuiltin = true,
            });
            BuiltinMap.Add(n, id);
        }
        foreach (var (name, owner, idx, arity) in Builtin.PreludeCtors)
        {
            var id = Arena.Push(new Symbol
            {
                Kind = SymbolKind.Ctor,
                Name = name,
                Span = BuiltinSpan(),
                Arity = arity,
                OwnerName = owner,
                CaseIndex = idx,
                IsBuiltin = true,
            });
            _ctorScope.Add(name, id);
        }
    }

    // ———— 第一遍：收集顶层 struct/enum 符号（def 表随后统一注册）———

    private void CollectTypes(IReadOnlyList<Stmt> prog)
    {
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.StructDecl decl:
                    {
                        if (_typeScopes[0].ContainsKey(decl.Name))
                        {
                            Err(decl.Span, DiagnosticCode.E2001, $"struct {decl.Name} 重复定义");
                            continue;
                        }
                        var nominal = new NominalRef.Struct(Tables.RegisterStruct(new StructDef
                        {
                            Name = decl.Name,
                            Fields = [],
                        }));
                        var id = Arena.Push(StructSymbol(decl.Name, decl.Span, decl.Fields, nominal, decl.TypeParams));
                        _typeScopes[0].Add(decl.Name, id);
                        break;
                    }
                case Stmt.EnumDecl decl:
                    {
                        if (_typeScopes[0].ContainsKey(decl.Name))
                        {
                            Err(decl.Span, DiagnosticCode.E2001, $"enum {decl.Name} 重复定义");
                            continue;
                        }
                        var nominal = new NominalRef.Enum(Tables.RegisterEnum(new EnumDef
                        {
                            Name = decl.Name,
                            Cases = [],
                        }));
                        var id = Arena.Push(new Symbol
                        {
                            Kind = SymbolKind.Enum,
                            Name = decl.Name,
                            Span = decl.Span,
                            TypeParams = decl.TypeParams,
                            Nominal = nominal,
                        });
                        _typeScopes[0].Add(decl.Name, id);
                        for (var i = 0; i < decl.Cases.Count; i++)
                        {
                            var c = decl.Cases[i];
                            if (BuiltinMap.ContainsKey(c.Name))
                            {
                                Err(c.Span, DiagnosticCode.E2006, $"case {c.Name} 与内建名冲突");
                                continue;
                            }
                            var cid = Arena.Push(new Symbol
                            {
                                Kind = SymbolKind.Ctor,
                                Name = c.Name,
                                Span = c.Span,
                                Arity = c.Payloads.Count,
                                OwnerName = decl.Name,
                                CaseIndex = (uint)i,
                                PayloadAnnos = c.Payloads.Select(p => (TypeEx?)p).ToList(),
                            });
                            DefineCtor(c.Name, c.Span, cid);
                        }
                        break;
                    }
            }
        }
    }

    private Symbol StructSymbol(string name, Span span, IReadOnlyList<Param> fields, NominalRef nominal,
        IReadOnlyList<string> typeParams) => new()
        {
            Kind = SymbolKind.Struct,
            Name = name,
            Span = span,
            TypeParams = typeParams,
            Fields = fields.Select(f => f.Name).ToList(),
            FieldAnnos = fields.Select(f => f.Typ).ToList(),
            Nominal = nominal,
        };

    /// <summary>第二步：顶层类型 def 表注册（字段/载荷注解静默解析；此时全部顶层类型名可见，
    /// 前向类型引用与证据的惰性解析同效）。</summary>
    private void RegisterTopDefs(IReadOnlyList<Stmt> prog)
    {
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.StructDecl decl:
                    {
                        if (!_typeScopes[0].TryGetValue(decl.Name, out var sid)) continue;
                        var defs = decl.Fields.Select(f => new FieldDef
                        {
                            Name = f.Name,
                            Ty = f.Typ is not null ? Anno.ResolveAnnoQuiet(this, f.Typ, decl.TypeParams) : TypeTables.Unknown,
                        }).ToList();
                        if (Arena.Get(sid).Nominal is NominalRef.Struct st)
                        {
                            Tables.GetStruct(st.Def).Fields = defs;
                        }
                        break;
                    }
                case Stmt.EnumDecl decl:
                    {
                        if (!_typeScopes[0].TryGetValue(decl.Name, out var sid)) continue;
                        var defs = decl.Cases.Select(c => new EnumCaseDef
                        {
                            Name = c.Name,
                            Payloads = c.Payloads
                                .Select(p => Anno.ResolveAnnoQuiet(this, p, decl.TypeParams))
                                .ToList(),
                        }).ToList();
                        if (Arena.Get(sid).Nominal is NominalRef.Enum en)
                        {
                            Tables.GetEnum(en.Def).Cases = defs;
                        }
                        break;
                    }
            }
        }
    }

    // ———— 第二遍：收集 fn/extern/method 符号（体在第四遍走）———

    private void CollectFns(IReadOnlyList<Stmt> prog)
    {
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.FnDecl { Recv: not null } d:
                    CollectMethod(d.Span, d.Name, d.Recv, d.Params, d.Ret);
                    break;
                case Stmt.FnDecl d:
                    {
                        var old = LookupTop(d.Name);
                        if (old is not null)
                        {
                            var oldLine = Arena.Get(old.Value).Span.Start.Line;
                            Err(d.Span, DiagnosticCode.E2001, $"fn {d.Name} 重复定义（先前定义于第 {oldLine} 行）");
                            continue;
                        }
                        var id = Arena.Push(MakeFnSymbol(d, isExtern: false));
                        _scopes[0].Add(d.Name, id);
                        break;
                    }
                case Stmt.Extern ext:
                    {
                        foreach (var f in ext.Fns)
                        {
                            if (f is not Stmt.FnDecl fd) continue;
                            var old = LookupTop(fd.Name);
                            if (old is not null)
                            {
                                var oldLine = Arena.Get(old.Value).Span.Start.Line;
                                Err(ext.Span, DiagnosticCode.E2001, $"fn {fd.Name} 重复定义（先前定义于第 {oldLine} 行）");
                                continue;
                            }
                            var id = Arena.Push(MakeFnSymbol(fd, isExtern: true));
                            _scopes[0].Add(fd.Name, id);
                        }
                        break;
                    }
            }
        }
    }

    private Symbol MakeFnSymbol(Stmt.FnDecl d, bool isExtern) => new()
    {
        Kind = SymbolKind.Fn,
        Name = d.Name,
        Span = d.Span,
        TypeEx = d.Ret,
        ParamAnnos = d.Params.Select(p => p.Typ).ToList(),
        ParamNames = d.Params.Select(p => p.Name).ToList(),
        Arity = d.Params.Count,
        TypeParams = d.TypeParams,
        IsExtern = isExtern,
    };

    private void CollectMethod(Span span, string name, Param recv, IReadOnlyList<Param> params_, TypeEx? ret)
    {
        if (recv.Typ is not TypeEx.Named rt)
        {
            Err(span, DiagnosticCode.E2004, "方法接收者须为具名 struct 类型");
            return;
        }
        SymbolId? st = _typeScopes[0].TryGetValue(rt.Name, out var found) ? found : null;
        if (st is null || Arena.Get(st.Value).Kind != SymbolKind.Struct)
        {
            Err(span, DiagnosticCode.E2004, $"为未知 struct {rt.Name} 定义方法");
            return;
        }
        var stSym = Arena.Get(st.Value);
        if (stSym.Methods.ContainsKey(name))
        {
            Err(span, DiagnosticCode.E2001, $"方法 {name} 在 struct {rt.Name} 上重复定义");
            return;
        }
        var id = Arena.Push(new Symbol
        {
            Kind = SymbolKind.Method,
            Name = name,
            Span = span,
            TypeEx = ret,
            ParamAnnos = params_.Select(p => p.Typ).ToList(),
            ParamNames = params_.Select(p => p.Name).ToList(),
            RecvName = rt.Name,
            Arity = params_.Count,
            OwnerName = rt.Name,
        });
        stSym.Methods.Add(name, id);
    }

    // ———— 第三遍：顶层绑定依序解析———

    private void BindGlobals(IReadOnlyList<Stmt> prog)
    {
        foreach (var s in prog)
        {
            switch (s)
            {
                case Stmt.Let let:
                    {
                        WalkExpr(let.Init);
                        if (let.Name == "_") continue;
                        var old = LookupTop(let.Name);
                        if (old is not null)
                        {
                            var oldLine = Arena.Get(old.Value).Span.Start.Line;
                            Err(let.Span, DiagnosticCode.E2001,
                                $"绑定 {let.Name} 在此作用域重复定义（先前定义于第 {oldLine} 行）");
                            continue;
                        }
                        var id = Arena.Push(new Symbol
                        {
                            Kind = SymbolKind.Var,
                            Name = let.Name,
                            Span = let.Span,
                            Mutable = let.Mutable,
                            TypeEx = let.Typ,
                        });
                        _scopes[0].Add(let.Name, id);
                        _declSyms.Set(s, id);
                        break;
                    }
                case Stmt.FnDecl or Stmt.StructDecl or Stmt.EnumDecl or Stmt.Extern or Stmt.Import:
                    break;
                default:
                    WalkStmt(s);
                    break;
            }
        }
    }

    // ———— 第四遍：函数体———

    private void BindFns(IReadOnlyList<Stmt> prog)
    {
        foreach (var s in prog)
        {
            if (s is Stmt.FnDecl { Recv: null } d)
            {
                SymbolId? sym = _scopes[0].TryGetValue(d.Name, out var found) ? found : null;
                if (sym is not null)
                {
                    _declSyms.Set(s, sym.Value);
                }
                FnScope(d, sym);
            }
        }
        foreach (var s in prog)
        {
            if (s is Stmt.FnDecl { Recv: { Typ: TypeEx.Named rt } } d)
            {
                if (!_typeScopes[0].TryGetValue(rt.Name, out var st)) continue;
                if (Arena.Get(st).Kind != SymbolKind.Struct) continue;
                if (!Arena.Get(st).Methods.TryGetValue(d.Name, out var msym)) continue;
                _declSyms.Set(s, msym);
                FnScope(d, msym);
            }
        }
    }

    private static Symbol ParamSymbol(string name, Span span, TypeEx? typ) => new()
    {
        Kind = SymbolKind.Param,
        Name = name,
        Span = span,
        TypeEx = typ,
    };

    /// <summary>函数体作用域：接收者 + 参数，fn_depth 保护 return。
    /// 参数符号按序记入 fn_params[fn 符号]（接收者在前；`_` 跳过），供 checker/HIR 对位取类型。</summary>
    private void FnScope(Stmt.FnDecl d, SymbolId? sym)
    {
        PushScope();
        var psyms = new List<SymbolId>();
        if (d.Recv is not null)
        {
            var id = Arena.Push(ParamSymbol(d.Recv.Name, d.Recv.Span, d.Recv.Typ));
            Define(d.Recv.Name, d.Recv.Span, SymbolKind.Param, id);
            psyms.Add(id);
        }
        foreach (var p in d.Params)
        {
            if (p.Name == "_") continue;
            var id = Arena.Push(ParamSymbol(p.Name, p.Span, p.Typ));
            Define(p.Name, p.Span, SymbolKind.Param, id);
            psyms.Add(id);
        }
        if (sym is not null)
        {
            _fnParams[sym.Value] = psyms;
        }
        _fnDepth++;
        foreach (var st in d.Body)
        {
            WalkStmt(st);
        }
        _fnDepth--;
        PopScope();
    }

    private void WalkStmt(Stmt s)
    {
        switch (s)
        {
            case Stmt.Let let:
                {
                    WalkExpr(let.Init);
                    if (let.Name == "_") return;
                    var id = Arena.Push(new Symbol
                    {
                        Kind = SymbolKind.Var,
                        Name = let.Name,
                        Span = let.Span,
                        Mutable = let.Mutable,
                        TypeEx = let.Typ,
                    });
                    _declSyms.Set(s, id);
                    Define(let.Name, let.Span, SymbolKind.Var, id);
                    break;
                }
            case Stmt.Assign assign:
                {
                    if (assign.Target is Expr.Ident ident)
                    {
                        var sym = Lookup(ident.Name);
                        if (sym is null)
                        {
                            Err(ident.Span, DiagnosticCode.E2002, $"未定义变量 {ident.Name}");
                        }
                        else if (!Arena.Get(sym.Value).Mutable)
                        {
                            Err(assign.Span, DiagnosticCode.E2005, $"{ident.Name} 由 let 绑定，不可重新赋值");
                        }
                        else
                        {
                            _refs.Set(assign.Target, sym.Value);
                        }
                    }
                    else
                    {
                        AssignTarget(assign.Target);
                    }
                    WalkExpr(assign.Value);
                    break;
                }
            case Stmt.ExprStmt e:
                WalkExpr(e.Expr);
                break;
            case Stmt.Return ret:
                {
                    if (_fnDepth == 0)
                    {
                        Err(ret.Span, DiagnosticCode.E2008, "return 出现在函数之外");
                    }
                    if (ret.Value is not null)
                    {
                        WalkExpr(ret.Value);
                    }
                    break;
                }
            case Stmt.If ifStmt:
                {
                    WalkExpr(ifStmt.Cond);
                    Block(ifStmt.ThenBody);
                    if (ifStmt.ElseBody is not null)
                    {
                        Block(ifStmt.ElseBody);
                    }
                    break;
                }
            case Stmt.While w:
                {
                    WalkExpr(w.Cond);
                    _loopDepth++;
                    Block(w.Body);
                    _loopDepth--;
                    break;
                }
            case Stmt.ForIn forIn:
                {
                    WalkExpr(forIn.Iter);
                    PushScope();
                    if (forIn.Variable != "_")
                    {
                        var id = Arena.Push(new Symbol
                        {
                            Kind = SymbolKind.Var,
                            Name = forIn.Variable,
                            Span = forIn.Span,
                        });
                        _declSyms.Set(s, id);
                        Define(forIn.Variable, forIn.Span, SymbolKind.Var, id);
                    }
                    _loopDepth++;
                    foreach (var b in forIn.Body)
                    {
                        WalkStmt(b);
                    }
                    _loopDepth--;
                    PopScope();
                    break;
                }
            case Stmt.Break br:
                if (_loopDepth == 0)
                {
                    Err(br.Span, DiagnosticCode.E2007, "break 出现在循环之外");
                }
                break;
            case Stmt.Continue cont:
                if (_loopDepth == 0)
                {
                    Err(cont.Span, DiagnosticCode.E2007, "continue 出现在循环之外");
                }
                break;
            case Stmt.FnDecl { Recv: not null }:
                break; // 方法体在 BindFns 统一走
            case Stmt.FnDecl d:
                {
                    // 嵌套 fn：定义进当前作用域
                    var id = Arena.Push(MakeFnSymbol(d, isExtern: false));
                    _declSyms.Set(s, id);
                    Define(d.Name, d.Span, SymbolKind.Fn, id);
                    FnScope(d, id);
                    break;
                }
            case Stmt.StructDecl decl:
                NestedStructDecl(decl);
                break;
            case Stmt.EnumDecl decl:
                NestedEnumDecl(decl);
                break;
            case Stmt.Extern ext:
                {
                    foreach (var f in ext.Fns)
                    {
                        if (f is not Stmt.FnDecl fd) continue;
                        if (Lookup(fd.Name) is null)
                        {
                            var id = Arena.Push(MakeFnSymbol(fd, isExtern: true));
                            Define(fd.Name, fd.Span, SymbolKind.Fn, id);
                        }
                    }
                    break;
                }
            case Stmt.Go go:
                {
                    if (go.Call is Expr.Call call)
                    {
                        if (call.Callee is Expr.Ident calleeIdent)
                        {
                            var sym = Lookup(calleeIdent.Name);
                            if (sym is not null && Arena.Get(sym.Value).Kind == SymbolKind.Fn)
                            {
                                _refs.Set(call.Callee, sym.Value);
                            }
                            else
                            {
                                Err(calleeIdent.Span, DiagnosticCode.E2002, $"go 的目标须是顶层函数：{calleeIdent.Name}");
                            }
                        }
                        else
                        {
                            Err(go.Span, DiagnosticCode.E2002, "go 的目标须是具名函数调用");
                        }
                        foreach (var a in call.Args)
                        {
                            WalkExpr(a);
                        }
                    }
                    break;
                }
            case Stmt.Import:
                break; // 装载器已展开
        }
    }

    /// <summary>嵌套类型声明：E2010（类型集合须平坦，NAME-NEST-009）；符号仍就地建立。</summary>
    private void NestedStructDecl(Stmt.StructDecl decl)
    {
        Err(decl.Span, DiagnosticCode.E2010, $"嵌套类型声明不支持（struct {decl.Name} 须在顶层声明）");
        var absent = !_typeScopes[^1].ContainsKey(decl.Name);
        if (!absent) return;
        var nominal = new NominalRef.Struct(Tables.RegisterStruct(new StructDef
        {
            Name = decl.Name,
            Fields = [],
        }));
        // 嵌套类型字段注解就地静默解析
        var defs = decl.Fields.Select(f => new FieldDef
        {
            Name = f.Name,
            Ty = f.Typ is not null ? Anno.ResolveAnnoQuiet(this, f.Typ, decl.TypeParams) : TypeTables.Unknown,
        }).ToList();
        if (nominal is NominalRef.Struct st)
        {
            Tables.GetStruct(st.Def).Fields = defs;
        }
        var id = Arena.Push(StructSymbol(decl.Name, decl.Span, decl.Fields, nominal, decl.TypeParams));
        _typeScopes[^1].Add(decl.Name, id);
    }

    private void NestedEnumDecl(Stmt.EnumDecl decl)
    {
        Err(decl.Span, DiagnosticCode.E2010, $"嵌套类型声明不支持（enum {decl.Name} 须在顶层声明）");
        var absent = !_typeScopes[^1].ContainsKey(decl.Name);
        if (!absent) return;
        var nominal = new NominalRef.Enum(Tables.RegisterEnum(new EnumDef
        {
            Name = decl.Name,
            Cases = [],
        }));
        var id = Arena.Push(new Symbol
        {
            Kind = SymbolKind.Enum,
            Name = decl.Name,
            Span = decl.Span,
            TypeParams = decl.TypeParams,
            Nominal = nominal,
        });
        _typeScopes[^1].Add(decl.Name, id);
    }

    private void Block(IReadOnlyList<Stmt> stmts)
    {
        PushScope();
        foreach (var s in stmts)
        {
            WalkStmt(s);
        }
        PopScope();
    }

    /// <summary>字段/索引赋值目标：只走子表达式（形状合法性由解析器保证）。</summary>
    private void AssignTarget(Expr t)
    {
        switch (t)
        {
            case Expr.Field f:
                WalkExpr(f.Obj);
                break;
            case Expr.Index ix:
                WalkExpr(ix.Obj);
                WalkExpr(ix.Idx);
                break;
            default:
                WalkExpr(t);
                break;
        }
    }

    private void WalkExpr(Expr e)
    {
        switch (e)
        {
            case Expr.Ident ident:
                {
                    var sym = Lookup(ident.Name);
                    if (sym is not null)
                    {
                        _refs.Set(e, sym.Value);
                        return;
                    }
                    var ctor = LookupCtor(ident.Name);
                    if (ctor is not null)
                    {
                        _refs.Set(e, ctor.Value);
                        return;
                    }
                    if (BuiltinMap.ContainsKey(ident.Name))
                    {
                        return; // 裸内建引用是否合法由 checker 判定
                    }
                    Err(ident.Span, DiagnosticCode.E2002, $"未定义 {ident.Name}");
                    break;
                }
            case Expr.Unary u:
                WalkExpr(u.Operand);
                break;
            case Expr.Binary b:
                WalkExpr(b.Lhs);
                WalkExpr(b.Rhs);
                break;
            case Expr.Range r:
                WalkExpr(r.Lo);
                WalkExpr(r.Hi);
                break;
            case Expr.Call call:
                {
                    WalkExpr(call.Callee);
                    foreach (var a in call.Args)
                    {
                        WalkExpr(a);
                    }
                    break;
                }
            case Expr.Index ix:
                WalkExpr(ix.Obj);
                WalkExpr(ix.Idx);
                break;
            case Expr.Slice sl:
                WalkExpr(sl.Obj);
                WalkExpr(sl.Lo);
                WalkExpr(sl.Hi);
                break;
            case Expr.Field f:
                WalkExpr(f.Obj);
                break;
            case Expr.Try t:
                WalkExpr(t.Operand);
                break;
            case Expr.StructLit lit:
                {
                    var sym = LookupType(lit.TypeName);
                    if (sym is not null)
                    {
                        _refs.Set(e, sym.Value);
                    }
                    else
                    {
                        Err(lit.Span, DiagnosticCode.E2002, $"未知 struct {lit.TypeName}");
                    }
                    foreach (var f in lit.Fields)
                    {
                        WalkExpr(f.Value);
                    }
                    break;
                }
            case Expr.MapLit map:
                {
                    foreach (var item in map.Items)
                    {
                        WalkExpr(item.Key);
                        WalkExpr(item.Value);
                    }
                    break;
                }
            case Expr.ArrLit arr:
                {
                    foreach (var el in arr.Items)
                    {
                        WalkExpr(el);
                    }
                    break;
                }
            case Expr.Match m:
                {
                    WalkExpr(m.Scrutinee);
                    foreach (var arm in m.Arms)
                    {
                        PushScope();
                        WalkPat(arm.Pat);
                        WalkExpr(arm.Body);
                        PopScope();
                    }
                    break;
                }
            case Expr.FnLit fn:
                {
                    PushScope();
                    foreach (var p in fn.Params)
                    {
                        if (p.Name != "_")
                        {
                            var id = Arena.Push(ParamSymbol(p.Name, p.Span, p.Typ));
                            Define(p.Name, p.Span, SymbolKind.Param, id);
                        }
                    }
                    _fnDepth++;
                    foreach (var st in fn.Body)
                    {
                        WalkStmt(st);
                    }
                    _fnDepth--;
                    PopScope();
                    break;
                }
            case Expr.IntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit:
                break;
        }
    }

    private void WalkPat(Pat p)
    {
        switch (p)
        {
            case Pat.Wild:
                break;
            case Pat.Bind bind:
                {
                    var id = Arena.Push(new Symbol
                    {
                        Kind = SymbolKind.Var,
                        Name = bind.Name,
                        Span = bind.Span,
                    });
                    _patRefs.Set(p, id);
                    Define(bind.Name, bind.Span, SymbolKind.Var, id);
                    break;
                }
            case Pat.Lit lit:
                WalkExpr(lit.Value);
                break;
            case Pat.Case case_:
                {
                    var sym = LookupCtor(case_.Name);
                    if (sym is not null)
                    {
                        _patRefs.Set(p, sym.Value);
                    }
                    else
                    {
                        Err(case_.Span, DiagnosticCode.E2002, $"未知构造器 {case_.Name}");
                    }
                    foreach (var sp in case_.Subs)
                    {
                        WalkPat(sp);
                    }
                    break;
                }
        }
    }
}

internal static class ResolverDriver
{
    /// <summary>借用驱动整个解析（prog 为装载器合并后的语句序列，NAME-MERGE-010）。</summary>
    public static ResolveOutput ResolveProgram(IReadOnlyList<Stmt> prog, SourceId _source)
    {
        var r = new Resolver();
        r.Prelude();
        r.ResetRootScopes();
        r.Run(prog);
        return new ResolveOutput
        {
            Arena = r.Arena,
            Refs = r.Refs,
            PatRefs = r.PatRefs,
            DeclSyms = r.DeclSyms,
            FnParams = r.FnParams,
            BuiltinMap = r.BuiltinMap,
            TopTypes = r.TopTypesSnapshot(),
            Tables = r.Tables,
            Diags = r.Diags,
        };
    }
}
