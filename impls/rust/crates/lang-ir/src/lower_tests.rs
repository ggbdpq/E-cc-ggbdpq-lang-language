//! HIR → IR lowering 降形专项测试（第三波）。
//!
//! 红→绿锚点：短路不入 Binary（Branch 控制流 + 结果槽）、装箱纪律
//! （捕获/取址槽持 Ref：BoxNew/Deref/RefSet）、循环变量逐轮 BoxNew 重绑、
//! `?` → Try + fail 块合成 Err 返回、match 四分类的指令形态、init 合成、
//! extern 形态、chan/spawn 专指令、全指令覆盖、确定性。

use lang_core::ids::{LocalId, SourceId, TypeId};
use lang_core::source::SourceText;
use lang_semantic::analyze_stmts;

use crate::compile::compile;
use crate::ids::BlockId;
use crate::model::{Binop, Inst, IrFunction, IrProgram, Terminator};
use crate::validate::validate;

/// 源码 → IrProgram（parse → analyze → compile；产物已过 validate）。
fn compile_src(src: &str) -> IrProgram {
    let source = SourceId(3);
    let text = SourceText::new("test.lang-go", src.to_string());
    let stmts = lang_syntax::parse(&text, source).expect("parse 应通过");
    let mut analysis = analyze_stmts(&stmts, source);
    assert!(
        analysis.diags.is_empty(),
        "run 源码应无语义诊断: {:?}",
        analysis.diags
    );
    compile(&stmts, &mut analysis).expect("compile 应通过（含 validate）")
}

fn fn_by_name<'p>(p: &'p IrProgram, name: &str) -> &'p IrFunction {
    p.functions
        .iter()
        .find(|f| f.name == name)
        .unwrap_or_else(|| panic!("函数 {name} 应存在"))
}

fn inst_kind(i: &Inst) -> &'static str {
    match i {
        Inst::ConstI64(_) => "ConstI64",
        Inst::ConstF64(_) => "ConstF64",
        Inst::ConstBool(_) => "ConstBool",
        Inst::ConstStr(_) => "ConstStr",
        Inst::LocalGet(_) => "LocalGet",
        Inst::LocalSet(_) => "LocalSet",
        Inst::GlobalGet(_) => "GlobalGet",
        Inst::GlobalSet(_) => "GlobalSet",
        Inst::Unary(_) => "Unary",
        Inst::Binary(_) => "Binary",
        Inst::Pop => "Pop",
        Inst::Call(_) => "Call",
        Inst::CallIndirect { .. } => "CallIndirect",
        Inst::CallBuiltin { .. } => "CallBuiltin",
        Inst::Spawn { .. } => "Spawn",
        Inst::ClosureNew { .. } => "ClosureNew",
        Inst::IndexGet => "IndexGet",
        Inst::IndexSet => "IndexSet",
        Inst::SliceGet => "SliceGet",
        Inst::FieldGet { .. } => "FieldGet",
        Inst::FieldSet { .. } => "FieldSet",
        Inst::ArrayPush => "ArrayPush",
        Inst::ArrayNew { .. } => "ArrayNew",
        Inst::MapNew { .. } => "MapNew",
        Inst::StructNew { .. } => "StructNew",
        Inst::EnumNew { .. } => "EnumNew",
        Inst::RangeNew => "RangeNew",
        Inst::BoxNew => "BoxNew",
        Inst::Deref => "Deref",
        Inst::RefSet => "RefSet",
        Inst::EnumPayloadGet { .. } => "EnumPayloadGet",
        Inst::IterNew => "IterNew",
        Inst::IterNext { .. } => "IterNext",
        Inst::ChanNew => "ChanNew",
        Inst::ChanSend => "ChanSend",
        Inst::ChanRecv => "ChanRecv",
        Inst::Try { .. } => "Try",
    }
}

// ———— 短路降形（EVAL-SHORTCUT-006） ————

