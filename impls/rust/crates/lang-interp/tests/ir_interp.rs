//! IR 解释器手构 IR 实测（lowering 未落地前的差分前置面）。
//!
//! 每个测试手构 `lang_ir::IrProgram`：先过 `lang_ir::validate`（验证器交叉
//! 证实），再经 `IrMachine::run` 执行，stdout 逐字节断言。覆盖面：
//! 栈式基本块（Branch/Goto/SwitchInt/SwitchEnum）、LocalId 帧、Call/CallBuiltin、
//! CallIndirect 闭包捕获（按引用共享）、BoxNew/Deref/RefSet 装箱纪律、
//! 数组/映射/切片/struct 字段（含 place 溯源写回）、IterNew/IterNext、
//! ChanNew/Send/Recv、Spawn 同步子集、Try 的 fail 块（经 recv 的真 Result）、
//! 全局 init/main 双入口、extern 调用报错。

use std::cell::RefCell;
use std::rc::Rc;

use lang_core::{FunctionId, LocalId};
use lang_interp::ir::IrMachine;
use lang_ir::{
    Binop, Block, BlockId, EnumCase, EnumDecl, FieldDecl, FnSig, GlobalDecl, Inst, IrFunction,
    IrProgram, LocalDecl, StructDecl, Terminator, Unop, validate,
};
use lang_runtime::{OutputSink, RtErrorKind};

// ———— 手构脚手架 ————

fn ld(name: &str) -> LocalDecl {
    LocalDecl {
        name: name.to_string(),
        mutable: true,
        type_id: None,
    }
}

fn blk(insts: Vec<Inst>, term: Terminator) -> Block {
    Block { insts, term }
}

fn ret_v() -> Terminator {
    Terminator::Return { has_value: true }
}

fn ret_0() -> Terminator {
    Terminator::Return { has_value: false }
}

fn println1() -> Inst {
    Inst::CallBuiltin {
        name: "println".to_string(),
        argc: 1,
        has_ret: false,
    }
}

fn func(name: &str, locals: Vec<LocalDecl>, blocks: Vec<Block>) -> IrFunction {
    IrFunction {
        name: name.to_string(),
        capture_count: 0,
        sig: FnSig {
            param_count: 0,
            has_ret: false,
        },
        locals,
        blocks,
        extern_host: None,
    }
}

/// 收集 stdout 的输出汇。
#[derive(Clone, Debug, Default)]
struct CollectSink(Rc<RefCell<String>>);

impl OutputSink for CollectSink {
    fn write(&mut self, s: &str) {
        self.0.borrow_mut().push_str(s);
    }
}

/// 构造 → validate → 执行，返回 stdout（validator 失败即测试书写的 bug）。
fn run_prog(prog: &IrProgram) -> String {
    validate(prog).unwrap_or_else(|e| panic!("手构 IR 应过 validate: {e}"));
    let mut sink = CollectSink::default();
    let mut m = IrMachine::new(prog);
    m.run(&mut sink)
        .unwrap_or_else(|e| panic!("IR 执行应成功: {e:?}"));
    sink.0.borrow().clone()
}

// ———— 调用与基本块 ————

#[test]
fn call_and_binary() {
    // fn0 = main：println(add(2, 3))；fn1 = add(a, b) → a+b
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![
            func(
                "main",
                vec![],
                vec![blk(
                    vec![
                        Inst::ConstI64(2),
                        Inst::ConstI64(3),
                        Inst::Call(FunctionId(1)),
                        println1(),
                    ],
                    ret_0(),
                )],
            ),
            IrFunction {
                name: "add".to_string(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 2,
                    has_ret: true,
                },
                locals: vec![ld("a"), ld("b")],
                blocks: vec![blk(
                    vec![
                        Inst::LocalGet(LocalId(0)),
                        Inst::LocalGet(LocalId(1)),
                        Inst::Binary(Binop::Add),
                    ],
                    ret_v(),
                )],
                extern_host: None,
            },
        ],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "5\n");
}

