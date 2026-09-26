using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>
/// HIR → IR lowering 降形专项测试（第二波）。
///
/// 红→绿锚点：短路不入 Binary（Branch 控制流 + 结果槽）、装箱纪律（捕获/取址槽持
/// Ref：BoxNew/Deref/RefSet）、循环变量逐轮 BoxNew 重绑、`?` → Try + fail 块合成
/// Err 返回、match 四分类的指令形态、init 合成、extern 形态、chan/spawn 专指令、
/// 方法 recv 第 0 参、CallIndirect、全指令覆盖、确定性、死块，以及 B1 集成轮
/// 三教训的直接锁定（struct 字段写原槽 / 索引赋值求值序+压栈序 / 复合赋值经原槽）。
/// </summary>
public class LowerTests
{
    private static readonly SourceId SrcId = new(3);

    /// <summary>源码 → IrProgram（parse → analyze → compile；产物已过 validate）。</summary>
    private static IrProgram CompileSrc(string src)
    {
        var text = new SourceText("test.lang-go", src);
        var parsed = Parser.Parse(text, SrcId);
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, SrcId);
        Assert.True(analysis.Diags.Count == 0,
            $"run 源码应无语义诊断: {string.Join("; ", analysis.Diags.Select(d => d.Code.CodeText()))}");
        var res = Compiler.Compile(parsed.Program, analysis);
        Assert.True(res.IsOk, $"compile 应通过（含 validate）: {res.Error}");
        return res.Value!;
    }

    private static IrFunction FnByName(IrProgram p, string name) =>
        p.Functions.FirstOrDefault(f => f.Name == name)
        ?? throw new InvalidOperationException($"函数 {name} 应存在");

    private static IReadOnlyList<Inst> AllInsts(IrFunction f) =>
        f.Blocks.SelectMany(b => b.Insts).ToList();

    private static IReadOnlyList<Inst> AllInsts(IrProgram p) =>
        p.Functions.SelectMany(AllInsts).ToList();

    private static string KindOf(Inst i) => i switch
    {
        Inst.ConstI64 => "ConstI64",
        Inst.ConstF64 => "ConstF64",
        Inst.ConstBool => "ConstBool",
        Inst.ConstStr => "ConstStr",
        Inst.LocalGet => "LocalGet",
        Inst.LocalSet => "LocalSet",
        Inst.GlobalGet => "GlobalGet",
        Inst.GlobalSet => "GlobalSet",
        Inst.Unary => "Unary",
        Inst.Binary => "Binary",
        Inst.Pop => "Pop",
        Inst.Call => "Call",
        Inst.CallIndirect => "CallIndirect",
        Inst.CallBuiltin => "CallBuiltin",
        Inst.Spawn => "Spawn",
        Inst.ClosureNew => "ClosureNew",
        Inst.IndexGet => "IndexGet",
        Inst.IndexSet => "IndexSet",
        Inst.SliceGet => "SliceGet",
        Inst.FieldGet => "FieldGet",
        Inst.FieldSet => "FieldSet",
        Inst.ArrayPush => "ArrayPush",
        Inst.ArrayNew => "ArrayNew",
        Inst.MapNew => "MapNew",
        Inst.StructNew => "StructNew",
        Inst.EnumNew => "EnumNew",
        Inst.RangeNew => "RangeNew",
        Inst.BoxNew => "BoxNew",
        Inst.Deref => "Deref",
        Inst.RefSet => "RefSet",
        Inst.EnumPayloadGet => "EnumPayloadGet",
        Inst.IterNew => "IterNew",
        Inst.IterNext => "IterNext",
        Inst.ChanNew => "ChanNew",
        Inst.ChanSend => "ChanSend",
        Inst.ChanRecv => "ChanRecv",
        Inst.Try => "Try",
        _ => throw new InvalidOperationException("Inst 变体未穷尽"),
    };

    /// <summary>insts 从 pos 起是否与期望逐条相等。</summary>
    private static bool WindowAt(IReadOnlyList<Inst> insts, int pos, params Inst[] expected)
    {
        if (pos + expected.Length > insts.Count)
        {
            return false;
        }
        for (var k = 0; k < expected.Length; k++)
        {
            if (!insts[pos + k].Equals(expected[k]))
            {
                return false;
            }
        }
        return true;
    }

    private static int IndexOf(IReadOnlyList<Inst> insts, params Inst[] window)
    {
        for (var i = 0; i <= insts.Count - window.Length; i++)
        {
            if (WindowAt(insts, i, window))
            {
                return i;
            }
        }
        return -1;
    }

    // ———— 短路降形（EVAL-SHORTCUT-006） ————

    [Fact]
    public void And_lower_to_branch_not_binary()
    {
        var p = CompileSrc(
            "fn main() { let x = true && false\n    println(x) }");
        var main = FnByName(p, "main");
        // 块 0：lhs 常量 + Branch；rhs 块与短路块汇合到结果槽。
        Assert.Equal(new Inst[] { new Inst.ConstBool(true) }, main.Blocks[0].Insts);
        Assert.IsType<Terminator.Branch>(main.Blocks[0].Term);
        // rhs 块：ConstBool(false) → LocalSet(结果槽) → Goto(end)。
        Assert.Equal(new Inst.ConstBool(false), main.Blocks[1].Insts[0]);
        Assert.IsType<Terminator.Goto>(main.Blocks[1].Term);
        // 短路块：ConstBool(false)（&& 短路值）→ LocalSet → Goto(end)。
        Assert.Equal(new Inst.ConstBool(false), main.Blocks[2].Insts[0]);
        Assert.IsType<Terminator.Goto>(main.Blocks[2].Term);
        // 汇合块：LocalGet(结果槽)。
        Assert.IsType<Inst.LocalGet>(main.Blocks[3].Insts[0]);
        // 全函数无 Binary（&& 不入 Binary 指令）。
        Assert.DoesNotContain(AllInsts(main), i => i is Inst.Binary);
    }

    [Fact]
    public void Or_short_circuit_value_is_true()
    {
        var p = CompileSrc(
            "fn main() { let x = true || false\n    println(x) }");
        var main = FnByName(p, "main");
        // Branch 左 true 直接进短路块，短路值 = ConstBool(true)。
        Assert.Equal(new Inst.ConstBool(true), main.Blocks[2].Insts[0]);
    }

    // ———— 装箱纪律（VALUE-REF-003 / VALUE-CAPTURE-004） ————

    [Fact]
    public void Captured_binding_is_boxed_and_closure_pops_ref()
    {
        var src = """
fn counter(): fn(): int {
    var n = 0
    return fn(): int {
        n = n + 1
        return n
    }
}
fn main() {
    let c = counter()
    println(c())
}
""";
        var p = CompileSrc(src);
        var counter = FnByName(p, "counter");
        // n 装箱：声明处 BoxNew 落槽。
        Assert.Contains(counter.Blocks[0].Insts, i => i is Inst.BoxNew);
        // fnlit 站点：LocalGet(n) 压 Ref（不 Deref）→ ClosureNew，恰一次。
        var closureSite = Assert.Single(AllInsts(counter).OfType<Inst.ClosureNew>());
        Assert.Equal(1, closureSite.Captures);
        // 闭包函数：捕获槽 1 个（名 n），读 Deref、写 RefSet。
        var closure = p.Functions[(int)closureSite.Func.Value];
        Assert.Equal(1, closure.CaptureCount);
        Assert.Equal("n", closure.Locals[0].Name);
        var closureInsts = AllInsts(closure).ToList();
        Assert.True(IndexOf(closureInsts, new Inst.LocalGet(new LocalId(0)), new Inst.Deref()) >= 0
            || IndexOf(closureInsts, new Inst.LocalGet(new LocalId(1)), new Inst.Deref()) >= 0,
            "捕获绑定读应经 Deref");
        Assert.Contains(closureInsts, i => i is Inst.RefSet);
    }

    [Fact]
    public void Address_of_reads_slot_ref_directly()
    {
        var src = """
fn main() {
    var s = 1
    let r = &s
    s = 2
    println(s)
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        // &s：LocalGet(s) 直压 Ref（装箱槽，声明处已 BoxNew，此处无 BoxNew）。
        var b0 = main.Blocks[0].Insts;
        var boxCount = b0.Count(i => i is Inst.BoxNew);
        Assert.Equal(1, boxCount); // 仅声明处装箱
        // s = 2：LocalGet(addr) + LocalGet(val) + RefSet 收尾。
        Assert.Contains(AllInsts(main), i => i is Inst.RefSet);
        // 读 s：LocalGet + Deref。
        var insts = AllInsts(main).ToList();
        Assert.Contains(insts, i => i is Inst.Deref);
    }

    // ———— 循环变量逐轮重绑（CTRL-FORIN-003） ————

    [Fact]
    public void Loop_var_captured_gets_fresh_box_per_round()
    {
        var src = """
fn main() {
    let fs: [fn(): int] = []
    for i in 0..3 {
        push(fs, fn(): int { return i })
    }
    println(len(fs))
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        // cond 块：LocalGet(iter) → IterNext → BoxNew → LocalSet(var)。
        var cond = main.Blocks[1].Insts;
        var found = false;
        for (var i = 0; i + 4 <= cond.Count; i++)
        {
            if (cond[i] is Inst.LocalGet && cond[i + 1] is Inst.IterNext
                && cond[i + 2] is Inst.BoxNew && cond[i + 3] is Inst.LocalSet)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "装箱循环变量逐轮 BoxNew 重绑");
        // 闭包捕获循环变量：ClosureNew 捕获 1 个 Ref；fnlit 捕获槽名 i。
        var closure = Assert.Single(AllInsts(main).OfType<Inst.ClosureNew>());
        Assert.Equal(1, closure.Captures);
        Assert.Equal("i", p.Functions[(int)closure.Func.Value].Locals[0].Name);
    }

    [Fact]
    public void Loop_var_uncaptured_stays_plain_slot()
    {
        var src = """
fn main() {
    for i in 0..3 {
        println(i)
    }
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        Assert.Contains(AllInsts(main), i => i is Inst.IterNext);
        Assert.DoesNotContain(AllInsts(main), i => i is Inst.BoxNew);
    }

    // ———— `?` 传播（RESULT-TRY-003） ————

    [Fact]
    public void Try_lowers_to_try_with_synthesized_err_return()
    {
        var src = """
fn step(x: int): Result[int, error] {
    if x < 0 {
        return Err(err("neg"))
    }
    return Ok(x + 1)
}
fn run(x: int): Result[int, error] {
    let a = step(x)?
    return Ok(a)
}
fn main() {
    println(run(1))
}
""";
        var p = CompileSrc(src);
        var run = FnByName(p, "run");
        // err 槽存在（名 <err>）。
        var errSlot = -1;
        for (var i = 0; i < run.Locals.Count; i++)
        {
            if (run.Locals[i].Name == "<err>")
            {
                errSlot = i;
                break;
            }
        }
        Assert.True(errSlot >= 0, "? 应引入 err 槽");
        // Try 指令：err 槽 + fail 块。
        var tryInst = Assert.Single(AllInsts(run).OfType<Inst.Try>());
        Assert.Equal((uint)errSlot, tryInst.Err.Value);
        // fail 块：LocalGet(err) → EnumNew(Result.Err) → Return(true)。
        var fail = run.Blocks[(int)tryInst.Fail.Value];
        Assert.Equal(2, fail.Insts.Count);
        Assert.IsType<Inst.LocalGet>(fail.Insts[0]);
        var en = Assert.IsType<Inst.EnumNew>(fail.Insts[1]);
        Assert.Equal(1, en.Case);
        Assert.IsType<Terminator.Return>(fail.Term);
        Assert.True(((Terminator.Return)fail.Term).HasValue);
    }

    // ———— match 降形（MATCH-KIND-001） ————

    [Fact]
    public void Int_match_uses_switch_int()
    {
        var src = """
fn classify(n: int): string {
    return match n {
        0 => "zero"
        1 => "one"
        _ => "many"
    }
}
fn main() {
    println(classify(9))
}
""";
        var p = CompileSrc(src);
        var f = FnByName(p, "classify");
        var si = f.Blocks.Select(b => b.Term).OfType<Terminator.SwitchInt>().SingleOrDefault();
        Assert.NotNull(si);
        Assert.Equal(new long[] { 0, 1 }, si!.Arms.Select(a => a.Value).ToArray());
        Assert.NotNull(si.Default);
    }

    [Fact]
    public void String_match_uses_eq_chain()
    {
        var src = """
fn f(s: string): int {
    return match s {
        "a" => 1
        _ => 0
    }
}
fn main() {
    println(f("b"))
}
""";
        var p = CompileSrc(src);
        var f = FnByName(p, "f");
        Assert.DoesNotContain(f.Blocks, b => b.Term is Terminator.SwitchInt);
        // Eq 链：LocalGet(scrut) → ConstStr("a") → Binary(Eq)。
        var chain = IndexOf(f.Blocks[0].Insts,
            new Inst.LocalGet(new LocalId(1)), new Inst.ConstStr("a"), new Inst.Binary(Binop.Eq));
        Assert.True(chain >= 0, "字符串臂经 Eq 链");
        Assert.IsType<Terminator.Branch>(f.Blocks[0].Term);
    }

    [Fact]
    public void Enum_match_uses_switch_enum_with_case_parallel_arms()
    {
        var src = """
enum E {
    Pair(int, int)
    Single(int)
}
fn describe(e: E): string {
    return match e {
        Pair(a, b) => "p"
        Single(v) => "s"
    }
}
fn main() {
    println(describe(Pair(1, 2)))
}
""";
        var p = CompileSrc(src);
        var f = FnByName(p, "describe");
        var se = f.Blocks.Select(b => b.Term).OfType<Terminator.SwitchEnum>().SingleOrDefault();
        Assert.NotNull(se);
        Assert.Equal(2, se!.Arms.Count);
        Assert.Null(se.Default);
        // 载荷解构：Pair 臂两次 EnumPayloadGet（index 0/1）。
        var arm0 = f.Blocks[(int)se.Arms[0].Value].Insts.ToList();
        var idxs = arm0.OfType<Inst.EnumPayloadGet>().Select(x => x.Index).ToList();
        Assert.Equal(new[] { 0, 1 }, idxs);
    }

    // ———— init 合成与全局 ————

    [Fact]
    public void Globals_synth_init_in_declaration_order()
    {
        var src = """
let a = 1
var b = a + 1
fn main() {
    b = b + 10
    println(a, b)
}
""";
        var p = CompileSrc(src);
        Assert.Equal(2, p.Globals.Count);
        var initId = p.Init ?? throw new InvalidOperationException("有全局应合成 init");
        var init = p.Functions[(int)initId.Value];
        var sets = AllInsts(init).OfType<Inst.GlobalSet>().Select(x => (int)x.Global.Value).ToList();
        Assert.Equal(new[] { 0, 1 }, sets);
        // main 写全局 b：GlobalSet。
        var main = FnByName(p, "main");
        Assert.Contains(AllInsts(main), i => i is Inst.GlobalSet);
    }

    [Fact]
    public void No_globals_means_no_init()
    {
        var p = CompileSrc("fn main() { println(1) }");
        Assert.Null(p.Init);
        Assert.NotNull(p.Main);
    }

    // ———— extern / chan / spawn ————

    [Fact]
    public void Extern_decls_lower_to_extern_form()
    {
        var src = """
extern "host" {
    fn hostAdd(a: int, b: int): int
}
fn main() {
    println(hostAdd(1, 2))
}
""";
        var p = CompileSrc(src);
        var ext = FnByName(p, "hostAdd");
        Assert.Equal("host", ext.ExternHost);
        Assert.Empty(ext.Blocks);
        Assert.Equal(2, ext.Sig.ParamCount);
        Assert.True(ext.Sig.HasRet);
    }

    [Fact]
    public void Chan_and_spawn_lower_to_dedicated_insts()
    {
        var src = """
let ch = makeChan()
fn produce(c: chan[int]) {
    send(c, 7)
}
fn main() {
    go produce(ch)
    println(recv(ch))
}
""";
        var p = CompileSrc(src);
        // init：ChanNew → GlobalSet。
        var initId = p.Init ?? throw new InvalidOperationException("有全局应合成 init");
        var init = p.Functions[(int)initId.Value];
        Assert.Contains(init.Blocks[0].Insts, i => i is Inst.ChanNew);
        // produce：ChanSend。
        var produce = FnByName(p, "produce");
        Assert.Contains(produce.Blocks[0].Insts, i => i is Inst.ChanSend);
        // main：Spawn{argc 1} + ChanRecv。
        var main = FnByName(p, "main");
        var spawn = Assert.Single(AllInsts(main).OfType<Inst.Spawn>());
        Assert.Equal(1, spawn.Argc);
        Assert.Equal("produce", p.Functions[(int)spawn.Func.Value].Name);
        Assert.Contains(AllInsts(main), i => i is Inst.ChanRecv);
    }

    // ———— 方法与一等函数 ————

    [Fact]
    public void Method_call_passes_recv_first()
    {
        var src = """
struct Acc {
    total: int
}
fn (a Acc).add(n: int): Acc {
    return Acc{total: a.total + n}
}
fn main() {
    let x = Acc{total: 1}
    println(x.add(2).total)
}
""";
        var p = CompileSrc(src);
        var add = FnByName(p, "add");
        Assert.Equal(2, add.Sig.ParamCount); // recv 为第 0 参
        Assert.Equal("a", add.Locals[0].Name);
        // 调用点：Call(add)。
        var main = FnByName(p, "main");
        var call = Assert.Single(AllInsts(main).OfType<Inst.Call>());
        Assert.Equal("add", p.Functions[(int)call.Func.Value].Name);
    }

    [Fact]
    public void Fn_value_call_uses_call_indirect()
    {
        var src = """
fn apply(f: fn(int): int, x: int): int {
    return f(x)
}
fn main() {
    println(apply(fn(x: int): int { return x * 10 }, 4))
}
""";
        var p = CompileSrc(src);
        var apply = FnByName(p, "apply");
        // f(x)：闭包值先压（callee 槽 2）再压实参（槽 3）→ CallIndirect{argc 1, has_ret true}。
        var window = IndexOf(apply.Blocks[0].Insts,
            new Inst.LocalGet(new LocalId(2)), new Inst.LocalGet(new LocalId(3)),
            new Inst.CallIndirect(1, true));
        Assert.True(window >= 0, "f(x) 应落 CallIndirect");
        // fnlit → ClosureNew 零捕获。
        var main = FnByName(p, "main");
        Assert.Contains(AllInsts(main), i => i is Inst.ClosureNew { Captures: 0 });
    }

    // ———— 全指令覆盖 + 确定性 ————

    [Fact]
    public void Full_feature_source_covers_all_insts_and_terminators()
    {
        var src = """
let g0 = 1
var g1 = 2
let ch = makeChan()

struct P {
    x: int
    y: int
}
enum E {
    A(int)
    B
}

fn worker(c: chan[int], v: int) {
    send(c, v)
}

fn step(x: int): Result[int, error] {
    if x < 0 {
        return Err(err("neg"))
    }
    return Ok(x)
}

fn (p P).sum(o: P): int {
    return p.x + p.y + o.x + o.y
}

fn branchy(n: int): int {
    return match n {
        0 => 10
        _ => 20
    }
}

fn enummy(e: E): int {
    return match e {
        A(v) => v
        B => 0
    }
}

fn stringy(s: string): int {
    return match s {
        "k" => 1
        _ => 2
    }
}

fn main() {
    g1 = g1 + g0
    var arr = [1, 2, 3]
    arr += [4]
    arr[0] = 9
    let m = {"a": 1, "b": 2}
    m["c"] = 3
    let p = P{x: 1, y: 2}
    p.x = 5
    let sl = arr[1:3]
    let r = 0..3
    var n = -p.x
    let b = !(n == 0)
    n = branchy(1)
    n = enummy(A(7))
    n = stringy("z")
    let e = B
    let f = fn(x: int): int { return x + 1 }
    n = f(n)
    let inc = fn(x: int): int { return x + 1 }
    let fs: [fn(int): int] = [inc]
    n = fs[0](n)
    for i in r {
        if i == 1 {
            continue
        }
        if i == 2 {
            break
        }
        n = n + i
    }
    while n > 100 {
        n = n - 1
    }
    let flag = true
    let ratio = 1.5
    n = n + int(ratio)
    var boxed = 7
    let rb = &boxed
    boxed = 8
    if flag && boxed > 0 {
        println(boxed, rb)
    }
    go worker(ch, 1)
    let got = recv(ch)
    let final = runStep(3)
    println(g1, arr, m, p.sum(P{x: 0, y: 0}), sl, got, e, b, final, ratio)
    let _ = len(arr)
}

fn runStep(x: int): Result[int, error] {
    let v = step(x)?
    return Ok(v)
}
""";
        var p = CompileSrc(src);
        var kinds = AllInsts(p).Select(KindOf).Distinct().ToList();
        var terms = p.Functions.SelectMany(f => f.Blocks).Select(b => b.Term)
            .Select(t => t switch
            {
                Terminator.Goto => "Goto",
                Terminator.Branch => "Branch",
                Terminator.SwitchInt => "SwitchInt",
                Terminator.SwitchEnum => "SwitchEnum",
                Terminator.Return => "Return",
                Terminator.Unreachable => "Unreachable",
                _ => throw new InvalidOperationException("Terminator 变体未穷尽"),
            })
            .Distinct().ToList();
        string[] allInsts =
        [
            "ConstI64", "ConstF64", "ConstBool", "ConstStr",
            "LocalGet", "LocalSet", "GlobalGet", "GlobalSet",
            "Unary", "Binary", "Pop",
            "Call", "CallIndirect", "CallBuiltin", "Spawn", "ClosureNew",
            "IndexGet", "IndexSet", "SliceGet", "FieldGet", "FieldSet", "ArrayPush",
            "ArrayNew", "MapNew", "StructNew", "EnumNew", "RangeNew",
            "BoxNew", "Deref", "RefSet", "EnumPayloadGet",
            "IterNew", "IterNext", "ChanNew", "ChanSend", "ChanRecv", "Try",
        ];
        foreach (var k in allInsts)
        {
            Assert.True(kinds.Contains(k), $"全指令覆盖缺 {k}（现有 {string.Join(",", kinds)}）");
        }
        foreach (var t in new[] { "Goto", "Branch", "SwitchInt", "SwitchEnum", "Return" })
        {
            Assert.True(terms.Contains(t), $"终接覆盖缺 {t}（现有 {string.Join(",", terms)}）");
        }
    }

    [Fact]
    public void Same_source_compiles_field_identical_twice()
    {
        var src = """
let g = 10
struct P {
    v: int
}
enum E {
    A(int)
    B
}
fn (p P).get(): int {
    return p.v
}
fn f(n: int): int {
    return match n {
        0 => 1
        _ => n * 2
    }
}
fn main() {
    let xs = [1, 2]
    for x in xs {
        println(f(x), A(x), P{v: x}.get(), g)
    }
}
""";
        var a = CompileSrc(src);
        var b = CompileSrc(src);
        Assert.Equal(a, b);
    }

    // ———— 控制流终止与死块 ————

    [Fact]
    public void Dead_code_after_return_lowers_to_valid_blocks()
    {
        var src = """
fn f(): int {
    return 1
    println("dead")
    return 2
}
fn main() {
    println(f())
}
""";
        var p = CompileSrc(src);
        Assert.Null(Validator.Validate(p));
    }

    [Fact]
    public void Loop_break_continue_goto_targets()
    {
        var src = """
fn main() {
    while true {
        break
    }
    for i in 0..2 {
        continue
    }
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        // while: break → Goto(end)；for-in: continue → Goto(cond)。
        var gotos = main.Blocks.Select(b => b.Term).OfType<Terminator.Goto>().ToList();
        Assert.True(gotos.Count >= 2, "break/continue 应落 Goto");
    }

    // ———— 类型事实搬运（不重推断） ————

    [Fact]
    public void Type_ids_flow_from_analysis_into_tables()
    {
        var src = """
struct P {
    v: int
}
fn main() {
    let p = P{v: 1}
    println(p.v)
}
""";
        var p = CompileSrc(src);
        // struct 字段类型 = checker 结论（int = TypeId(1) 预载位）。
        var st = p.Structs[0];
        Assert.Equal("P", st.Name);
        Assert.Equal(new TypeId(1), st.Fields[0].TypeId);
    }

    // ———— B1 集成轮教训的直接锁定（EVAL-ASSIGN-003 冻结合同） ————

    /// <summary>教训一（根因二）：地址组件禁落临时槽——`c.n = 41` 的 FieldSet 必须写在
    /// 原槽（根 LocalGet 直压，压栈序 obj→val，中间无 LocalSet 深拷贝）。</summary>
    [Fact]
    public void Struct_field_write_targets_original_slot()
    {
        var src = """
struct C {
    n: int
}
fn main() {
    let c = C{n: 0}
    c.n = 41
    println(c.n)
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        // 手写全序列锁定（B1 坏形会在根与 FieldSet 之间插入 LocalSet(addr)+LocalGet(addr)）：
        // 槽 0 = 字面量临时，1 = c，2 = 右值临时，3 = println 实参临时。
        var expected = new Inst[]
        {
            // let c = C{n: 0}
            new Inst.ConstI64(0), new Inst.LocalSet(new LocalId(0)),
            new Inst.LocalGet(new LocalId(0)), new Inst.StructNew(new StructId(0)),
            new Inst.LocalSet(new LocalId(1)),
            // c.n = 41：右值落临时槽 → 压根（原槽 LocalGet 直压）→ 压右值 → FieldSet
            new Inst.ConstI64(41), new Inst.LocalSet(new LocalId(2)),
            new Inst.LocalGet(new LocalId(1)),
            new Inst.LocalGet(new LocalId(2)),
            new Inst.FieldSet("n"),
            // println(c.n)
            new Inst.LocalGet(new LocalId(1)), new Inst.FieldGet("n"),
            new Inst.LocalSet(new LocalId(3)), new Inst.LocalGet(new LocalId(3)),
            new Inst.CallBuiltin("println", 1, false),
        };
        Assert.Equal(expected, main.Blocks[0].Insts);
        Assert.IsType<Terminator.Return>(main.Blocks[0].Term);
    }

    /// <summary>教训一（根因一）：`a[i()] = v()` 压栈序违契与求值序——地址组件（i()）
    /// 先于右值（v()）求值；压栈序 = 冻结合同 obj→idx→val，地址链上无 LocalSet。</summary>
    [Fact]
    public void Assign_index_address_components_evaluated_before_rhs_in_contract_push_order()
    {
        var src = """
fn v(): int {
    return 7
}
fn idx(): int {
    return 1
}
fn main() {
    let a = [0, 0]
    a[idx()] = v()
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        var b0 = main.Blocks[0].Insts;
        // 求值序：Call(idx) 先于 Call(v)（EVAL-ASSIGN-003 地址组件先于右值）。
        var callIdx = b0.Select((x, i) => (x, i)).First(t => t.x is Inst.Call { Func: { } f1 } && f1.Value == 1).i;
        var callV = b0.Select((x, i) => (x, i)).First(t => t.x is Inst.Call { Func: { } f0 } && f0.Value == 0).i;
        Assert.True(callIdx < callV, "地址组件 idx() 应先于右值 v() 求值");
        // 手写全序列锁定：槽 0/1 = 数组元素临时，2 = a，3 = idx 临时，4 = 右值临时。
        var expected = new Inst[]
        {
            // let a = [0, 0]
            new Inst.ConstI64(0), new Inst.LocalSet(new LocalId(0)),
            new Inst.ConstI64(0), new Inst.LocalSet(new LocalId(1)),
            new Inst.LocalGet(new LocalId(0)), new Inst.LocalGet(new LocalId(1)),
            new Inst.ArrayNew(2), new Inst.LocalSet(new LocalId(2)),
            // 地址组件先落临时槽：a[idx()]
            new Inst.Call(new FunctionId(1)), new Inst.LocalSet(new LocalId(3)),
            // 右值后求值：v()
            new Inst.Call(new FunctionId(0)), new Inst.LocalSet(new LocalId(4)),
            // 压栈序 = 冻结合同：obj → idx → val → IndexSet
            new Inst.LocalGet(new LocalId(2)),
            new Inst.LocalGet(new LocalId(3)),
            new Inst.LocalGet(new LocalId(4)),
            new Inst.IndexSet(),
        };
        Assert.Equal(expected, b0);
    }

    /// <summary>复合赋值经原槽：`c.n += 1` 的读（FieldGet）与写回（FieldSet）都挂在
    /// 根槽 LocalGet 上，两读间无副作用，运算结果落临时槽后再按弹序写回。</summary>
    [Fact]
    public void Compound_assign_reads_and_writes_through_original_slot()
    {
        var src = """
struct C {
    n: int
}
fn main() {
    let c = C{n: 7}
    c.n += 1
}
""";
        var p = CompileSrc(src);
        var main = FnByName(p, "main");
        // 手写全序列锁定：槽 0 = 字面量临时，1 = c，2 = 右值临时，3 = 运算结果临时。
        var expected = new Inst[]
        {
            // let c = C{n: 7}
            new Inst.ConstI64(7), new Inst.LocalSet(new LocalId(0)),
            new Inst.LocalGet(new LocalId(0)), new Inst.StructNew(new StructId(0)),
            new Inst.LocalSet(new LocalId(1)),
            // c.n += 1：右值先落槽
            new Inst.ConstI64(1), new Inst.LocalSet(new LocalId(2)),
            // 读旧值：压根（原槽）→ FieldGet
            new Inst.LocalGet(new LocalId(1)), new Inst.FieldGet("n"),
            // 运算 → 结果落临时槽
            new Inst.LocalGet(new LocalId(2)), new Inst.Binary(Binop.Add),
            new Inst.LocalSet(new LocalId(3)),
            // 写回：重压根（原槽）→ 压结果 → FieldSet
            new Inst.LocalGet(new LocalId(1)),
            new Inst.LocalGet(new LocalId(3)),
            new Inst.FieldSet("n"),
        };
        Assert.Equal(expected, main.Blocks[0].Insts);
    }
}