#[test]
fn and_lower_to_branch_not_binary() {
    let p = compile_src(
        "fn main() { let x = true && false
    println(x) }",
    );
    let main = fn_by_name(&p, "main");
    // 块 0：lhs 常量 + Branch；rhs 块与短路块汇合到结果槽。
    assert_eq!(
        main.blocks[0].insts,
        vec![Inst::ConstBool(true)],
        "块 0 只有 lhs，终接 Branch"
    );
    assert!(matches!(main.blocks[0].term, Terminator::Branch { .. }));
    // rhs 块：ConstBool(false) → LocalSet(结果槽) → Goto(end)。
    let rhs = &main.blocks[1];
    assert_eq!(rhs.insts[0], Inst::ConstBool(false));
    assert!(matches!(rhs.term, Terminator::Goto { .. }));
    // 短路块：ConstBool(false)（&& 短路值）→ LocalSet → Goto(end)。
    let short = &main.blocks[2];
    assert_eq!(short.insts[0], Inst::ConstBool(false));
    assert!(matches!(short.term, Terminator::Goto { .. }));
    // 汇合块：LocalGet(结果槽)。
    assert!(matches!(main.blocks[3].insts[0], Inst::LocalGet(_)));
    // 全函数无 Binary（&& 不入 Binary 指令）。
    assert!(
        !main
            .blocks
            .iter()
            .any(|b| b.insts.iter().any(|i| matches!(i, Inst::Binary(_))))
    );
}

#[test]
fn or_short_circuit_value_is_true() {
    let p = compile_src(
        "fn main() { let x = true || false
    println(x) }",
    );
    let main = fn_by_name(&p, "main");
    // Branch 左 true 直接进短路块，短路值 = ConstBool(true)。
    let short = &main.blocks[2];
    assert_eq!(short.insts[0], Inst::ConstBool(true));
}

// ———— 装箱纪律（VALUE-REF-003 / VALUE-CAPTURE-004） ————

#[test]
fn captured_binding_is_boxed_and_closure_pops_ref() {
    let src = r#"
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
"#;
    let p = compile_src(src);
    let counter = fn_by_name(&p, "counter");
    // n 装箱：声明处 BoxNew 落槽。
    let b0 = &counter.blocks[0];
    assert!(
        b0.insts.iter().any(|i| matches!(i, Inst::BoxNew)),
        "被捕获绑定声明处应 BoxNew"
    );
    // fnlit 站点：LocalGet(n) 压 Ref（不 Deref）→ ClosureNew。
    let closure_site = counter
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .filter(|i| matches!(i, Inst::ClosureNew { .. }))
        .count();
    assert_eq!(closure_site, 1, "闭包构造恰一次");
    let (fid, captures) = counter
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .find_map(|i| match i {
            Inst::ClosureNew { func, captures } => Some((*func, *captures)),
            _ => None,
        })
        .unwrap();
    assert_eq!(captures, 1);
    // 闭包函数：捕获槽 1 个（名 n），读 Deref、写 RefSet。
    let closure = &p.functions[usize::from(fid)];
    assert_eq!(closure.capture_count, 1);
    assert_eq!(closure.locals[0].name, "n");
    let has_deref_read = closure.blocks.iter().any(|b| {
        b.insts
            .windows(2)
            .any(|w| matches!(w[0], Inst::LocalGet(_)) && matches!(w[1], Inst::Deref))
    });
    assert!(has_deref_read, "捕获绑定读应经 Deref");
    let has_refset_write = closure
        .blocks
        .iter()
        .any(|b| b.insts.iter().any(|i| matches!(i, Inst::RefSet)));
    assert!(has_refset_write, "捕获绑定写应经 RefSet");
}

