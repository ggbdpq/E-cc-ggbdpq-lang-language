using Lang.Core;
using Lang.Ir;

namespace Lang.BackendGo.Tests;

/// <summary>
/// E2E 夹具程序（形状参照 lang-rs lang-backend-go tests/e2e.rs 同一夹具，
/// 语义覆盖对抗矩阵）：算术 / 字符串 / 闭包捕获计数器 / 引用写穿 / slice-map
/// 共享 / struct 拷贝 / map 插入序打印 / chan FIFO / Option-Result / ? 传播 /
/// spawn 快照，另加 for-in、SwitchInt、全局 init、一元、字符串切片索引，以及
/// lang-rs 集成轮「B1 写链缺口」回归段（数组元素 struct 经 IndexGet 链写字段）。
/// 每块栈效应手算平衡，过 Validator。
/// </summary>
internal static class Fixture
{
    internal static Inst C64(long v) => new Inst.ConstI64(v);
    internal static Inst CStr(string s) => new Inst.ConstStr(s);
    internal static Inst Ld(uint i) => new Inst.LocalGet(new LocalId(i));
    internal static Inst St(uint i) => new Inst.LocalSet(new LocalId(i));
    internal static Inst Bin(Binop op) => new Inst.Binary(op);
    internal static Inst PrintlnN(int n) => new Inst.CallBuiltin("println", n, false);
    internal static Inst Builtin(string name, int argc, bool hasRet) => new Inst.CallBuiltin(name, argc, hasRet);
    internal static Block Blk(Inst[] insts, Terminator term) => new(insts, term);
    internal static Terminator RetV() => new Terminator.Return(true);
    internal static Terminator Ret0() => new Terminator.Return(false);
    internal static Terminator Goto(uint t) => new Terminator.Goto(new BlockId(t));
    internal static LocalDecl Local(string name) => new(name, true, null);

