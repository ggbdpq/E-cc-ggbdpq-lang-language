using System.Collections.Generic;
using Lang.Core;
using Lang.Hir;
using Lang.Semantic;
using Lang.Types;
using HirAssignOp = Lang.Hir.AssignOp;
using HirBinaryOp = Lang.Hir.BinaryOp;
using HirUnaryOp = Lang.Hir.UnaryOp;

namespace Lang.Ir;

/// <summary>
/// <see cref="ILowerIr"/> 的实现：Typed HIR → Core IR（机械降级，不发明语义；语义已由
/// Lang.Semantic 背书）。构造期注入 <see cref="TypeTables"/>/<see cref="SymbolArena"/>
/// （须来自产生该 HIR 的同一次分析），用于三张 HIR 未展开的映射：ctor 符号 → (enum, case)、
/// fn/方法符号 → 函数表下标、TypeId → enum/struct 名。
///
/// 块模型两条铁律（validator「块入口栈深 0」的推论）：
/// ①多操作数构造一律先落临时槽再统一压栈；②跨块存活的值一律驻局部槽。
///
/// <b>赋值降形（EVAL-ASSIGN-003，lang-rs B1 集成轮冻结合同）</b>：地址组件压根槽
/// <see cref="Inst.LocalGet"/>/<see cref="Inst.GlobalGet"/> 直压（保 Place 溯源，禁落临时槽
/// ——B1 教训：落槽即深拷贝，FieldSet/IndexSet 写在副本上），路径 Index 表达式左到右
/// 落临时槽（先于右值），右值落临时槽，压栈序 = 冻结合同（FieldSet (obj,val)、
/// IndexSet (obj,idx,val)）；复合赋值读-算-写经原槽；数组 <c>+=</c>（右值为数组）走
/// <see cref="Inst.ArrayPush"/> 原位拼接。
/// </summary>
public sealed class IrLowerer(TypeTables types, SymbolArena symbols) : ILowerIr
{
    private readonly TypeTables _types = types;
    private readonly SymbolArena _symbols = symbols;
    private readonly List<StructDecl> _structs = [];
    private readonly Dictionary<string, StructId> _structIds = new(StringComparer.Ordinal);
    private readonly List<EnumDecl> _enums = [];
    private readonly Dictionary<string, EnumId> _enumIds = new(StringComparer.Ordinal);
    private readonly List<GlobalDecl> _globals = [];
    private readonly Dictionary<string, GlobalId> _globalIds = new(StringComparer.Ordinal);
    private readonly List<IrFunction> _functions = [];
    /// <summary>fn/方法/嵌套 fn 符号 → 函数表下标（分配即登记，先于体下降）。</summary>
    private readonly Dictionary<SymbolId, FunctionId> _fnBySym = [];
    /// <summary>符号 → (参数个数, 是否有返回值)。分配期从 HIR 登记——函数表本体在体
    /// 下降完成前是占位签名，表达式栈效应判定不得读表（前向引用面）。</summary>
    private readonly Dictionary<SymbolId, (int ParamCount, bool HasRet)> _fnSigBySym = [];

    /// <summary>下降整个程序。产物必须通过 <see cref="Validator.Validate"/>。</summary>
    public LowerResult<IrProgram> Lower(HirProg hir)
    {
        try
        {
            var prog = LowerProg(hir);
            var err = Validator.Validate(prog);
            return err is null
                ? LowerResult<IrProgram>.Ok(prog)
                : LowerResult<IrProgram>.Fail($"产物未过 validate: {err}");
        }
        catch (LowerAbort e)
        {
            return LowerResult<IrProgram>.Fail(e.Message);
        }
    }

    /// <summary>内部控制流短路（公开面返回受控错误，不 throw 表达）。</summary>
    private sealed class LowerAbort(string message) : Exception(message);

    private LowerAbort Abort(string message) => throw new LowerAbort(message);

    // ———— 程序表合成 ————

    private IrProgram LowerProg(HirProg hir)
    {
        // 1. struct 表（声明序）+ 预载 error（名字查表需要总表）。
        foreach (var sd in hir.Structs)
        {
            var id = new StructId((uint)_structs.Count);
            _structs.Add(new StructDecl(sd.Name, sd.TypeParams,
                sd.Fields.Select(f => new FieldDecl(f.Name, f.TypeId)).ToList()));
            _structIds[sd.Name] = id;
        }
        var errId = new StructId((uint)_structs.Count);
        _structs.Add(new StructDecl("error", [],
            [new FieldDecl(TypeTables.ErrorMsgField, TypeTables.Str)]));
        _structIds["error"] = errId;

        // 2. enum 表（声明序）+ 预载 Option/Result（case 序与 RESULT-PRELUDE-001 一致；
        //    载荷类型为泛型擦除占位 null）。
        foreach (var ed in hir.Enums)
        {
            var id = new EnumId((uint)_enums.Count);
            _enums.Add(EnumDeclToIr(ed));
            _enumIds[ed.Name] = id;
        }
        AddPreloadedEnum("Option", ("Some", 1), ("None", 0));
        AddPreloadedEnum("Result", ("Ok", 1), ("Err", 1));

        // 3. 全局表（声明序）。
        foreach (var g in hir.Globals)
        {
            var id = new GlobalId((uint)_globals.Count);
            _globals.Add(new GlobalDecl(g.Name, g.Mutable, g.Meta.TypeId));
            _globalIds[g.Name] = id;
        }

        // 4. 函数表：先全体分配下标并登记符号（顶层 fn 互相前向可见），再逐个降体；
        //    main 按 HIR main 节点身份对认。
        var fids = new List<FunctionId>(hir.Fns.Count);
        foreach (var f in hir.Fns)
        {
            fids.Add(AllocFunction(f));
        }
        FunctionId? mainFid = null;
        for (var i = 0; i < hir.Fns.Count; i++)
        {
            var f = hir.Fns[i];
            var fid = fids[i];
            if (hir.Main is { } m && m.Meta.Id == f.Meta.Id)
            {
                mainFid = fid;
            }
            if (f.IsExtern)
            {
                _functions[(int)fid.Value] = new IrFunction(
                    f.Name, 0, new FnSig(FnParamCount(f), f.Ret is not null),
                    [], [], f.ExternHost);
                continue;
            }
            PrescanNested(f.Body);
            LowerFnBody(f, fid, []);
        }

        // 5. init：从 globals 初始化合成（声明序 GlobalSet；无全局缺省）。
        FunctionId? init = null;
        if (_globals.Count > 0)
        {
            init = LowerInit(hir.Globals);
        }

        return new IrProgram(_structs, _enums, _globals, _functions, init, mainFid);
    }

    /// <summary>分配函数表项（占位）并登记符号 → fid。</summary>
    private FunctionId AllocFunction(HirFn hf)
    {
        if (hf.Meta.Sym is not { } sym)
        {
            throw Abort($"函数 {hf.Name} 缺符号结论（语义缺口）");
        }
        var fid = new FunctionId((uint)_functions.Count);
        _fnBySym[sym] = fid;
        _fnSigBySym[sym] = (FnParamCount(hf), hf.Ret is not null);
        _functions.Add(new IrFunction(hf.Name, 0, new FnSig(0, false), [], [], null));
        return fid;
    }