#[test]
fn address_of_reads_slot_ref_directly() {
    let src = r#"
fn main() {
    var s = 1
    let r = &s
    s = 2
    println(s)
}
"#;
    let p = compile_src(src);
    let main = fn_by_name(&p, "main");
    // &s：LocalGet(s) 直压 Ref（装箱槽），无 BoxNew（Box 已在声明处建）。
    let ref_read = main.blocks[0]
        .insts
        .iter()
        .filter(|i| matches!(i, Inst::LocalGet(_)))
        .count();
    assert!(ref_read >= 2, "& 与后续读取均走槽位");
    // s = 2：地址 Ref 落临时槽 → 右值 → LocalGet(addr) + LocalGet(val) + RefSet。
    assert!(
        main.blocks.iter().any(|b| {
            b.insts
                .windows(2)
                .any(|w| matches!(w[0], Inst::LocalGet(_)) && matches!(w[1], Inst::RefSet))
        }),
        "装箱绑定写以 RefSet 收尾"
    );
    // 读 s：LocalGet + Deref。
    assert!(
        main.blocks.iter().any(|b| b
            .insts
            .windows(2)
            .any(|w| matches!(w[0], Inst::LocalGet(_)) && matches!(w[1], Inst::Deref))),
        "装箱绑定读应 Deref"
    );
}

// ———— 循环变量逐轮重绑（CTRL-FORIN-003） ————

#[test]
fn loop_var_captured_gets_fresh_box_per_round() {
    let src = r#"
fn main() {
    let fs: [fn(): int] = []
    for i in 0..3 {
        push(fs, fn(): int { return i })
    }
    println(len(fs))
}
"#;
    let p = compile_src(src);
    let main = fn_by_name(&p, "main");
    // cond 块：LocalGet(iter) → IterNext → BoxNew → LocalSet(var)。
    let cond = &main.blocks[1];
    assert!(
        cond.insts.windows(4).any(|w| {
            matches!(w[0], Inst::LocalGet(_))
                && matches!(w[1], Inst::IterNext { .. })
                && matches!(w[2], Inst::BoxNew)
                && matches!(w[3], Inst::LocalSet(_))
        }),
        "装箱循环变量逐轮 BoxNew 重绑"
    );
    // 闭包捕获循环变量：CaptureNew 捕获 1 个 Ref；fnlit 捕获槽名 i。
    let (fid, captures) = main
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .find_map(|i| match i {
            Inst::ClosureNew { func, captures } => Some((*func, *captures)),
            _ => None,
        })
        .expect("循环体内应有闭包");
    assert_eq!(captures, 1);
    assert_eq!(p.functions[usize::from(fid)].locals[0].name, "i");
}

#[test]
fn loop_var_uncaptured_stays_plain_slot() {
    let src = r#"
fn main() {
    for i in 0..3 {
        println(i)
    }
}
"#;
    let p = compile_src(src);
    let main = fn_by_name(&p, "main");
    // 未捕获循环变量：LocalSet 直落，无 BoxNew。
    let cond = &main.blocks[1];
    assert!(
        cond.insts
            .iter()
            .any(|i| matches!(i, Inst::IterNext { .. }))
    );
    assert!(
        !main
            .blocks
            .iter()
            .any(|b| b.insts.iter().any(|i| matches!(i, Inst::BoxNew))),
        "未捕获循环变量不应装箱"
    );
}

// ———— `?` 传播（RESULT-TRY-003） ————

#[test]
fn try_lowers_to_try_with_synthesized_err_return() {
    let src = r#"
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
"#;
    let p = compile_src(src);
    let run = fn_by_name(&p, "run");
    // err 槽存在（名 <err>）。
    let err_slot = run
        .locals
        .iter()
        .position(|l| l.name == "<err>")
        .expect("? 应引入 err 槽");
    // Try 指令：err 槽 + fail 块。
    let (fail_b, ty) = run
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .find_map(|i| match i {
            Inst::Try { err, fail } => Some((*fail, *err)),
            _ => None,
        })
        .expect("应有 Try 指令");
    assert_eq!(ty, LocalId(err_slot as u32));
    // fail 块：LocalGet(err) → EnumNew(Result.Err) → Return(true)。
    let fail = &run.blocks[usize::from(fail_b)];
    assert_eq!(fail.insts.len(), 2);
    assert!(matches!(fail.insts[0], Inst::LocalGet(_)));
    assert!(matches!(fail.insts[1], Inst::EnumNew { case_id: 1, .. }));
    assert!(matches!(fail.term, Terminator::Return { has_value: true }));
}