    /// <summary>E2E 夹具程序（每块栈效应手算平衡）。</summary>
    internal static IrProgram Build()
    {
        var structs = new[]
        {
            new StructDecl("P", [], [new FieldDecl("x", null), new FieldDecl("y", null)]),
        };
        var enums = new[]
        {
            new EnumDecl("Option", ["T"], [new EnumCase("Some", [null]), new EnumCase("None", [])]),
            new EnumDecl("Result", ["T", "E"], [new EnumCase("Ok", [null]), new EnumCase("Err", [null])]),
        };
        var globals = new[] { new GlobalDecl("counter", true, null) };

        // fn0 init：counter = 7
        var init = new IrFunction("init", 0, new FnSig(0, false), [],
            [Blk([C64(7), new Inst.GlobalSet(new GlobalId(0))], Ret0())], null);

        // fn1 inc（闭包体）：捕获槽 0（Ref）写穿 +1
        var inc = new IrFunction("inc", 1, new FnSig(0, false), [Local("c0")],
            [Blk([Ld(0), Ld(0), new Inst.Deref(), C64(1), Bin(Binop.Add), new Inst.RefSet()], Ret0())], null);

        // fn2 triple(x) = x*3
        var triple = new IrFunction("triple", 0, new FnSig(1, true), [Local("x")],
            [Blk([Ld(0), C64(3), Bin(Binop.Mul)], RetV())], null);

        // fn3 parse(x) -> Result：x>0 ? Ok(x) : Err(err("neg"))
        var parse = new IrFunction("parse", 0, new FnSig(1, true), [Local("x")],
        [
            Blk([Ld(0), C64(0), Bin(Binop.Gt)],
                new Terminator.Branch(new BlockId(1), new BlockId(2))),
            Blk([Ld(0), new Inst.EnumNew(new EnumId(1), 0)], RetV()),
            Blk([CStr("neg"), Builtin("err", 1, true), new Inst.EnumNew(new EnumId(1), 1)], RetV()),
        ], null);

        // fn4 caller(x) -> Result：let v = parse(x)?; Ok(v*10)
        var caller = new IrFunction("caller", 0, new FnSig(1, true), [Local("x"), Local("err"), Local("v")],
        [
            Blk(
            [
                Ld(0), new Inst.Call(new FunctionId(3)),
                new Inst.Try(new LocalId(1), new BlockId(1)),
                St(2), Ld(2), C64(10), Bin(Binop.Mul),
                new Inst.EnumNew(new EnumId(1), 0),
            ], RetV()),
            Blk([Ld(1), new Inst.EnumNew(new EnumId(1), 1)], RetV()),
        ], null);

        // fn5 send_task(ch, v)：send(ch, v)
        var sendTask = new IrFunction("send_task", 0, new FnSig(2, false), [Local("ch"), Local("v")],
            [Blk([Ld(0), Ld(1), new Inst.ChanSend()], Ret0())], null);

        // fn6 main：矩阵全覆盖（块布局见各段注释）
        var m0 = new Inst[]
        {
            // a = 10 + 5*2 - 4/2 = 18
            C64(10), C64(5), C64(2), Bin(Binop.Mul), Bin(Binop.Add), C64(4), C64(2), Bin(Binop.Div), Bin(Binop.Sub),
            St(1),
            // f = 1.5 + 0.25
            new Inst.ConstF64(1.5), new Inst.ConstF64(0.25), Bin(Binop.Add),
            St(2),
            // s = "a" + "b" + str(3)
            CStr("a"), CStr("b"), Bin(Binop.Add), C64(3), Builtin("str", 1, true), Bin(Binop.Add),
            St(3),
            Ld(1), Ld(2), Ld(3),
            PrintlnN(3), // "18 1.75 ab3"
            Ld(3),
            PrintlnN(1), // "ab3"
            C64(5), new Inst.Call(new FunctionId(2)),
            PrintlnN(1), // "15"
            // 闭包捕获计数器：cbox 捕获进 inc，调 3 次后读穿 = 3
            C64(0), new Inst.BoxNew(),
            St(4),
            Ld(4), new Inst.ClosureNew(new FunctionId(1), 1),
            St(5),
            Ld(5), new Inst.CallIndirect(0, false),
            Ld(5), new Inst.CallIndirect(0, false),
            Ld(5), new Inst.CallIndirect(0, false),
            Ld(4), new Inst.Deref(),
            PrintlnN(1), // "3"
        };
        var b1 = new Inst[]
        {
            // 引用别名写穿：r2 = r1（同一 Box），RefSet(r1, 42) → Deref(r2) = 42
            C64(1), new Inst.BoxNew(),
            St(6),
            Ld(6),
            St(7),
            Ld(6), C64(42), new Inst.RefSet(),
            Ld(7), new Inst.Deref(),
            PrintlnN(1), // "42"
        };
        var b2 = new Inst[]
        {
            // 数组/映射引用共享
            C64(1), new Inst.ArrayNew(1),
            St(8),
            Ld(8),
            St(9),
            Ld(9), C64(2),
            // ArrayPush = 拼接（rhs 须为数组，机器冻结语义）：压 [2] 拼入（B2 教训）
            new Inst.ArrayNew(1),
            new Inst.ArrayPush(),
            Ld(8), Builtin("len", 1, true),
            PrintlnN(1), // "2"
            new Inst.MapNew(0),
            St(10),
            Ld(10),
            St(11),
            Ld(11), CStr("k"), C64(1), new Inst.IndexSet(),
            Ld(10), CStr("k"), Builtin("has", 2, true),
            PrintlnN(1), // "true"
        };
        var b3 = new Inst[]
        {
            // struct 值语义：q = p 拷贝，q.x = 9 不影响 p.x
            C64(1), C64(2), new Inst.StructNew(new StructId(0)),
            St(12),
            Ld(12),
            St(13),
            Ld(13), C64(9), new Inst.FieldSet("x"),
            Ld(12), new Inst.FieldGet("x"),
            Ld(13), new Inst.FieldGet("x"),
            PrintlnN(2), // "1 9"
        };
        var b4 = new Inst[]
        {
            // 映射插入序打印（b、a、c ≠ 任何排序序）
            CStr("b"), C64(1), CStr("a"), C64(2), new Inst.MapNew(2),
            St(14),
            Ld(14), CStr("c"), C64(3), new Inst.IndexSet(),
            Ld(14),
            PrintlnN(1), // {"b": 1, "a": 2, "c": 3}
        };
        var b5 = new Inst[]
        {
            // chan FIFO（先 send 后 recv，缓冲 1024 不阻塞）
            new Inst.ChanNew(),
            St(15),
            Ld(15), C64(1), new Inst.ChanSend(),
            Ld(15), C64(2), new Inst.ChanSend(),
            Ld(15), new Inst.ChanRecv(),
            new Inst.Try(new LocalId(0), new BlockId(24)),
            PrintlnN(1), // "1"
            Ld(15), new Inst.ChanRecv(),
            new Inst.Try(new LocalId(0), new BlockId(24)),
            PrintlnN(1), // "2"
        };
        var b6 = new Inst[]
        {
            C64(5), new Inst.EnumNew(new EnumId(0), 0),
            St(17),
            Ld(17),
        };
        var b7 = new Inst[] { Ld(17), new Inst.EnumPayloadGet(0), PrintlnN(1) }; // "5"
        var b8 = new Inst[] { CStr("none"), PrintlnN(1) };
        var b9 = new Inst[] { C64(5), new Inst.Call(new FunctionId(4)), St(18), Ld(18) };
        var b10 = new Inst[] { Ld(18), new Inst.EnumPayloadGet(0), PrintlnN(1) }; // "50"
        var b11 = new Inst[] { Ld(18), new Inst.EnumPayloadGet(0), new Inst.FieldGet("msg"), PrintlnN(1) };
        var b12 = new Inst[] { C64(-1), new Inst.Call(new FunctionId(4)), St(19), Ld(19) };
        var b13 = new Inst[] { Ld(19), new Inst.EnumPayloadGet(0), PrintlnN(1) };
        var b14 = new Inst[] { Ld(19), new Inst.EnumPayloadGet(0), new Inst.FieldGet("msg"), PrintlnN(1) }; // "neg"
        var b15 = new Inst[]
        {
            // spawn 快照：实参在 go 处取值（x=1），随后改 x=2 不影响 Task
            C64(1),
            St(20),
            new Inst.ChanNew(),
            St(21),
            Ld(21), Ld(20), new Inst.Spawn(new FunctionId(5), 2),
            C64(2),
            St(20),
            Ld(21), new Inst.ChanRecv(),
            new Inst.Try(new LocalId(0), new BlockId(24)),
            PrintlnN(1), // "1"
        };
        var b16 = new Inst[]
        {
            // for-in sum(1..4) = 6
            C64(0),
            St(22),
            C64(1), C64(4), new Inst.RangeNew(), new Inst.IterNew(),
            St(23),
        };
        var b17 = new Inst[]
        {
            Ld(23), new Inst.IterNext(new BlockId(18)),
            Ld(22), Bin(Binop.Add),
            St(22),
        };
        var b18 = new Inst[] { Ld(22), PrintlnN(1) }; // "6"
        var b19 = new Inst[] { C64(1) };
        var b20 = new Inst[] { CStr("zero"), PrintlnN(1) };
        var b21 = new Inst[] { CStr("one"), PrintlnN(1) }; // 命中臂
        var b22 = new Inst[] { CStr("other"), PrintlnN(1) };
        var b23 = new Inst[]
        {
            // 全局（init 初始化 + 读改写）、一元、字符串/数组切片与索引
            new Inst.GlobalGet(new GlobalId(0)), C64(1), Bin(Binop.Add), new Inst.GlobalSet(new GlobalId(0)),
            new Inst.GlobalGet(new GlobalId(0)),
            PrintlnN(1), // "8"
            C64(3), new Inst.Unary(Unop.Neg),
            PrintlnN(1), // "-3"
            new Inst.ConstBool(true), new Inst.Unary(Unop.Not),
            PrintlnN(1), // "false"
            CStr("hello"), C64(1), C64(3), new Inst.SliceGet(),
            PrintlnN(1), // "el"
            CStr("hello"), C64(1), new Inst.IndexGet(),
            PrintlnN(1), // "e"
            C64(1), C64(2), C64(3), new Inst.ArrayNew(3), C64(0), C64(2), new Inst.SliceGet(),
            Builtin("len", 1, true),
            PrintlnN(1), // "2"
        };
        var b24 = new Inst[] { Ld(0) }; // `?` fail 落点：Err(e) → return e（夹具中不可达）
        var b25 = new Inst[]
        {
            // 写链写穿（lang-rs B1 牵出的写链缺口回归段）：arr[0].x = 9 ——
            // IndexGet 作为写链地址组件须发 Ref 读形（langGoIndexRef），
            // 否则 FieldSet 写在 langGoCopy 的副本上、原数组不变（出 7 而非 9）
            C64(7), C64(8), new Inst.StructNew(new StructId(0)),
            new Inst.ArrayNew(1),
            St(24),
            C64(0),
            St(25),
            Ld(24), Ld(25), new Inst.IndexGet(), C64(9), new Inst.FieldSet("x"),
            Ld(24), Ld(25), new Inst.IndexGet(),
            PrintlnN(1), // "P{x: 9, y: 8}"
            C64(0),
        };

        var mainFn = new IrFunction("main", 0, new FnSig(0, true),
            Enumerable.Range(0, 26)
                .Select(i => Local(i switch
                {
                    0 => "err",
                    1 => "a",
                    2 => "f",
                    3 => "s",
                    4 => "cbox",
                    5 => "inc",
                    6 => "r1",
                    7 => "r2",
                    8 => "arr",
                    9 => "arr2",
                    10 => "m",
                    11 => "m2",
                    12 => "p",
                    13 => "q",
                    14 => "m3",
                    15 => "ch",
                    16 => "r",
                    17 => "o",
                    18 => "res",
                    19 => "res2",
                    20 => "x",
                    21 => "ch2",
                    22 => "sum",
                    23 => "it",
                    24 => "arrs",
                    _ => "i0",
                }))
                .ToList(),
            new[]
            {
                Blk(m0, Goto(1)),
                Blk(b1, Goto(2)),
                Blk(b2, Goto(3)),
                Blk(b3, Goto(4)),
                Blk(b4, Goto(5)),
                Blk(b5, Goto(6)),
                Blk(b6, new Terminator.SwitchEnum(new EnumId(0), [new BlockId(7), new BlockId(8)], null)),
                Blk(b7, Goto(9)),
                Blk(b8, Goto(9)),
                Blk(b9, new Terminator.SwitchEnum(new EnumId(1), [new BlockId(10), new BlockId(11)], null)),
                Blk(b10, Goto(12)),
                Blk(b11, Goto(12)),
                Blk(b12, new Terminator.SwitchEnum(new EnumId(1), [new BlockId(13), new BlockId(14)], null)),
                Blk(b13, Goto(15)),
                Blk(b14, Goto(15)),
                Blk(b15, Goto(16)),
                Blk(b16, Goto(17)),
                Blk(b17, Goto(17)),
                Blk(b18, Goto(19)),
                Blk(b19, new Terminator.SwitchInt([(0, new BlockId(20)), (1, new BlockId(21))], new BlockId(22))),
                Blk(b20, Goto(23)),
                Blk(b21, Goto(23)),
                Blk(b22, Goto(23)),
                Blk(b23, Goto(25)),
                Blk(b24, RetV()),
                Blk(b25, RetV()),
            }, null);

        return new IrProgram(structs, enums, globals, [init, inc, triple, parse, caller, sendTask, mainFn],
            new FunctionId(0), new FunctionId(6));
    }

    /// <summary>预期 stdout（夹具内 println 顺序；手算）。</summary>
    internal const string ExpectedStdout =
        """
        18 1.75 ab3
        ab3
        15
        3
        42
        2
        true
        1 9
        {"b": 1, "a": 2, "c": 3}
        1
        2
        5
        50
        neg
        1
        6
        one
        8
        -3
        false
        el
        e
        2
        P{x: 9, y: 8}
        """ + "\n";
}
