using Lang.Core;

namespace Lang.Runtime;

/// <summary>
/// Machine：运行时 facade——解释器（Lang.Interp）驱动的唯一状态面。
/// 组成：堆 arena、环境 arena、channel 表、调度器、定义表、内建注册表。
/// 值语义操作（引用穿透、Place 读/写、容器原位变更）都在此层实现，
/// 解释器只负责按 EVAL-* 顺序调用。
/// </summary>
public sealed class Machine
{
    /// <summary>引用穿透深度上限（x = &amp;y; y = &amp;x 式环的防护；正常程序远达不到）。</summary>
    private const int DerefLimit = 64;

    /// <summary>引用语义容器堆。</summary>
    public HeapArena Heap { get; }

    /// <summary>变量/闭包环境 arena。</summary>
    public EnvArena Envs { get; }

    /// <summary>channel 表。</summary>
    public ChannelTable Chans { get; }

    /// <summary>协作式调度器。</summary>
    public Scheduler Sched { get; }

    /// <summary>struct/enum 定义表（含预载 Option/Result/error）。</summary>
    public DefTables Defs { get; }

    /// <summary>内建注册表。</summary>
    public BuiltinRegistry Builtins { get; }

    /// <summary>根（全局）环境。</summary>
    public EnvId Root { get; }

    private readonly List<string> _fnNames = new();

    /// <summary>新 Machine：预载定义 + 核心内建 + 根环境。</summary>
    public Machine()
    {
        Heap = new HeapArena();
        Envs = new EnvArena();
        Chans = new ChannelTable();
        Sched = new Scheduler();
        Defs = DefTables.WithPrelude();
        Builtins = BuiltinRegistry.Core();
        Root = Envs.Root();
    }

    // —— 函数命名（输出 `<fn 名>` 用；id 分配由前级完成时可只登记名字） ——

    /// <summary>顺序分配 FunctionId 并登记名字（v0 解释器波次的便捷入口）。</summary>
    public FunctionId RegisterFunction(string name)
    {
        var id = new FunctionId((uint)_fnNames.Count);
        _fnNames.Add(name);
        return id;
    }

    /// <summary>登记既有 FunctionId 的名字（前级已分配 id 时）。</summary>
    public RtResult SetFnName(FunctionId id, string name)
    {
        var slot = id.Value;
        while (_fnNames.Count <= slot)
        {
            _fnNames.Add(string.Empty);
        }

        _fnNames[(int)slot] = name;
        return RtResult.Ok;
    }

    /// <summary>函数名（未登记返回空名；输出面渲染为 `<fn 匿名>`）。</summary>
    public string FnName(FunctionId id) => id.Value < (uint)_fnNames.Count ? _fnNames[(int)id.Value] : string.Empty;

    // —— 值构造便捷 ——

    /// <summary>分配 slice 并返回持索引的值。</summary>
    public Value AllocSliceValue(IReadOnlyList<Value> items) => Heap.AllocSlice(items);

    /// <summary>分配空 map 并返回持索引的值。</summary>
    public Value AllocMapValue() => Heap.AllocMap(new MapObj());

    /// <summary>makeChan() 值。</summary>
    public Value MakeChanValue() => new Value.Chan(Chans.Make());

    /// <summary>Some(v)（预载 Option case 0）。</summary>
    public Value SomeValue(Value v) => new Value.Enum(Defs.OptionDef(), 0, new[] { v });

    /// <summary>None（预载 Option case 1）。</summary>
    public Value NoneValue() => new Value.Enum(Defs.OptionDef(), 1, Array.Empty<Value>());

    /// <summary>Ok(v)（预载 Result case 0）。</summary>
    public Value OkValue(Value v) => new Value.Enum(Defs.ResultDef(), 0, new[] { v });

    /// <summary>Err(e)（预载 Result case 1）。</summary>
    public Value ErrValue(Value e) => new Value.Enum(Defs.ResultDef(), 1, new[] { e });

    /// <summary>err(msg)：构造 error{msg} struct（业务错误统一 Result[T, error]）。</summary>
    public Value ErrorValue(string msg) =>
        new Value.Struct(new StructVal(Defs.ErrorDef(), new Value[] { new Value.Str(msg) }));

    /// <summary>isNone(v)：Option 的 None case（非 Option 值 → false）。</summary>
    public bool IsNone(Value v) =>
        v is Value.Enum { Case: 1 } e && e.Def == Defs.OptionDef();

