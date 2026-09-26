using Lang.Core;

namespace Lang.Ir;

/// <summary>
/// 校验失败（内部错误形态；定位到函数/块/指令序号）。
/// 这里的失败全部是<b>内部错误</b>（lowering/手写 IR 的不变量破坏），
/// 不是用户诊断——用户源码问题在 semantic 期已被拒绝（IR 不产生用户诊断，IR 不含 span）。
/// </summary>
public sealed record IrError(FunctionId Function, BlockId? Block, int? Inst, string Message)
{
    public override string ToString()
    {
        var s = $"IR 内部错误 fn#{Function.Value}";
        if (Block is not null)
        {
            s += $" block#{Block.Value.Value}";
        }
        if (Inst is not null)
        {
            s += $" inst#{Inst.Value}";
        }
        return s + $": {Message}";
    }
}

/// <summary>
/// IR Validator：线性扫描结构校验（纯线性扫描，无递归无图遍历）。
/// 校验面（栈式 + 基本块模型的结构不变量）：
/// 1. 引用合法：LocalId/GlobalId/FunctionId/BlockId/StructId/EnumId 全部落表；
///    EnumNew.Case 落 case 数内。
/// 2. 签名一致：Call/Spawn 实参个数 = 目标 ParamCount；ClosureNew.Captures = 目标 CaptureCount。
/// 3. 块终接：每块恰一个显式终接（结构上由 Block.Term 保证）；非 extern 函数至少一个块；
///    extern 函数不得有块。
/// 4. 栈效应：块入口栈深 0；逐指令弹压平衡（下溢即错）；终接处栈深恰为终接消费量；
///    终接后归零。SwitchEnum arms 长度 = case 数。
/// </summary>
public static class Validator
{
    /// <summary>校验整个程序。合法返回 null，否则返回首个错误。</summary>
    public static IrError? Validate(IrProgram prog)
    {
        for (int fi = 0; fi < prog.Functions.Count; fi++)
        {
            var err = ValidateFunction(prog, fi, prog.Functions[fi]);
            if (err is not null)
            {
                return err;
            }
        }
        return null;
    }

    private static IrError? ValidateFunction(IrProgram prog, int fi, IrFunction func)
    {
        var fid = new FunctionId((uint)fi);
        // extern 函数：只有声明，无块。
        if (func.ExternHost is not null)
        {
            if (func.Blocks.Count != 0)
            {
                return Err(fid, null, null, "extern 函数不得有函数体");
            }
            return null;
        }
        if (func.Blocks.Count == 0)
        {
            return Err(fid, null, null, "函数没有基本块（缺入口块）");
        }
        // 捕获槽 + 参数必须落在局部表内。
        if (func.Locals.Count < func.FixedSlots)
        {
            return Err(
                fid, null, null,
                $"局部表 {func.Locals.Count} 个槽位 < 捕获 {func.CaptureCount} + 参数 {func.Sig.ParamCount}");
        }

        for (int bi = 0; bi < func.Blocks.Count; bi++)
        {
            var err = ValidateBlock(prog, func, fid, bi, func.Blocks[bi]);
            if (err is not null)
            {
                return err;
            }
        }
        return null;
    }

