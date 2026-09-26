using Lang.Core;
using Lang.Ir;
using Lang.Runtime;

namespace Lang.Interp;

/// <summary>
/// IR 解释器：<see cref="IrProgram"/> 的栈式基本块执行（与 AST 参考解释器
/// <see cref="Interp"/> 共享 Machine/Value/内建/输出面，遍历彼此独立——差分价值约束）。
///
/// 执行模型：
/// - 每次调用建一个帧：帧局部槽位落在 <see cref="EnvArena"/> 的帧环境里
///   （<see cref="LocalId"/> = 槽位号），使 <see cref="Place.Var"/> 可寻址局部槽
///   （装箱纪律与字段/索引写回的地址面）；
/// - 操作数栈条目带可选 Place 溯源（<see cref="StackVal"/>）：<see cref="Inst.FieldGet"/>/
///   <see cref="Inst.IndexGet"/> 产生的「地址组件」在 <see cref="Inst.FieldSet"/>/
///   <see cref="Inst.IndexSet"/> 写回时落地（与 AST 侧 <see cref="Machine.PlaceStore"/>
///   同一机制）；<see cref="Value.Ref"/> 值自带 Place（装箱纪律：BoxNew 分配 box 槽，
///   读 <see cref="Inst.Deref"/> 写 <see cref="Inst.RefSet"/>）；
/// - 闭包：<see cref="Inst.ClosureNew"/> 弹出捕获的 Ref 值存入闭包环境；调用时逐槽落地到
///   被调函数局部表前 <c>capture_count</c> 个槽位（按引用共享，VALUE-CAPTURE-004）；
/// - <c>go</c>（<see cref="Inst.Spawn"/>）= 同步确定子集：经 <see cref="Machine.Spawn"/>
///   快照实参后立即执行至返回，返回值丢弃（TASK-SPAWN-002）；
/// - for-in 迭代器：<see cref="Inst.IterNew"/> 快照元素序列，迭代器本体以 box 槽引用形态
///   驻留局部槽（不跨块压栈），<see cref="Inst.IterNext"/> 耗尽跳 done（CTRL-FORIN-003）；
/// - <c>?</c>（<see cref="Inst.Try"/>）：Err 载荷存 err 槽跳 fail 块，函数边界合成
///   Err 返回（RESULT-TRY-003）。
/// </summary>
public sealed class IrMachine : TaskExec
{
    /// <summary>操作数栈条目：值 + 可选地址溯源（null = 无地址，如常量/运算结果）。</summary>
    private readonly struct StackVal(Value v, Place? place)
    {
        public Value V { get; } = v;

        public Place? Place { get; } = place;
    }

    /// <summary>for-in 迭代器状态（IterNew 快照；box 槽引用为键）。</summary>
    private sealed class IterState(IReadOnlyList<Value> items)
    {
        public IReadOnlyList<Value> Items { get; } = items;

        public int Pos { get; set; }
    }

    private readonly IrProgram _program;
    private readonly Machine _machine = new();
    /// <summary>StructId → 运行时 StructDefId（装载序 = 声明序）。</summary>
    private readonly List<StructDefId> _structIds = [];
    /// <summary>EnumId → 运行时 EnumDefId。</summary>
    private readonly List<EnumDefId> _enumIds = [];
    /// <summary>活跃迭代器（box 槽环境 id → 状态）。</summary>
    private readonly Dictionary<EnvId, IterState> _iters = new();
    private OutputSink _sink = new StdoutSink();

    /// <summary>装载程序：登记 struct/enum 定义、函数名与全局槽位。
    ///
    /// 程序表尾部的预载 <c>error</c>/<c>Option</c>/<c>Result</c>（lowering 恒追加，供名字
    /// 查表）**按名映射**到运行时 DefTables 的预载定义（error=StructDefId(0)、
    /// Option=EnumDefId(0)、Result=EnumDefId(1)）——<see cref="Inst.Try"/> 的 Result 判定、
    /// <c>isNone</c>、JSON None 序列化与 Ok/Err 构造都以预载 id 为身份锚；**不得重复注册**
    /// （重复注册会产生第二套同形定义，预载判定全部落空——lang-rs 第四波 bug 教训）。
    /// 名字判定与 lowering 的按名查表同口径（合法程序中该三名为预载保留名）。</summary>
    public IrMachine(IrProgram program)
    {
        _program = program;
        foreach (var s in program.Structs)
        {
            var def = s.Name == "error"
                ? _machine.Defs.ErrorDef()
                : _machine.Defs.RegisterStruct(s.Name, s.Fields.Select(f => f.Name).ToList());
            _structIds.Add(def);
        }

        foreach (var e in program.Enums)
        {
            var def = e.Name switch
            {
                "Option" => _machine.Defs.OptionDef(),
                "Result" => _machine.Defs.ResultDef(),
                _ => _machine.Defs.RegisterEnum(e.Name, e.Cases.Select(c => c.Name).ToList()),
            };
            _enumIds.Add(def);
        }

        for (var i = 0; i < program.Functions.Count; i++)
        {
            _ = _machine.SetFnName(new FunctionId((uint)i), program.Functions[i].Name);
        }

        // 全局槽位：GlobalId = 根环境槽位号（声明序 GlobalSet 寻址）
        for (var i = 0; i < program.Globals.Count; i++)
        {
            // 语言级 let/var 可变性是静态检查面；IR 帧槽位一律可写
            var d = _machine.Envs.Define(_machine.Root, $"%g{i}", Value.Unit.Default, mutable: true);
            if (!d.IsOk)
            {
                throw new InvalidOperationException($"全局槽位 %g{i} 定义失败（内部不变量）");
            }
        }
    }

