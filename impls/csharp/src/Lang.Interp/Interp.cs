using Lang.Core;
using Lang.Runtime;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Interp;

/// <summary>
/// AST 参考解释器（Reference Oracle）：直接按 <c>specs/expressions.md</c> EVAL-* 遍历语法树。
/// 与 IR 解释器（<see cref="IrMachine"/>）**只共享 Machine/Value/内建与本程序集的输出面桥**，
/// 遍历彼此独立——差分价值约束。
///
/// 求值顺序即各 eval 分支内的语句序：
/// - 调用：callee（含方法接收者）先于实参，实参左到右（EVAL-CALL-002）；
/// - 赋值：target 地址组件先于右值（EVAL-ASSIGN-003）；
/// - 索引：obj 先于 idx（EVAL-INDEX-005）；字面量元素/键值对/struct 字段左到右（EVAL-LITERAL-004）；
/// - <c>&amp;&amp;</c>/<c>||</c> 短路（EVAL-SHORTCUT-006）；<c>go</c> 实参立即求值快照（EVAL-GO-007）。
///
/// 引用穿透：读操作数/字段/元素时经 <see cref="Machine.DerefValue"/> 自动解引用
/// （VALUE-REF-003 的使用值面）；<c>&amp;x</c> 产生 <see cref="Value.Ref"/>。
/// 诊断非空拒绝执行；<c>?</c> 的 Err 载荷在函数边界合成 <c>Err</c> 返回值（RESULT-TRY-003）；
/// <c>extern</c> 调用报「仅转译可用」（FFI-001）；宿主内建（jsonParse/jsonStringify）
/// 在路由前拦截（<see cref="InterpFmt.HostBuiltin"/>）。
/// </summary>
public sealed class Interp : TaskExec
{
    /// <summary>语言标准内建名面（与 <see cref="BuiltinRegistry.Core"/> 冻结名单一致；
    /// 一致性由测试 prelude 名单锁定）。</summary>
    public static readonly string[] PreludeBuiltins =
    [
        "println", "print", "len", "str", "int", "float", "keys", "values", "has",
        "push", "assert", "err", "bytes", "char", "makeChan", "send", "recv", "isNone",
        "jsonParse", "jsonStringify",
    ];

    /// <summary>函数定义快照（声明点的引用；AST 节点不可变，直接持有）。</summary>
    private sealed class FuncDef
    {
        public required string Name { get; init; }

        /// <summary>方法接收者参数名（null = 普通函数）。</summary>
        public string? Recv { get; init; }

        public required IReadOnlyList<string> Params { get; init; }

        public required IReadOnlyList<Stmt> Body { get; init; }

        /// <summary>extern 声明桩：调用即报「仅转译可用」（FFI-001）。</summary>
        public bool IsExtern { get; init; }

        public string ExternHost { get; init; } = string.Empty;

        /// <summary>诊断/错误消息里的函数名（匿名 → 「匿名函数」，与证据一致）。</summary>
        public string DisplayName => Name.Length == 0 ? "匿名函数" : Name;
    }

    private sealed class CtorEntry(EnumDefId def, uint c, int arity)
    {
        public EnumDefId Def { get; } = def;

        public uint Case { get; } = c;

        public int Arity { get; } = arity;
    }

    private sealed class MethodEntry(FunctionId func, EnvId env)
    {
        public FunctionId Func { get; } = func;

        public EnvId Env { get; } = env;
    }

    /// <summary>
    /// 控制流信号：语句层消化 return/break/continue，<c>?</c> 的 Err 载荷（Propagate）
    /// 一路穿到函数边界合成 <c>Err</c> 返回值。内部控制流短路（私有异常形态，公开面
    /// 仍是 <see cref="RtResult"/> 受控错误；与 A3 lowering 内部同款决策）。
    /// 用户源码错误不走本通道——一律 <see cref="RtError"/> 受控值。
    /// </summary>
    private abstract class Signal : Exception
    {
        private Signal() : base(null!)
        {
        }

        public sealed class BreakSig : Signal
        {
            public static readonly BreakSig Instance = new();
            private BreakSig()
            {
            }
        }

        public sealed class ContinueSig : Signal
        {
            public static readonly ContinueSig Instance = new();
            private ContinueSig()
            {
            }
        }

        public sealed class ReturnSig(Value value) : Signal
        {
            public Value Value { get; } = value;
        }

        public sealed class PropagateSig(Value payload) : Signal
        {
            public Value Payload { get; } = payload;
        }
    }

    private readonly Machine _machine;

    /// <summary>struct 名 → 运行时 def（按声明语句执行序登记，TS 证据同构）。</summary>
    private readonly Dictionary<string, StructDefId> _structs = new(StringComparer.Ordinal);

    /// <summary>enum 名 → 运行时 def。</summary>
    private readonly Dictionary<string, EnumDefId> _enums = new(StringComparer.Ordinal);

    /// <summary>构造器名 → (def, case 序号, 载荷数)。含预载 Some/None/Ok/Err。</summary>
    private readonly Dictionary<string, CtorEntry> _ctors = new(StringComparer.Ordinal);

    /// <summary>(接收者 struct def, 方法名) → 方法函数。</summary>
    private readonly Dictionary<(StructDefId, string), MethodEntry> _methods = new();

    /// <summary>FunctionId → 函数定义（与 machine 的 fn_names 对齐：只经本表登记）。</summary>
    private readonly List<FuncDef> _funcs = [];

    private OutputSink _sink = new StdoutSink();

    /// <summary>新解释器（携带既有运行时状态机；预载定义与核心内建已就绪）。</summary>
    public Interp(Machine machine)
    {
        _machine = machine;
        PreludeCtors();
    }

    /// <summary>预载构造器（RESULT-PRELUDE-001：Some=0/None=1、Ok=0/Err=1，
    /// 与 DefTables 冻结 case 序一致）。</summary>
    private void PreludeCtors()
    {
        var option = _machine.Defs.OptionDef();
        var result = _machine.Defs.ResultDef();
        _ctors["Some"] = new CtorEntry(option, 0, 1);
        _ctors["None"] = new CtorEntry(option, 1, 0);
        _ctors["Ok"] = new CtorEntry(result, 0, 1);
        _ctors["Err"] = new CtorEntry(result, 1, 1);
    }

    /// <summary>顺序登记函数并录入本解释器函数表（与 machine 的 FunctionId 分配对齐）。</summary>
    private FunctionId RegisterFn(FuncDef def)
    {
        var id = _machine.RegisterFunction(def.Name);
        _funcs.Add(def);
        return id;
    }

    private RtResult<FuncDef> FnDef(FunctionId id) =>
        id.Value < (uint)_funcs.Count
            ? RtResult<FuncDef>.Ok(_funcs[(int)id.Value])
            : RtResult<FuncDef>.Fail(RtError.Internal($"FunctionId({id.Value}) 未登记"));

