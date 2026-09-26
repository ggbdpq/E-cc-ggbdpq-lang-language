namespace Lang.Runtime;

/// <summary>环境身份（arena 下标 newtype）。</summary>
public readonly record struct EnvId(uint Value);

/// <summary>变量 cell：值 + 可变性（var 可写、let 不可写；捕获语义随 cell）。</summary>
public sealed class Cell
{
    public Value Value { get; internal set; }

    public bool Mutable { get; internal set; }

    internal Cell(Value value, bool mutable)
    {
        Value = value;
        Mutable = mutable;
    }
}

/// <summary>环境对象：父链 + 有序变量表（Dictionary 只做名字 → 下标索引，序来自 List）。</summary>
internal sealed class EnvObj
{
    public EnvId? Parent { get; init; }

    public List<string> Names { get; } = new();

    public List<Cell> Cells { get; } = new();

    public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 变量环境 arena：cell 语义 + 闭包按引用捕获（VALUE-CAPTURE-004）。
/// 每个变量是一个 cell，环境链经父指针解析；闭包保存**定义处环境的 EnvId**——
/// cell 在 arena 中共享，定义作用域与闭包体读写同一 cell，这就是「按引用捕获」的全部机制。
/// 循环体每迭代新建子环境，闭包捕获的循环变量互不串扰。
/// </summary>
public sealed class EnvArena
{
    private readonly List<EnvObj> _envs = new();

    /// <summary>根环境（全局）。</summary>
    public EnvId Root() => ChildOf(null);

    /// <summary>子环境（块/函数调用帧/循环每迭代）。</summary>
    public EnvId Child(EnvId parent) => ChildOf(parent);

    private EnvId ChildOf(EnvId? parent)
    {
        var id = new EnvId((uint)_envs.Count);
        _envs.Add(new EnvObj { Parent = parent });
        return id;
    }

    private RtResult<EnvObj> EnvObjAt(EnvId id) =>
        id.Value < (uint)_envs.Count
            ? RtResult<EnvObj>.Ok(_envs[(int)id.Value])
            : RtResult<EnvObj>.Fail(RtError.Internal($"EnvId({id.Value}) 越界"));

    private static RtResult<T> SlotOob<T>(EnvId env, uint slot) =>
        RtResult<T>.Fail(RtError.Internal($"EnvId({env.Value}) 槽位 {slot} 越界"));

    /// <summary>在指定环境定义变量，返回槽位号。重复定义 → <see cref="RtErrorKind.Scope"/>
    /// （正常程序被静态拒绝，此处兜底）。载荷按值语义快照。</summary>
    public RtResult<uint> Define(EnvId env, string name, Value value, bool mutable)
    {
        var r = EnvObjAt(env);
        if (!r.IsOk)
        {
            return RtResult<uint>.Fail(r.Error!);
        }

        var obj = r.Value;
        if (obj.Index.ContainsKey(name))
        {
            return RtResult<uint>.Fail(new RtError(RtErrorKind.Scope, $"{name} 在此作用域重复定义"));
        }

        var slot = (uint)obj.Cells.Count;
        obj.Index[name] = (int)slot;
        obj.Names.Add(name);
        obj.Cells.Add(new Cell(value.CloneValue(), mutable));
        return RtResult<uint>.Ok(slot);
    }

    /// <summary>函数声明面：插入或覆盖（顶层/局部 fn 声明语句可重复执行，后者覆盖前者；
    /// 与证据 env.redefine 一致；绑定量恒不可变）。</summary>
    public RtResult<uint> Redefine(EnvId env, string name, Value value)
    {
        var r = EnvObjAt(env);
        if (!r.IsOk)
        {
            return RtResult<uint>.Fail(r.Error!);
        }

        var obj = r.Value;
        if (obj.Index.TryGetValue(name, out var existing))
        {
            var cell = obj.Cells[existing];
            cell.Value = value.CloneValue();
            cell.Mutable = false;
            return RtResult<uint>.Ok((uint)existing);
        }

        var slot = (uint)obj.Cells.Count;
        obj.Index[name] = (int)slot;
        obj.Names.Add(name);
        obj.Cells.Add(new Cell(value.CloneValue(), mutable: false));
        return RtResult<uint>.Ok(slot);
    }

    /// <summary>沿父链解析变量名，返回**属主**环境与槽位（按引用捕获的关键：
    /// 拿到属主 cell 才能共享读写）。未找到返回 null。</summary>
    public RtResult<(EnvId Env, uint Slot)?> Lookup(EnvId env, string name)
    {
        var cur = (EnvId?)env;
        while (cur is { } id)
        {
            var r = EnvObjAt(id);
            if (!r.IsOk)
            {
                return RtResult<(EnvId, uint)?>.Fail(r.Error!);
            }

            if (r.Value.Index.TryGetValue(name, out var slot))
            {
                return RtResult<(EnvId, uint)?>.Ok((id, (uint)slot));
            }

            cur = r.Value.Parent;
        }

        return RtResult<(EnvId, uint)?>.Ok(null);
    }

    /// <summary>读 cell 值（CloneValue；标量廉价，容器共享索引）。</summary>
    public RtResult<Value> Get(EnvId env, uint slot) => CellAt(env, slot).Map(c => c.Value.CloneValue());

    /// <summary>字段写回路径专用：把「含字段更新的 struct 值」写回 base 槽位。
    /// 这不是对绑定的重新赋值（E2005 是静态面）：绑定不可重绑 ≠ 值的字段不可变——
    /// let p = P{..}; p.x = 1 更新的是 p 所持值的字段（conformance struct-field-write /
    /// nested-struct-copy 的行为真值）。variable 级可变性检查只在 <see cref="Set"/>（重绑定路径）执行。</summary>
    public RtResult SetFieldWriteback(EnvId env, uint slot, Value value)
    {
        var r = CellAt(env, slot);
        if (!r.IsOk)
        {
            return RtResult.Fail(r.Error!);
        }

        r.Value.Value = value.CloneValue();
        return RtResult.Ok;
    }

    /// <summary>写 cell 值：let cell 拒绝写入（<see cref="RtErrorKind.ImmutableAssign"/>）。</summary>
    public RtResult Set(EnvId env, uint slot, Value value)
    {
        var r = CellAt(env, slot);
        if (!r.IsOk)
        {
            return RtResult.Fail(r.Error!);
        }

        var cell = r.Value;
        if (!cell.Mutable)
        {
            return RtResult.Fail(new RtError(RtErrorKind.ImmutableAssign, "由 let 绑定，不可重新赋值"));
        }

        cell.Value = value.CloneValue();
        return RtResult.Ok;
    }

    /// <summary>cell 只读访问。</summary>
    public RtResult<Cell> CellAt(EnvId env, uint slot)
    {
        var r = EnvObjAt(env);
        if (!r.IsOk)
        {
            return RtResult<Cell>.Fail(r.Error!);
        }

        var cells = r.Value.Cells;
        return slot < (uint)cells.Count ? RtResult<Cell>.Ok(cells[(int)slot]) : SlotOob<Cell>(env, slot);
    }

    /// <summary>环境的父链（调试/作用域检查用）。</summary>
    public RtResult<EnvId?> ParentOf(EnvId env) => EnvObjAt(env).Map(o => o.Parent);
}