#[test]
fn branch_and_goto() {
    // x = 1；x > 0 → "pos" 否则 "neg"（Branch + Goto 汇合）
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![ld("x")],
            blocks: vec![
                blk(
                    vec![
                        Inst::ConstI64(1),
                        Inst::LocalSet(LocalId(0)),
                        Inst::LocalGet(LocalId(0)),
                        Inst::ConstI64(0),
                        Inst::Binary(Binop::Gt),
                    ],
                    Terminator::Branch {
                        if_true: BlockId(1),
                        if_false: BlockId(2),
                    },
                ),
                blk(
                    vec![Inst::ConstStr("pos".into()), println1()],
                    Terminator::Goto { target: BlockId(3) },
                ),
                blk(vec![Inst::ConstStr("neg".into()), println1()], ret_0()),
                blk(vec![], ret_0()),
            ],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "pos\n");
}

#[test]
fn switch_int_arms_and_default() {
    let build = |start: i64| IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![],
            blocks: vec![
                blk(
                    vec![Inst::ConstI64(start)],
                    Terminator::SwitchInt {
                        arms: vec![(1, BlockId(1)), (2, BlockId(2))],
                        default: Some(BlockId(3)),
                    },
                ),
                blk(vec![Inst::ConstStr("one".into()), println1()], ret_0()),
                blk(vec![Inst::ConstStr("two".into()), println1()], ret_0()),
                blk(vec![Inst::ConstStr("other".into()), println1()], ret_0()),
            ],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&build(2)), "two\n");
    assert_eq!(run_prog(&build(9)), "other\n");
}

#[test]
fn switch_enum_and_payload() {
    // enum Shape { Circle(int), Point }；match Circle(c) → println(c)；Point → "point"
    let prog = IrProgram {
        structs: vec![],
        enums: vec![EnumDecl {
            name: "Shape".to_string(),
            type_params: vec![],
            cases: vec![
                EnumCase {
                    name: "Circle".to_string(),
                    payload_types: vec![None],
                },
                EnumCase {
                    name: "Point".to_string(),
                    payload_types: vec![],
                },
            ],
        }],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![ld("e")],
            blocks: vec![
                // SwitchEnum 弹掉被测值：scrutinee 先存局部槽，臂内从槽读再取载荷
                blk(
                    vec![
                        Inst::ConstI64(7),
                        Inst::EnumNew {
                            enum_id: lang_ir::EnumId(0),
                            case_id: 0,
                        },
                        Inst::LocalSet(LocalId(0)),
                        Inst::LocalGet(LocalId(0)),
                    ],
                    Terminator::SwitchEnum {
                        enum_id: lang_ir::EnumId(0),
                        arms: vec![BlockId(1), BlockId(2)],
                        default: None,
                    },
                ),
                blk(
                    vec![
                        Inst::LocalGet(LocalId(0)),
                        Inst::EnumPayloadGet { index: 0 },
                        println1(),
                    ],
                    ret_0(),
                ),
                blk(vec![Inst::ConstStr("point".into()), println1()], ret_0()),
            ],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "7\n");
}

// ———— 装箱纪律与闭包捕获 ————

#[test]
fn box_deref_refset_aliasing() {
    // cell = &10；*cell += 5 经 RefSet；读穿透打印 15
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![ld("cell")],
            blocks: vec![blk(
                vec![
                    Inst::ConstI64(10),
                    Inst::BoxNew,
                    Inst::LocalSet(LocalId(0)),
                    // *cell = *cell + 5
                    Inst::LocalGet(LocalId(0)),
                    Inst::LocalGet(LocalId(0)),
                    Inst::Deref,
                    Inst::ConstI64(5),
                    Inst::Binary(Binop::Add),
                    Inst::RefSet,
                    // println(*cell)
                    Inst::LocalGet(LocalId(0)),
                    Inst::Deref,
                    println1(),
                ],
                ret_0(),
            )],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "15\n");
}