    /// <summary>enum 的 case 名（match 输出/诊断用）。</summary>
    public RtResult<string> EnumCaseName(EnumDefId def, uint c)
    {
        var r = Defs.EnumDef(def);
        if (!r.IsOk)
        {
            return RtResult<string>.Fail(r.Error!);
        }

        var cases = r.Value.Cases;
        return RtResult<string>.Ok(c < (uint)cases.Count ? cases[(int)c] : string.Empty);
    }

    /// <summary>struct 的字段名表（输出用）。</summary>
    public RtResult<IReadOnlyList<string>> StructFieldNames(StructDefId def) =>
        Defs.StructDef(def).Map(d => d.Fields);

    // —— 引用（VALUE-REF-003）——

    /// <summary>&amp;x：对变量 cell 取引用（E3012 保证仅变量；解释器在静态面拒绝其余形态）。</summary>
    public Value TakeRef(EnvId env, uint slot) => new Value.Ref(new Place.Var(env, slot));

    /// <summary>穿透引用链，返回最终使用值（操作数/条件/实参使用值时的自动解引用面）。</summary>
    public RtResult<Value> DerefValue(Value v) => DerefInner(v, 0);

    private RtResult<Value> DerefInner(Value v, int depth)
    {
        if (depth > DerefLimit)
        {
            return RtResult<Value>.Fail(RtError.Internal("引用穿透过深（疑似引用环）"));
        }

        return v switch
        {
            Value.Ref r => PlaceLoad(r.Place).Then(stored => DerefInner(stored, depth + 1)),
            _ => RtResult<Value>.Ok(v),
        };
    }

    /// <summary>位置的「使用值」：装载并穿透引用（读变量持有引用时的语义面）。</summary>
    public RtResult<Value> PlaceLoadResolved(Place p) =>
        PlaceLoad(p).Then(v => DerefValue(v));

    // —— Place 读/写（EVAL-ASSIGN-003 / VALUE-ELEM-009）——

    /// <summary>装载位置当前存储值（**不**自动穿透引用——可能返回 <see cref="Value.Ref"/>）。</summary>
    public RtResult<Value> PlaceLoad(Place p)
    {
        switch (p)
        {
            case Place.Var varP:
                return Envs.Get(varP.Env, varP.Slot);
            case Place.Element el:
                return Heap.SliceGet(el.Slice, el.Index);
            case Place.MapEntry me:
                return Heap.MapGet(me.Map, me.Key);
            case Place.Field f:
                {
                    // base 经引用穿透（VALUE-REF-003：经引用读字段即读原值）；读出即副本（VALUE-ELEM-009）
                    var baseP = ResolvePlace(f.Base, 0);
                    if (!baseP.IsOk)
                    {
                        return RtResult<Value>.Fail(baseP.Error!);
                    }

                    var obj = PlaceLoad(baseP.Value);
                    if (!obj.IsOk)
                    {
                        return obj;
                    }

                    return obj.Value switch
                    {
                        Value.Struct sv when f.Index < (uint)sv.Val.Fields.Count =>
                            RtResult<Value>.Ok(sv.Val.Fields[(int)f.Index].CloneValue()),
                        Value.Struct sv => RtResult<Value>.Fail(RtError.Internal(
                            $"字段槽位 {f.Index} 越界（{sv.Val.Fields.Count} 字段）")),
                        var other => RtResult<Value>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"只能读取 struct 字段，得到 {other.TypeHint()}")),
                    };
                }