    /// <summary>执行程序：先 <c>init</c>（全局初始化）再 <c>main</c>（均可缺省）。</summary>
    public RtResult Run(OutputSink sink)
    {
        _sink = sink;
        if (_program.Init is { } init)
        {
            var r = CallFunction(init.Value, [], null);
            if (!r.IsOk)
            {
                return r.Ignore();
            }
        }

        if (_program.Main is { } main)
        {
            var r = CallFunction(main.Value, [], null);
            if (!r.IsOk)
            {
                return r.Ignore();
            }
        }

        return RtResult.Ok;
    }

    /// <summary>调用函数：帧环境 + 局部槽位落位 + 基本块循环。</summary>
    private RtResult<Value> CallFunction(uint func, IReadOnlyList<Value> args, EnvId? captureEnv)
    {
        if (func >= (uint)_program.Functions.Count)
        {
            return RtResult<Value>.Fail(RtError.Internal($"FunctionId({func}) 越界"));
        }

        var f = _program.Functions[(int)func];
        if (f.ExternHost is not null)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.UnsupportedHost,
                $"extern 函数 {f.Name} 仅转译可用（宿主已声明，未链接）"));
        }

        var env = _machine.Envs.Child(_machine.Root);
        // 局部槽位：捕获槽 + 参数 + 局部/临时；语言级可变性是静态面，槽位一律可写
        for (var i = 0; i < f.Locals.Count; i++)
        {
            var d = _machine.Envs.Define(env, $"%{i}", Value.Unit.Default, mutable: true);
            if (!d.IsOk)
            {
                return RtResult<Value>.Fail(d.Error!);
            }
        }

        // 捕获槽：从闭包环境逐槽取 Ref 值（按引用共享）
        if (captureEnv is { } cenv)
        {
            for (uint i = 0; i < (uint)f.CaptureCount; i++)
            {
                var v = _machine.Envs.Get(cenv, i);
                if (!v.IsOk)
                {
                    return RtResult<Value>.Fail(v.Error!);
                }

                var set = _machine.Envs.Set(env, i, v.Value);
                if (!set.IsOk)
                {
                    return RtResult<Value>.Fail(set.Error!);
                }
            }
        }

        // 参数槽
        var paramBase = (uint)f.CaptureCount;
        if (args.Count != f.Sig.ParamCount)
        {
            return RtResult<Value>.Fail(RtError.Internal(
                $"{f.Name} 实参个数与签名不符（{args.Count} vs {f.Sig.ParamCount}）"));
        }

        for (var i = 0; i < args.Count; i++)
        {
            var set = _machine.Envs.Set(env, paramBase + (uint)i, args[i]);
            if (!set.IsOk)
            {
                return RtResult<Value>.Fail(set.Error!);
            }
        }

        // 基本块循环：块入口栈深恒 0（validator 不变量），栈随块连续推进
        var stack = new List<StackVal>();
        var cur = new BlockId(0);
        while (true)
        {
            if (cur.Value >= (uint)f.Blocks.Count)
            {
                return RtResult<Value>.Fail(RtError.Internal($"块 {cur.Value} 越界"));
            }

            var block = f.Blocks[(int)cur.Value];
            var jumped = false;
            foreach (var inst in block.Insts)
            {
                var target = ExecInst(inst, env, stack);
                if (!target.IsOk)
                {
                    return RtResult<Value>.Fail(target.Error!);
                }

                if (target.Value is { } t)
                {
                    // 块内跳转（IterNext 耗尽 / Try 的 fail 块）：终接不执行
                    cur = t;
                    jumped = true;
                    break;
                }
            }

            if (jumped)
            {
                continue;
            }

            var term = f.Blocks[(int)cur.Value].Term;
            switch (term)
            {
                case Terminator.Goto g:
                    cur = g.Target;
                    break;
                case Terminator.Branch b:
                    {
                        var cond = Pop(stack);
                        if (!cond.IsOk)
                        {
                            return RtResult<Value>.Fail(cond.Error!);
                        }

                        var truth = cond.Value.V.Truthy();
                        if (!truth.IsOk)
                        {
                            return RtResult<Value>.Fail(truth.Error!);
                        }

                        cur = truth.Value ? b.IfTrue : b.IfFalse;
                        break;
                    }

                case Terminator.SwitchInt si:
                    {
                        var v = Pop(stack);
                        if (!v.IsOk)
                        {
                            return RtResult<Value>.Fail(v.Error!);
                        }

                        if (v.Value.V is not Value.Int i)
                        {
                            return RtResult<Value>.Fail(new RtError(
                                RtErrorKind.TypeCast,
                                $"int match 分派须为 int，得到 {v.Value.V.TypeHint()}"));
                        }

                        // 源码臂序首中即跳（lowering 契约）；default 可缺省
                        BlockId? hit = null;
                        foreach (var (val, target) in si.Arms)
                        {
                            if (val == i.V)
                            {
                                hit = target;
                                break;
                            }
                        }

                        if (hit is { } h)
                        {
                            cur = h;
                        }
                        else if (si.Default is { } d)
                        {
                            cur = d;
                        }
                        else
                        {
                            return RtResult<Value>.Fail(new RtError(
                                RtErrorKind.MatchNoArm, "match 无匹配分支"));
                        }

                        break;
                    }

                case Terminator.SwitchEnum se:
                    {
                        var v = Pop(stack);
                        if (!v.IsOk)
                        {
                            return RtResult<Value>.Fail(v.Error!);
                        }

                        var next = SwitchEnum(se, v.Value.V);
                        if (!next.IsOk)
                        {
                            return RtResult<Value>.Fail(next.Error!);
                        }

                        cur = next.Value;
                        break;
                    }

                case Terminator.Return ret:
                    {
                        if (!ret.HasValue)
                        {
                            return RtResult<Value>.Ok(Value.Unit.Default);
                        }

                        var v = Pop(stack);
                        return v.IsOk
                            ? RtResult<Value>.Ok(v.Value.V)
                            : RtResult<Value>.Fail(v.Error!);
                    }

                case Terminator.Unreachable:
                    return RtResult<Value>.Fail(RtError.Internal("执行到不可达锚"));
                default:
                    return RtResult<Value>.Fail(RtError.Internal("未知终接变体（内部不变量）"));
            }
        }
    }

    /// <summary>enum/Option/Result match 分派：同 def 且 case 在臂表内跳对应臂，否则 default。</summary>
    private RtResult<BlockId> SwitchEnum(Terminator.SwitchEnum se, Value v)
    {
        if (v is not Value.Enum { Def: var def, Case: var c })
        {
            return RtResult<BlockId>.Fail(new RtError(
                RtErrorKind.TypeCast, $"enum match 分派须为 enum 值，得到 {v.TypeHint()}"));
        }

        var expected = EnumDef(se.Enum);
        if (!expected.IsOk)
        {
            return RtResult<BlockId>.Fail(expected.Error!);
        }

        if (def == expected.Value && c < (uint)se.Arms.Count)
        {
            return RtResult<BlockId>.Ok(se.Arms[(int)c]);
        }

        return se.Default is { } d
            ? RtResult<BlockId>.Ok(d)
            : RtResult<BlockId>.Fail(new RtError(RtErrorKind.MatchNoArm, "match 无匹配分支"));
    }

    private RtResult<StructDefId> StructDef(StructId id) =>
        id.Value < (uint)_structIds.Count
            ? RtResult<StructDefId>.Ok(_structIds[(int)id.Value])
            : RtResult<StructDefId>.Fail(RtError.Internal($"StructId({id.Value}) 越界"));

    private RtResult<EnumDefId> EnumDef(EnumId id) =>
        id.Value < (uint)_enumIds.Count
            ? RtResult<EnumDefId>.Ok(_enumIds[(int)id.Value])
            : RtResult<EnumDefId>.Fail(RtError.Internal($"EnumId({id.Value}) 越界"));

    /// <summary>弹栈顶（下溢 = IR 栈效应被破坏，内部错误）。</summary>
    private RtResult<StackVal> Pop(List<StackVal> stack)
    {
        if (stack.Count == 0)
        {
            return RtResult<StackVal>.Fail(RtError.Internal("操作数栈下溢（IR 栈效应被破坏）"));
        }

        var v = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return RtResult<StackVal>.Ok(v);
    }

    /// <summary>弹 n 个条目，**按栈序（= 压入序）返回，不反转**——构造类指令
    /// （ArrayNew/MapNew/StructNew/EnumNew/Call 实参/ClosureNew 捕获）的冻结合同。</summary>
    private RtResult<IReadOnlyList<StackVal>> PopN(List<StackVal> stack, int n)
    {
        if (stack.Count < n)
        {
            return RtResult<IReadOnlyList<StackVal>>.Fail(
                RtError.Internal("操作数栈下溢（IR 栈效应被破坏）"));
        }

        var slice = stack.GetRange(stack.Count - n, n);
        stack.RemoveRange(stack.Count - n, n);
        return RtResult<IReadOnlyList<StackVal>>.Ok(slice);
    }

    /// <summary>栈条目的地址形态：溯源 Place 优先，Ref 值自带 Place 次之。</summary>
    private static Place? PlaceOf(StackVal e) =>
        e.Place ?? (e.V is Value.Ref r ? r.Place : null);

    /// <summary>块内单指令执行；返回非 null = 块内跳转目标（IterNext 耗尽 / Try fail）。</summary>
    private RtResult<BlockId?> ExecInst(Inst inst, EnvId env, List<StackVal> stack)
    {
        switch (inst)
        {
            case Inst.ConstI64 c:
                stack.Add(new StackVal(new Value.Int(c.Value), null));
                return RtResult<BlockId?>.Ok(null);
            case Inst.ConstF64 c:
                stack.Add(new StackVal(new Value.Float(c.Value), null));
                return RtResult<BlockId?>.Ok(null);
            case Inst.ConstBool c:
                stack.Add(new StackVal(new Value.Bool(c.Value), null));
                return RtResult<BlockId?>.Ok(null);
            case Inst.ConstStr c:
                stack.Add(new StackVal(new Value.Str(c.Value), null));
                return RtResult<BlockId?>.Ok(null);

            case Inst.LocalGet l:
                {
                    var slot = l.Local.Value;
                    var v = _machine.Envs.Get(env, slot);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    stack.Add(new StackVal(v.Value, new Place.Var(env, slot)));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.LocalSet l:
                {
                    var v = Pop(stack);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    var set = _machine.Envs.Set(env, l.Local.Value, v.Value.V);
                    return set.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(set.Error!);
                }

            case Inst.GlobalGet g:
                {
                    var slot = g.Global.Value;
                    var v = _machine.Envs.Get(_machine.Root, slot);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    stack.Add(new StackVal(v.Value, new Place.Var(_machine.Root, slot)));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.GlobalSet g:
                {
                    var v = Pop(stack);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    var set = _machine.Envs.Set(_machine.Root, g.Global.Value, v.Value.V);
                    return set.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(set.Error!);
                }

            case Inst.Unary u:
                {
                    var e = Pop(stack);
                    if (!e.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(e.Error!);
                    }

                    var d = _machine.DerefValue(e.Value.V);
                    if (!d.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(d.Error!);
                    }

                    RtResult<Value> outV = u.Op switch
                    {
                        Unop.Neg => d.Value switch
                        {
                            Value.Int i => RtResult<Value>.Ok(new Value.Int(unchecked(-i.V))),
                            Value.Float f => RtResult<Value>.Ok(new Value.Float(-f.V)),
                            var other => RtResult<Value>.Fail(new RtError(
                                RtErrorKind.TypeCast, $"一元运算不支持 {other.TypeHint()}")),
                        },
                        Unop.Not => d.Value.Truthy().Map(b => (Value)new Value.Bool(!b)),
                        _ => RtResult<Value>.Fail(RtError.Internal("未知一元算符（内部不变量）")),
                    };
                    return outV.IsOk
                        ? Ok1(stack, outV.Value)
                        : RtResult<BlockId?>.Fail(outV.Error!);
                }

            case Inst.Binary b:
                {
                    var r = Pop(stack);
                    if (!r.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(r.Error!);
                    }

                    var l = Pop(stack);
                    if (!l.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(l.Error!);
                    }

                    var ld = _machine.DerefValue(l.Value.V);
                    if (!ld.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(ld.Error!);
                    }

                    var rd = _machine.DerefValue(r.Value.V);
                    if (!rd.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(rd.Error!);
                    }

                    var outV = Ops.Binary(Ops.Of(b.Op), ld.Value, rd.Value);
                    return outV.IsOk
                        ? Ok1(stack, outV.Value)
                        : RtResult<BlockId?>.Fail(outV.Error!);
                }

            case Inst.Pop:
                {
                    var v = Pop(stack);
                    return v.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(v.Error!);
                }

            case Inst.Call call:
                {
                    var callee = _program.Functions[(int)call.Func.Value];
                    var args = PopArgs(stack, callee.Sig.ParamCount);
                    if (!args.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(args.Error!);
                    }

                    var v = CallFunction(call.Func.Value, args.Value, null);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    return callee.Sig.HasRet ? Ok1(stack, v.Value) : RtResult<BlockId?>.Ok(null);
                }

            case Inst.CallIndirect ci:
                {
                    var args = PopArgs(stack, ci.Argc);
                    if (!args.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(args.Error!);
                    }

                    var callee = Pop(stack);
                    if (!callee.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(callee.Error!);
                    }

                    if (callee.Value.V is not Value.Closure closure)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"{callee.Value.V.TypeHint()} 不可调用"));
                    }

                    var v = CallFunction(closure.Func.Value, args.Value, closure.Env);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    return ci.HasRet ? Ok1(stack, v.Value) : RtResult<BlockId?>.Ok(null);
                }

            case Inst.CallBuiltin cb:
                {
                    var args = PopArgs(stack, cb.Argc);
                    if (!args.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(args.Error!);
                    }

                    var v = CallBuiltinByName(cb.Name, args.Value);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    return cb.HasRet ? Ok1(stack, v.Value) : RtResult<BlockId?>.Ok(null);
                }

            case Inst.Spawn sp:
                {
                    var args = PopArgs(stack, sp.Argc);
                    if (!args.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(args.Error!);
                    }

                    // 同步确定子集：Machine.Spawn 快照实参后立即执行至返回（TASK-SPAWN-002）
                    var r = _machine.Spawn(this, new FunctionId(sp.Func.Value), args.Value);
                    return r.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(r.Error!);
                }

            case Inst.ClosureNew cn:
                {
                    // 捕获值按压栈序（=捕获声明序）落槽；PopN 保序
                    var caps = PopN(stack, cn.Captures);
                    if (!caps.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(caps.Error!);
                    }

                    var cenv = _machine.Envs.Child(_machine.Root);
                    for (var i = 0; i < caps.Value.Count; i++)
                    {
                        var d = _machine.Envs.Define(cenv, $"%c{i}", caps.Value[i].V, mutable: true);
                        if (!d.IsOk)
                        {
                            return RtResult<BlockId?>.Fail(d.Error!);
                        }
                    }

                    stack.Add(new StackVal(new Value.Closure(cn.Func, cenv), null));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.IndexGet:
                {
                    var idx = Pop(stack);
                    if (!idx.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(idx.Error!);
                    }

                    var obj = Pop(stack);
                    if (!obj.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(obj.Error!);
                    }

                    return ExecIndexGet(obj.Value, idx.Value, stack);
                }

            case Inst.IndexSet:
                {
                    var val = Pop(stack);
                    if (!val.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(val.Error!);
                    }

                    var idx = Pop(stack);
                    if (!idx.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(idx.Error!);
                    }

                    var obj = Pop(stack);
                    if (!obj.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(obj.Error!);
                    }

                    return ExecIndexSet(obj.Value, idx.Value, val.Value.V);
                }

            case Inst.SliceGet:
                {
                    var hi = Pop(stack);
                    if (!hi.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(hi.Error!);
                    }

                    var lo = Pop(stack);
                    if (!lo.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(lo.Error!);
                    }

                    var obj = Pop(stack);
                    if (!obj.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(obj.Error!);
                    }

                    return ExecSliceGet(obj.Value, lo.Value.V, hi.Value.V, stack);
                }

            case Inst.FieldGet fg:
                {
                    var obj = Pop(stack);
                    if (!obj.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(obj.Error!);
                    }

                    var baseP = PlaceOf(obj.Value);
                    var d = _machine.DerefValue(obj.Value.V);
                    if (!d.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(d.Error!);
                    }

                    if (d.Value is not Value.Struct sv)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast,
                            $"{d.Value.TypeHint()} 不是 struct，无字段 {fg.Field}"));
                    }

                    var def = _machine.Defs.StructDef(sv.Val.Def);
                    if (!def.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(def.Error!);
                    }

                    var i = def.Value.Fields.ToList().IndexOf(fg.Field);
                    if (i < 0)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"没有字段 {fg.Field}"));
                    }

                    stack.Add(new StackVal(
                        sv.Val.Fields[i],
                        baseP is null ? null : new Place.Field(baseP, (uint)i)));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.FieldSet fs:
                {
                    var val = Pop(stack);
                    if (!val.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(val.Error!);
                    }

                    var obj = Pop(stack);
                    if (!obj.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(obj.Error!);
                    }

                    var baseP = PlaceOf(obj.Value);
                    if (baseP is null)
                    {
                        return RtResult<BlockId?>.Fail(RtError.Internal(
                            "FieldSet 目标无地址（lowering 须保留槽位溯源或装箱）"));
                    }

                    var d = _machine.DerefValue(obj.Value.V);
                    if (!d.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(d.Error!);
                    }

                    if (d.Value is not Value.Struct sv)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"只能给 struct 字段赋值，得到 {d.Value.TypeHint()}"));
                    }

                    var def = _machine.Defs.StructDef(sv.Val.Def);
                    if (!def.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(def.Error!);
                    }

                    var i = def.Value.Fields.ToList().IndexOf(fs.Field);
                    if (i < 0)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"没有字段 {fs.Field}"));
                    }

                    var store = _machine.PlaceStore(new Place.Field(baseP, (uint)i), val.Value.V);
                    return store.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(store.Error!);
                }

            case Inst.ArrayPush:
                {
                    var val = Pop(stack);
                    if (!val.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(val.Error!);
                    }

                    var arr = Pop(stack);
                    if (!arr.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(arr.Error!);
                    }

                    var av = _machine.DerefValue(arr.Value.V);
                    if (!av.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(av.Error!);
                    }

                    var vv = _machine.DerefValue(val.Value.V);
                    if (!vv.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(vv.Error!);
                    }

                    if (av.Value is not Value.Slice lhs)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"数组 += 需要数组，得到 {av.Value.TypeHint()}"));
                    }

                    if (vv.Value is not Value.Slice rhs)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"数组 += 需要数组，得到 {vv.Value.TypeHint()}"));
                    }

                    var items = _machine.Heap.Slice(rhs.Id);
                    if (!items.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(items.Error!);
                    }

                    foreach (var v in items.Value)
                    {
                        var p = _machine.Heap.SlicePush(lhs.Id, v);
                        if (!p.IsOk)
                        {
                            return RtResult<BlockId?>.Fail(p.Error!);
                        }
                    }

                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.ArrayNew an:
                {
                    // 元素左到右压栈；PopN 按栈序（=压入序）返回，不反转
                    var items = PopN(stack, an.Count);
                    if (!items.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(items.Error!);
                    }

                    return Ok1(stack, _machine.AllocSliceValue(items.Value.Select(e => e.V).ToList()));
                }

            case Inst.MapNew mn:
                {
                    // 键值对左到右压栈（k1,v1,k2,v2,…）；PopN 保压入序，成对插入
                    // 即压入对序（VALUE-MAP-006）
                    var flat = PopN(stack, mn.Count * 2);
                    if (!flat.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(flat.Error!);
                    }

                    var m = _machine.AllocMapValue();
                    if (m is not Value.Map mv)
                    {
                        return RtResult<BlockId?>.Fail(RtError.Internal("新分配映射缺索引"));
                    }

                    for (var i = 0; i < flat.Value.Count; i += 2)
                    {
                        var ins = _machine.Heap.MapInsert(mv.Id, flat.Value[i].V, flat.Value[i + 1].V);
                        if (!ins.IsOk)
                        {
                            return RtResult<BlockId?>.Fail(ins.Error!);
                        }
                    }

                    return Ok1(stack, m);
                }

            case Inst.StructNew sn:
                {
                    var def = StructDef(sn.Struct);
                    if (!def.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(def.Error!);
                    }

                    // 字段值按声明序压栈；PopN 保压入序 = 声明序
                    var fields = PopN(stack, _program.Structs[(int)sn.Struct.Value].Fields.Count);
                    if (!fields.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(fields.Error!);
                    }

                    return Ok1(stack, new Value.Struct(new StructVal(
                        def.Value, fields.Value.Select(e => e.V).ToList())));
                }

            case Inst.EnumNew en:
                {
                    var def = EnumDef(en.Enum);
                    if (!def.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(def.Error!);
                    }

                    // 载荷按 case 声明序压栈；PopN 保压入序
                    var payloads = PopN(stack, _program.Enums[(int)en.Enum.Value].Cases[(int)en.Case].PayloadTypes.Count);
                    if (!payloads.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(payloads.Error!);
                    }

                    return Ok1(stack, new Value.Enum(
                        def.Value, (uint)en.Case, payloads.Value.Select(e => e.V).ToList()));
                }

            case Inst.RangeNew:
                {
                    var hi = Pop(stack);
                    if (!hi.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(hi.Error!);
                    }

                    var lo = Pop(stack);
                    if (!lo.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(lo.Error!);
                    }

                    if (lo.Value.V is not Value.Int loI || hi.Value.V is not Value.Int hiI)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, "区间两端须为 int"));
                    }

                    return Ok1(stack, new Value.Range(loI.V, hiI.V));
                }

            case Inst.BoxNew:
                {
                    var v = Pop(stack);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    var boxEnv = _machine.Envs.Child(_machine.Root);
                    var d = _machine.Envs.Define(boxEnv, "%box", v.Value.V, mutable: true);
                    if (!d.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(d.Error!);
                    }

                    stack.Add(new StackVal(
                        new Value.Ref(new Place.Var(boxEnv, 0)), null));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.Deref:
                {
                    var e = Pop(stack);
                    if (!e.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(e.Error!);
                    }

                    if (e.Value.V is Value.Ref r)
                    {
                        var v = _machine.PlaceLoad(r.Place);
                        if (!v.IsOk)
                        {
                            return RtResult<BlockId?>.Fail(v.Error!);
                        }

                        stack.Add(new StackVal(v.Value, r.Place));
                    }
                    else
                    {
                        stack.Add(e.Value);
                    }

                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.RefSet:
                {
                    var val = Pop(stack);
                    if (!val.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(val.Error!);
                    }

                    var rv = Pop(stack);
                    if (!rv.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(rv.Error!);
                    }

                    if (rv.Value.V is not Value.Ref r)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"RefSet 需要 Ref，得到 {rv.Value.V.TypeHint()}"));
                    }

                    var store = _machine.PlaceStore(r.Place, val.Value.V);
                    return store.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(store.Error!);
                }

            case Inst.EnumPayloadGet ep:
                {
                    var e = Pop(stack);
                    if (!e.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(e.Error!);
                    }

                    if (e.Value.V is not Value.Enum en)
                    {
                        return RtResult<BlockId?>.Fail(RtError.Internal("EnumPayloadGet 需要 enum 值"));
                    }

                    if (ep.Index >= en.Payloads.Count)
                    {
                        return RtResult<BlockId?>.Fail(
                            RtError.Internal($"enum 载荷 {ep.Index} 越界"));
                    }

                    return Ok1(stack, en.Payloads[ep.Index]);
                }

            case Inst.IterNew:
                {
                    var e = Pop(stack);
                    if (!e.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(e.Error!);
                    }

                    var d = _machine.DerefValue(e.Value.V);
                    if (!d.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(d.Error!);
                    }

                    var items = SnapshotItems(d.Value);
                    if (!items.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(items.Error!);
                    }

                    // 迭代器本体驻留局部槽：以 box 槽引用为句柄（Value 形态内表达）
                    var boxEnv = _machine.Envs.Child(_machine.Root);
                    var def = _machine.Envs.Define(boxEnv, "%iter", Value.Unit.Default, mutable: true);
                    if (!def.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(def.Error!);
                    }

                    _iters[boxEnv] = new IterState(items.Value);
                    stack.Add(new StackVal(
                        new Value.Ref(new Place.Var(boxEnv, 0)), null));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Inst.IterNext next:
                {
                    var e = Pop(stack);
                    if (!e.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(e.Error!);
                    }

                    if (e.Value.V is not Value.Ref { Place: Place.Var handle })
                    {
                        return RtResult<BlockId?>.Fail(RtError.Internal("IterNext 需要迭代器句柄"));
                    }

                    if (!_iters.TryGetValue(handle.Env, out var state))
                    {
                        return RtResult<BlockId?>.Fail(RtError.Internal("迭代器状态丢失"));
                    }

                    if (state.Pos < state.Items.Count)
                    {
                        var v = state.Items[state.Pos];
                        state.Pos++;
                        return Ok1(stack, v);
                    }

                    return RtResult<BlockId?>.Ok(next.Done);
                }

            case Inst.ChanNew:
                return Ok1(stack, _machine.MakeChanValue());

            case Inst.ChanSend:
                {
                    var val = Pop(stack);
                    if (!val.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(val.Error!);
                    }

                    var ch = Pop(stack);
                    if (!ch.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(ch.Error!);
                    }

                    if (ch.Value.V is not Value.Chan cid)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"send 需要通道，得到 {ch.Value.V.TypeHint()}"));
                    }

                    var send = _machine.Chans.Send(cid.Id, val.Value.V);
                    return send.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(send.Error!);
                }

            case Inst.ChanRecv:
                {
                    var ch = Pop(stack);
                    if (!ch.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(ch.Error!);
                    }

                    if (ch.Value.V is not Value.Chan cid)
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast, $"recv 需要通道，得到 {ch.Value.V.TypeHint()}"));
                    }

                    // 调度面 recv：先给挂起 Task 运行机会再 FIFO 出队（同步子集下等价直接出队）
                    var got = _machine.Recv(this, cid.Id);
                    if (!got.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(got.Error!);
                    }

                    return Ok1(stack, got.Value.IsValue
                        ? _machine.OkValue(got.Value.Payload!)
                        : _machine.ErrValue(new Value.Str(ChannelTable.ChanEmptyMsg)));
                }

            case Inst.Try tryI:
                {
                    var e = Pop(stack);
                    if (!e.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(e.Error!);
                    }

                    if (e.Value.V is not Value.Enum en || en.Def != _machine.Defs.ResultDef())
                    {
                        return RtResult<BlockId?>.Fail(new RtError(
                            RtErrorKind.TypeCast,
                            $"? 的操作数须为 Result，得到 {e.Value.V.TypeHint()}"));
                    }

                    var payload = en.Payloads.Count > 0 ? en.Payloads[0] : Value.Unit.Default;
                    if (en.Case == 0)
                    {
                        return Ok1(stack, payload);
                    }

                    var set = _machine.Envs.Set(env, tryI.Err.Value, payload);
                    if (!set.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(set.Error!);
                    }

                    return RtResult<BlockId?>.Ok(tryI.Fail);
                }

            default:
                return RtResult<BlockId?>.Fail(RtError.Internal("未知指令变体（内部不变量）"));
        }
    }

    /// <summary>压一个产出值（无溯源）。</summary>
    private RtResult<BlockId?> Ok1(List<StackVal> stack, Value v)
    {
        stack.Add(new StackVal(v, null));
        return RtResult<BlockId?>.Ok(null);
    }

    /// <summary>弹 n 个实参（保压入序），下溢返回内部错误。</summary>
    private RtResult<IReadOnlyList<Value>> PopArgs(List<StackVal> stack, int argc)
    {
        var args = PopN(stack, argc);
        return args.IsOk
            ? RtResult<IReadOnlyList<Value>>.Ok(args.Value.Select(e => e.V).ToList())
            : RtResult<IReadOnlyList<Value>>.Fail(args.Error!);
    }

    private RtResult<BlockId?> ExecIndexGet(StackVal obj, StackVal idx, List<StackVal> stack)
    {
        var ov = _machine.DerefValue(obj.V);
        if (!ov.IsOk)
        {
            return RtResult<BlockId?>.Fail(ov.Error!);
        }

        var iv = _machine.DerefValue(idx.V);
        if (!iv.IsOk)
        {
            return RtResult<BlockId?>.Fail(iv.Error!);
        }

        switch (ov.Value)
        {
            case Value.Slice id:
                {
                    var len = _machine.Heap.SliceLen(id.Id);
                    if (!len.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(len.Error!);
                    }

                    var i = IntIndex(iv.Value, len.Value);
                    if (!i.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(i.Error!);
                    }

                    var v = _machine.Heap.SliceGet(id.Id, i.Value);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    stack.Add(new StackVal(v.Value, new Place.Element(id.Id, i.Value)));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Value.Map m:
                {
                    var key = _machine.MapKeyOf(iv.Value);
                    if (!key.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(key.Error!);
                    }

                    var v = _machine.Heap.MapGet(m.Id, key.Value);
                    if (!v.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(v.Error!);
                    }

                    stack.Add(new StackVal(v.Value, new Place.MapEntry(m.Id, key.Value)));
                    return RtResult<BlockId?>.Ok(null);
                }

            case Value.Str s:
                {
                    var runes = s.V.EnumerateRunes().ToList();
                    var i = IntIndex(iv.Value, runes.Count);
                    if (!i.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(i.Error!);
                    }

                    return Ok1(stack, new Value.Str(runes[(int)i.Value].ToString()));
                }

            default:
                return RtResult<BlockId?>.Fail(new RtError(
                    RtErrorKind.TypeCast, $"{ov.Value.TypeHint()} 不可索引"));
        }
    }

    private RtResult<BlockId?> ExecIndexSet(StackVal obj, StackVal idx, Value val)
    {
        var ov = _machine.DerefValue(obj.V);
        if (!ov.IsOk)
        {
            return RtResult<BlockId?>.Fail(ov.Error!);
        }

        var iv = _machine.DerefValue(idx.V);
        if (!iv.IsOk)
        {
            return RtResult<BlockId?>.Fail(iv.Error!);
        }

        switch (ov.Value)
        {
            case Value.Slice id:
                {
                    var len = _machine.Heap.SliceLen(id.Id);
                    if (!len.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(len.Error!);
                    }

                    var i = IntIndex(iv.Value, len.Value);
                    if (!i.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(i.Error!);
                    }

                    var set = _machine.Heap.SliceSet(id.Id, i.Value, val);
                    return set.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(set.Error!);
                }

            case Value.Map m:
                {
                    var ins = _machine.Heap.MapInsert(m.Id, iv.Value, val);
                    return ins.IsOk
                        ? RtResult<BlockId?>.Ok(null)
                        : RtResult<BlockId?>.Fail(ins.Error!);
                }

            case Value.Str:
                return RtResult<BlockId?>.Fail(new RtError(
                    RtErrorKind.TypeCast, "字符串不可索引赋值"));
            default:
                return RtResult<BlockId?>.Fail(new RtError(
                    RtErrorKind.TypeCast, $"{ov.Value.TypeHint()} 不可索引赋值"));
        }
    }

    private RtResult<BlockId?> ExecSliceGet(StackVal obj, Value lo, Value hi, List<StackVal> stack)
    {
        var ov = _machine.DerefValue(obj.V);
        if (!ov.IsOk)
        {
            return RtResult<BlockId?>.Fail(ov.Error!);
        }

        if (lo is not Value.Int loI || hi is not Value.Int hiI)
        {
            return RtResult<BlockId?>.Fail(new RtError(
                RtErrorKind.TypeCast, "切片端点须为 int"));
        }

        switch (ov.Value)
        {
            case Value.Slice id:
                {
                    var len = _machine.Heap.SliceLen(id.Id);
                    if (!len.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(len.Error!);
                    }

                    var b = CheckSlice(loI.V, hiI.V, len.Value);
                    if (!b.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(b.Error!);
                    }

                    var items = _machine.Heap.Slice(id.Id);
                    if (!items.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(items.Error!);
                    }

                    var seg = items.Value.Skip((int)loI.V).Take((int)(hiI.V - loI.V)).ToList();
                    return Ok1(stack, _machine.AllocSliceValue(seg));
                }

            case Value.Str s:
                {
                    var runes = s.V.EnumerateRunes().ToList();
                    var b = CheckSlice(loI.V, hiI.V, runes.Count);
                    if (!b.IsOk)
                    {
                        return RtResult<BlockId?>.Fail(b.Error!);
                    }

                    var text = string.Concat(
                        runes.Skip((int)loI.V).Take((int)(hiI.V - loI.V)).Select(r => r.ToString()));
                    return Ok1(stack, new Value.Str(text));
                }

            default:
                return RtResult<BlockId?>.Fail(new RtError(
                    RtErrorKind.TypeCast, $"{ov.Value.TypeHint()} 不可切片"));
        }
    }

    private RtResult<Value> CallBuiltinByName(string name, IReadOnlyList<Value> args)
    {
        var id = _machine.Builtins.Lookup(name);
        if (id is null)
        {
            return RtResult<Value>.Fail(RtError.Internal($"内建 {name} 未注册"));
        }

        if (_machine.Builtins.Kind(id.Value) == BuiltinKind.Host)
        {
            return InterpFmt.HostBuiltin(_machine, name, args);
        }

        return _machine.CallBuiltin(_sink, InterpFmt.Instance, id.Value, args);
    }

    /// <summary><see cref="TaskExec"/> 面：Spawn/Recv 协作点的 Task 体执行（返回值被调度器丢弃）。</summary>
    RtResult<Value> TaskExec.Call(Machine machine, FunctionId func, IReadOnlyList<Value> args) =>
        CallFunction(func.Value, args, null);

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

    /// <summary>索引合法性（RUNTIME-INDEX-001）：须为 int 且 0 &lt;= i &lt; len。</summary>
    private static RtResult<uint> IntIndex(Value v, int len)
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

    private static RtResult CheckSlice(long lo, long hi, int len)
    {
        return lo < 0 || hi < lo || hi > len
            ? RtResult.Fail(new RtError(
                RtErrorKind.Index, $"切片 [{lo}:{hi}] 越界（长度 {len}）"))
            : RtResult.Ok;
    }
}
