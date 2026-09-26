using Lang.Core;
using Lang.Ir;

namespace Lang.Ir.Tests;

/// <summary>手构 IR 测试助手：最小合法形态 + 全指令覆盖程序（活文档）。</summary>
internal static class TestSupport
{
    internal static readonly LocalDecl[] TenLocals =
        Enumerable.Range(0, 10).Select(i => new LocalDecl($"t{i}", true, null)).ToArray();

    internal static IrProgram Prog(
        IReadOnlyList<StructDecl>? structs = null,
        IReadOnlyList<EnumDecl>? enums = null,
        IReadOnlyList<GlobalDecl>? globals = null,
        IrFunction[]? fns = null,
        FunctionId? init = null,
        FunctionId? main = null) =>
        new(structs ?? Array.Empty<StructDecl>(), enums ?? Array.Empty<EnumDecl>(),
            globals ?? Array.Empty<GlobalDecl>(), fns ?? Array.Empty<IrFunction>(), init, main);

    internal static IrFunction Fn(
        string name,
        int captures,
        int paramCount,
        bool hasRet,
        IReadOnlyList<LocalDecl>? locals = null,
        IReadOnlyList<Block>? blocks = null,
        string? externHost = null) =>
        new(name, captures, new FnSig(paramCount, hasRet), locals ?? Array.Empty<LocalDecl>(),
            blocks ?? Array.Empty<Block>(), externHost);

    internal static Block B(Inst[] insts, Terminator term) => new(insts, term);

    internal static LocalDecl L(string name) => new(name, true, null);