    private static IrError? ValidateBlock(IrProgram prog, IrFunction func, FunctionId fid, int bi, Block block)
    {
        var bid = new BlockId((uint)bi);
        int nBlocks = func.Blocks.Count;
        int depth = 0;

        for (int ii = 0; ii < block.Insts.Count; ii++)
        {
            var inst = block.Insts[ii];
            IrError? err;
            switch (inst)
            {
                case Inst.ConstI64 or Inst.ConstF64 or Inst.ConstBool or Inst.ConstStr or Inst.ChanNew:
                    depth += 1;
                    break;

                case Inst.LocalGet local:
                    err = CheckLocal(func, fid, bid, ii, local.Local);
                    if (err is not null) return err;
                    depth += 1;
                    break;

                case Inst.LocalSet local:
                    err = CheckLocal(func, fid, bid, ii, local.Local);
                    if (err is not null) return err;
                    err = Pop(ref depth, 1, fid, bid, ii, "LocalSet");
                    if (err is not null) return err;
                    break;

                case Inst.GlobalGet globalGet:
                    err = CheckGlobal(prog, fid, bid, ii, globalGet.Global);
                    if (err is not null) return err;
                    depth += 1;
                    break;

                case Inst.GlobalSet globalSet:
                    err = CheckGlobal(prog, fid, bid, ii, globalSet.Global);
                    if (err is not null) return err;
                    err = Pop(ref depth, 1, fid, bid, ii, "GlobalSet");
                    if (err is not null) return err;
                    break;

                case Inst.Unary:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "Unary");
                    if (err is not null) return err;
                    break;

                case Inst.Binary:
                    err = PopPush(ref depth, 2, 1, fid, bid, ii, "Binary");
                    if (err is not null) return err;
                    break;

                case Inst.Pop:
                    err = Pop(ref depth, 1, fid, bid, ii, "Pop");
                    if (err is not null) return err;
                    break;

                case Inst.Call call:
                    {
                        var sig = CheckFunction(prog, fid, bid, ii, call.Func, out err);
                        if (err is not null) return err;
                        err = Pop(ref depth, sig.ParamCount, fid, bid, ii, "Call");
                        if (err is not null) return err;
                        if (sig.HasRet) depth += 1;
                        break;
                    }

                case Inst.CallIndirect indirect:
                    // 先压闭包值再压实参。
                    err = Pop(ref depth, indirect.Argc + 1, fid, bid, ii, "CallIndirect");
                    if (err is not null) return err;
                    if (indirect.HasRet) depth += 1;
                    break;

                case Inst.CallBuiltin builtin:
                    err = Pop(ref depth, builtin.Argc, fid, bid, ii, "CallBuiltin");
                    if (err is not null) return err;
                    if (builtin.HasRet) depth += 1;
                    break;

                case Inst.Spawn spawn:
                    {
                        var sig = CheckFunction(prog, fid, bid, ii, spawn.Func, out err);
                        if (err is not null) return err;
                        if (spawn.Argc != sig.ParamCount)
                        {
                            return BadRef(fid, bid, ii,
                                $"Spawn 实参 {spawn.Argc} 个 != 目标 fn#{spawn.Func.Value} 参数 {sig.ParamCount} 个");
                        }
                        err = Pop(ref depth, spawn.Argc, fid, bid, ii, "Spawn");
                        if (err is not null) return err;
                        break;
                    }

                case Inst.ClosureNew closure:
                    {
                        var callee = LookupFunction(prog, fid, bid, ii, closure.Func, out err);
                        if (err is not null) return err;
                        if (closure.Captures != callee.CaptureCount)
                        {
                            return BadRef(fid, bid, ii,
                                $"ClosureNew 捕获 {closure.Captures} 个 != 目标 fn#{closure.Func.Value} CaptureCount {callee.CaptureCount}");
                        }
                        err = Pop(ref depth, closure.Captures, fid, bid, ii, "ClosureNew");
                        if (err is not null) return err;
                        depth += 1;
                        break;
                    }

                case Inst.IndexGet:
                    err = PopPush(ref depth, 2, 1, fid, bid, ii, "IndexGet");
                    if (err is not null) return err;
                    break;

                case Inst.IndexSet:
                    err = Pop(ref depth, 3, fid, bid, ii, "IndexSet");
                    if (err is not null) return err;
                    break;

                case Inst.SliceGet:
                    err = PopPush(ref depth, 3, 1, fid, bid, ii, "SliceGet");
                    if (err is not null) return err;
                    break;

                case Inst.FieldGet:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "FieldGet");
                    if (err is not null) return err;
                    break;

                case Inst.FieldSet:
                    err = Pop(ref depth, 2, fid, bid, ii, "FieldSet");
                    if (err is not null) return err;
                    break;

                case Inst.ArrayPush:
                    err = Pop(ref depth, 2, fid, bid, ii, "ArrayPush");
                    if (err is not null) return err;
                    break;

                case Inst.ArrayNew arrayNew:
                    err = Pop(ref depth, arrayNew.Count, fid, bid, ii, "ArrayNew");
                    if (err is not null) return err;
                    depth += 1;
                    break;

                case Inst.MapNew mapNew:
                    err = Pop(ref depth, 2 * mapNew.Count, fid, bid, ii, "MapNew");
                    if (err is not null) return err;
                    depth += 1;
                    break;

                case Inst.StructNew structNew:
                    {
                        int si = (int)structNew.Struct.Value;
                        if (si >= prog.Structs.Count)
                        {
                            return BadRef(fid, bid, ii,
                                $"StructId {structNew.Struct.Value} 越界（共 {prog.Structs.Count} 个 struct）");
                        }
                        err = Pop(ref depth, prog.Structs[si].Fields.Count, fid, bid, ii, "StructNew");
                        if (err is not null) return err;
                        depth += 1;
                        break;
                    }

                case Inst.EnumNew enumNew:
                    {
                        int ei = (int)enumNew.Enum.Value;
                        if (ei >= prog.Enums.Count)
                        {
                            return BadRef(fid, bid, ii,
                                $"EnumId {enumNew.Enum.Value} 越界（共 {prog.Enums.Count} 个 enum）");
                        }
                        var cases = prog.Enums[ei].Cases;
                        if (enumNew.Case >= cases.Count)
                        {
                            return BadRef(fid, bid, ii,
                                $"case {enumNew.Case} 越界（enum {enumNew.Enum.Value} 共 {cases.Count} 个 case）");
                        }
                        err = Pop(ref depth, cases[enumNew.Case].PayloadTypes.Count, fid, bid, ii, "EnumNew");
                        if (err is not null) return err;
                        depth += 1;
                        break;
                    }

                case Inst.RangeNew:
                    err = PopPush(ref depth, 2, 1, fid, bid, ii, "RangeNew");
                    if (err is not null) return err;
                    break;

                case Inst.BoxNew:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "BoxNew");
                    if (err is not null) return err;
                    break;