// ———— match 降形（MATCH-KIND-001） ————

#[test]
fn int_match_uses_switch_int() {
    let src = r#"
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
"#;
    let p = compile_src(src);
    let f = fn_by_name(&p, "classify");
    let (arms, default) = f
        .blocks
        .iter()
        .find_map(|b| match &b.term {
            Terminator::SwitchInt { arms, default } => Some((arms.clone(), *default)),
            _ => None,
        })
        .expect("int match 应落 SwitchInt");
    assert_eq!(
        arms.iter().map(|(v, _)| *v).collect::<Vec<_>>(),
        vec![0, 1],
        "arms 依源码臂序"
    );
    assert!(default.is_some(), "通配臂 = default");
}

#[test]
fn string_match_uses_eq_chain() {
    let src = r#"
fn f(s: string): int {
    return match s {
        "a" => 1
        _ => 0
    }
}
fn main() {
    println(f("b"))
}
"#;
    let p = compile_src(src);
    let f = fn_by_name(&p, "f");
    assert!(
        !f.blocks
            .iter()
            .any(|b| matches!(b.term, Terminator::SwitchInt { .. })),
        "字符串 match 不设 SwitchInt"
    );
    // Eq 链：LocalGet(scrut) → ConstStr("a") → Binary(Eq) → Branch。
    let chain = f.blocks[0].insts.windows(3).any(|w| {
        matches!(w[0], Inst::LocalGet(_))
            && matches!(w[1], Inst::ConstStr(ref s) if s == "a")
            && matches!(w[2], Inst::Binary(Binop::Eq))
    });
    assert!(chain, "字符串臂经 Eq 链");
    assert!(matches!(f.blocks[0].term, Terminator::Branch { .. }));
}

#[test]
fn enum_match_uses_switch_enum_with_case_parallel_arms() {
    let src = r#"
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
"#;
    let p = compile_src(src);
    let f = fn_by_name(&p, "describe");
    let (arms, default) = f
        .blocks
        .iter()
        .find_map(|b| match &b.term {
            Terminator::SwitchEnum { arms, default, .. } => Some((arms.clone(), *default)),
            _ => None,
        })
        .expect("enum match 应落 SwitchEnum");
    assert_eq!(arms.len(), 2, "arms 与 case 声明序平行（穷尽，无 default）");
    assert!(default.is_none());
    // 载荷解构：Pair 臂两次 EnumPayloadGet（index 0/1）。
    let arm0 = &f.blocks[usize::from(arms[0])];
    let idxs: Vec<u16> = arm0
        .insts
        .iter()
        .filter_map(|i| match i {
            Inst::EnumPayloadGet { index } => Some(*index),
            _ => None,
        })
        .collect();
    assert_eq!(idxs, vec![0, 1], "构造模式按位置解构载荷");
}

// ———— init 合成与全局 ————

#[test]
fn globals_synth_init_in_declaration_order() {
    let src = r#"
let a = 1
var b = a + 1
fn main() {
    b = b + 10
    println(a, b)
}
"#;
    let p = compile_src(src);
    assert_eq!(p.globals.len(), 2);
    let init_fid = p.init.expect("有全局应合成 init");
    let init = &p.functions[usize::from(init_fid)];
    let sets: Vec<usize> = init
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .filter_map(|i| match i {
            Inst::GlobalSet(g) => Some(usize::from(*g)),
            _ => None,
        })
        .collect();
    assert_eq!(sets, vec![0, 1], "init 按声明序 GlobalSet");
    // main 写全局 b：GlobalSet。
    let main = fn_by_name(&p, "main");
    assert!(
        main.blocks
            .iter()
            .any(|b| b.insts.iter().any(|i| matches!(i, Inst::GlobalSet(_))))
    );
}