            default:
                return RtResult<Value>.Fail(RtError.Internal("未知 Place 变体（内部不变量）"));
        }
    }

    /// <summary>写位置：先解析引用穿透，再按位置种类落地。
    /// Field 走「读元素副本 → 改字段 → 写回槽位」（VALUE-ELEM-009），
    /// 写回对容器别名可见，但不影响此前读出的其他副本。</summary>
    public RtResult PlaceStore(Place p, Value v)
    {
        var target = ResolvePlace(p, 0);
        if (!target.IsOk)
        {
            return RtResult.Fail(target.Error!);
        }

        switch (target.Value)
        {
            case Place.Var varP:
                return Envs.Set(varP.Env, varP.Slot, v);
            case Place.Element el:
                return Heap.SliceSet(el.Slice, el.Index, v);
            case Place.MapEntry me:
                // 覆盖保插入位与原键形态；新键追加（VALUE-MAP-006）
                return Heap.MapInsertKey(me.Map, me.Key, me.Key.ToValue(), v);
            case Place.Field f:
                {
                    var baseP = ResolvePlace(f.Base, 0);
                    if (!baseP.IsOk)
                    {
                        return RtResult.Fail(baseP.Error!);
                    }

                    var objR = PlaceLoad(baseP.Value);
                    if (!objR.IsOk)
                    {
                        return objR.Ignore();
                    }

                    var obj = objR.Value;
                    if (obj is not Value.Struct sv)
                    {
                        return RtResult.Fail(new RtError(
                            RtErrorKind.TypeCast, $"只能给 struct 字段赋值，得到 {obj.TypeHint()}"));
                    }

                    if (f.Index >= (uint)sv.Val.Fields.Count)
                    {
                        return RtResult.Fail(RtError.Internal(
                            $"字段槽位 {f.Index} 越界（{sv.Val.Fields.Count} 字段）"));
                    }

                    var fields = sv.Val.Fields.ToArray();
                    fields[f.Index] = v;
                    var updated = new Value.Struct(new StructVal(sv.Val.Def, fields));
                    // 写回 base：Var 走字段写回路径（let cell 亦允许字段更新，VALUE-COPY-001
                    // ——绑定不可重绑 ≠ 值的字段不可变）；其余 base 形态照常落地
                    return baseP.Value is Place.Var varBase
                        ? Envs.SetFieldWriteback(varBase.Env, varBase.Slot, updated)
                        : PlaceStore(baseP.Value, updated);
                }

            default:
                return RtResult.Fail(RtError.Internal("未知 Place 变体（内部不变量）"));
        }
    }

    /// <summary>解析位置的引用穿透：装载为 <see cref="Value.Ref"/> 则落到内层位置（循环穿透）。
    /// 探测装载失败（映射新键、越界元素等暂不可读位置）视为「无引用可穿透」，原样返回；
    /// 真实错误由后续读/写路径自行报出。</summary>
    private RtResult<Place> ResolvePlace(Place p, int depth)
    {
        if (depth > DerefLimit)
        {
            return RtResult<Place>.Fail(RtError.Internal("引用穿透过深（疑似引用环）"));
        }

        var loaded = PlaceLoad(p);
        return loaded.IsOk && loaded.Value is Value.Ref inner
            ? ResolvePlace(inner.Place, depth + 1)
            : RtResult<Place>.Ok(p);
    }

    /// <summary>由值提取映射键（E3014 运行时面：int/string/bool 之外拒绝）。</summary>
    public RtResult<MapKey> MapKeyOf(Value v)
    {
        var k = MapKey.FromValue(v);
        return k is not null
            ? RtResult<MapKey>.Ok(k.Value)
            : RtResult<MapKey>.Fail(new RtError(RtErrorKind.TypeCast, $"映射键须为 int/string/bool，得到 {v.TypeHint()}"));
    }

    // —— 调度（TASK-*）——

    /// <summary>go f(args)：args 为 go 语句处求值的实参快照（EVAL-GO-007）；
    /// 同步子集下 Task 在本语句点执行完毕（TASK-SPAWN-002）。载荷按值语义快照（VALUE-TASKSHARE-007）。</summary>
    public RtResult Spawn(TaskExec exec, FunctionId func, IReadOnlyList<Value> args)
    {
        var snapshot = args.Select(a => a.CloneValue()).ToArray();
        Sched.Enqueue(new Task(func, snapshot));
        return RunPending(exec);
    }

    /// <summary>排空挂起 Task：逐个同步执行至返回，返回值丢弃（fire-and-forget）；
    /// 嵌套 spawn 入队后由本循环消化；Task 内运行时错误向上传播。</summary>
    public RtResult RunPending(TaskExec exec)
    {
        while (Sched.Pop() is { } task)
        {
            var r = exec.Call(this, task.Func, task.Args);
            if (!r.IsOk)
            {
                return RtResult.Fail(r.Error!);
            }
        }

        return RtResult.Ok;
    }

    /// <summary>recv(ch) 调度面：先给挂起 Task 运行机会（协作让位点），再 FIFO 出队。
    /// 同步确定子集下队列恒空，语义与直接出队一致（TASK-SPAWN-002）。</summary>
    public RtResult<RecvOutcome> Recv(TaskExec exec, ChannelId ch)
    {
        if (Sched.Pending > 0)
        {
            var drained = RunPending(exec);
            if (!drained.IsOk)
            {
                return RtResult<RecvOutcome>.Fail(drained.Error!);
            }
        }

        return Chans.TryRecv(ch);
    }

    // —— 内建调用口 ——

    /// <summary>调用运行时自有内建。Host 类别（json/http 等）返回 UnsupportedHost——
    /// 宿主（解释器/后端波次）应在路由前拦截自行实现。</summary>
    public RtResult<Value> CallBuiltin(
        OutputSink sink,
        ValueFormatter fmt,
        BuiltinId id,
        IReadOnlyList<Value> args) =>
        BuiltinDispatch.Call(this, sink, fmt, id, args);
}