    /// <summary>解释执行入口（AST 参考解释器）。
    ///
    /// <paramref name="analysis"/> 为同一棵 AST 的语义分析产物：含诊断时拒绝执行（诊断是
    /// check 语义的真值，执行只对干净程序负责）。执行顺序与证据一致：先顺序执行顶层语句
    /// （声明/全局初始化），再调用 <c>main</c>（若已定义；跨文件合并序下所有定义已就绪）。</summary>
    public RtResult Run(IReadOnlyList<Stmt> stmts, Analysis analysis, OutputSink sink)
    {
        if (analysis.HasErrors())
        {
            return RtResult.Fail(new RtError(
                RtErrorKind.Scope, $"程序含 {analysis.Diags.Count} 条静态诊断，拒绝执行"));
        }

        _sink = sink;
        var prelude = PreludeBuiltinsIntoRoot();
        if (!prelude.IsOk)
        {
            return prelude;
        }

        try
        {
            var r = ExecStmts(stmts, _machine.Root);
            if (!r.IsOk)
            {
                return r;
            }
        }
        catch (Signal.ReturnSig)
        {
            return RtResult.Fail(RtError.Internal("return 出现在函数之外"));
        }
        catch (Signal.BreakSig)
        {
            return RtResult.Fail(RtError.Internal("break 出现在循环之外"));
        }
        catch (Signal.ContinueSig)
        {
            return RtResult.Fail(RtError.Internal("continue 出现在循环之外"));
        }
        catch (Signal.PropagateSig)
        {
            return RtResult.Fail(RtError.Internal("Err 传播出函数之外（顶层禁用 ?）"));
        }

        // main 在全部顶层语句之后调用
        var hit = _machine.Envs.Lookup(_machine.Root, "main");
        if (!hit.IsOk)
        {
            return hit.Ignore();
        }

        if (hit.Value is not { } owner)
        {
            return RtResult.Ok;
        }

        var got = _machine.Envs.Get(owner.Env, owner.Slot);
        if (!got.IsOk)
        {
            return got.Ignore();
        }

        if (got.Value is Value.Closure c)
        {
            var run = RunFunction(c.Func, c.Env, null, []);
            return run.Ignore();
        }

        return RtResult.Ok;
    }

    /// <summary>预载内建进根环境（值面：<c>println</c> 等作为一等值可传递/打印）。</summary>
    private RtResult PreludeBuiltinsIntoRoot()
    {
        foreach (var name in PreludeBuiltins)
        {
            var id = _machine.Builtins.Lookup(name);
            if (id is null)
            {
                return RtResult.Fail(RtError.Internal($"内建 {name} 未在运行时注册表登记"));
            }

            var d = _machine.Envs.Redefine(_machine.Root, name, new Value.Builtin(id.Value));
            if (!d.IsOk)
            {
                return d.Ignore();
            }
        }

        return RtResult.Ok;
    }

    // ———— 语句 ————

    private RtResult ExecStmts(IReadOnlyList<Stmt> stmts, EnvId env)
    {
        foreach (var s in stmts)
        {
            var r = ExecStmt(s, env);
            if (!r.IsOk)
            {
                return r;
            }
        }

        return RtResult.Ok;
    }

    private RtResult ExecStmt(Stmt s, EnvId env)
    {
        switch (s)
        {
            case Stmt.Let { Name: var name, Mutable: var mut, Init: var init }:
                {
                    var v = Eval(init, env);
                    if (!v.IsOk)
                    {
                        return v.Ignore();
                    }

                    if (name != "_")
                    {
                        return _machine.Envs.Define(env, name, v.Value, mut).Ignore();
                    }

                    return RtResult.Ok;
                }

            case Stmt.Assign { Target: var target, Op: var op, Value: var value }:
                return ExecAssign(target, op, value, env);

            case Stmt.ExprStmt { Expr: var expr }:
                {
                    var v = Eval(expr, env);
                    return v.Ignore();
                }

            case Stmt.Return { Value: var value }:
                {
                    Value v = Value.Unit.Default;
                    if (value is not null)
                    {
                        var r = Eval(value, env);
                        if (!r.IsOk)
                        {
                            return r.Ignore();
                        }

                        v = r.Value;
                    }

                    throw new Signal.ReturnSig(v);
                }

            case Stmt.If { Cond: var cond, ThenBody: var thenBody, ElseBody: var elseBody }:
                {
                    var c = Cond(cond, env);
                    if (!c.IsOk)
                    {
                        return c.Ignore();
                    }

                    if (c.Value)
                    {
                        return ExecStmts(thenBody, _machine.Envs.Child(env));
                    }

                    return elseBody is not null ? ExecStmts(elseBody, _machine.Envs.Child(env)) : RtResult.Ok;
                }

            case Stmt.While { Cond: var cond, Body: var body }:
                {
                    while (true)
                    {
                        var c = Cond(cond, env);
                        if (!c.IsOk)
                        {
                            return c.Ignore();
                        }

                        if (!c.Value)
                        {
                            return RtResult.Ok;
                        }

                        var r = RunLoopBody(body, _machine.Envs.Child(env));
                        if (!r.IsOk)
                        {
                            return r;
                        }
                    }
                }

            case Stmt.ForIn { Variable: var variable, Iter: var iter, Body: var body }:
                return ExecForIn(variable, iter, body, env);

            case Stmt.Break:
                throw Signal.BreakSig.Instance;

            case Stmt.Continue:
                throw Signal.ContinueSig.Instance;

            case Stmt.FnDecl { Name: var name, Recv: var recv, Params: var pars, Body: var body }:
                return DeclFn(name, recv, pars, body, env);

            case Stmt.StructDecl { Name: var name, Fields: var fields }:
                return DeclStruct(name, fields);

            case Stmt.EnumDecl { Name: var name, Cases: var cases }:
                return DeclEnum(name, cases);

            // go = 同步确定子集：实参在此处求值快照后立即执行至返回，返回值丢弃
            // （TASK-SPAWN-002；经 Machine.Spawn 冻结调度面）。
            case Stmt.Go { Call: var call }:
                return ExecSpawn(call, env);

            case Stmt.Import:
                return RtResult.Fail(new RtError(
                    RtErrorKind.Scope, "import 由装载器在合并前展开（不应执行到这里）"));

            case Stmt.Extern { Host: var host, Fns: var fns }:
                return DeclExtern(host, fns, env);

            default:
                return RtResult.Fail(RtError.Internal("未知语句变体（内部不变量）"));
        }
    }