#[test]
fn no_globals_means_no_init() {
    let p = compile_src("fn main() { println(1) }");
    assert!(p.init.is_none());
    assert!(p.main.is_some());
}

// ———— extern / chan / spawn ————

#[test]
fn extern_decls_lower_to_extern_form() {
    let src = r#"
extern "host" {
    fn hostAdd(a: int, b: int): int
}
fn main() {
    println(hostAdd(1, 2))
}
"#;
    let p = compile_src(src);
    let ext = fn_by_name(&p, "hostAdd");
    assert_eq!(ext.extern_host.as_deref(), Some("host"));
    assert!(ext.blocks.is_empty(), "extern 无函数体");
    assert_eq!(ext.sig.param_count, 2);
    assert!(ext.sig.has_ret);
}

#[test]
fn chan_and_spawn_lower_to_dedicated_insts() {
    let src = r#"
let ch = makeChan()
fn produce(c: chan[int]) {
    send(c, 7)
}
fn main() {
    go produce(ch)
    println(recv(ch))
}
"#;
    let p = compile_src(src);
    // init：ChanNew → GlobalSet。
    let init = &p.functions[usize::from(p.init.unwrap())];
    assert!(init.blocks[0].insts.contains(&Inst::ChanNew));
    // produce：ChanSend。
    let produce = fn_by_name(&p, "produce");
    assert!(produce.blocks[0].insts.contains(&Inst::ChanSend));
    // main：Spawn{argc 1} + ChanRecv。
    let main = fn_by_name(&p, "main");
    let spawn = main
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .find_map(|i| match i {
            Inst::Spawn { func, argc } => Some((*func, *argc)),
            _ => None,
        })
        .expect("go 应落 Spawn");
    assert_eq!(spawn.1, 1);
    assert_eq!(p.functions[usize::from(spawn.0)].name, "produce");
    assert!(
        main.blocks
            .iter()
            .any(|b| b.insts.contains(&Inst::ChanRecv))
    );
}

// ———— 方法与一等函数 ————

#[test]
fn method_call_passes_recv_first() {
    let src = r#"
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
"#;
    let p = compile_src(src);
    let add = fn_by_name(&p, "add");
    assert_eq!(add.sig.param_count, 2, "recv 为第 0 参");
    assert_eq!(add.locals[0].name, "a");
    // 调用点：Call(add) 前实参 2 个已压栈（recv + n）。
    let main = fn_by_name(&p, "main");
    let call = main
        .blocks
        .iter()
        .flat_map(|b| b.insts.iter())
        .find_map(|i| match i {
            Inst::Call(fid) => Some(*fid),
            _ => None,
        })
        .expect("方法调用应落 Call");
    assert_eq!(p.functions[usize::from(call)].name, "add");
}

#[test]
fn fn_value_call_uses_call_indirect() {
    let src = r#"
fn apply(f: fn(int): int, x: int): int {
    return f(x)
}
fn main() {
    println(apply(fn(x: int): int { return x * 10 }, 4))
}
"#;
    let p = compile_src(src);
    let apply = fn_by_name(&p, "apply");
    // f(x)：读参数 f（栈顶闭包值）→ CallIndirect{argc 1, has_ret true}。
    assert!(
        apply.blocks[0]
            .insts
            .windows(2)
            .any(|w| matches!(w[0], Inst::LocalGet(_))
                && matches!(
                    w[1],
                    Inst::CallIndirect {
                        argc: 1,
                        has_ret: true
                    }
                ))
    );
    // fnlit → ClosureNew 零捕获。
    let main = fn_by_name(&p, "main");
    assert!(main.blocks.iter().any(|b| {
        b.insts
            .iter()
            .any(|i| matches!(i, Inst::ClosureNew { captures: 0, .. }))
    }));
}