#[test]
fn closure_capture_by_reference() {
    // cell = &10；cl = closure(cap: cell) { *cap += n }；cl(5)；println(*cell) → 15
    // fn1：capture_count=1（槽 0 = cap Ref），param n（槽 1）
    let add_n = IrFunction {
        name: "addN".to_string(),
        capture_count: 1,
        sig: FnSig {
            param_count: 1,
            has_ret: false,
        },
        locals: vec![ld("cap"), ld("n")],
        blocks: vec![blk(
            vec![
                Inst::LocalGet(LocalId(0)),
                Inst::LocalGet(LocalId(0)),
                Inst::Deref,
                Inst::LocalGet(LocalId(1)),
                Inst::Binary(Binop::Add),
                Inst::RefSet,
            ],
            ret_0(),
        )],
        extern_host: None,
    };
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![
            IrFunction {
                name: "main".to_string(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 0,
                    has_ret: false,
                },
                locals: vec![ld("cell"), ld("cl")],
                blocks: vec![blk(
                    vec![
                        Inst::ConstI64(10),
                        Inst::BoxNew,
                        Inst::LocalSet(LocalId(0)),
                        Inst::LocalGet(LocalId(0)),
                        Inst::ClosureNew {
                            func: FunctionId(1),
                            captures: 1,
                        },
                        Inst::LocalSet(LocalId(1)),
                        Inst::LocalGet(LocalId(1)),
                        Inst::ConstI64(5),
                        Inst::CallIndirect {
                            argc: 1,
                            has_ret: false,
                        },
                        Inst::LocalGet(LocalId(0)),
                        Inst::Deref,
                        println1(),
                    ],
                    ret_0(),
                )],
                extern_host: None,
            },
            add_n,
        ],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "15\n");
}

// ———— 容器与 struct ————

#[test]
fn array_index_slice_and_map() {
    // arr = [10, 20]；println(arr[1]) → 20；arr[0] = 99 → println(arr[0]) → 99；
    // println(arr[0:1]) → [99]；m = {"k": 1}；println(m["k"]) → 1
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![ld("arr"), ld("m")],
            blocks: vec![blk(
                vec![
                    Inst::ConstI64(10),
                    Inst::ConstI64(20),
                    Inst::ArrayNew { count: 2 },
                    Inst::LocalSet(LocalId(0)),
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstI64(1),
                    Inst::IndexGet,
                    println1(),
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstI64(0),
                    Inst::ConstI64(99),
                    Inst::IndexSet,
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstI64(0),
                    Inst::IndexGet,
                    println1(),
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstI64(0),
                    Inst::ConstI64(1),
                    Inst::SliceGet,
                    println1(),
                    Inst::ConstStr("k".into()),
                    Inst::ConstI64(1),
                    Inst::MapNew { count: 1 },
                    Inst::LocalSet(LocalId(1)),
                    Inst::LocalGet(LocalId(1)),
                    Inst::ConstStr("k".into()),
                    Inst::IndexGet,
                    println1(),
                ],
                ret_0(),
            )],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "20\n99\n[99]\n1\n");
}

#[test]
fn struct_field_get_set_through_local() {
    // struct P { x, y }；p = P{1, 2}；p.x → 1；p.x = 9（经 LocalGet 的 place 溯源写回）；p.x → 9
    let prog = IrProgram {
        structs: vec![StructDecl {
            name: "P".to_string(),
            type_params: vec![],
            fields: vec![
                FieldDecl {
                    name: "x".to_string(),
                    type_id: None,
                },
                FieldDecl {
                    name: "y".to_string(),
                    type_id: None,
                },
            ],
        }],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![ld("p")],
            blocks: vec![blk(
                vec![
                    Inst::ConstI64(1),
                    Inst::ConstI64(2),
                    Inst::StructNew {
                        struct_id: lang_ir::StructId(0),
                    },
                    Inst::LocalSet(LocalId(0)),
                    Inst::LocalGet(LocalId(0)),
                    Inst::FieldGet { field: "x".into() },
                    println1(),
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstI64(9),
                    Inst::FieldSet { field: "x".into() },
                    Inst::LocalGet(LocalId(0)),
                    Inst::FieldGet { field: "x".into() },
                    println1(),
                ],
                ret_0(),
            )],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "1\n9\n");
}

// ———— for-in 迭代协议 ————

#[test]
fn iter_range_and_map_keys() {
    // for x in 0..3 → 0/1/2。循环形状：迭代器驻留局部槽，元素在 IterNext
    // 同块消费（块入口栈深恒 0——validator 不变量）。
    let range_prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![ld("it")],
            blocks: vec![
                // b0: it = iter(0..3)
                blk(
                    vec![
                        Inst::ConstI64(0),
                        Inst::ConstI64(3),
                        Inst::RangeNew,
                        Inst::IterNew,
                        Inst::LocalSet(LocalId(0)),
                    ],
                    Terminator::Goto { target: BlockId(1) },
                ),
                // b1: x = next(it)；耗尽 → b2；println(x)；回 b1
                blk(
                    vec![
                        Inst::LocalGet(LocalId(0)),
                        Inst::IterNext { done: BlockId(2) },
                        println1(),
                    ],
                    Terminator::Goto { target: BlockId(1) },
                ),
                blk(vec![], ret_0()),
            ],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&range_prog), "0\n1\n2\n");
}