                case Inst.Deref:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "Deref");
                    if (err is not null) return err;
                    break;

                case Inst.RefSet:
                    err = Pop(ref depth, 2, fid, bid, ii, "RefSet");
                    if (err is not null) return err;
                    break;

                case Inst.EnumPayloadGet:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "EnumPayloadGet");
                    if (err is not null) return err;
                    break;

                case Inst.IterNew:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "IterNew");
                    if (err is not null) return err;
                    break;

                case Inst.IterNext iterNext:
                    err = CheckBlock(nBlocks, fid, bid, ii, iterNext.Done);
                    if (err is not null) return err;
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "IterNext");
                    if (err is not null) return err;
                    break;

                case Inst.ChanSend:
                    err = Pop(ref depth, 2, fid, bid, ii, "ChanSend");
                    if (err is not null) return err;
                    break;

                case Inst.ChanRecv:
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "ChanRecv");
                    if (err is not null) return err;
                    break;

                case Inst.Try tryInst:
                    err = CheckLocal(func, fid, bid, ii, tryInst.Err);
                    if (err is not null) return err;
                    err = CheckBlock(nBlocks, fid, bid, ii, tryInst.Fail);
                    if (err is not null) return err;
                    err = PopPush(ref depth, 1, 1, fid, bid, ii, "Try");
                    if (err is not null) return err;
                    break;

                default:
                    throw new InvalidOperationException("IR 指令变体未穷尽（禁 default 兜底，新变体必须显式登记栈效应）");
            }
        }

        // 终接：消费量必须恰为当前栈深，消费后归零；跳转目标全部落表内。
        IrError? termErr = ValidateTerminator(prog, func, fid, bid, block.Term, depth);
        return termErr;
    }

    private static IrError? ValidateTerminator(
        IrProgram prog, IrFunction func, FunctionId fid, BlockId bid, Terminator term, int depth)
    {
        int nBlocks = func.Blocks.Count;
        int consumes;
        var targets = new List<BlockId>();
        switch (term)
        {
            case Terminator.Goto gotoTerm:
                consumes = 0;
                targets.Add(gotoTerm.Target);
                break;

            case Terminator.Branch branch:
                consumes = 1;
                targets.Add(branch.IfTrue);
                targets.Add(branch.IfFalse);
                break;

            case Terminator.SwitchInt switchInt:
                consumes = 1;
                foreach (var (_, target) in switchInt.Arms)
                {
                    targets.Add(target);
                }
                if (switchInt.Default is not null)
                {
                    targets.Add(switchInt.Default.Value);
                }
                break;

            case Terminator.SwitchEnum switchEnum:
                {
                    int ei = (int)switchEnum.Enum.Value;
                    if (ei >= prog.Enums.Count)
                    {
                        return BadRef(fid, bid, null,
                            $"EnumId {switchEnum.Enum.Value} 越界（共 {prog.Enums.Count} 个 enum）");
                    }
                    int caseCount = prog.Enums[ei].Cases.Count;
                    if (switchEnum.Arms.Count != caseCount)
                    {
                        return BadRef(fid, bid, null,
                            $"SwitchEnum arms {switchEnum.Arms.Count} 个 != enum {switchEnum.Enum.Value} case {caseCount} 个");
                    }
                    consumes = 1;
                    targets.AddRange(switchEnum.Arms);
                    if (switchEnum.Default is not null)
                    {
                        targets.Add(switchEnum.Default.Value);
                    }
                    break;
                }

            case Terminator.Return ret:
                consumes = ret.HasValue ? 1 : 0;
                break;

            case Terminator.Unreachable:
                consumes = 0;
                break;

            default:
                throw new InvalidOperationException("IR 终接变体未穷尽（禁 default 兜底，新变体必须显式登记栈效应）");
        }

        if (depth != consumes)
        {
            return Err(fid, bid, null,
                $"终接处栈深 {depth} != 终接消费 {consumes}（块必须以平衡栈收尾）");
        }
        foreach (var t in targets)
        {
            var err = CheckBlock(nBlocks, fid, bid, null, t);
            if (err is not null)
            {
                return err;
            }
        }
        return null;
    }

    private static IrFunction LookupFunction(
        IrProgram prog, FunctionId fid, BlockId bid, int ii, FunctionId target, out IrError? error)
    {
        if (target.Value >= (uint)prog.Functions.Count)
        {
            error = BadRef(fid, bid, ii,
                $"FunctionId {target.Value} 越界（共 {prog.Functions.Count} 个函数）");
            return null!; // 调用方先判 error；此路径不使用返回值
        }
        error = null;
        return prog.Functions[(int)target.Value];
    }

    private static FnSig CheckFunction(
        IrProgram prog, FunctionId fid, BlockId bid, int ii, FunctionId target, out IrError? error)
    {
        var func = LookupFunction(prog, fid, bid, ii, target, out error);
        return error is null ? func.Sig : default;
    }

    private static IrError? CheckLocal(IrFunction func, FunctionId fid, BlockId bid, int ii, LocalId local)
    {
        if (local.Value >= (uint)func.Locals.Count)
        {
            return BadRef(fid, bid, ii, $"LocalId {local.Value} 越界（共 {func.Locals.Count} 个局部）");
        }
        return null;
    }

    private static IrError? CheckGlobal(IrProgram prog, FunctionId fid, BlockId bid, int ii, GlobalId global)
    {
        if (global.Value >= (uint)prog.Globals.Count)
        {
            return BadRef(fid, bid, ii, $"GlobalId {global.Value} 越界（共 {prog.Globals.Count} 个全局）");
        }
        return null;
    }

    private static IrError? CheckBlock(int nBlocks, FunctionId fid, BlockId bid, int? ii, BlockId target)
    {
        if (target.Value >= (uint)nBlocks)
        {
            return BadRef(fid, bid, ii, $"BlockId {target.Value} 越界（共 {nBlocks} 个块）");
        }
        return null;
    }

    private static IrError? Pop(ref int depth, int n, FunctionId fid, BlockId bid, int ii, string what)
    {
        if (depth < n)
        {
            return Err(fid, bid, ii, $"{what} 栈下溢：需要 {n} 个操作数，只有 {depth}");
        }
        depth -= n;
        return null;
    }

    private static IrError? PopPush(ref int depth, int popN, int pushN, FunctionId fid, BlockId bid, int ii, string what)
    {
        var err = Pop(ref depth, popN, fid, bid, ii, what);
        if (err is not null)
        {
            return err;
        }
        depth += pushN;
        return null;
    }

    private static IrError Err(FunctionId fid, BlockId? bid, int? ii, string message) =>
        new(fid, bid, ii, message);

    private static IrError BadRef(FunctionId fid, BlockId? bid, int? ii, string message) =>
        new(fid, bid, ii, message);
}