    /// <summary>循环体执行的 break/continue 消化（while/for-in 共用）。</summary>
    private RtResult RunLoopBody(IReadOnlyList<Stmt> body, EnvId benv)
    {
        try
        {
            return ExecStmts(body, benv);
        }
        catch (Signal.BreakSig)
        {
            return RtResult.Ok;
        }
        catch (Signal.ContinueSig)
        {
            return RtResult.Ok;
        }
    }

    /// <summary>条件求值：仅 bool（CTRL-IF-001，无隐式真值转换）。</summary>
    private RtResult<bool> Cond(Expr e, EnvId env)
    {
        var v = Eval(e, env);
        return v.IsOk ? v.Value.Truthy() : RtResult<bool>.Fail(v.Error!);
    }

    private RtResult ExecForIn(string variable, Expr iter, IReadOnlyList<Stmt> body, EnvId env)
    {
        // 迭代对象在循环开始时求值一次并快照元素（CTRL-FORIN-003）：
        // 数组索引序 / 映射插入序键 / 字符串码点 / 区间半开。
        var vr = Eval(iter, env);
        if (!vr.IsOk)
        {
            return vr.Ignore();
        }

        var deref = _machine.DerefValue(vr.Value);
        if (!deref.IsOk)
        {
            return deref.Ignore();
        }

        var itemsR = SnapshotItems(deref.Value);
        if (!itemsR.IsOk)
        {
            return itemsR.Ignore();
        }

        foreach (var item in itemsR.Value)
        {
            // 每迭代新子环境：循环变量每轮重新绑定，闭包捕获互不串扰
            // （CTRL-FORIN-003 / VALUE-CAPTURE-004 的 loop capture 面）。
            var benv = _machine.Envs.Child(env);
            if (variable != "_")
            {
                var d = _machine.Envs.Define(benv, variable, item, mutable: false);
                if (!d.IsOk)
                {
                    return d.Ignore();
                }
            }

            var r = RunLoopBody(body, benv);
            if (!r.IsOk)
            {
                return r;
            }
        }

        return RtResult.Ok;
    }

    /// <summary>迭代元素快照（数组索引序/映射插入序键/字符串码点序/区间半开）。</summary>
    private RtResult<IReadOnlyList<Value>> SnapshotItems(Value v) => v switch
    {
        Value.Slice s => _machine.Heap.Slice(s.Id).Map<IReadOnlyList<Value>>(items => items.ToList()),
        Value.Map m => _machine.Heap.MapKeys(m.Id),
        Value.Str str => RtResult<IReadOnlyList<Value>>.Ok(
            str.V.EnumerateRunes().Select(r => new Value.Str(r.ToString())).ToList()),
        Value.Range r => RtResult<IReadOnlyList<Value>>.Ok(
            r.Hi > r.Lo
                ? Enumerable.Range(0, checked((int)(r.Hi - r.Lo))).Select(i => (Value)new Value.Int(r.Lo + i)).ToList()
                : []),
        var other => RtResult<IReadOnlyList<Value>>.Fail(new RtError(
            RtErrorKind.NotIterable, $"{other.TypeHint()} 不可迭代")),
    };

    private RtResult ExecSpawn(Expr call, EnvId env)
    {
        if (call is Expr.Call { Callee: Expr.Ident ident, Args: var argExprs })
        {
            var hit = _machine.Envs.Lookup(env, ident.Name);
            if (hit.IsOk && hit.Value is { } owner)
            {
                var got = _machine.Envs.Get(owner.Env, owner.Slot);
                if (got.IsOk && got.Value is Value.Closure c)
                {
                    var args = EvalArgs(argExprs, env);
                    if (!args.IsOk)
                    {
                        return args.Ignore();
                    }

                    // go 的目标被 resolver 约束为顶层 fn（EVAL-GO-007/E2002）；
                    // 顶层 fn 闭包的定义处环境恒为根环境。
                    return _machine.Spawn(this, c.Func, args.Value);
                }
            }
        }

        // 兜底：非「顶层具名 fn 调用」形态直接按调用求值（静态面已拒绝，此处只为错误一致）
        var r = Eval(call, env);
        return r.Ignore();
    }

    private RtResult DeclFn(string name, Param? recv, IReadOnlyList<Param> pars, IReadOnlyList<Stmt> body, EnvId env)
    {
        if (recv is not null)
        {
            // 方法：接收者须为已声明 struct（E2004 静态面；运行时兜底报错）
            if (recv.Typ is not TypeEx.Named { Name: var rname })
            {
                return RtResult.Fail(new RtError(
                    RtErrorKind.TypeCast, "方法接收者须为具名 struct 类型"));
            }

            if (!_structs.TryGetValue(rname, out var sdef))
            {
                return RtResult.Fail(new RtError(
                    RtErrorKind.Scope, $"为未知 struct {rname} 定义方法"));
            }

            var fdef = new FuncDef
            {
                Name = name,
                Recv = recv.Name,
                Params = pars.Select(p => p.Name).ToList(),
                Body = body,
            };
            var id = RegisterFn(fdef);
            _methods[(sdef, name)] = new MethodEntry(id, env);
            return RtResult.Ok;
        }

        var plain = new FuncDef
        {
            Name = name,
            Params = pars.Select(p => p.Name).ToList(),
            Body = body,
        };
        var fid = RegisterFn(plain);
        return _machine.Envs.Redefine(env, name, new Value.Closure(fid, env)).Ignore();
    }

    private RtResult DeclExtern(string host, IReadOnlyList<Stmt> fns, EnvId env)
    {
        foreach (var f in fns)
        {
            if (f is not Stmt.FnDecl { Name: var name, Params: var pars })
            {
                return RtResult.Fail(RtError.Internal("extern 签名恒为 FnDecl"));
            }

            var def = new FuncDef
            {
                Name = name,
                Params = pars.Select(p => p.Name).ToList(),
                Body = [],
                IsExtern = true,
                ExternHost = host,
            };
            var id = RegisterFn(def);
            var d = _machine.Envs.Redefine(env, name, new Value.Closure(id, env));
            if (!d.IsOk)
            {
                return d.Ignore();
            }
        }

        return RtResult.Ok;
    }

    private RtResult DeclStruct(string name, IReadOnlyList<Param> fields)
    {
        if (_structs.ContainsKey(name))
        {
            return RtResult.Fail(new RtError(RtErrorKind.Scope, $"struct {name} 重复定义"));
        }

        var fieldNames = fields.Select(f => f.Name).ToList();
        var id = _machine.Defs.RegisterStruct(name, fieldNames);
        _structs[name] = id;
        return RtResult.Ok;
    }