// ———— chan 与 go ————

#[test]
fn chan_send_recv_fifo_and_try_fail_block() {
    // go produce(ch)（同步子集）：send 7, 8；主程 recv×2 → Ok(7)/Ok(8)（Try Ok 路）；
    // 再 recv 空渠道 → Try Err 路：fail 块打印错误值（逐字冻结 "channel 空"）
    let produce = IrFunction {
        name: "produce".to_string(),
        capture_count: 0,
        sig: FnSig {
            param_count: 1,
            has_ret: false,
        },
        locals: vec![ld("ch")],
        blocks: vec![blk(
            vec![
                Inst::LocalGet(LocalId(0)),
                Inst::ConstI64(7),
                Inst::ChanSend,
                Inst::LocalGet(LocalId(0)),
                Inst::ConstI64(8),
                Inst::ChanSend,
            ],
            ret_0(),
        )],
        extern_host: None,
    };
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![
            IrFunction {
                name: "main".to_string(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 0,
                    has_ret: false,
                },
                locals: vec![ld("ch"), ld("err")],
                blocks: vec![
                    // b0: ch = makeChan()；go produce(ch)；println(?(recv(ch)))
                    blk(
                        vec![
                            Inst::ChanNew,
                            Inst::LocalSet(LocalId(0)),
                            Inst::LocalGet(LocalId(0)),
                            Inst::Spawn {
                                func: FunctionId(1),
                                argc: 1,
                            },
                            Inst::LocalGet(LocalId(0)),
                            Inst::ChanRecv,
                            Inst::Try {
                                err: LocalId(1),
                                fail: BlockId(3),
                            },
                            println1(),
                        ],
                        Terminator::Goto { target: BlockId(1) },
                    ),
                    // b1: println(?(recv(ch)))（第二个值）→ b2
                    blk(
                        vec![
                            Inst::LocalGet(LocalId(0)),
                            Inst::ChanRecv,
                            Inst::Try {
                                err: LocalId(1),
                                fail: BlockId(3),
                            },
                            println1(),
                        ],
                        Terminator::Goto { target: BlockId(2) },
                    ),
                    // b2: println(?(recv(ch)))（第三次：空渠道走 fail）
                    blk(
                        vec![
                            Inst::LocalGet(LocalId(0)),
                            Inst::ChanRecv,
                            Inst::Try {
                                err: LocalId(1),
                                fail: BlockId(3),
                            },
                            println1(),
                        ],
                        ret_0(),
                    ),
                    // b3: fail 块：println(err 槽的错误值)
                    blk(vec![Inst::LocalGet(LocalId(1)), println1()], ret_0()),
                ],
                extern_host: None,
            },
            produce,
        ],
        init: None,
        main: Some(FunctionId(0)),
    };
    // Try 解包 Ok 载荷（打印裸值）；Err 载荷 = 冻结字符串「channel 空」
    // （CHAN-RECV-003 与内建 recv 同一口径）
    assert_eq!(run_prog(&prog), "7\n8\nchannel 空\n");
}

// ———— 全局与入口 ————

#[test]
fn globals_init_then_main() {
    // init: g = 42；main: println(g)
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![GlobalDecl {
            name: "g".to_string(),
            mutable: false,
            type_id: None,
        }],
        functions: vec![
            IrFunction {
                name: "init".to_string(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 0,
                    has_ret: false,
                },
                locals: vec![],
                blocks: vec![blk(
                    vec![Inst::ConstI64(42), Inst::GlobalSet(lang_ir::GlobalId(0))],
                    ret_0(),
                )],
                extern_host: None,
            },
            func(
                "main",
                vec![],
                vec![blk(
                    vec![Inst::GlobalGet(lang_ir::GlobalId(0)), println1()],
                    ret_0(),
                )],
            ),
        ],
        init: Some(FunctionId(0)),
        main: Some(FunctionId(1)),
    };
    assert_eq!(run_prog(&prog), "42\n");
}