    /// <summary>
    /// 全指令覆盖程序（37 Inst + 6 Terminator 逐项出现；活文档，validator/dump/确定性共用）。
    /// 块图：b0 入口 Branch → b1 主体 SwitchInt → b3 命中 Goto b4 / default b4 → SwitchEnum
    /// → b5/b6 Goto b7 → b7 迭代循环（done=b8 Return）→ b9 Unreachable 锚；
    /// b2 提前 Return；b10 = Try 的 fail 块（合成 Err 返回）。
    /// </summary>
    internal static IrProgram FullFeature()
    {
        var mainBlocks = new[]
        {
            // b0：入口。常量/局部/全局/一元/二元/Pop + 条件分支。
            B(new Inst[]
            {
                new Inst.ConstI64(1), new Inst.LocalSet(new LocalId(0)),
                new Inst.LocalGet(new LocalId(0)), new Inst.LocalGet(new LocalId(0)),
                new Inst.Binary(Binop.Add), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(0)), new Inst.Unary(Unop.Neg), new Inst.Pop(),
                new Inst.GlobalGet(new GlobalId(0)), new Inst.GlobalSet(new GlobalId(0)),
                new Inst.ConstBool(true),
            }, new Terminator.Branch(new BlockId(1), new BlockId(2))),
            // b1：主体。索引/切片/字段/构造/装箱/迭代器/chan/Try/调用/闭包/spawn。
            B(new Inst[]
            {
                new Inst.ConstF64(1.5), new Inst.Pop(),
                new Inst.ConstStr("héllo 🦀"), new Inst.Pop(),
                new Inst.ConstI64(2), new Inst.ConstI64(3), new Inst.RangeNew(), new Inst.Pop(),
                new Inst.ConstI64(1), new Inst.ConstI64(2), new Inst.ArrayNew(2),
                new Inst.LocalSet(new LocalId(1)),
                new Inst.LocalGet(new LocalId(1)), new Inst.ConstI64(0), new Inst.IndexGet(), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(1)), new Inst.ConstI64(0), new Inst.ConstI64(9),
                new Inst.IndexSet(),
                new Inst.LocalGet(new LocalId(1)), new Inst.ConstI64(0), new Inst.ConstI64(1),
                new Inst.SliceGet(), new Inst.Pop(),
                new Inst.ConstI64(7), new Inst.ConstI64(8), new Inst.StructNew(new StructId(0)),
                new Inst.LocalSet(new LocalId(2)),
                new Inst.LocalGet(new LocalId(2)), new Inst.FieldGet("x"), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(2)), new Inst.ConstI64(42), new Inst.FieldSet("x"),
                new Inst.LocalGet(new LocalId(1)), new Inst.ConstI64(5), new Inst.ArrayPush(),
                new Inst.ConstI64(1), new Inst.ConstI64(2), new Inst.MapNew(1), new Inst.Pop(),
                new Inst.ConstI64(1), new Inst.EnumNew(new EnumId(0), 0), new Inst.LocalSet(new LocalId(3)),
                new Inst.ConstI64(1), new Inst.BoxNew(), new Inst.LocalSet(new LocalId(4)),
                new Inst.LocalGet(new LocalId(4)), new Inst.Deref(), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(4)), new Inst.ConstI64(3), new Inst.RefSet(),
                new Inst.LocalGet(new LocalId(3)), new Inst.EnumPayloadGet(0), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(1)), new Inst.IterNew(), new Inst.LocalSet(new LocalId(5)),
                new Inst.ChanNew(), new Inst.LocalSet(new LocalId(6)),
                new Inst.LocalGet(new LocalId(6)), new Inst.ConstI64(1), new Inst.ChanSend(),
                new Inst.LocalGet(new LocalId(6)), new Inst.ChanRecv(), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(3)), new Inst.Try(new LocalId(7), new BlockId(10)),
                new Inst.Pop(),
                new Inst.ConstI64(1), new Inst.Call(new FunctionId(1)), new Inst.Pop(),
                new Inst.ConstStr("hi"), new Inst.CallBuiltin("println", 1, false),
                new Inst.ConstStr("abc"), new Inst.CallBuiltin("len", 1, true), new Inst.Pop(),
                new Inst.LocalGet(new LocalId(4)), new Inst.ClosureNew(new FunctionId(2), 1),
                new Inst.LocalSet(new LocalId(8)),
                new Inst.LocalGet(new LocalId(8)), new Inst.ConstI64(9),
                new Inst.CallIndirect(1, true), new Inst.Pop(),
                new Inst.ConstI64(1), new Inst.Spawn(new FunctionId(1), 1),
                new Inst.LocalGet(new LocalId(0)),
            }, new Terminator.SwitchInt(new[] { (1L, new BlockId(3)) }, new BlockId(4))),
            // b2：条件为 false 直接返回。
            B(Array.Empty<Inst>(), new Terminator.Return(false)),
            // b3：int match 命中臂。
            B(Array.Empty<Inst>(), new Terminator.Goto(new BlockId(4))),
            // b4：enum match 分派（穷尽：无 default）。
            B(new Inst[] { new Inst.LocalGet(new LocalId(3)) },
                new Terminator.SwitchEnum(new EnumId(0), new[] { new BlockId(5), new BlockId(6) }, null)),
            B(Array.Empty<Inst>(), new Terminator.Goto(new BlockId(7))),
            B(Array.Empty<Inst>(), new Terminator.Goto(new BlockId(7))),
            // b7：for-in 迭代循环（迭代器驻 l5，元素落 l9）。
            B(new Inst[]
            {
                new Inst.LocalGet(new LocalId(5)),
                new Inst.IterNext(new BlockId(8)),
                new Inst.LocalSet(new LocalId(9)),
            }, new Terminator.Goto(new BlockId(7))),
            // b8：迭代耗尽。
            B(Array.Empty<Inst>(), new Terminator.Return(false)),
            // b9：不可达锚（穷尽 match 兜底形态）。
            B(Array.Empty<Inst>(), new Terminator.Unreachable()),
            // b10：Try fail 块：err 槽 → EnumNew(Result.Err) → Return。
            B(new Inst[]
            {
                new Inst.LocalGet(new LocalId(7)),
                new Inst.EnumNew(new EnumId(1), 1),
            }, new Terminator.Return(true)),
        };

        var fns = new[]
        {
            Fn("main", 0, 0, false, locals: TenLocals, blocks: mainBlocks),
            Fn("helper", 0, 1, true, locals: new[] { L("n") },
                blocks: new[] { B(new Inst[] { new Inst.LocalGet(new LocalId(0)) }, new Terminator.Return(true)) }),
            Fn("closure", 1, 1, true, locals: new[] { L("cap"), L("n") },
                blocks: new[]
                {
                    B(new Inst[]
                    {
                        new Inst.LocalGet(new LocalId(0)), new Inst.LocalGet(new LocalId(1)),
                        new Inst.Binary(Binop.Add),
                    }, new Terminator.Return(true)),
                }),
            Fn("hostRead", 0, 1, true, locals: new[] { L("arg") }, externHost: "go:hostRead"),
        };

        return Prog(
            structs: new[]
            {
                new StructDecl("Point", Array.Empty<string>(), new FieldDecl[] { new("x", null), new("y", null) }),
            },
            enums: new[]
            {
                new EnumDecl("Shape", Array.Empty<string>(), new EnumCase[]
                {
                    new("Circle", new TypeId?[] { null }), new("None", Array.Empty<TypeId?>()),
                }),
                new EnumDecl("Result", Array.Empty<string>(), new EnumCase[]
                {
                    new("Ok", new TypeId?[] { null }), new("Err", new TypeId?[] { null }),
                }),
            },
            globals: new GlobalDecl[] { new("counter", true, null), new("name", false, null) },
            fns: fns,
            main: new FunctionId(0));
    }

    /// <summary>遍历 37 条指令各产出一个样本（效果/dump 覆盖测试用；漏新变体时此表先红）。</summary>
    internal static IReadOnlyList<Inst> AllInstSamples() => new Inst[]
    {
        new Inst.ConstI64(1),
        new Inst.ConstF64(1.5),
        new Inst.ConstBool(true),
        new Inst.ConstStr("s"),
        new Inst.LocalGet(new LocalId(0)),
        new Inst.LocalSet(new LocalId(0)),
        new Inst.GlobalGet(new GlobalId(0)),
        new Inst.GlobalSet(new GlobalId(0)),
        new Inst.Unary(Unop.Neg),
        new Inst.Binary(Binop.Add),
        new Inst.Pop(),
        new Inst.Call(new FunctionId(0)),
        new Inst.CallIndirect(1, true),
        new Inst.CallBuiltin("len", 1, true),
        new Inst.Spawn(new FunctionId(0), 0),
        new Inst.ClosureNew(new FunctionId(0), 0),
        new Inst.IndexGet(),
        new Inst.IndexSet(),
        new Inst.SliceGet(),
        new Inst.FieldGet("f"),
        new Inst.FieldSet("f"),
        new Inst.ArrayPush(),
        new Inst.ArrayNew(1),
        new Inst.MapNew(1),
        new Inst.StructNew(new StructId(0)),
        new Inst.EnumNew(new EnumId(0), 0),
        new Inst.RangeNew(),
        new Inst.BoxNew(),
        new Inst.Deref(),
        new Inst.RefSet(),
        new Inst.EnumPayloadGet(0),
        new Inst.IterNew(),
        new Inst.IterNext(new BlockId(0)),
        new Inst.ChanNew(),
        new Inst.ChanSend(),
        new Inst.ChanRecv(),
        new Inst.Try(new LocalId(0), new BlockId(0)),
    };
}