    private RtResult DeclEnum(string name, IReadOnlyList<EnumCase> cases)
    {
        if (_enums.ContainsKey(name))
        {
            return RtResult.Fail(new RtError(RtErrorKind.Scope, $"enum {name} 重复定义"));
        }

        // case 名全局唯一（NAME-CTOR-005）：与既有构造器及内建名冲突即拒
        foreach (var c in cases)
        {
            if (_ctors.ContainsKey(c.Name) || _machine.Builtins.Lookup(c.Name) is not null)
            {
                return RtResult.Fail(new RtError(RtErrorKind.Scope, $"构造器 {c.Name} 重名"));
            }
        }

        var caseNames = cases.Select(c => c.Name).ToList();
        var def = _machine.Defs.RegisterEnum(name, caseNames);
        for (var i = 0; i < cases.Count; i++)
        {
            _ctors[cases[i].Name] = new CtorEntry(def, (uint)i, cases[i].Payloads.Count);
        }

        _enums[name] = def;
        return RtResult.Ok;
    }

    // ———— 表达式（EVAL-*） ————

    /// <summary>「使用值」读取：求值并穿透引用（操作数/条件/实参/接收者的语义面）。</summary>
    private RtResult<Value> DerefEval(Expr e, EnvId env)
    {
        var v = Eval(e, env);
        return v.IsOk ? _machine.DerefValue(v.Value) : v;
    }

    private RtResult<Value> Eval(Expr e, EnvId env)
    {
        switch (e)
        {
            case Expr.IntLit { Value: var v }:
                return RtResult<Value>.Ok(new Value.Int(v));
            case Expr.FloatLit { Value: var v }:
                return RtResult<Value>.Ok(new Value.Float(v));
            case Expr.StrLit { Value: var v }:
                return RtResult<Value>.Ok(new Value.Str(v));
            case Expr.BoolLit { Value: var v }:
                return RtResult<Value>.Ok(new Value.Bool(v));
            case Expr.Ident { Name: var name }:
                return EvalIdent(name, env);
            case Expr.Unary { Op: var op, Operand: var operand }:
                return EvalUnary(op, operand, env);
            case Expr.Binary { Op: var op, Lhs: var lhs, Rhs: var rhs }:
                return EvalBinary(op, lhs, rhs, env);
            case Expr.Range { Lo: var lo, Hi: var hi }:
                {
                    var lov = Eval(lo, env);
                    if (!lov.IsOk)
                    {
                        return lov;
                    }

                    var hiv = Eval(hi, env);
                    if (!hiv.IsOk)
                    {
                        return hiv;
                    }

                    if (lov.Value is not Value.Int a || hiv.Value is not Value.Int b)
                    {
                        return RtResult<Value>.Fail(new RtError(
                            RtErrorKind.TypeCast, "区间两端须为 int"));
                    }

                    return RtResult<Value>.Ok(new Value.Range(a.V, b.V));
                }

            case Expr.Call { Callee: var callee, Args: var args }:
                return EvalCall(callee, args, env);
            case Expr.Index { Obj: var obj, Idx: var idx }:
                {
                    // EVAL-INDEX-005：先 obj 后 idx
                    var ov = DerefEval(obj, env);
                    if (!ov.IsOk)
                    {
                        return ov;
                    }

                    var iv = Eval(idx, env);
                    if (!iv.IsOk)
                    {
                        return iv;
                    }

                    return IndexValue(ov.Value, iv.Value);
                }

            case Expr.Slice { Obj: var obj, Lo: var lo, Hi: var hi }:
                {
                    // a[i:j]：a、i、j 依序（EVAL-INDEX-005）
                    var ov = DerefEval(obj, env);
                    if (!ov.IsOk)
                    {
                        return ov;
                    }

                    var lov = Eval(lo, env);
                    if (!lov.IsOk)
                    {
                        return lov;
                    }

                    var hiv = Eval(hi, env);
                    if (!hiv.IsOk)
                    {
                        return hiv;
                    }

                    return SliceValue(ov.Value, lov.Value, hiv.Value);
                }

            case Expr.Field { Obj: var obj, Name: var name }:
                {
                    var ov = DerefEval(obj, env);
                    if (!ov.IsOk)
                    {
                        return ov;
                    }

                    return FieldValue(ov.Value, name);
                }

            case Expr.Try { Operand: var operand }:
                {
                    var v = Eval(operand, env);
                    if (!v.IsOk)
                    {
                        return v;
                    }

                    if (v.Value is Value.Enum en && en.Def == _machine.Defs.ResultDef())
                    {
                        var payload = en.Payloads.Count > 0 ? en.Payloads[0] : Value.Unit.Default;
                        if (en.Case == 0)
                        {
                            return RtResult<Value>.Ok(payload);
                        }

                        // Err：载荷向上传播，函数边界合成 Err 返回（RESULT-TRY-003）
                        throw new Signal.PropagateSig(payload);
                    }

                    return RtResult<Value>.Fail(new RtError(
                        RtErrorKind.TypeCast,
                        $"? 的操作数须为 Result，得到 {v.Value.TypeHint()}"));
                }

            case Expr.StructLit { TypeName: var typeName, Fields: var fields }:
                return EvalStructLit(typeName, fields, env);

            case Expr.MapLit { Items: var items }:
                {
                    var m = _machine.AllocMapValue();
                    if (m is not Value.Map mv)
                    {
                        return RtResult<Value>.Fail(RtError.Internal("新分配映射缺索引"));
                    }

                    foreach (var item in items)
                    {
                        // EVAL-LITERAL-004：键值对逐对左到右（键先值后）
                        var k = Eval(item.Key, env);
                        if (!k.IsOk)
                        {
                            return k;
                        }

                        var v = Eval(item.Value, env);
                        if (!v.IsOk)
                        {
                            return v;
                        }

                        var ins = _machine.Heap.MapInsert(mv.Id, k.Value, v.Value);
                        if (!ins.IsOk)
                        {
                            return RtResult<Value>.Fail(ins.Error!);
                        }
                    }

                    return RtResult<Value>.Ok(m);
                }

            case Expr.ArrLit { Items: var items }:
                {
                    // EVAL-LITERAL-004：元素左到右
                    var vals = new List<Value>(items.Count);
                    foreach (var it in items)
                    {
                        var v = Eval(it, env);
                        if (!v.IsOk)
                        {
                            return v;
                        }

                        vals.Add(v.Value);
                    }

                    return RtResult<Value>.Ok(_machine.AllocSliceValue(vals));
                }

            case Expr.Match { Scrutinee: var scrutinee, Arms: var arms }:
                return EvalMatch(scrutinee, arms, env);

            case Expr.FnLit { Params: var pars, Body: var body }:
                {
                    var def = new FuncDef
                    {
                        Name = string.Empty,
                        Params = pars.Select(p => p.Name).ToList(),
                        Body = body,
                    };
                    var id = RegisterFn(def);
                    return RtResult<Value>.Ok(new Value.Closure(id, env));
                }

            default:
                return RtResult<Value>.Fail(RtError.Internal("未知表达式变体（内部不变量）"));
        }
    }