// ———— 全指令覆盖 + 确定性 ————

#[test]
fn full_feature_source_covers_all_insts_and_terminators() {
    let src = r#"
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
"#;
    let p = compile_src(src);
    let mut kinds: Vec<&'static str> = Vec::new();
    let mut terms: Vec<&'static str> = Vec::new();
    for f in &p.functions {
        for b in &f.blocks {
            for i in &b.insts {
                let k = inst_kind(i);
                if !kinds.contains(&k) {
                    kinds.push(k);
                }
            }
            let t = match b.term {
                Terminator::Goto { .. } => "Goto",
                Terminator::Branch { .. } => "Branch",
                Terminator::SwitchInt { .. } => "SwitchInt",
                Terminator::SwitchEnum { .. } => "SwitchEnum",
                Terminator::Return { .. } => "Return",
                Terminator::Unreachable => "Unreachable",
            };
            if !terms.contains(&t) {
                terms.push(t);
            }
        }
    }
    let all_insts = [
        "ConstI64",
        "ConstF64",
        "ConstBool",
        "ConstStr",
        "LocalGet",
        "LocalSet",
        "GlobalGet",
        "GlobalSet",
        "Unary",
        "Binary",
        "Pop",
        "Call",
        "CallIndirect",
        "CallBuiltin",
        "Spawn",
        "ClosureNew",
        "IndexGet",
        "IndexSet",
        "SliceGet",
        "FieldGet",
        "FieldSet",
        "ArrayPush",
        "ArrayNew",
        "MapNew",
        "StructNew",
        "EnumNew",
        "RangeNew",
        "BoxNew",
        "Deref",
        "RefSet",
        "EnumPayloadGet",
        "IterNew",
        "IterNext",
        "ChanNew",
        "ChanSend",
        "ChanRecv",
        "Try",
    ];
    for k in all_insts {
        assert!(kinds.contains(&k), "全指令覆盖缺 {k}（现有 {kinds:?}）");
    }
    for t in ["Goto", "Branch", "SwitchInt", "SwitchEnum", "Return"] {
        assert!(terms.contains(&t), "终接覆盖缺 {t}（现有 {terms:?}）");
    }
}

#[test]
fn same_source_compiles_field_identical_twice() {
    let src = r#"
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
"#;
    let a = compile_src(src);
    let b = compile_src(src);
    assert_eq!(a, b, "同输入两次 compile 产物逐字段相等");
}

// ———— 控制流终止与死块 ————

#[test]
fn dead_code_after_return_lowers_to_valid_blocks() {
    let src = r#"
fn f(): int {
    return 1
    println("dead")
    return 2
}
fn main() {
    println(f())
}
"#;
    let p = compile_src(src);
    assert!(validate(&p).is_ok(), "死块也须过 validate");
}

#[test]
fn loop_break_continue_goto_targets() {
    let src = r#"
fn main() {
    while true {
        break
    }
    for i in 0..2 {
        continue
    }
}
"#;
    let p = compile_src(src);
    let main = fn_by_name(&p, "main");
    // while: break → Goto(end)；for-in: continue → Goto(cond)。
    let gotos: Vec<BlockId> = main
        .blocks
        .iter()
        .filter_map(|b| match b.term {
            Terminator::Goto { target } => Some(target),
            _ => None,
        })
        .collect();
    assert!(gotos.len() >= 2, "break/continue 应落 Goto");
}

// ———— 类型事实搬运（不重推断） ————

#[test]
fn type_ids_flow_from_analysis_into_tables() {
    let src = r#"
struct P {
    v: int
}
fn main() {
    let p = P{v: 1}
    println(p.v)
}
"#;
    let p = compile_src(src);
    // struct 字段类型 = checker 结论（int = TypeId(1) 预载位）。
    let st = &p.structs[0];
    assert_eq!(st.name, "P");
    assert_eq!(st.fields[0].type_id, Some(TypeId(1)));
}