    /// <summary>预扫描体内的嵌套 fn 声明：分配 fid 并登记（兄弟前向引用可达），递归其体。
    /// 只登记不降体；不进 fnlit 体（fnlit 在降位点自行预扫描）。</summary>
    private void PrescanNested(IReadOnlyList<HirStmt> stmts)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case HirStmt.FnStmt { Func: var func }:
                    AllocFunction(func);
                    PrescanNested(func.Body);
                    break;
                case HirStmt.If { Then: var then, Els: var els }:
                    PrescanNested(then);
                    PrescanNested(els);
                    break;
                case HirStmt.While { Body: var body }:
                    PrescanNested(body);
                    break;
                case HirStmt.ForIn { Body: var body }:
                    PrescanNested(body);
                    break;
            }
        }
    }

    /// <summary>下降函数体（捕获槽布局/装箱序言/块图）并回填函数表。
    /// 前置：该函数体内的嵌套 fn 已经 <see cref="PrescanNested"/> 登记。</summary>
    private void LowerFnBody(HirFn hf, FunctionId fid, IReadOnlyList<string> captures)
    {
        var boxed = CollectBoxedNames(hf.Body);
        var f = new FnCtx(hf.Name, captures, hf.Recv, hf.Params, hf.Ret, boxed);
        // 装箱参数的入口序言：槽位从实参值变为 Ref（捕获槽天然持 Ref）。
        for (var i = 0; i < f.ParamCount; i++)
        {
            var slot = new LocalId((uint)(f.CaptureCount + i));
            if (f.ParamKind(slot) == SlotKind.Boxed)
            {
                f.Emit(new Inst.LocalGet(slot));
                f.Emit(new Inst.BoxNew());
                f.Emit(new Inst.LocalSet(slot));
            }
        }
        var terminated = Stmts(f, hf.Body);
        if (!terminated)
        {
            // 体走完未显式返回：隐式 `return ()`（与 interp 语义一致）。
            f.SetTerm(new Terminator.Return(false));
        }
        FillTryFails(f);
        _functions[(int)fid.Value] = f.Finish();
    }

    /// <summary>回填本函数全部 <see cref="Inst.Try"/> 的 fail 块：LocalGet(err) →
    /// EnumNew(Result.Err) → Return（函数边界合成 Err 返回，RESULT-TRY-003）。</summary>
    private void FillTryFails(FnCtx f)
    {
        if (f.ErrSlot is not { } errSlot)
        {
            return;
        }
        var resultId = EnumIdByName("Result");
        var fails = f.TakePendingTryFails();
        foreach (var b in fails)
        {
            f.SwitchTo(b);
            f.Emit(new Inst.LocalGet(errSlot));
            f.Emit(new Inst.EnumNew(resultId, 1));
            f.SetTerm(new Terminator.Return(true));
        }
    }

    /// <summary>合成 init 函数：逐全局（声明序）求值 init 并 <see cref="Inst.GlobalSet"/>。</summary>
    private FunctionId LowerInit(IReadOnlyList<HirLet> globals)
    {
        var boxed = CollectBoxedNames(globals.Select(g => new HirStmt.Let(g)).ToList());
        var f = new FnCtx("init", [], null, [], null, boxed);
        for (var i = 0; i < globals.Count; i++)
        {
            var g = globals[i];
            var hasValue = ExprHasValue(g.Init);
            Expr(f, g.Init);
            if (hasValue)
            {
                f.Emit(new Inst.GlobalSet(new GlobalId((uint)i)));
            }
            // 无值 init（语义缺口面）：无物可存，不做补救。
        }
        f.SetTerm(new Terminator.Return(false));
        FillTryFails(f);
        var irfn = f.Finish();
        var fid = new FunctionId((uint)_functions.Count);
        _functions.Add(irfn);
        return fid;
    }

    // ———— 查表助手 ————

    private EnumId EnumIdByName(string name) => _enumIds.TryGetValue(name, out var id)
        ? id
        : throw Abort($"enum {name} 不在函数表（语义缺口）");

    private StructId StructIdByName(string name) => _structIds.TryGetValue(name, out var id)
        ? id
        : throw Abort($"struct {name} 不在结构表（语义缺口）");

    /// <summary>构造器符号 → (enum, case)（NAME-CTOR-005：case 名全局唯一，符号自带 owner 与 case 序）。</summary>
    private (EnumId Enum, int Case) CtorCase(SymbolId sym)
    {
        var s = _symbols.Get(sym);
        return (EnumIdByName(s.OwnerName), (int)s.CaseIndex);
    }

    /// <summary>match scrutinee 类型 → enum 表项（Enum(def)/Result/Option）。</summary>
    private EnumId EnumIdOfType(TypeId? t)
    {
        if (t is null)
        {
            throw Abort("match scrutinee 无类型结论（语义缺口）");
        }
        return _types.Data(t.Value) switch
        {
            TypeData.Enum e => EnumIdByName(_types.GetEnum(e.Def).Name),
            TypeData.Result => EnumIdByName("Result"),
            TypeData.Option => EnumIdByName("Option"),
            _ => throw Abort("match scrutinee 类型非 enum/Result/Option（语义缺口）"),
        };
    }

    /// <summary>类型 → struct 表项（Struct(def)/Error）。</summary>
    private StructId StructIdOfType(TypeId? t)
    {
        if (t is null)
        {
            throw Abort("struct 字面量无类型结论（语义缺口）");
        }
        return _types.Data(t.Value) switch
        {
            TypeData.Struct d => StructIdByName(_types.GetStruct(d.Def).Name),
            TypeData.Error => StructIdByName("error"),
            _ => throw Abort("struct 字面量类型非 struct（语义缺口）"),
        };
    }

    private Place ResolvePlace(FnCtx f, string name)
    {
        if (f.Resolve(name) is { } slot)
        {
            return new Place.OfSlot(slot);
        }
        if (_globalIds.TryGetValue(name, out var g))
        {
            return new Place.OfGlobal(g);
        }
        throw Abort($"名字 {name} 未解析到槽位或全局（语义缺口）");
    }

    // ———— 语句 ————

    /// <summary>下降语句序列；返回是否以控制流终止收尾（后续同层语句进死块）。</summary>
    private bool Stmts(FnCtx f, IReadOnlyList<HirStmt> list)
    {
        var terminated = false;
        foreach (var s in list)
        {
            if (terminated)
            {
                f.OpenDeadBlock();
            }
            terminated = Stmt(f, s) || terminated;
        }
        return terminated;
    }

    private bool Stmt(FnCtx f, HirStmt s)
    {
        switch (s)
        {
            case HirStmt.Let l:
                {
                    var hasValue = ExprHasValue(l.Value.Init);
                    Expr(f, l.Value.Init);
                    if (hasValue)
                    {
                        if (l.Value.Name == "_")
                        {
                            f.Emit(new Inst.Pop());
                        }
                        else
                        {
                            var slot = DeclareBinding(f, l.Value.Name, l.Value.Meta.TypeId);
                            if (f.Boxed.Contains(l.Value.Name))
                            {
                                f.Emit(new Inst.BoxNew());
                            }
                            f.Emit(new Inst.LocalSet(slot));
                        }
                    }
                    else if (l.Value.Name != "_")
                    {
                        // 无值初始化（语义缺口面）：绑槽不存值，不做补救。
                        DeclareBinding(f, l.Value.Name, l.Value.Meta.TypeId);
                    }
                    return false;
                }
            case HirStmt.Assign a:
                Assign(f, a.Value);
                return false;
            case HirStmt.Eval ev:
                {
                    var hasValue = ExprHasValue(ev.Expr);
                    Expr(f, ev.Expr);
                    if (hasValue)
                    {
                        f.Emit(new Inst.Pop());
                    }
                    return false;
                }
            case HirStmt.Return ret:
                if (ret.Val is null)
                {
                    f.SetTerm(new Terminator.Return(false));
                }
                else
                {
                    Expr(f, ret.Val);
                    f.SetTerm(new Terminator.Return(true));
                }
                return true;
            case HirStmt.If ifStmt:
                {
                    var thenB = f.NewBlock();
                    var endB = f.NewBlock();
                    var elseB = ifStmt.Els.Count == 0 ? endB : f.NewBlock();
                    Expr(f, ifStmt.Cond);
                    f.SetTerm(new Terminator.Branch(thenB, elseB));
                    f.SwitchTo(thenB);
                    f.PushScope();
                    var t1 = Stmts(f, ifStmt.Then);
                    f.PopScope();
                    if (!t1)
                    {
                        f.SetTerm(new Terminator.Goto(endB));
                    }
                    var t2 = false;
                    if (ifStmt.Els.Count != 0)
                    {
                        f.SwitchTo(elseB);
                        f.PushScope();
                        t2 = Stmts(f, ifStmt.Els);
                        f.PopScope();
                        if (!t2)
                        {
                            f.SetTerm(new Terminator.Goto(endB));
                        }
                    }
                    if (ifStmt.Els.Count != 0 && t1 && t2)
                    {
                        f.MarkDead(endB); // 两臂皆终止：end 不可达
                    }
                    f.SwitchTo(endB);
                    return ifStmt.Els.Count != 0 && t1 && t2;
                }
            case HirStmt.While w:
                {
                    var condB = f.NewBlock();
                    var bodyB = f.NewBlock();
                    var endB = f.NewBlock();
                    f.SetTerm(new Terminator.Goto(condB));
                    f.SwitchTo(condB);
                    Expr(f, w.Cond);
                    f.SetTerm(new Terminator.Branch(bodyB, endB));
                    f.SwitchTo(bodyB);
                    f.Loops.Add((condB, endB));
                    f.PushScope();
                    var t = Stmts(f, w.Body);
                    f.PopScope();
                    f.Loops.RemoveAt(f.Loops.Count - 1);
                    if (!t)
                    {
                        f.SetTerm(new Terminator.Goto(condB));
                    }
                    f.SwitchTo(endB);
                    return false;
                }
            case HirStmt.ForIn forIn:
                ForIn(f, forIn.Variable, forIn.Iter, forIn.Body);
                return false;
            case HirStmt.Break:
                {
                    if (f.Loops.Count == 0)
                    {
                        throw Abort("break 出现在循环外（语义缺口）");
                    }
                    f.SetTerm(new Terminator.Goto(f.Loops[^1].Brk));
                    return true;
                }
            case HirStmt.Continue:
                {
                    if (f.Loops.Count == 0)
                    {
                        throw Abort("continue 出现在循环外（语义缺口）");
                    }
                    f.SetTerm(new Terminator.Goto(f.Loops[^1].Cont));
                    return true;
                }
            case HirStmt.Spawn sp:
                {
                    if (sp.Func is not { } sym)
                    {
                        throw Abort("go 目标缺符号结论（resolver 已约束顶层 fn）");
                    }
                    if (!_fnBySym.TryGetValue(sym, out var target))
                    {
                        throw Abort($"go 目标符号 {sym.Value} 未登记（语义缺口）");
                    }
                    // 实参在 go 语句处立即求值（EVAL-GO-007），左到右。
                    var tmps = ExprsToTemps(f, sp.Args);
                    foreach (var t in tmps)
                    {
                        f.Emit(new Inst.LocalGet(t));
                    }
                    f.Emit(new Inst.Spawn(target, sp.Args.Count));
                    return false;
                }
            case HirStmt.FnStmt fs:
                {
                    // 嵌套 fn：fid 已预扫描登记；此处降体并回填（互为前向可见）。
                    if (fs.Func.Meta.Sym is not { } sym)
                    {
                        throw Abort($"嵌套 fn {fs.Func.Name} 缺符号结论（语义缺口）");
                    }
                    if (!_fnBySym.TryGetValue(sym, out var fid))
                    {
                        throw Abort($"嵌套 fn 符号 {sym.Value} 未登记（语义缺口）");
                    }
                    LowerFnBody(fs.Func, fid, []);
                    return false;
                }
            default:
                throw Abort("未登记的 HirStmt 变体");
        }
    }

    /// <summary>声明绑定：分配槽位并入当前作用域（返回槽位 id）。</summary>
    private LocalId DeclareBinding(FnCtx f, string name, TypeId? ty)
    {
        var slot = f.NewLocal(name, ty);
        var kind = f.Boxed.Contains(name) ? SlotKind.Boxed : SlotKind.Plain;
        f.Bind(name, new Slot(slot, kind));
        return slot;
    }

    private void ForIn(FnCtx f, string variable, HirExpr iter, IReadOnlyList<HirStmt> body)
    {
        // 迭代器驻局部槽（不跨块压栈——块入口栈深 0）。
        var iterSlot = f.NewLocal("<iter>", null);
        Expr(f, iter);
        f.Emit(new Inst.IterNew());
        f.Emit(new Inst.LocalSet(iterSlot));

        var condB = f.NewBlock();
        var endB = f.NewBlock();
        f.SetTerm(new Terminator.Goto(condB));
        f.SwitchTo(condB);
        f.Emit(new Inst.LocalGet(iterSlot));
        f.Emit(new Inst.IterNext(endB));
        f.PushScope();
        if (variable != "_")
        {
            // 装箱循环变量逐轮 BoxNew 重绑（CTRL-FORIN-003）。
            var varSlot = DeclareBinding(f, variable, null);
            if (f.Boxed.Contains(variable))
            {
                f.Emit(new Inst.BoxNew());
            }
            f.Emit(new Inst.LocalSet(varSlot));
        }
        else
        {
            f.Emit(new Inst.Pop());
        }
        f.Loops.Add((condB, endB));
        var t = Stmts(f, body);
        f.Loops.RemoveAt(f.Loops.Count - 1);
        f.PopScope();
        if (!t)
        {
            f.SetTerm(new Terminator.Goto(condB));
        }
        f.SwitchTo(endB);
    }

    // ———— 赋值（EVAL-ASSIGN-003：地址组件 → 右值 → 读旧值 → 运算 → 写回） ————

    private void Assign(FnCtx f, HirAssign a)
    {
        // 数组 `+=` 原位拼接（checker 已约束两侧为数组；VALUE-SHARE-002）。
        var isConcat = a.Op == HirAssignOp.AddAssign
            && MetaOf(a.Rhs).TypeId is { } rhsTy && _types.Data(rhsTy) is TypeData.Slice;
        // 目标恒为 Ident 根 + Field/Index 链（checker 保证）；分解后分派。
        var (root, path) = SplitAssignPath(a.Target);
        if (root is not HirExpr.LocalRef { Name: var name })
        {
            throw Abort("未预期的赋值目标根（解析器已约束）");
        }
        var place = ResolvePlace(f, name);
        if (path.Count == 0)
        {
            // 整体赋值/复合赋值：局部槽与全局直写，现有路径（正确）保留。
            switch (place)
            {
                case Place.OfSlot { Slot: { Kind: SlotKind.Plain, Local: var plain } } when a.Op == HirAssignOp.Assign:
                    Expr(f, a.Rhs);
                    f.Emit(new Inst.LocalSet(plain));
                    break;
                case Place.OfSlot { Slot: { Local: var boxedSlot } } when a.Op == HirAssignOp.Assign:
                    // 装箱：地址组件（Ref）先行落槽，右值后求值，RefSet 写穿透。
                    {
                        var addr = f.NewTemp(null);
                        f.Emit(new Inst.LocalGet(boxedSlot));
                        f.Emit(new Inst.LocalSet(addr));
                        var tmp = f.NewTemp(null);
                        Expr(f, a.Rhs);
                        f.Emit(new Inst.LocalSet(tmp));
                        f.Emit(new Inst.LocalGet(addr));
                        f.Emit(new Inst.LocalGet(tmp));
                        f.Emit(new Inst.RefSet());
                    }
                    break;
                case Place.OfGlobal { Global: var gDirect } when a.Op == HirAssignOp.Assign:
                    Expr(f, a.Rhs);
                    f.Emit(new Inst.GlobalSet(gDirect));
                    break;
                default:
                    if (isConcat)
                    {
                        var tmp = f.NewTemp(null);
                        Expr(f, a.Rhs);
                        f.Emit(new Inst.LocalSet(tmp));
                        LoadPlace(f, place);
                        f.Emit(new Inst.LocalGet(tmp));
                        f.Emit(new Inst.ArrayPush());
                        // 原位拼接：装箱目标无需写回。
                    }
                    else if (place is Place.OfSlot { Slot: { Kind: SlotKind.Plain, Local: var slot } })
                    {
                        var tmp = f.NewTemp(null);
                        Expr(f, a.Rhs);
                        f.Emit(new Inst.LocalSet(tmp));
                        f.Emit(new Inst.LocalGet(slot));
                        f.Emit(new Inst.LocalGet(tmp));
                        f.Emit(new Inst.Binary(AssignBinop(a.Op)));
                        f.Emit(new Inst.LocalSet(slot));
                    }
                    else if (place is Place.OfSlot { Slot: { Local: var bslot } })
                    {
                        var addr = f.NewTemp(null);
                        f.Emit(new Inst.LocalGet(bslot));
                        f.Emit(new Inst.LocalSet(addr));
                        var tmp = f.NewTemp(null);
                        Expr(f, a.Rhs);
                        f.Emit(new Inst.LocalSet(tmp));
                        f.Emit(new Inst.LocalGet(addr));
                        f.Emit(new Inst.LocalGet(addr));
                        f.Emit(new Inst.Deref());
                        f.Emit(new Inst.LocalGet(tmp));
                        f.Emit(new Inst.Binary(AssignBinop(a.Op)));
                        f.Emit(new Inst.RefSet());
                    }
                    else if (place is Place.OfGlobal { Global: var g })
                    {
                        var tmp = f.NewTemp(null);
                        Expr(f, a.Rhs);
                        f.Emit(new Inst.LocalSet(tmp));
                        f.Emit(new Inst.GlobalGet(g));
                        f.Emit(new Inst.LocalGet(tmp));
                        f.Emit(new Inst.Binary(AssignBinop(a.Op)));
                        f.Emit(new Inst.GlobalSet(g));
                    }
                    else
                    {
                        throw Abort("未预期的整体赋值落点（lowering 内部序错）");
                    }
                    break;
            }
            return;
        }
        AssignPath(f, a, place, path, isConcat);
    }

    /// <summary>Field/Index 链赋值降形（压栈序 = 冻结合同：FieldSet 2→0 (obj,val)、
    /// IndexSet 3→0 (obj,idx,val)；弹序 = 压序逆序）。
    ///
    /// 地址组件不落临时槽——根槽 LocalGet/GlobalGet 直接压栈（操作数带 Place.Var 溯源；
    /// 装箱槽原样压 Ref，place_store 写穿透原槽/全局），中间组件经 FieldGet/
    /// LocalGet(idx)+IndexGet 产生带溯源的操作数。路径上的 Index 表达式从左到右求值落
    /// 临时槽（int/键值，无身份问题），且先于右值（EVAL-ASSIGN-003）；右值统一落临时槽
    /// （多块右值安全）。</summary>
    private void AssignPath(FnCtx f, HirAssign a, Place root, List<PathComp> path, bool isConcat)
    {
        // 1. 路径 Index 表达式左到右落临时槽（地址组件先于右值）。
        var idxTemps = new List<LocalId>();
        foreach (var comp in path)
        {
            if (comp.Index is { } idxExpr)
            {
                var t = f.NewTemp(null);
                Expr(f, idxExpr);
                f.Emit(new Inst.LocalSet(t));
                idxTemps.Add(t);
            }
        }
        // 2. 右值求值落临时槽。
        var valT = f.NewTemp(null);
        Expr(f, a.Rhs);
        f.Emit(new Inst.LocalSet(valT));

        var last = path[^1];
        var mids = path.Take(path.Count - 1).ToList();
        // 第 k 个 Index 组件 ↔ idxTemps[k]；prefix 消费 mids 里的，终组件的临时槽由各分支自取。
        var k = 0;
        // 3. 压根 + 中间组件（FieldGet/IndexGet 带溯源，不落临时槽）。
        PushPathPrefix(f, root, mids, idxTemps, ref k);
        switch (a.Op, last)
        {
            case (HirAssignOp.Assign, { Field: { } fname }):
                f.Emit(new Inst.LocalGet(valT));
                f.Emit(new Inst.FieldSet(fname));
                break;
            case (HirAssignOp.Assign, { Index: not null }):
                f.Emit(new Inst.LocalGet(idxTemps[k]));
                f.Emit(new Inst.LocalGet(valT));
                f.Emit(new Inst.IndexSet());
                break;
            case (_, _) when isConcat:
                // 数组 `+=`：读终组件（容器共享堆身份）+ 右值 → 原位拼接。
                if (last.Field is { } cf)
                {
                    f.Emit(new Inst.FieldGet(cf));
                }
                else
                {
                    f.Emit(new Inst.LocalGet(idxTemps[k]));
                    f.Emit(new Inst.IndexGet());
                }
                f.Emit(new Inst.LocalGet(valT));
                f.Emit(new Inst.ArrayPush());
                break;
            default:
                {
                    // 复合赋值：读旧值 → 运算 → 写回原槽（读写都经带溯源操作数，
                    // place_store 落到根槽本体）。
                    if (last.Field is { } rf)
                    {
                        f.Emit(new Inst.FieldGet(rf));
                    }
                    else
                    {
                        f.Emit(new Inst.LocalGet(idxTemps[k]));
                        f.Emit(new Inst.IndexGet());
                    }
                    f.Emit(new Inst.LocalGet(valT));
                    f.Emit(new Inst.Binary(AssignBinop(a.Op)));
                    var newT = f.NewTemp(null);
                    f.Emit(new Inst.LocalSet(newT));
                    // 写回：重压根 + 中间组件（两读间无副作用，槽值未变）。
                    var k2 = 0;
                    PushPathPrefix(f, root, mids, idxTemps, ref k2);
                    if (last.Field is { } wf)
                    {
                        f.Emit(new Inst.LocalGet(newT));
                        f.Emit(new Inst.FieldSet(wf));
                    }
                    else
                    {
                        f.Emit(new Inst.LocalGet(idxTemps[k2]));
                        f.Emit(new Inst.LocalGet(newT));
                        f.Emit(new Inst.IndexSet());
                    }
                    break;
                }
        }
    }

    /// <summary>压根槽与中间组件（FieldGet/LocalGet(idx)+IndexGet，操作数带 place 溯源）。
    /// <paramref name="k"/> 沿途消费 Index 组件的临时槽序号。</summary>
    private void PushPathPrefix(FnCtx f, Place root, List<PathComp> mids, List<LocalId> idxTemps, ref int k)
    {
        switch (root)
        {
            case Place.OfSlot { Slot: var s }:
                f.Emit(new Inst.LocalGet(s.Local));
                break;
            case Place.OfGlobal { Global: var g }:
                f.Emit(new Inst.GlobalGet(g));
                break;
        }
        foreach (var comp in mids)
        {
            if (comp.Field is { } fname)
            {
                f.Emit(new Inst.FieldGet(fname));
            }
            else
            {
                if (k >= idxTemps.Count)
                {
                    throw Abort("路径 Index 临时槽缺失（lowering 内部序错）");
                }
                var t = idxTemps[k];
                k++;
                f.Emit(new Inst.LocalGet(t));
                f.Emit(new Inst.IndexGet());
            }
        }
    }

    // ———— 表达式 ————

    /// <summary>该表达式求值后是否在栈上留值（栈效应声明的唯一事实来源）。
    /// 调用看目标签名；内建看单元面；其余看节点类型。</summary>
    private bool ExprHasValue(HirExpr e)
    {
        switch (e)
        {
            case HirExpr.Call { Func: { } sym1 }:
                return _fnSigBySym.TryGetValue(sym1, out var sig1) && sig1.HasRet;
            case HirExpr.Call { Builtin: { } name }:
                return name is not ("println" or "print" or "assert" or "send");
            case HirExpr.MethodCall { Method: { } sym2 }:
                return _fnSigBySym.TryGetValue(sym2, out var sig2) && sig2.HasRet;
            default:
                return TypeHasValue(e);
        }
    }

    /// <summary>节点类型是否携带值（Unit/Unknown/Invalid/Never 视为无值：void 调用与
    /// checker 缺口面；与栈平衡的保守侧一致）。</summary>
    private bool TypeHasValue(HirExpr e)
    {
        if (MetaOf(e).TypeId is not { } t)
        {
            return false;
        }
        return _types.Data(t) is not (TypeData.Unit or TypeData.Unknown or TypeData.Invalid or TypeData.Never);
    }

    /// <summary>表达式下降：进入时栈必须干净（深度 0），返回时恰留 0/1 个值（1 ⇔ ExprHasValue）。</summary>
    private void Expr(FnCtx f, HirExpr e)
    {
        switch (e)
        {
            case HirExpr.Lit l:
                EmitLit(f, l.Value);
                break;
            case HirExpr.LocalRef { Name: var name }:
                LoadPlace(f, ResolvePlace(f, name));
                break;
            case HirExpr.FnRef fnRef:
                {
                    // 顶层 fn 一等引用 = 零捕获闭包值。
                    if (fnRef.Meta.Sym is not { } sym)
                    {
                        throw Abort("fn 引用缺符号结论（语义缺口）");
                    }
                    var fid = FnBySymOrAbort(sym);
                    f.Emit(new Inst.ClosureNew(fid, 0));
                    break;
                }
            case HirExpr.BuiltinRef { Name: var bname }:
                throw Abort(
                    $"内建 {bname} 的一等引用无 IR 形态（CallBuiltin 为指令非值）；语义面未冻结，登记 SPEC-GAP 候选");
            case HirExpr.Ctor ctor:
                {
                    if (ctor.CtorSym is not { } csym)
                    {
                        throw Abort("构造器缺符号结论（语义缺口）");
                    }
                    var (enumId, caseId) = CtorCase(csym);
                    // 载荷按 case 声明序压栈；实参求值左到右（先落临时槽）。
                    var argc = _enums[(int)enumId.Value].Cases[caseId].PayloadTypes.Count;
                    var tmps = ExprsToTemps(f, ctor.Args);
                    if (tmps.Count != argc)
                    {
                        throw Abort($"构造器实参 {tmps.Count} 个 != case 载荷 {argc} 个（语义缺口）");
                    }
                    foreach (var t in tmps)
                    {
                        f.Emit(new Inst.LocalGet(t));
                    }
                    f.Emit(new Inst.EnumNew(enumId, caseId));
                    break;
                }
            case HirExpr.Call call:
                {
                    if (call.Func is { } fsym)
                    {
                        var fid = FnBySymOrAbort(fsym);
                        var tmps = ExprsToTemps(f, call.Args);
                        foreach (var t in tmps)
                        {
                            f.Emit(new Inst.LocalGet(t));
                        }
                        f.Emit(new Inst.Call(fid));
                    }
                    else if (call.Builtin is { } bname)
                    {
                        BuiltinCall(f, bname, call.Args, e);
                    }
                    else if (call.Callee is { } c)
                    {
                        // 函数值调用：闭包值先压，再压实参（CALL-CLOSURE-003）。
                        var calleeT = f.NewTemp(null);
                        Expr(f, c);
                        f.Emit(new Inst.LocalSet(calleeT));
                        var tmps = ExprsToTemps(f, call.Args);
                        f.Emit(new Inst.LocalGet(calleeT));
                        foreach (var t in tmps)
                        {
                            f.Emit(new Inst.LocalGet(t));
                        }
                        f.Emit(new Inst.CallIndirect(call.Args.Count, TypeHasValue(e)));
                    }
                    else
                    {
                        throw Abort("调用节点 func/builtin/callee 全空（HIR 缺口）");
                    }
                    break;
                }
            case HirExpr.MethodCall mc:
                {
                    if (mc.Method is { } msym)
                    {
                        // 方法调用：接收者先于实参（EVAL-CALL-002）。
                        var fid = FnBySymOrAbort(msym);
                        var recvT = f.NewTemp(null);
                        Expr(f, mc.Recv);
                        f.Emit(new Inst.LocalSet(recvT));
                        var tmps = ExprsToTemps(f, mc.Args);
                        f.Emit(new Inst.LocalGet(recvT));
                        foreach (var t in tmps)
                        {
                            f.Emit(new Inst.LocalGet(t));
                        }
                        f.Emit(new Inst.Call(fid));
                    }
                    else
                    {
                        // 字段函数值间接调用（CALL-RESOLVE-001）：
                        // callee = 字段读取（值 = 闭包），闭包值先压再压实参。
                        if (mc.FieldName is not { } fname)
                        {
                            throw Abort("MethodCall 缺 method 与 field（HIR 缺口）");
                        }
                        var calleeT = f.NewTemp(null);
                        Expr(f, mc.Recv);
                        f.Emit(new Inst.FieldGet(fname));
                        f.Emit(new Inst.LocalSet(calleeT));
                        var tmps = ExprsToTemps(f, mc.Args);
                        f.Emit(new Inst.LocalGet(calleeT));
                        foreach (var t in tmps)
                        {
                            f.Emit(new Inst.LocalGet(t));
                        }
                        f.Emit(new Inst.CallIndirect(mc.Args.Count, TypeHasValue(e)));
                    }
                    break;
                }
            case HirExpr.Unary un:
                if (un.Op == HirUnaryOp.Ref)
                {
                    // `&x`（E3012 仅变量）：装箱槽位直接持 Ref。
                    if (un.Expr is not HirExpr.LocalRef { Name: var rname })
                    {
                        throw Abort("& 操作数非变量（E3012 已拒绝）");
                    }
                    var slot = f.Resolve(rname)
                        ?? throw Abort("& 仅可作用于局部变量（全局取引用无 IR 形态，SPEC-GAP 候选）");
                    if (slot.Kind == SlotKind.Plain)
                    {
                        throw Abort($"& 目标 {rname} 未装箱（装箱名集缺口）");
                    }
                    f.Emit(new Inst.LocalGet(slot.Local));
                }
                else
                {
                    Expr(f, un.Expr);
                    f.Emit(new Inst.Unary(un.Op == HirUnaryOp.Neg ? Unop.Neg : Unop.Not));
                }
                break;
            case HirExpr.Binary bin:
                {
                    var l = f.NewTemp(null);
                    Expr(f, bin.Lhs);
                    f.Emit(new Inst.LocalSet(l));
                    var r = f.NewTemp(null);
                    Expr(f, bin.Rhs);
                    f.Emit(new Inst.LocalSet(r));
                    f.Emit(new Inst.LocalGet(l));
                    f.Emit(new Inst.LocalGet(r));
                    f.Emit(new Inst.Binary(MapBinop(bin.Op)));
                    break;
                }
            case HirExpr.AndThen sc:
                ShortCircuit(f, sc.Lhs, sc.Rhs, isOr: false);
                break;
            case HirExpr.OrElse sc:
                ShortCircuit(f, sc.Lhs, sc.Rhs, isOr: true);
                break;
            case HirExpr.Range range:
                {
                    var l = f.NewTemp(null);
                    Expr(f, range.Lo);
                    f.Emit(new Inst.LocalSet(l));
                    var h = f.NewTemp(null);
                    Expr(f, range.Hi);
                    f.Emit(new Inst.LocalSet(h));
                    f.Emit(new Inst.LocalGet(l));
                    f.Emit(new Inst.LocalGet(h));
                    f.Emit(new Inst.RangeNew());
                    break;
                }
            case HirExpr.Index index:
                {
                    var o = f.NewTemp(null);
                    Expr(f, index.Obj);
                    f.Emit(new Inst.LocalSet(o));
                    var i = f.NewTemp(null);
                    Expr(f, index.Idx);
                    f.Emit(new Inst.LocalSet(i));
                    f.Emit(new Inst.LocalGet(o));
                    f.Emit(new Inst.LocalGet(i));
                    f.Emit(new Inst.IndexGet());
                    break;
                }
            case HirExpr.Slice slice:
                {
                    var o = f.NewTemp(null);
                    Expr(f, slice.Obj);
                    f.Emit(new Inst.LocalSet(o));
                    var l = f.NewTemp(null);
                    Expr(f, slice.Lo);
                    f.Emit(new Inst.LocalSet(l));
                    var h = f.NewTemp(null);
                    Expr(f, slice.Hi);
                    f.Emit(new Inst.LocalSet(h));
                    f.Emit(new Inst.LocalGet(o));
                    f.Emit(new Inst.LocalGet(l));
                    f.Emit(new Inst.LocalGet(h));
                    f.Emit(new Inst.SliceGet());
                    break;
                }
            case HirExpr.Field field:
                Expr(f, field.Obj);
                f.Emit(new Inst.FieldGet(field.Name));
                break;
            case HirExpr.Try tryE:
                {
                    // `?` 传播（RESULT-TRY-003）：Ok 压解包值继续；Err 存槽跳 fail 块。
                    if (f.ErrSlot is not { } errSlot)
                    {
                        errSlot = f.NewLocal("<err>", TypeTables.Error);
                        f.ErrSlot = errSlot;
                    }
                    var failB = f.NewBlock();
                    Expr(f, tryE.Expr);
                    f.Emit(new Inst.Try(errSlot, failB));
                    f.PendingTryFails.Add(failB);
                    break;
                }
            case HirExpr.StructLit lit:
                {
                    var sid = StructIdOfType(lit.Meta.TypeId);
                    // 字段值按源码序求值（EVAL-LITERAL-004），按声明序压栈。
                    var temps = new List<(string Name, LocalId Temp)>(lit.Fields.Count);
                    foreach (var fl in lit.Fields)
                    {
                        var t = f.NewTemp(null);
                        Expr(f, fl.Value);
                        f.Emit(new Inst.LocalSet(t));
                        temps.Add((fl.Name, t));
                    }
                    var decl = _structs[(int)sid.Value];
                    foreach (var fd in decl.Fields)
                    {
                        LocalId? t = null;
                        foreach (var p in temps)
                        {
                            if (p.Name == fd.Name)
                            {
                                t = p.Temp;
                                break;
                            }
                        }
                        if (t is null)
                        {
                            throw Abort($"struct {decl.Name} 字面量缺字段 {fd.Name}（语义缺口）");
                        }
                        f.Emit(new Inst.LocalGet(t.Value));
                    }
                    f.Emit(new Inst.StructNew(sid));
                    break;
                }
            case HirExpr.MapLit map:
                {
                    // 键先值后、逐对左到右（EVAL-LITERAL-004）；压序 k1,v1,k2,v2。
                    var temps = new List<(LocalId K, LocalId V)>(map.Items.Count);
                    foreach (var it in map.Items)
                    {
                        var k = f.NewTemp(null);
                        Expr(f, it.Key);
                        f.Emit(new Inst.LocalSet(k));
                        var v = f.NewTemp(null);
                        Expr(f, it.Val);
                        f.Emit(new Inst.LocalSet(v));
                        temps.Add((k, v));
                    }
                    foreach (var (k, v) in temps)
                    {
                        f.Emit(new Inst.LocalGet(k));
                        f.Emit(new Inst.LocalGet(v));
                    }
                    f.Emit(new Inst.MapNew(map.Items.Count));
                    break;
                }
            case HirExpr.ArrayLit arr:
                {
                    var tmps = ExprsToTemps(f, arr.Elems);
                    foreach (var t in tmps)
                    {
                        f.Emit(new Inst.LocalGet(t));
                    }
                    f.Emit(new Inst.ArrayNew(arr.Elems.Count));
                    break;
                }
            case HirExpr.Match match:
                MatchExpr(f, match.Kind, match.Scrutinee, match.Arms, e);
                break;
            case HirExpr.FnLit fnlit:
                {
                    // 捕获 Ref 按 captures 升序压栈（A4 对接约定）。
                    foreach (var name in fnlit.Captures)
                    {
                        var slot = f.Resolve(name)
                            ?? throw Abort($"捕获名 {name} 未解析到外层槽位（递归闭包/语义缺口）");
                        if (slot.Kind == SlotKind.Plain)
                        {
                            throw Abort($"捕获绑定 {name} 未装箱（装箱名集缺口）");
                        }
                        f.Emit(new Inst.LocalGet(slot.Local));
                    }
                    var fid = new FunctionId((uint)_functions.Count);
                    _functions.Add(new IrFunction("<fnlit>", 0, new FnSig(0, false), [], [], null));
                    f.Emit(new Inst.ClosureNew(fid, fnlit.Captures.Count));
                    // fnlit 是函数边界：新 FnCtx（捕获槽 → 参数 → 体），作用域不接外层。
                    var boxed = CollectBoxedNames(fnlit.Body);
                    var g = new FnCtx("<fnlit>", fnlit.Captures, null, fnlit.Params, fnlit.Ret, boxed);
                    PrescanNested(fnlit.Body);
                    var terminated = Stmts(g, fnlit.Body);
                    if (!terminated)
                    {
                        g.SetTerm(new Terminator.Return(false));
                    }
                    FillTryFails(g);
                    _functions[(int)fid.Value] = g.Finish();
                    break;
                }
            default:
                throw Abort("未登记的 HirExpr 变体");
        }
    }

    private FunctionId FnBySymOrAbort(SymbolId sym) => _fnBySym.TryGetValue(sym, out var fid)
        ? fid
        : throw Abort($"调用符号 {sym.Value} 未登记（语义缺口）");

    private void EmitLit(FnCtx f, HirLit l)
    {
        switch (l.Kind)
        {
            case LitKind.Int: f.Emit(new Inst.ConstI64(l.I64)); break;
            case LitKind.Float: f.Emit(new Inst.ConstF64(l.F64)); break;
            case LitKind.Str: f.Emit(new Inst.ConstStr(l.S)); break;
            case LitKind.Bool: f.Emit(new Inst.ConstBool(l.B)); break;
        }
    }

    /// <summary>逐实参求值并落临时槽（求值序 = 源码序；返回槽序列供压栈）。</summary>
    private List<LocalId> ExprsToTemps(FnCtx f, IReadOnlyList<HirExpr> args)
    {
        var outp = new List<LocalId>(args.Count);
        foreach (var a in args)
        {
            var t = f.NewTemp(null);
            Expr(f, a);
            f.Emit(new Inst.LocalSet(t));
            outp.Add(t);
        }
        return outp;
    }

    private void LoadPlace(FnCtx f, Place place)
    {
        switch (place)
        {
            case Place.OfSlot { Slot: { Kind: SlotKind.Plain, Local: var plain } }:
                f.Emit(new Inst.LocalGet(plain));
                break;
            case Place.OfSlot { Slot: { Local: var boxed } }:
                f.Emit(new Inst.LocalGet(boxed));
                f.Emit(new Inst.Deref());
                break;
            case Place.OfGlobal { Global: var g }:
                f.Emit(new Inst.GlobalGet(g));
                break;
        }
    }

    /// <summary>短路降形（EVAL-SHORTCUT-006）：左值 Branch 分派，短路值常量落槽。</summary>
    private void ShortCircuit(FnCtx f, HirExpr lhs, HirExpr rhs, bool isOr)
    {
        var res = f.NewTemp(TypeTables.Bool);
        var rhsB = f.NewBlock();
        var shortB = f.NewBlock();
        var endB = f.NewBlock();
        Expr(f, lhs);
        // &&：左 false → 短路 false 不求值右；||：左 true → 短路 true。
        f.SetTerm(new Terminator.Branch(
            isOr ? shortB : rhsB,
            isOr ? rhsB : shortB));
        f.SwitchTo(rhsB);
        Expr(f, rhs);
        f.Emit(new Inst.LocalSet(res));
        f.SetTerm(new Terminator.Goto(endB));
        f.SwitchTo(shortB);
        f.Emit(new Inst.ConstBool(isOr));
        f.Emit(new Inst.LocalSet(res));
        f.SetTerm(new Terminator.Goto(endB));
        f.SwitchTo(endB);
        f.Emit(new Inst.LocalGet(res));
    }

    private void BuiltinCall(FnCtx f, string name, IReadOnlyList<HirExpr> args, HirExpr call)
    {
        // makeChan/send/recv 升专指令（冻结约定）；其余落 CallBuiltin。
        switch (name)
        {
            case "makeChan":
                f.Emit(new Inst.ChanNew());
                break;
            case "send":
                {
                    if (args.Count != 2)
                    {
                        throw Abort($"send 实参 {args.Count} 个 != 2（语义缺口）");
                    }
                    var ch = f.NewTemp(null);
                    Expr(f, args[0]);
                    f.Emit(new Inst.LocalSet(ch));
                    var val = f.NewTemp(null);
                    Expr(f, args[1]);
                    f.Emit(new Inst.LocalSet(val));
                    f.Emit(new Inst.LocalGet(ch));
                    f.Emit(new Inst.LocalGet(val));
                    f.Emit(new Inst.ChanSend());
                    break;
                }
            case "recv":
                {
                    if (args.Count != 1)
                    {
                        throw Abort($"recv 实参 {args.Count} 个 != 1（语义缺口）");
                    }
                    Expr(f, args[0]);
                    f.Emit(new Inst.ChanRecv());
                    break;
                }
            default:
                {
                    var tmps = ExprsToTemps(f, args);
                    foreach (var t in tmps)
                    {
                        f.Emit(new Inst.LocalGet(t));
                    }
                    f.Emit(new Inst.CallBuiltin(name, args.Count, TypeHasValue(call)));
                    break;
                }
        }
    }

    // ———— match 降形 ————

    private void MatchExpr(FnCtx f, Hir.MatchKind kind, HirExpr scrutinee, IReadOnlyList<HirMatchArm> arms, HirExpr matchNode)
    {
        var matchTy = MetaOf(matchNode).TypeId;
        var scrut = f.NewTemp(null);
        Expr(f, scrutinee);
        f.Emit(new Inst.LocalSet(scrut));
        // 分派块 = 持有 scrut 的当前块；臂体下降会切走 cur，由 match_literal/match_enum
        // 负责切回并终接分派块。
        var dispatchB = f.Cur;
        // 值性取 match 节点类型（checker 已统一臂型）：void 臂（如 `Ok(v) => println(v)`）
        // 无结果槽，臂体只求值副作用。
        var hasValue = TypeHasValue(matchNode);
        LocalId? res = hasValue ? f.NewTemp(matchTy) : null;
        var endB = f.NewBlock();
        var plan = new MatchPlan(dispatchB, scrut, scrutinee, arms, res, endB);
        switch (kind)
        {
            case Hir.MatchKind.Literal:
                MatchLiteral(f, plan);
                break;
            case Hir.MatchKind.Enum or Hir.MatchKind.Result or Hir.MatchKind.Option:
                MatchEnum(f, plan);
                break;
            default:
                throw Abort("未登记的 MatchKind");
        }
        f.SwitchTo(endB);
        if (res is { } r)
        {
            f.Emit(new Inst.LocalGet(r));
        }
    }

    /// <summary>臂体公共尾：绑定作用域内求值臂体 →（有值 match）结果槽 → Goto(end)。</summary>
    private void ArmBody(FnCtx f, HirExpr body, LocalId? res, BlockId endB)
    {
        Expr(f, body);
        if (res is { } r)
        {
            f.Emit(new Inst.LocalSet(r));
        }
        f.SetTerm(new Terminator.Goto(endB));
    }

    /// <summary>字面量 match：int → SwitchInt；str/bool/float → Eq 链 + Branch。
    /// 负责终接 plan 的分派块。</summary>
    private void MatchLiteral(FnCtx f, MatchPlan plan)
    {
        var scrutIsInt = MetaOf(plan.Scrutinee).TypeId is { } st && _types.Data(st) is TypeData.Int;
        var allIntArms = true;
        foreach (var a in plan.Arms)
        {
            if (a.Pat is HirPat.Ctor)
            {
                allIntArms = false;
                break;
            }
            if (a.Pat is HirPat.Lit { Value: { } lit } && lit.Kind != LitKind.Int)
            {
                allIntArms = false;
                break;
            }
        }
        if (scrutIsInt && allIntArms)
        {
            // SwitchInt：arms 依源码臂序，首个命中即跳；通配/绑定臂 = default。
            var switchArms = new List<(long Value, BlockId Target)>();
            BlockId? @default = null;
            var armBlocks = new List<(BlockId Block, HirMatchArm Arm)>();
            foreach (var a in plan.Arms)
            {
                switch (a.Pat)
                {
                    case HirPat.Lit { Value: { } lit } when lit.Kind == LitKind.Int:
                        {
                            var b = f.NewBlock();
                            switchArms.Add((lit.I64, b));
                            armBlocks.Add((b, a));
                            break;
                        }
                    case HirPat.Wild or HirPat.Bind:
                        if (@default is null)
                        {
                            var b = f.NewBlock();
                            @default = b;
                            armBlocks.Add((b, a));
                        }
                        // 第二个通配臂不可达（首中即跳），不生成。
                        break;
                    default:
                        throw Abort("int match 混入构造模式（语义缺口）");
                }
            }
            f.SwitchTo(plan.DispatchB);
            f.Emit(new Inst.LocalGet(plan.Scrut));
            f.SetTerm(new Terminator.SwitchInt(switchArms, @default));
            foreach (var (b, a) in armBlocks)
            {
                f.SwitchTo(b);
                f.PushScope();
                BindWhole(f, a.Pat, plan.Scrut);
                ArmBody(f, a.Body, plan.Res, plan.EndB);
                f.PopScope();
            }
        }
        else
        {
            // Eq 链：逐臂 LocalGet(scrut) → 常量 → Eq → Branch；首检查落分派块。
            f.SwitchTo(plan.DispatchB);
            var fallback = WildcardArm(plan.Arms);
            foreach (var a in plan.Arms)
            {
                if (a.Pat is not HirPat.Lit { Value: { } lit })
                {
                    continue;
                }
                var armB = f.NewBlock();
                var failB = f.NewBlock();
                f.Emit(new Inst.LocalGet(plan.Scrut));
                EmitLit(f, lit);
                f.Emit(new Inst.Binary(Binop.Eq));
                f.SetTerm(new Terminator.Branch(armB, failB));
                f.SwitchTo(armB);
                f.PushScope();
                ArmBody(f, a.Body, plan.Res, plan.EndB);
                f.PopScope();
                f.SwitchTo(failB);
            }
            if (fallback is { } fb)
            {
                f.PushScope();
                BindWhole(f, fb.Pat, plan.Scrut);
                ArmBody(f, fb.Body, plan.Res, plan.EndB);
                f.PopScope();
            }
            else
            {
                // 无兜底：运行时无匹配 = RUNTIME-MATCH-001（消费方解释）。
                f.MarkDead(f.Cur);
                f.SetTerm(new Terminator.Unreachable());
            }
        }
    }

    private static HirMatchArm? WildcardArm(IReadOnlyList<HirMatchArm> arms)
    {
        foreach (var a in arms)
        {
            if (a.Pat is HirPat.Wild or HirPat.Bind)
            {
                return a;
            }
        }
        return null;
    }

    /// <summary>通配/绑定臂对 scrut 的整体绑定（绑定臂落槽；通配臂无操作）。</summary>
    private void BindWhole(FnCtx f, HirPat pat, LocalId scrut)
    {
        if (pat is HirPat.Bind { Name: var name })
        {
            var slot = DeclareBinding(f, name, null);
            f.Emit(new Inst.LocalGet(scrut));
            if (f.Boxed.Contains(name))
            {
                f.Emit(new Inst.BoxNew());
            }
            f.Emit(new Inst.LocalSet(slot));
        }
    }

    /// <summary>enum/Option/Result match：SwitchEnum 分派 + 同 case 候选链 + 递归子模式。
    /// 负责终接 plan 的分派块。</summary>
    private void MatchEnum(FnCtx f, MatchPlan plan)
    {
        var enumId = EnumIdOfType(MetaOf(plan.Scrutinee).TypeId);
        var caseCount = _enums[(int)enumId.Value].Cases.Count;
        // 臂分类：构造臂按 case 分组（源码序）；首个通配/绑定臂 = 兜底。
        var perCase = new List<List<HirMatchArm>>(caseCount);
        for (var i = 0; i < caseCount; i++)
        {
            perCase.Add([]);
        }
        HirMatchArm? wildcard = null;
        foreach (var a in plan.Arms)
        {
            switch (a.Pat)
            {
                case HirPat.Ctor { CtorSym: { } csym }:
                    {
                        var ci = (int)_symbols.Get(csym).CaseIndex;
                        if (ci >= caseCount)
                        {
                            throw Abort($"case 序 {ci} 越界（enum 共 {caseCount} case；语义缺口）");
                        }
                        perCase[ci].Add(a);
                        break;
                    }
                case HirPat.Ctor:
                    throw Abort("构造模式缺符号结论（语义缺口）");
                case HirPat.Wild or HirPat.Bind when wildcard is null:
                    wildcard = a;
                    break;
                case HirPat.Lit:
                    throw Abort("enum match 混入字面量模式（E4002 已拒绝）");
            }
        }
        // 兜底块（通配臂或不可达锚）。
        var fallbackB = f.NewBlock();
        if (wildcard is { } wc)
        {
            f.SwitchTo(fallbackB);
            f.PushScope();
            BindWhole(f, wc.Pat, plan.Scrut);
            ArmBody(f, wc.Body, plan.Res, plan.EndB);
            f.PopScope();
        }
        else
        {
            f.SwitchTo(fallbackB);
            f.MarkDead(fallbackB);
            f.SetTerm(new Terminator.Unreachable());
        }
        // 每 case：候选链（源码序），末候选失败落兜底。
        var switchArms = new List<BlockId>(caseCount);
        foreach (var candidates in perCase)
        {
            if (candidates.Count == 0)
            {
                switchArms.Add(fallbackB);
                continue;
            }
            var cblocks = new List<BlockId>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                cblocks.Add(f.NewBlock());
            }
            switchArms.Add(cblocks[0]);
            for (var k = 0; k < candidates.Count; k++)
            {
                var onFail = k + 1 < cblocks.Count ? cblocks[k + 1] : fallbackB;
                if (candidates[k].Pat is not HirPat.Ctor { Subs: var subs })
                {
                    throw Abort("候选臂非构造模式（内部序错）");
                }
                f.SwitchTo(cblocks[k]);
                f.PushScope();
                // 载荷解构：按位置取载荷入临时槽，再逐子模式匹配。
                var pays = new List<LocalId>(subs.Count);
                for (var i = 0; i < subs.Count; i++)
                {
                    var p = f.NewTemp(null);
                    f.Emit(new Inst.LocalGet(plan.Scrut));
                    f.Emit(new Inst.EnumPayloadGet(i));
                    f.Emit(new Inst.LocalSet(p));
                    pays.Add(p);
                }
                for (var i = 0; i < subs.Count; i++)
                {
                    var okB = EmitSubPat(f, subs[i], pays[i], onFail);
                    f.SwitchTo(okB);
                }
                ArmBody(f, candidates[k].Body, plan.Res, plan.EndB);
                f.PopScope();
            }
        }
        f.SwitchTo(plan.DispatchB);
        f.Emit(new Inst.LocalGet(plan.Scrut));
        f.SetTerm(new Terminator.SwitchEnum(enumId, switchArms, wildcard is null ? null : fallbackB));
    }

    /// <summary>子模式匹配：值驻 val 槽；失败跳 onFail；成功控制到达返回块（绑定已装入
    /// 当前作用域）。通配/绑定直落，字面量 Eq 检查，嵌套构造递归 SwitchEnum。</summary>
    private BlockId EmitSubPat(FnCtx f, HirPat pat, LocalId val, BlockId onFail)
    {
        switch (pat)
        {
            case HirPat.Wild:
                return f.Cur;
            case HirPat.Bind { Name: var name }:
                {
                    var slot = DeclareBinding(f, name, null);
                    f.Emit(new Inst.LocalGet(val));
                    if (f.Boxed.Contains(name))
                    {
                        f.Emit(new Inst.BoxNew());
                    }
                    f.Emit(new Inst.LocalSet(slot));
                    return f.Cur;
                }
            case HirPat.Lit { Value: { } lit }:
                {
                    var okB = f.NewBlock();
                    f.Emit(new Inst.LocalGet(val));
                    EmitLit(f, lit);
                    f.Emit(new Inst.Binary(Binop.Eq));
                    f.SetTerm(new Terminator.Branch(okB, onFail));
                    f.SwitchTo(okB);
                    return okB;
                }
            case HirPat.Ctor { CtorSym: { } csym, Subs: var subs }:
                {
                    var (enumId, caseId) = CtorCase(csym);
                    var caseCount = _enums[(int)enumId.Value].Cases.Count;
                    var entry = f.NewBlock();
                    var arms = new List<BlockId>(caseCount);
                    for (var i = 0; i < caseCount; i++)
                    {
                        arms.Add(onFail);
                    }
                    arms[caseId] = entry;
                    f.Emit(new Inst.LocalGet(val));
                    f.SetTerm(new Terminator.SwitchEnum(enumId, arms, null));
                    f.SwitchTo(entry);
                    var cur = f.Cur;
                    for (var i = 0; i < subs.Count; i++)
                    {
                        var p = f.NewTemp(null);
                        f.Emit(new Inst.LocalGet(val));
                        f.Emit(new Inst.EnumPayloadGet(i));
                        f.Emit(new Inst.LocalSet(p));
                        cur = EmitSubPat(f, subs[i], p, onFail);
                        f.SwitchTo(cur);
                    }
                    return cur;
                }
            default:
                throw Abort("未登记的 HirPat 变体");
        }
    }

    // ———— 自由函数 ————

    /// <summary>节点元数据读取（HIR 元数据按变体内嵌，统一取用口）。</summary>
    private static NodeInfo MetaOf(HirExpr e) => e switch
    {
        HirExpr.Lit l => l.Value.Meta,
        HirExpr.LocalRef x => x.Meta,
        HirExpr.FnRef x => x.Meta,
        HirExpr.BuiltinRef x => x.Meta,
        HirExpr.Ctor x => x.Meta,
        HirExpr.Call x => x.Meta,
        HirExpr.MethodCall x => x.Meta,
        HirExpr.Unary x => x.Meta,
        HirExpr.Binary x => x.Meta,
        HirExpr.AndThen x => x.Meta,
        HirExpr.OrElse x => x.Meta,
        HirExpr.Range x => x.Meta,
        HirExpr.Index x => x.Meta,
        HirExpr.Slice x => x.Meta,
        HirExpr.Field x => x.Meta,
        HirExpr.Try x => x.Meta,
        HirExpr.StructLit x => x.Meta,
        HirExpr.MapLit x => x.Meta,
        HirExpr.ArrayLit x => x.Meta,
        HirExpr.Match x => x.Meta,
        HirExpr.FnLit x => x.Meta,
        _ => throw new InvalidOperationException("HIR 表达式变体未穷尽"),
    };

    /// <summary>装箱名集：函数体内所有 FnLit.Captures 名 ∪ `&amp;x` 操作数名。
    /// 名字集过近似（同名兄弟绑定亦装箱，语义透明）；不穿越嵌套具名 fn 边界。</summary>
    private static HashSet<string> CollectBoxedNames(IReadOnlyList<HirStmt> stmts)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        CollectStmts(stmts, set);
        return set;
    }

    private static void CollectStmts(IReadOnlyList<HirStmt> stmts, HashSet<string> set)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case HirStmt.Let l:
                    CollectExpr(l.Value.Init, set);
                    break;
                case HirStmt.Assign a:
                    CollectExpr(a.Value.Target, set);
                    CollectExpr(a.Value.Rhs, set);
                    break;
                case HirStmt.Eval ev:
                    CollectExpr(ev.Expr, set);
                    break;
                case HirStmt.Return { Val: { } rv }:
                    CollectExpr(rv, set);
                    break;
                case HirStmt.If ifStmt:
                    CollectExpr(ifStmt.Cond, set);
                    CollectStmts(ifStmt.Then, set);
                    CollectStmts(ifStmt.Els, set);
                    break;
                case HirStmt.While w:
                    CollectExpr(w.Cond, set);
                    CollectStmts(w.Body, set);
                    break;
                case HirStmt.ForIn forIn:
                    CollectExpr(forIn.Iter, set);
                    CollectStmts(forIn.Body, set);
                    break;
                case HirStmt.Spawn sp:
                    foreach (var a in sp.Args)
                    {
                        CollectExpr(a, set);
                    }
                    break;
                // 嵌套具名 fn 是独立函数边界：捕获不穿越，其体自行收集（无 case，直接跳过）。
                default:
                    break;
            }
        }
    }

    private static void CollectExpr(HirExpr e, HashSet<string> set)
    {
        switch (e)
        {
            case HirExpr.FnLit fnlit:
                foreach (var n in fnlit.Captures)
                {
                    set.Add(n);
                }
                CollectStmts(fnlit.Body, set);
                break;
            case HirExpr.Unary { Op: HirUnaryOp.Ref, Expr: HirExpr.LocalRef { Name: var name } }:
                set.Add(name);
                break;
            case HirExpr.Unary un:
                CollectExpr(un.Expr, set);
                break;
            case HirExpr.Binary bin:
                CollectExpr(bin.Lhs, set);
                CollectExpr(bin.Rhs, set);
                break;
            case HirExpr.AndThen sc:
                CollectExpr(sc.Lhs, set);
                CollectExpr(sc.Rhs, set);
                break;
            case HirExpr.OrElse sc:
                CollectExpr(sc.Lhs, set);
                CollectExpr(sc.Rhs, set);
                break;
            case HirExpr.Range r:
                CollectExpr(r.Lo, set);
                CollectExpr(r.Hi, set);
                break;
            case HirExpr.Index ix:
                CollectExpr(ix.Obj, set);
                CollectExpr(ix.Idx, set);
                break;
            case HirExpr.Slice sl:
                CollectExpr(sl.Obj, set);
                CollectExpr(sl.Lo, set);
                CollectExpr(sl.Hi, set);
                break;
            case HirExpr.Field fl:
                CollectExpr(fl.Obj, set);
                break;
            case HirExpr.Try t:
                CollectExpr(t.Expr, set);
                break;
            case HirExpr.Ctor ctor:
                foreach (var a in ctor.Args)
                {
                    CollectExpr(a, set);
                }
                break;
            case HirExpr.ArrayLit arr:
                foreach (var el in arr.Elems)
                {
                    CollectExpr(el, set);
                }
                break;
            case HirExpr.Call call:
                if (call.Callee is { } c)
                {
                    CollectExpr(c, set);
                }
                foreach (var a in call.Args)
                {
                    CollectExpr(a, set);
                }
                break;
            case HirExpr.MethodCall mc:
                CollectExpr(mc.Recv, set);
                foreach (var a in mc.Args)
                {
                    CollectExpr(a, set);
                }
                break;
            case HirExpr.StructLit lit:
                foreach (var f in lit.Fields)
                {
                    CollectExpr(f.Value, set);
                }
                break;
            case HirExpr.MapLit map:
                foreach (var it in map.Items)
                {
                    CollectExpr(it.Key, set);
                    CollectExpr(it.Val, set);
                }
                break;
            case HirExpr.Match match:
                CollectExpr(match.Scrutinee, set);
                foreach (var a in match.Arms)
                {
                    CollectExpr(a.Body, set);
                }
                break;
        }
    }

    /// <summary>预载代数类型（case 序与 RESULT-PRELUDE-001 一致；载荷类型为泛型擦除占位 null）。</summary>
    private void AddPreloadedEnum(string name, params (string Case, int Payloads)[] cases)
    {
        var id = new EnumId((uint)_enums.Count);
        var cdecls = new List<EnumCase>(cases.Length);
        foreach (var (caseName, payloads) in cases)
        {
            var ps = new List<TypeId?>(payloads);
            for (var i = 0; i < payloads; i++)
            {
                ps.Add(null);
            }
            cdecls.Add(new EnumCase(caseName, ps));
        }
        _enums.Add(new EnumDecl(name, [], cdecls));
        _enumIds[name] = id;
    }

    private static EnumDecl EnumDeclToIr(HirEnumDecl ed) => new(
        ed.Name,
        ed.TypeParams,
        ed.Cases.Select(c => new EnumCase(c.Name, c.Payloads)).ToList());

    private static int FnParamCount(HirFn f) => f.Params.Count + (f.Recv is null ? 0 : 1);

    private static Binop AssignBinop(HirAssignOp op) => op switch
    {
        HirAssignOp.AddAssign => Binop.Add,
        HirAssignOp.SubAssign => Binop.Sub,
        HirAssignOp.MulAssign => Binop.Mul,
        HirAssignOp.DivAssign => Binop.Div,
        HirAssignOp.Assign => throw new InvalidOperationException("Assign 不进复合赋值降形"),
        _ => throw new InvalidOperationException($"未登记的 AssignOp: {op}"),
    };

    private static Binop MapBinop(HirBinaryOp op) => op switch
    {
        HirBinaryOp.Eq => Binop.Eq,
        HirBinaryOp.Ne => Binop.Ne,
        HirBinaryOp.Lt => Binop.Lt,
        HirBinaryOp.Le => Binop.Le,
        HirBinaryOp.Gt => Binop.Gt,
        HirBinaryOp.Ge => Binop.Ge,
        HirBinaryOp.Add => Binop.Add,
        HirBinaryOp.Sub => Binop.Sub,
        HirBinaryOp.Mul => Binop.Mul,
        HirBinaryOp.Div => Binop.Div,
        HirBinaryOp.Rem => Binop.Rem,
        HirBinaryOp.BitAnd => Binop.BitAnd,
        HirBinaryOp.BitOr => Binop.BitOr,
        HirBinaryOp.BitXor => Binop.BitXor,
        HirBinaryOp.Shl => Binop.Shl,
        HirBinaryOp.Shr => Binop.Shr,
        _ => throw new InvalidOperationException($"未登记的 BinaryOp: {op}"),
    };

    // ———— 降形上下文 ————

    /// <summary>槽位种类：Plain 直存值；Boxed 槽位持 Ref；Capture 捕获槽（天然持 Ref）。</summary>
    private enum SlotKind
    {
        Plain,
        Boxed,
        Capture,
    }

    private readonly record struct Slot(LocalId Local, SlotKind Kind);

    /// <summary>名字的落点：局部槽位或全局。</summary>
    private abstract record Place
    {
        public sealed record OfSlot(Slot Slot) : Place;

        public sealed record OfGlobal(GlobalId Global) : Place;
    }

    /// <summary>赋值目标路径组件（源码序）：`a[i].j` = [Index(i), Field("j")]。</summary>
    private readonly struct PathComp(string? field, HirExpr? index)
    {
        public string? Field { get; } = field;
        public HirExpr? Index { get; } = index;
    }

    /// <summary>赋值目标分解：Ident 根 + Field/Index 链（checker 保证目标形态）。</summary>
    private static (HirExpr Root, List<PathComp> Path) SplitAssignPath(HirExpr target)
    {
        var comps = new List<PathComp>();
        var cur = target;
        while (true)
        {
            switch (cur)
            {
                case HirExpr.Field { Obj: var obj, Name: var name }:
                    comps.Add(new PathComp(name, null));
                    cur = obj;
                    break;
                case HirExpr.Index { Obj: var obj, Idx: var idx }:
                    comps.Add(new PathComp(null, idx));
                    cur = obj;
                    break;
                default:
                    comps.Reverse();
                    return (cur, comps);
            }
        }
    }

    /// <summary>match 降形的上下文打包（分派块/scrut 槽/臂集/结果槽/汇合块）。</summary>
    private sealed record MatchPlan(
        BlockId DispatchB,
        LocalId Scrut,
        HirExpr Scrutinee,
        IReadOnlyList<HirMatchArm> Arms,
        LocalId? Res,
        BlockId EndB);

    /// <summary>每函数下降上下文：局部表（捕获 → 接收者/参数 → 局部/临时）、块图、
    /// 作用域链、循环栈、`?` 的 err 槽与 fail 块队列。</summary>
    private sealed class FnCtx
    {
        private readonly List<BlockData> _blocks = [];
        private readonly List<bool> _dead = [];
        private readonly List<List<(string Name, Slot Slot)>> _scopes = [[]];

        public FnCtx(
            string name,
            IReadOnlyList<string> captures,
            HirParam? recv,
            IReadOnlyList<HirParam> parameters,
            TypeId? ret,
            HashSet<string> boxed)
        {
            Name = name;
            CaptureCount = captures.Count;
            ParamCount = parameters.Count + (recv is null ? 0 : 1);
            HasRet = ret is not null;
            Boxed = boxed;
            // 布局 = [捕获槽][接收者][参数]（recv 为第 0 参，与 Call 压栈序一致）。
            foreach (var c in captures)
            {
                var local = PushLocal(c, null);
                Bind(c, new Slot(local, SlotKind.Capture));
            }
            if (recv is { } r)
            {
                var local = PushLocal(r.Name, r.TypeId);
                Bind(r.Name, new Slot(local, SlotKindFor(r.Name)));
            }
            foreach (var p in parameters)
            {
                var local = PushLocal(p.Name, p.TypeId);
                Bind(p.Name, new Slot(local, SlotKindFor(p.Name)));
            }
            NewBlock(); // 入口块 = 块 0
            Cur = new BlockId(0);
        }

        public string Name { get; }
        public int CaptureCount { get; }
        public int ParamCount { get; }
        public bool HasRet { get; }
        public List<LocalDecl> Locals { get; } = [];
        public BlockId Cur { get; set; }
        public List<(BlockId Cont, BlockId Brk)> Loops { get; } = [];
        public LocalId? ErrSlot { get; set; }
        public List<BlockId> PendingTryFails { get; } = [];
        public HashSet<string> Boxed { get; }

        public SlotKind SlotKindFor(string name) => Boxed.Contains(name) ? SlotKind.Boxed : SlotKind.Plain;

        public LocalId PushLocal(string name, TypeId? ty)
        {
            var id = new LocalId((uint)Locals.Count);
            Locals.Add(new LocalDecl(name, true, ty));
            return id;
        }

        public LocalId NewLocal(string name, TypeId? ty) => PushLocal(name, ty);

        public LocalId NewTemp(TypeId? ty)
        {
            var n = Locals.Count;
            return PushLocal($"<t{n}>", ty);
        }

        /// <summary>槽位种类的运行期判定（装箱参数入口序言用；构造时已登记 kind 于作用域）。</summary>
        public SlotKind ParamKind(LocalId slot) => SlotKindFor(Locals[(int)slot.Value].Name);

        public BlockId NewBlock()
        {
            _blocks.Add(new BlockData());
            _dead.Add(false);
            return new BlockId((uint)_blocks.Count - 1);
        }

        public void SwitchTo(BlockId b) => Cur = b;

        public void OpenDeadBlock()
        {
            var b = NewBlock();
            _dead[(int)b.Value] = true;
            Cur = b;
        }

        public void MarkDead(BlockId b) => _dead[(int)b.Value] = true;

        public void Emit(Inst inst) => _blocks[(int)Cur.Value].Insts.Add(inst);

        public void SetTerm(Terminator term)
        {
            var b = _blocks[(int)Cur.Value];
            if (b.Term is not null)
            {
                throw new LowerAbort($"fn {Name} block {Cur.Value} 重复终接（lowering 内部序错）");
            }
            b.Term = term;
        }

        public void PushScope() => _scopes.Add([]);

        public void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

        public void Bind(string name, Slot slot) => _scopes[^1].Add((name, slot));

        /// <summary>名字 → 槽位（内层作用域优先，后绑定遮蔽先绑定）。</summary>
        public Slot? Resolve(string name)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                var scope = _scopes[i];
                for (var j = scope.Count - 1; j >= 0; j--)
                {
                    if (scope[j].Name == name)
                    {
                        return scope[j].Slot;
                    }
                }
            }
            return null;
        }

        public List<BlockId> TakePendingTryFails()
        {
            var outp = new List<BlockId>(PendingTryFails);
            PendingTryFails.Clear();
            return outp;
        }

        /// <summary>组装 IrFunction；未终接块：死块补 Unreachable，活块为内部错误。</summary>
        public IrFunction Finish()
        {
            for (var bid = 0; bid < _blocks.Count; bid++)
            {
                if (_blocks[bid].Term is null)
                {
                    if (_dead[bid])
                    {
                        _blocks[bid].Term = new Terminator.Unreachable();
                    }
                    else
                    {
                        throw new LowerAbort($"fn {Name} block {bid} 未终接（lowering 内部序错）");
                    }
                }
            }
            var blocks = _blocks.Select(b => new Block(b.Insts, b.Term!)).ToList();
            return new IrFunction(
                Name, CaptureCount, new FnSig(ParamCount, HasRet), Locals, blocks, null);
        }

        private sealed class BlockData
        {
            public List<Inst> Insts { get; } = [];

            public Terminator? Term { get; set; }
        }
    }
}