    private RtResult<Value> EvalIdent(string name, EnvId env)
    {
        // 环境优先，其次零参构造器裸写（None 这类 case），最后报未定义
        var hit = _machine.Envs.Lookup(env, name);
        if (!hit.IsOk)
        {
            return RtResult<Value>.Fail(hit.Error!);
        }

        if (hit.Value is { } owner)
        {
            return _machine.Envs.Get(owner.Env, owner.Slot);
        }

        if (_ctors.TryGetValue(name, out var entry))
        {
            return CtorValue(entry, [], name);
        }

        return RtResult<Value>.Fail(new RtError(RtErrorKind.Scope, $"未定义 {name}"));
    }

    private RtResult<Value> CtorValue(CtorEntry entry, IReadOnlyList<Value> payloads, string name)
    {
        if (payloads.Count != entry.Arity)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.TypeCast,
                $"{name} 需要 {entry.Arity} 个参数，得到 {payloads.Count} 个"));
        }

        return RtResult<Value>.Ok(new Value.Enum(entry.Def, entry.Case, payloads));
    }

    private RtResult<Value> EvalUnary(UnaryOp op, Expr operand, EnvId env)
    {
        var v = Eval(operand, env);
        if (!v.IsOk)
        {
            return v;
        }

        var deref = _machine.DerefValue(v.Value);
        if (!deref.IsOk)
        {
            return deref;
        }

        switch (op)
        {
            case UnaryOp.Neg:
                return deref.Value switch
                {
                    Value.Int i => RtResult<Value>.Ok(new Value.Int(unchecked(-i.V))),
                    Value.Float f => RtResult<Value>.Ok(new Value.Float(-f.V)),
                    var other => RtResult<Value>.Fail(new RtError(
                        RtErrorKind.TypeCast, $"- 不能用于 {other.TypeHint()}")),
                };
            case UnaryOp.Not:
                {
                    var b = deref.Value.Truthy();
                    return b.IsOk
                        ? RtResult<Value>.Ok(new Value.Bool(!b.Value))
                        : RtResult<Value>.Fail(b.Error!);
                }

            case UnaryOp.Ref:
                {
                    // E3012：仅变量可取引用（静态面）；运行时只对 Ident 产生 Place
                    if (operand is not Expr.Ident { Name: var name })
                    {
                        return RtResult<Value>.Fail(new RtError(
                            RtErrorKind.TypeCast, "v0 只能对变量取引用"));
                    }

                    var hit = _machine.Envs.Lookup(env, name);
                    if (!hit.IsOk)
                    {
                        return RtResult<Value>.Fail(hit.Error!);
                    }

                    return hit.Value is { } owner
                        ? RtResult<Value>.Ok(_machine.TakeRef(owner.Env, owner.Slot))
                        : RtResult<Value>.Fail(new RtError(RtErrorKind.Scope, $"未定义 {name}"));
                }

            default:
                return RtResult<Value>.Fail(RtError.Internal("未知一元算符（内部不变量）"));
        }
    }

    private RtResult<Value> EvalBinary(BinaryOp op, Expr lhs, Expr rhs, EnvId env)
    {
        // EVAL-SHORTCUT-006：短路运算左侧决定是否求值右侧
        switch (op)
        {
            case BinaryOp.AndAnd:
                {
                    var l = Cond(lhs, env);
                    if (!l.IsOk)
                    {
                        return RtResult<Value>.Fail(l.Error!);
                    }

                    if (!l.Value)
                    {
                        return RtResult<Value>.Ok(new Value.Bool(false));
                    }

                    var r = Cond(rhs, env);
                    return r.IsOk
                        ? RtResult<Value>.Ok(new Value.Bool(r.Value))
                        : RtResult<Value>.Fail(r.Error!);
                }

            case BinaryOp.OrOr:
                {
                    var l = Cond(lhs, env);
                    if (!l.IsOk)
                    {
                        return RtResult<Value>.Fail(l.Error!);
                    }

                    if (l.Value)
                    {
                        return RtResult<Value>.Ok(new Value.Bool(true));
                    }

                    var r = Cond(rhs, env);
                    return r.IsOk
                        ? RtResult<Value>.Ok(new Value.Bool(r.Value))
                        : RtResult<Value>.Fail(r.Error!);
                }
        }

        var lv = Eval(lhs, env);
        if (!lv.IsOk)
        {
            return lv;
        }

        var rv = Eval(rhs, env);
        if (!rv.IsOk)
        {
            return rv;
        }

        var ld = _machine.DerefValue(lv.Value);
        if (!ld.IsOk)
        {
            return ld;
        }

        var rd = _machine.DerefValue(rv.Value);
        if (!rd.IsOk)
        {
            return rd;
        }

        return Ops.Binary(Ops.Of(op), ld.Value, rd.Value);
    }

    private RtResult<Value> EvalCall(Expr callee, IReadOnlyList<Expr> args, EnvId env)
    {
        // 构造器调用（调用位置的 Ident 先查构造器表，证据口径）
        if (callee is Expr.Ident { Name: var cname } && _ctors.TryGetValue(cname, out var entry))
        {
            var payloads = EvalArgs(args, env);
            if (!payloads.IsOk)
            {
                return RtResult<Value>.Fail(payloads.Error!);
            }

            return CtorValue(entry, payloads.Value, cname);
        }

        // 方法调用：接收者先于实参（EVAL-CALL-002）；非方法落通用路径
        if (callee is Expr.Field { Obj: var recvExpr, Name: var methodName })
        {
            var ov = DerefEval(recvExpr, env);
            if (!ov.IsOk)
            {
                return ov;
            }

            if (ov.Value is Value.Struct sv
                && _methods.TryGetValue((sv.Val.Def, methodName), out var m))
            {
                var argv = EvalArgs(args, env);
                if (!argv.IsOk)
                {
                    return RtResult<Value>.Fail(argv.Error!);
                }

                return RunFunction(m.Func, m.Env, ov.Value, argv.Value);
            }

            // 字段存函数值：落到通用路径（callee 表达式整体再求值）
        }

        // 通用：callee 先于实参（EVAL-CALL-002）
        var cv = Eval(callee, env);
        if (!cv.IsOk)
        {
            return cv;
        }

        var cd = _machine.DerefValue(cv.Value);
        if (!cd.IsOk)
        {
            return cd;
        }

        var argvs = EvalArgs(args, env);
        if (!argvs.IsOk)
        {
            return RtResult<Value>.Fail(argvs.Error!);
        }

        switch (cd.Value)
        {
            case Value.Closure c:
                return RunFunction(c.Func, c.Env, null, argvs.Value);
            case Value.Builtin b:
                return CallBuiltinRoute(b.Id, argvs.Value);
            case var other:
                return RtResult<Value>.Fail(new RtError(
                    RtErrorKind.TypeCast, $"{other.TypeHint()} 不可调用"));
        }
    }

    private RtResult<IReadOnlyList<Value>> EvalArgs(IReadOnlyList<Expr> args, EnvId env)
    {
        var vals = new List<Value>(args.Count);
        foreach (var a in args)
        {
            var v = Eval(a, env);
            if (!v.IsOk)
            {
                return RtResult<IReadOnlyList<Value>>.Fail(v.Error!);
            }

            var d = _machine.DerefValue(v.Value);
            if (!d.IsOk)
            {
                return RtResult<IReadOnlyList<Value>>.Fail(d.Error!);
            }

            vals.Add(d.Value);
        }

        return RtResult<IReadOnlyList<Value>>.Ok(vals);
    }

    private RtResult<Value> CallBuiltinRoute(BuiltinId id, IReadOnlyList<Value> args)
    {
        var kind = _machine.Builtins.Kind(id);
        if (kind == BuiltinKind.Host)
        {
            var name = _machine.Builtins.Name(id);
            return InterpFmt.HostBuiltin(_machine, name, args);
        }

        return _machine.CallBuiltin(_sink, InterpFmt.Instance, id, args);
    }

    // ———— 索引 / 切片 / 字段 ————

    private RtResult<Value> IndexValue(Value obj, Value idx)
    {
        switch (obj)
        {
            case Value.Slice id:
                {
                    var len = _machine.Heap.SliceLen(id.Id);
                    if (!len.IsOk)
                    {
                        return RtResult<Value>.Fail(len.Error!);
                    }

                    var i = IntIndex(idx, len.Value);
                    if (!i.IsOk)
                    {
                        return RtResult<Value>.Fail(i.Error!);
                    }

                    return _machine.Heap.SliceGet(id.Id, i.Value);
                }

            case Value.Map m:
                {
                    var key = _machine.MapKeyOf(idx);
                    if (!key.IsOk)
                    {
                        return RtResult<Value>.Fail(key.Error!);
                    }

                    return _machine.Heap.MapGet(m.Id, key.Value);
                }

            case Value.Str s:
                {
                    var runes = s.V.EnumerateRunes().ToList();
                    var i = IntIndex(idx, runes.Count);
                    if (!i.IsOk)
                    {
                        return RtResult<Value>.Fail(i.Error!);
                    }

                    return RtResult<Value>.Ok(new Value.Str(runes[(int)i.Value].ToString()));
                }

            default:
                return RtResult<Value>.Fail(new RtError(
                    RtErrorKind.TypeCast, $"{obj.TypeHint()} 不可索引"));
        }
    }

    private RtResult<Value> SliceValue(Value obj, Value lo, Value hi)
    {
        if (lo is not Value.Int loI || hi is not Value.Int hiI)
        {
            return RtResult<Value>.Fail(new RtError(RtErrorKind.TypeCast, "切片端点须为 int"));
        }

        switch (obj)
        {
            case Value.Slice id:
                {
                    var len = _machine.Heap.SliceLen(id.Id);
                    if (!len.IsOk)
                    {
                        return RtResult<Value>.Fail(len.Error!);
                    }

                    var b = CheckSliceBounds(loI.V, hiI.V, len.Value);
                    if (!b.IsOk)
                    {
                        return RtResult<Value>.Fail(b.Error!);
                    }

                    var items = _machine.Heap.Slice(id.Id);
                    if (!items.IsOk)
                    {
                        return RtResult<Value>.Fail(items.Error!);
                    }

                    var seg = items.Value.Skip((int)loI.V).Take((int)(hiI.V - loI.V)).ToList();
                    return RtResult<Value>.Ok(_machine.AllocSliceValue(seg));
                }

            case Value.Str s:
                {
                    var runes = s.V.EnumerateRunes().ToList();
                    var b = CheckSliceBounds(loI.V, hiI.V, runes.Count);
                    if (!b.IsOk)
                    {
                        return RtResult<Value>.Fail(b.Error!);
                    }

                    var text = string.Concat(runes.Skip((int)loI.V).Take((int)(hiI.V - loI.V)).Select(r => r.ToString()));
                    return RtResult<Value>.Ok(new Value.Str(text));
                }

            default:
                return RtResult<Value>.Fail(new RtError(
                    RtErrorKind.TypeCast, $"{obj.TypeHint()} 不可切片"));
        }
    }

    private RtResult<Value> FieldValue(Value obj, string name)
    {
        if (obj is not Value.Struct sv)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.TypeCast, $"{name} 没有字段（{obj.TypeHint()} 不是 struct）"));
        }

        var def = _machine.Defs.StructDef(sv.Val.Def);
        if (!def.IsOk)
        {
            return RtResult<Value>.Fail(def.Error!);
        }

        var i = def.Value.Fields.ToList().IndexOf(name);
        return i < 0
            ? RtResult<Value>.Fail(new RtError(
                RtErrorKind.TypeCast, $"{def.Value.Name} 没有字段 {name}"))
            : RtResult<Value>.Ok(sv.Val.Fields[i]);
    }

    private RtResult<Value> EvalStructLit(string typeName, IReadOnlyList<FieldLit> fields, EnvId env)
    {
        if (!_structs.TryGetValue(typeName, out var sdef))
        {
            return RtResult<Value>.Fail(new RtError(RtErrorKind.Scope, $"未知 struct {typeName}"));
        }

        var def = _machine.Defs.StructDef(sdef);
        if (!def.IsOk)
        {
            return RtResult<Value>.Fail(def.Error!);
        }

        var names = def.Value.Fields;
        if (fields.Count != names.Count)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.TypeCast,
                $"{typeName} 需要 {names.Count} 个字段，得到 {fields.Count} 个"));
        }

        // EVAL-LITERAL-004：字段值按声明序左到右；字段名须与声明序一致
        var vals = new List<Value>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name != names[i])
            {
                return RtResult<Value>.Fail(new RtError(
                    RtErrorKind.TypeCast,
                    $"{typeName} 第 {i + 1} 个字段应为 {names[i]}，得到 {fields[i].Name}"));
            }

            var v = Eval(fields[i].Value, env);
            if (!v.IsOk)
            {
                return v;
            }

            vals.Add(v.Value);
        }

        return RtResult<Value>.Ok(new Value.Struct(new StructVal(sdef, vals)));
    }

    // ———— match ————

    private RtResult<Value> EvalMatch(Expr scrutinee, IReadOnlyList<Arm> arms, EnvId env)
    {
        var scrut = Eval(scrutinee, env);
        if (!scrut.IsOk)
        {
            return scrut;
        }

        foreach (var arm in arms)
        {
            // 臂内新作用域：绑定限本臂（MATCH-BIND-003）
            var aenv = _machine.Envs.Child(env);
            var matched = MatchPat(scrut.Value, arm.Pat, aenv);
            if (!matched.IsOk)
            {
                return RtResult<Value>.Fail(matched.Error!);
            }

            if (matched.Value)
            {
                return Eval(arm.Body, aenv);
            }
        }

        return RtResult<Value>.Fail(new RtError(RtErrorKind.MatchNoArm, "match 无匹配分支"));
    }

    private RtResult<bool> MatchPat(Value v, Pat pat, EnvId env)
    {
        switch (pat)
        {
            case Pat.Wild:
                return RtResult<bool>.Ok(true);
            case Pat.Bind { Name: var name }:
                {
                    if (name != "_")
                    {
                        var d = _machine.Envs.Define(env, name, v, mutable: false);
                        if (!d.IsOk)
                        {
                            return RtResult<bool>.Fail(d.Error!);
                        }
                    }

                    return RtResult<bool>.Ok(true);
                }

            case Pat.Lit { Value: var lit }:
                {
                    // 字面量模式按 == 比较（类型不可比 = 不匹配，与证据一致）
                    var lv = Eval(lit, env);
                    if (!lv.IsOk)
                    {
                        return RtResult<bool>.Fail(lv.Error!);
                    }

                    var d = _machine.DerefValue(lv.Value);
                    if (!d.IsOk)
                    {
                        return RtResult<bool>.Fail(d.Error!);
                    }

                    var eq = Value.EqualsLang(v, d.Value);
                    return RtResult<bool>.Ok(eq.IsOk && eq.Value);
                }

            case Pat.Case { Name: var name, Subs: var subs }:
                {
                    if (v is not Value.Enum { Def: var def, Case: var c, Payloads: var payloads })
                    {
                        return RtResult<bool>.Ok(false);
                    }

                    var cases = _machine.Defs.EnumDef(def);
                    if (!cases.IsOk)
                    {
                        return RtResult<bool>.Fail(cases.Error!);
                    }

                    var idx = cases.Value.Cases.ToList().IndexOf(name);
                    if (idx < 0 || (uint)idx != c)
                    {
                        return RtResult<bool>.Ok(false);
                    }

                    if (subs.Count != payloads.Count)
                    {
                        return RtResult<bool>.Fail(new RtError(
                            RtErrorKind.TypeCast,
                            $"case {name} 需要 {payloads.Count} 个子模式，得到 {subs.Count} 个"));
                    }

                    for (var i = 0; i < subs.Count; i++)
                    {
                        var m = MatchPat(payloads[i], subs[i], env);
                        if (!m.IsOk)
                        {
                            return m;
                        }

                        if (!m.Value)
                        {
                            return RtResult<bool>.Ok(false);
                        }
                    }

                    return RtResult<bool>.Ok(true);
                }

            default:
                return RtResult<bool>.Fail(RtError.Internal("未知模式变体（内部不变量）"));
        }
    }

    // ———— 赋值（EVAL-ASSIGN-003） ————

    /// <summary>解析赋值目标的地址（地址组件按 EVAL-* 求值：Ident 绑定名；<c>obj.f</c> 的
    /// obj；<c>a[i]</c> 的 obj、i——均先于右值执行）。</summary>
    private RtResult<Place> AssignPlace(Expr target, EnvId env)
    {
        switch (target)
        {
            case Expr.Ident { Name: var name }:
                {
                    var hit = _machine.Envs.Lookup(env, name);
                    if (!hit.IsOk)
                    {
                        return RtResult<Place>.Fail(hit.Error!);
                    }

                    return hit.Value is { } owner
                        ? RtResult<Place>.Ok(new Place.Var(owner.Env, owner.Slot))
                        : RtResult<Place>.Fail(new RtError(RtErrorKind.Scope, $"未定义变量 {name}"));
                }

            case Expr.Field { Obj: var obj, Name: var name }:
                {
                    var baseP = AssignPlace(obj, env);
                    if (!baseP.IsOk)
                    {
                        return baseP;
                    }

                    // 字段以名寻址：读 base 当前 struct 定位槽位（地址组件面）
                    var objv = _machine.PlaceLoadResolved(baseP.Value);
                    if (!objv.IsOk)
                    {
                        return RtResult<Place>.Fail(objv.Error!);
                    }

                    if (objv.Value is not Value.Struct sv)
                    {
                        return RtResult<Place>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"只能给 struct 字段赋值，得到 {objv.Value.TypeHint()}"));
                    }

                    var def = _machine.Defs.StructDef(sv.Val.Def);
                    if (!def.IsOk)
                    {
                        return RtResult<Place>.Fail(def.Error!);
                    }

                    var i = def.Value.Fields.ToList().IndexOf(name);
                    return i >= 0
                        ? RtResult<Place>.Ok(new Place.Field(baseP.Value, (uint)i))
                        : RtResult<Place>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"{def.Value.Name} 没有字段 {name}"));
                }

            case Expr.Index { Obj: var obj, Idx: var idx }:
                {
                    var ov = DerefEval(obj, env);
                    if (!ov.IsOk)
                    {
                        return RtResult<Place>.Fail(ov.Error!);
                    }

                    var iv = Eval(idx, env);
                    if (!iv.IsOk)
                    {
                        return RtResult<Place>.Fail(iv.Error!);
                    }

                    switch (ov.Value)
                    {
                        case Value.Slice id:
                            {
                                var len = _machine.Heap.SliceLen(id.Id);
                                if (!len.IsOk)
                                {
                                    return RtResult<Place>.Fail(len.Error!);
                                }

                                var i = IntIndex(iv.Value, len.Value);
                                if (!i.IsOk)
                                {
                                    return RtResult<Place>.Fail(i.Error!);
                                }

                                return RtResult<Place>.Ok(new Place.Element(id.Id, i.Value));
                            }

                        case Value.Map m:
                            {
                                var key = _machine.MapKeyOf(iv.Value);
                                return key.IsOk
                                    ? RtResult<Place>.Ok(new Place.MapEntry(m.Id, key.Value))
                                    : RtResult<Place>.Fail(key.Error!);
                            }

                        default:
                            return RtResult<Place>.Fail(new RtError(
                                RtErrorKind.TypeCast, $"{ov.Value.TypeHint()} 不可索引赋值"));
                    }
                }

            default:
                return RtResult<Place>.Fail(new RtError(RtErrorKind.TypeCast, "赋值目标不合法"));
        }
    }

    private RtResult ExecAssign(Expr target, AssignOp op, Expr value, EnvId env)
    {
        // 1) 地址组件先求值（EVAL-ASSIGN-003）
        if (target is Expr.Ident { Name: var name })
        {
            var hit = _machine.Envs.Lookup(env, name);
            if (!hit.IsOk)
            {
                return hit.Ignore();
            }

            if (hit.Value is not { } owner)
            {
                return RtResult.Fail(new RtError(RtErrorKind.Scope, $"未定义变量 {name}"));
            }

            // 2) 右值
            var rhs = Eval(value, env);
            if (!rhs.IsOk)
            {
                return rhs.Ignore();
            }

            if (op == AssignOp.Assign)
            {
                // Ident 重绑定写 cell 本身（不穿透引用——VALUE-REF-003 只覆盖
                // 字段/索引面；重绑定面 = cell 替换，与证据一致）
                return _machine.Envs.Set(owner.Env, owner.Slot, rhs.Value);
            }

            // 3) 复合：读旧值 → 运算 → 写回
            var old = _machine.Envs.Get(owner.Env, owner.Slot);
            if (!old.IsOk)
            {
                return old.Ignore();
            }

            var oldD = _machine.DerefValue(old.Value);
            if (!oldD.IsOk)
            {
                return oldD.Ignore();
            }

            // 数组 +=：原位扩展（VALUE-SHARE-002），别名同见
            if (op == AssignOp.AddAssign && oldD.Value is Value.Slice lhs)
            {
                return ArrayExtend(lhs.Id, rhs.Value);
            }

            var combined = Ops.Compound(Ops.Of(op), oldD.Value, rhs.Value);
            return combined.IsOk
                ? _machine.Envs.Set(owner.Env, owner.Slot, combined.Value)
                : RtResult.Fail(combined.Error!);
        }

        var place = AssignPlace(target, env);
        if (!place.IsOk)
        {
            return place.Ignore();
        }

        var rv = Eval(value, env);
        if (!rv.IsOk)
        {
            return rv.Ignore();
        }

        if (op == AssignOp.Assign)
        {
            return _machine.PlaceStore(place.Value, rv.Value);
        }

        // 复合：读旧值（Field/Index 的 base 经引用穿透）→ 运算 → 写回
        var loaded = _machine.PlaceLoad(place.Value);
        if (!loaded.IsOk)
        {
            return loaded.Ignore();
        }

        var oldDeref = _machine.DerefValue(loaded.Value);
        if (!oldDeref.IsOk)
        {
            return oldDeref.Ignore();
        }

        if (op == AssignOp.AddAssign && oldDeref.Value is Value.Slice lhs2)
        {
            return ArrayExtend(lhs2.Id, rv.Value);
        }

        var compound = Ops.Compound(Ops.Of(op), oldDeref.Value, rv.Value);
        return compound.IsOk
            ? _machine.PlaceStore(place.Value, compound.Value)
            : RtResult.Fail(compound.Error!);
    }

    /// <summary><c>xs += ys</c>（数组）：把 ys 的元素逐个原位 push 进 xs（证据口径：
    /// 数组复合加法的右操作数须为数组）。</summary>
    private RtResult ArrayExtend(ValueId lhs, Value rhs)
    {
        if (rhs is not Value.Slice rhsId)
        {
            return RtResult.Fail(new RtError(
                RtErrorKind.TypeCast, $"数组 += 需要数组，得到 {rhs.TypeHint()}"));
        }

        var items = _machine.Heap.Slice(rhsId.Id);
        if (!items.IsOk)
        {
            return items.Ignore();
        }

        foreach (var v in items.Value)
        {
            var p = _machine.Heap.SlicePush(lhs, v);
            if (!p.IsOk)
            {
                return p;
            }
        }

        return RtResult.Ok;
    }

    // ———— 调用 ————

    /// <summary>函数体执行：帧环境 = 定义处环境的子环境；接收者/参数依次落位；
    /// Return 正常返回、Propagate 在边界合成 <c>Err</c>（RESULT-TRY-003）。</summary>
    private RtResult<Value> RunFunction(FunctionId func, EnvId defEnv, Value? recv, IReadOnlyList<Value> args)
    {
        var f = FnDef(func);
        if (!f.IsOk)
        {
            return RtResult<Value>.Fail(f.Error!);
        }

        if (f.Value.IsExtern)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.UnsupportedHost,
                $"extern 函数 {f.Value.Name} 仅转译可用（宿主 {f.Value.ExternHost}）"));
        }

        var fenv = _machine.Envs.Child(defEnv);
        if (f.Value.Recv is { } rname && recv is not null)
        {
            var d = _machine.Envs.Define(fenv, rname, recv, mutable: false);
            if (!d.IsOk)
            {
                return RtResult<Value>.Fail(d.Error!);
            }
        }

        if (args.Count != f.Value.Params.Count)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.TypeCast,
                $"{f.Value.DisplayName} 需要 {f.Value.Params.Count} 个参数，得到 {args.Count} 个"));
        }

        for (var i = 0; i < f.Value.Params.Count; i++)
        {
            var d = _machine.Envs.Define(fenv, f.Value.Params[i], args[i], mutable: false);
            if (!d.IsOk)
            {
                return RtResult<Value>.Fail(d.Error!);
            }
        }

        try
        {
            var r = ExecStmts(f.Value.Body, fenv);
            return r.IsOk ? RtResult<Value>.Ok(Value.Unit.Default) : RtResult<Value>.Fail(r.Error!);
        }
        catch (Signal.ReturnSig ret)
        {
            return RtResult<Value>.Ok(ret.Value);
        }
        catch (Signal.PropagateSig prop)
        {
            return RtResult<Value>.Ok(_machine.ErrValue(prop.Payload));
        }
    }

    /// <summary><see cref="TaskExec"/> 面：go 语句实参快照后的同步执行（返回值被调度器丢弃）。</summary>
    RtResult<Value> TaskExec.Call(Machine machine, FunctionId func, IReadOnlyList<Value> args) =>
        RunFunction(func, machine.Root, null, args);

    // ———— 索引合法性 ————

    /// <summary>索引合法性（RUNTIME-INDEX-001）：须为 int 且 0 &lt;= i &lt; len。</summary>
    private RtResult<uint> IntIndex(Value v, int len)
    {
        if (v is not Value.Int i)
        {
            return RtResult<uint>.Fail(new RtError(
                RtErrorKind.TypeCast, $"索引须为 int，得到 {v.TypeHint()}"));
        }

        return i.V < 0 || i.V >= len
            ? RtResult<uint>.Fail(new RtError(
                RtErrorKind.Index, $"索引 {i.V} 越界（长度 {len}）"))
            : RtResult<uint>.Ok((uint)i.V);
    }

    private static RtResult CheckSliceBounds(long lo, long hi, int len)
    {
        return lo < 0 || hi < lo || hi > len
            ? RtResult.Fail(new RtError(
                RtErrorKind.Index, $"切片 [{lo}:{hi}] 越界（长度 {len}）"))
            : RtResult.Ok;
    }
}