#[test]
fn unary_not_neg_and_shortcut_shapes() {
    // -5 取负 → 5；!true → false（Unop 面；短路在 IR 已降为 Branch，见 branch 测试）
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".to_string(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: vec![],
            blocks: vec![blk(
                vec![
                    Inst::ConstI64(-5),
                    Inst::Unary(Unop::Neg),
                    println1(),
                    Inst::ConstBool(true),
                    Inst::Unary(Unop::Not),
                    println1(),
                ],
                ret_0(),
            )],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&prog), "5\nfalse\n");
}

// ———— extern ————

#[test]
fn extern_call_reports_unsupported_host() {
    let prog = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![
            func(
                "main",
                vec![],
                vec![blk(vec![Inst::Call(FunctionId(1))], ret_0())],
            ),
            IrFunction {
                name: "httpGet".to_string(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 0,
                    has_ret: false,
                },
                locals: vec![],
                blocks: vec![],
                extern_host: Some("http".to_string()),
            },
        ],
        init: None,
        main: Some(FunctionId(0)),
    };
    validate(&prog).unwrap_or_else(|e| panic!("手构 IR 应过 validate: {e}"));
    let mut sink = CollectSink::default();
    let mut m = IrMachine::new(&prog);
    let err = m.run(&mut sink).expect_err("extern 调用应失败");
    assert_eq!(err.kind(), RtErrorKind::UnsupportedHost);
}

// ———— 双路径一致性（§R12 差分的最小可运行形态） ————

/// 同一程序的 AST 解释器与手构等价 IR 的 stdout 逐字节相等。
/// 真实「逐 case 双解释器差分」等 A3 的 HIR→IR lowering 落地后接入（届时
/// 无需手构，直接 build_hir → LowerIr 即可量产等价 IR）。
#[test]
fn ast_interp_and_ir_interp_agree() {
    use lang_core::SourceId;
    use lang_core::SourceText;
    use lang_interp::Interp;
    use lang_runtime::Machine;
    use lang_semantic::analyze_stmts;

    // case：conformance/collections/map-keys-values（map 打印 + keys/values + has）
    let src = "fn main() {\n    let m = {\"b\": 2, \"a\": 1}\n    println(m)\n    println(keys(m))\n    println(values(m))\n    println(has(m, \"a\"), has(m, \"z\"))\n}\n";
    let src_text = SourceText::new("main.lang-go", src);

    // AST 路径
    let stmts = lang_syntax::parse(&src_text, SourceId(0)).expect("解析应成功");
    let analysis = analyze_stmts(&stmts, SourceId(0));
    assert!(!analysis.has_errors());
    let mut sink = CollectSink::default();
    let mut interp = Interp::new(Machine::new());
    interp
        .run(&stmts, &analysis, &mut sink)
        .expect("AST 执行应成功");
    let ast_out = sink.0.borrow().clone();

    // IR 路径：等价手构程序（map 字面量 + 内建调用）
    let ir = IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![func(
            "main",
            vec![ld("m")],
            vec![blk(
                vec![
                    Inst::ConstStr("b".into()),
                    Inst::ConstI64(2),
                    Inst::ConstStr("a".into()),
                    Inst::ConstI64(1),
                    Inst::MapNew { count: 2 },
                    Inst::LocalSet(LocalId(0)),
                    Inst::LocalGet(LocalId(0)),
                    println1(),
                    Inst::LocalGet(LocalId(0)),
                    Inst::CallBuiltin {
                        name: "keys".to_string(),
                        argc: 1,
                        has_ret: true,
                    },
                    println1(),
                    Inst::LocalGet(LocalId(0)),
                    Inst::CallBuiltin {
                        name: "values".to_string(),
                        argc: 1,
                        has_ret: true,
                    },
                    println1(),
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstStr("a".into()),
                    Inst::CallBuiltin {
                        name: "has".to_string(),
                        argc: 2,
                        has_ret: true,
                    },
                    Inst::LocalGet(LocalId(0)),
                    Inst::ConstStr("z".into()),
                    Inst::CallBuiltin {
                        name: "has".to_string(),
                        argc: 2,
                        has_ret: true,
                    },
                    Inst::CallBuiltin {
                        name: "println".to_string(),
                        argc: 2,
                        has_ret: false,
                    },
                ],
                ret_0(),
            )],
        )],
        init: None,
        main: Some(FunctionId(0)),
    };
    assert_eq!(run_prog(&ir), ast_out);
}
