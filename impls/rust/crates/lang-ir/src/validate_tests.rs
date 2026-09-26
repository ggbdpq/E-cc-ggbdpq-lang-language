//! Validator 的红→绿测试与模型确定性测试。
//!
//! 期望值全部手算（指令栈效应表见 model.rs 注释），不拿实现当期望：
//! 非法程序必须被拒（红），合法程序必须通过（绿），同构造两次逐字段相等（确定性）。

use lang_core::ids::{FunctionId, LocalId};

use crate::ids::{BlockId, EnumId, GlobalId, StructId};
use crate::model::*;
use crate::validate::{IrError, validate};

/// 合法程序 A：`fn main() -> int { 1 + 2 }`
/// 块 0：ConstI64 1, ConstI64 2, Binary(Add), Return{true}（栈 0→1→2→1→0）。
fn prog_add() -> IrProgram {
    IrProgram {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        functions: vec![IrFunction {
            name: "main".into(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: true,
            },
            locals: vec![],
            blocks: vec![Block {
                insts: vec![
                    Inst::ConstI64(1),
                    Inst::ConstI64(2),
                    Inst::Binary(Binop::Add),
                ],
                term: Terminator::Return { has_value: true },
            }],
            extern_host: None,
        }],
        init: None,
        main: Some(FunctionId(0)),
    }
}

#[test]
fn legal_program_passes() {
    assert_eq!(validate(&prog_add()), Ok(()));
}

#[test]
fn same_program_built_twice_is_field_identical() {
    assert_eq!(prog_add(), prog_add());
}

#[test]
fn full_feature_program_is_deterministic_and_valid() {
    let a = full_feature_program();
    let b = full_feature_program();
    assert_eq!(a, b);
    assert_eq!(validate(&a), Ok(()));
}

/// 覆盖全部指令形态与两种终接之外的程序：struct/enum/全局/迭代/chan/闭包/装箱/分派。
/// 手算各块栈深，作为 model.rs 栈效应表的活文档。
fn full_feature_program() -> IrProgram {
    IrProgram {
        structs: vec![StructDecl {
            name: "P".into(),
            type_params: vec![],
            fields: vec![
                FieldDecl {
                    name: "x".into(),
                    type_id: None,
                },
                FieldDecl {
                    name: "y".into(),
                    type_id: None,
                },
            ],
        }],
        enums: vec![EnumDecl {
            name: "Option".into(),
            type_params: vec!["T".into()],
            cases: vec![
                EnumCase {
                    name: "Some".into(),
                    payload_types: vec![None],
                },
                EnumCase {
                    name: "None".into(),
                    payload_types: vec![],
                },
            ],
        }],
        globals: vec![GlobalDecl {
            name: "g".into(),
            mutable: true,
            type_id: None,
        }],
        functions: vec![
            // fn#0 init：全局初始化（声明序 GlobalSet）。
            IrFunction {
                name: "init".into(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 0,
                    has_ret: false,
                },
                locals: vec![],
                blocks: vec![Block {
                    insts: vec![Inst::ConstI64(7), Inst::GlobalSet(GlobalId(0))],
                    term: Terminator::Return { has_value: false },
                }],
                extern_host: None,
            },
            // fn#1 闭包体：捕获 1 槽 + 0 参，读穿透捕获引用。
            IrFunction {
                name: "clo".into(),
                capture_count: 1,
                sig: FnSig {
                    param_count: 0,
                    has_ret: true,
                },
                locals: vec![LocalDecl {
                    name: "c0".into(),
                    mutable: false,
                    type_id: None,
                }],
                blocks: vec![Block {
                    insts: vec![
                        Inst::LocalGet(LocalId(0)),
                        Inst::Deref,
                        Inst::Unary(Unop::Neg),
                    ],
                    term: Terminator::Return { has_value: true },
                }],
                extern_host: None,
            },
            // fn#2 外部宿主函数（extern，无体）。
            IrFunction {
                name: "host".into(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 1,
                    has_ret: false,
                },
                locals: vec![LocalDecl {
                    name: "a".into(),
                    mutable: false,
                    type_id: None,
                }],
                blocks: vec![],
                extern_host: Some("go".into()),
            },
            // fn#3 main：其余指令全走一遍。
            IrFunction {
                name: "main".into(),
                capture_count: 0,
                sig: FnSig {
                    param_count: 0,
                    has_ret: true,
                },
                // 槽位：0=err 1=it 2=elem 3=p 4=clo 5=装箱循环变量
                locals: vec![
                    LocalDecl {
                        name: "err".into(),
                        mutable: true,
                        type_id: None,
                    },
                    LocalDecl {
                        name: "it".into(),
                        mutable: true,
                        type_id: None,
                    },
                    LocalDecl {
                        name: "elem".into(),
                        mutable: true,
                        type_id: None,
                    },
                    LocalDecl {
                        name: "p".into(),
                        mutable: true,
                        type_id: None,
                    },
                    LocalDecl {
                        name: "clo".into(),
                        mutable: true,
                        type_id: None,
                    },
                    LocalDecl {
                        name: "boxed".into(),
                        mutable: true,
                        type_id: None,
                    },
                ],
                blocks: vec![
                    // 块 0：构造与容器。
                    Block {
                        insts: vec![
                            // struct 字面量：P{ x: 1, y: 2 }
                            Inst::ConstI64(1),
                            Inst::ConstI64(2),
                            Inst::StructNew {
                                struct_id: StructId(0),
                            },
                            Inst::LocalSet(LocalId(3)),
                            // 数组字面量 + 原位 push（ArrayPush 已 2→0）
                            Inst::ConstI64(1),
                            Inst::ConstI64(2),
                            Inst::ArrayNew { count: 2 },
                            Inst::ConstI64(3),
                            Inst::ArrayPush,
                            // 映射字面量（键值对序）
                            Inst::ConstStr("k".into()),
                            Inst::ConstBool(true),
                            Inst::MapNew { count: 1 },
                            Inst::Pop,
                            // enum 构造（Some 1 载荷）
                            Inst::ConstI64(1),
                            Inst::EnumNew {
                                enum_id: EnumId(0),
                                case_id: 0,
                            },
                            // enum 载荷抽取
                            Inst::EnumPayloadGet { index: 0 },
                            Inst::Pop,
                            // 区间 + 引用装箱
                            Inst::ConstI64(0),
                            Inst::ConstI64(3),
                            Inst::RangeNew,
                            Inst::Pop,
                            Inst::ConstI64(9),
                            Inst::BoxNew,
                            Inst::Deref,
                            Inst::Pop,
                            // 闭包：Box(1) 作为捕获环境
                            Inst::ConstI64(1),
                            Inst::BoxNew,
                            Inst::ClosureNew {
                                func: FunctionId(1),
                                captures: 1,
                            },
                            Inst::LocalSet(LocalId(4)),
                            // 间接调用闭包
                            Inst::LocalGet(LocalId(4)),
                            Inst::CallIndirect {
                                argc: 0,
                                has_ret: true,
                            },
                            Inst::Pop,
                            // chan 收发
                            Inst::ChanNew,
                            Inst::ConstStr("m".into()),
                            Inst::ChanSend,
                            Inst::ChanNew,
                            Inst::ChanRecv,
                            // `?`：Ok 解包继续；Err 存 err 槽跳块 1
                            Inst::Try {
                                err: LocalId(0),
                                fail: BlockId(1),
                            },
                            Inst::Pop,
                            // 内建调用 + 直接调用 extern
                            Inst::ConstStr("hi".into()),
                            Inst::CallBuiltin {
                                name: "println".into(),
                                argc: 1,
                                has_ret: false,
                            },
                            Inst::ConstI64(0),
                            Inst::Call(FunctionId(2)),
                            // spawn（0 参任务假设 fn#4）
                            Inst::Spawn {
                                func: FunctionId(1),
                                argc: 0,
                            },
                            // 全局读
                            Inst::GlobalGet(GlobalId(0)),
                            Inst::Pop,
                            // 被分派值（字面量 int match）
                            Inst::ConstI64(0),
                        ],
                        // 字面量 int match 分派（0/1 → 块 3，缺省 → 块 2）
                        term: Terminator::SwitchInt {
                            arms: vec![(0, BlockId(3)), (1, BlockId(3))],
                            default: Some(BlockId(2)),
                        },
                    },
                    // 块 1：`?` 的 Err 传播路径——合成 Err 返回。
                    Block {
                        insts: vec![Inst::LocalGet(LocalId(0))],
                        term: Terminator::Return { has_value: true },
                    },
                    // 块 2：for-in 循环（迭代器驻槽；元素逐轮装箱）。
                    Block {
                        insts: vec![
                            Inst::ConstI64(0),
                            Inst::ConstI64(2),
                            Inst::RangeNew,
                            Inst::IterNew,
                            Inst::LocalSet(LocalId(1)),
                        ],
                        term: Terminator::Goto { target: BlockId(4) },
                    },
                    // 块 3：SwitchInt 命中臂 → 汇合返回。
                    Block {
                        insts: vec![Inst::ConstI64(42)],
                        term: Terminator::Return { has_value: true },
                    },
                    // 块 4：循环头。迭代尽 → 块 6；否则取元素、装箱重绑、体、continue 回头。
                    Block {
                        insts: vec![
                            Inst::LocalGet(LocalId(1)),
                            Inst::IterNext { done: BlockId(6) },
                            Inst::BoxNew,
                            Inst::LocalSet(LocalId(5)),
                            Inst::LocalGet(LocalId(5)),
                            Inst::Deref,
                            Inst::Pop,
                        ],
                        term: Terminator::Goto { target: BlockId(4) }, // continue
                    },
                    // 块 5：Unreachable 兜底锚。
                    Block {
                        insts: vec![],
                        term: Terminator::Unreachable,
                    },
                    // 块 6：循环出口（break 落点）→ 返回。
                    Block {
                        insts: vec![Inst::ConstI64(0)],
                        term: Terminator::Return { has_value: true },
                    },
                ],
                extern_host: None,
            },
        ],
        init: Some(FunctionId(0)),
        main: Some(FunctionId(3)),
    }
}

// —— 红场景：悬垂引用 ——

#[test]
fn empty_function_rejected() {
    let mut p = prog_add();
    p.functions[0].blocks.clear();
    let e = validate(&p).unwrap_err();
    assert_eq!(e.function, FunctionId(0));
    assert!(e.message.contains("没有基本块"), "实际：{e}");
}

#[test]
fn dangling_local_rejected() {
    let mut p = prog_add();
    p.functions[0].locals.push(LocalDecl {
        name: "x".into(),
        mutable: true,
        type_id: None,
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0]
        .insts
        .push(Inst::LocalSet(LocalId(99)));
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("LocalId 99 越界"), "实际：{e}");
}

#[test]
fn dangling_function_ref_rejected() {
    let mut p = prog_add();
    p.functions[0].blocks[0]
        .insts
        .insert(0, Inst::Call(FunctionId(7)));
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("FunctionId 7 越界"), "实际：{e}");
}

#[test]
fn dangling_block_ref_rejected() {
    let mut p = prog_add();
    p.functions[0].blocks[0].insts.clear(); // 清栈：隔离被测不变量（块引用合法性）
    p.functions[0].blocks[0].term = Terminator::Goto { target: BlockId(5) };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("BlockId 5 越界"), "实际：{e}");
}

#[test]
fn dangling_global_ref_rejected() {
    let mut p = prog_add();
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0]
        .insts
        .push(Inst::GlobalGet(GlobalId(3)));
    p.functions[0].blocks[0].term = Terminator::Return { has_value: true };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("GlobalId 3 越界"), "实际：{e}");
}

#[test]
fn dangling_struct_ref_rejected() {
    let mut p = prog_add();
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0].insts.push(Inst::StructNew {
        struct_id: StructId(4),
    });
    p.functions[0].blocks[0].term = Terminator::Return { has_value: true };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("StructId 4 越界"), "实际：{e}");
}

#[test]
fn dangling_enum_case_rejected() {
    let mut p = prog_add();
    p.enums.push(EnumDecl {
        name: "Option".into(),
        type_params: vec![],
        cases: vec![EnumCase {
            name: "None".into(),
            payload_types: vec![],
        }],
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0].insts.push(Inst::EnumNew {
        enum_id: EnumId(0),
        case_id: 5,
    });
    p.functions[0].blocks[0].term = Terminator::Return { has_value: true };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("case 5 越界"), "实际：{e}");
}

// —— 红场景：栈效应 ——

#[test]
fn stack_underflow_rejected() {
    let mut p = prog_add();
    p.functions[0].locals.push(LocalDecl {
        name: "x".into(),
        mutable: true,
        type_id: None,
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0]
        .insts
        .push(Inst::LocalSet(LocalId(0)));
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("栈下溢"), "实际：{e}");
}

#[test]
fn leftover_stack_at_return_rejected() {
    let mut p = prog_add();
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e = validate(&p).unwrap_err();
    assert!(
        e.message.contains("终接处栈深 1 != 终接消费 0"),
        "实际：{e}"
    );
}

#[test]
fn call_argument_count_must_match_signature() {
    // 目标 1 参，调用点没压实参：Call 弹 1 → 栈下溢。
    let mut p = prog_add();
    p.functions.push(IrFunction {
        name: "id".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 1,
            has_ret: true,
        },
        locals: vec![LocalDecl {
            name: "a".into(),
            mutable: false,
            type_id: None,
        }],
        blocks: vec![Block {
            insts: vec![Inst::LocalGet(LocalId(0))],
            term: Terminator::Return { has_value: true },
        }],
        extern_host: None,
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0]
        .insts
        .push(Inst::Call(FunctionId(1)));
    p.functions[0].blocks[0].term = Terminator::Return { has_value: true };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("Call 栈下溢"), "实际：{e}");

    // 压足实参后通过（Call 1→1，Return 有值平衡）。
    p.functions[0].blocks[0].insts.insert(0, Inst::ConstI64(5));
    assert_eq!(validate(&p), Ok(()));
}

#[test]
fn spawn_argument_count_must_match_target() {
    let mut p = prog_add();
    p.functions.push(IrFunction {
        name: "task".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 2,
            has_ret: false,
        },
        locals: vec![],
        blocks: vec![Block {
            insts: vec![],
            term: Terminator::Return { has_value: false },
        }],
        extern_host: None,
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0].insts.push(Inst::Spawn {
        func: FunctionId(1),
        argc: 1,
    });
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e = validate(&p).unwrap_err();
    assert!(
        e.message.contains("Spawn 实参 1 个 != 目标 fn#1 参数 2 个"),
        "实际：{e}"
    );
}

#[test]
fn closure_capture_count_must_match_target() {
    let mut p = prog_add();
    p.functions.push(IrFunction {
        name: "clo".into(),
        capture_count: 2,
        sig: FnSig {
            param_count: 0,
            has_ret: false,
        },
        locals: vec![
            LocalDecl {
                name: "c0".into(),
                mutable: false,
                type_id: None,
            },
            LocalDecl {
                name: "c1".into(),
                mutable: false,
                type_id: None,
            },
        ],
        blocks: vec![Block {
            insts: vec![],
            term: Terminator::Return { has_value: false },
        }],
        extern_host: None,
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0].insts.push(Inst::ClosureNew {
        func: FunctionId(1),
        captures: 1,
    });
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e = validate(&p).unwrap_err();
    assert!(
        e.message
            .contains("ClosureNew 捕获 1 个 != 目标 fn#1 capture_count 2"),
        "实际：{e}"
    );

    // 压足 2 个 Ref 后通过。
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0].insts.push(Inst::ConstI64(1));
    p.functions[0].blocks[0].insts.push(Inst::BoxNew);
    p.functions[0].blocks[0].insts.push(Inst::ConstI64(2));
    p.functions[0].blocks[0].insts.push(Inst::BoxNew);
    p.functions[0].blocks[0].insts.push(Inst::ClosureNew {
        func: FunctionId(1),
        captures: 2,
    });
    p.functions[0].blocks[0].insts.push(Inst::Pop);
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    assert_eq!(validate(&p), Ok(()));
}

// —— 红场景：块/终接 ——

#[test]
fn extern_function_must_have_no_body() {
    let mut p = prog_add();
    p.functions.push(IrFunction {
        name: "host".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 0,
            has_ret: false,
        },
        locals: vec![],
        blocks: vec![Block {
            insts: vec![],
            term: Terminator::Return { has_value: false },
        }],
        extern_host: Some("go".into()),
    });
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("extern 函数不得有函数体"), "实际：{e}");
}

#[test]
fn switch_enum_arm_count_must_match_cases() {
    let mut p = prog_add();
    p.enums.push(EnumDecl {
        name: "Option".into(),
        type_params: vec!["T".into()],
        cases: vec![
            EnumCase {
                name: "Some".into(),
                payload_types: vec![None],
            },
            EnumCase {
                name: "None".into(),
                payload_types: vec![],
            },
        ],
    });
    p.functions[0].blocks.clear();
    p.functions[0].blocks.push(Block {
        insts: vec![Inst::ConstBool(true)],
        term: Terminator::Branch {
            if_true: BlockId(1),
            if_false: BlockId(2),
        },
    });
    p.functions[0].blocks.push(Block {
        insts: vec![],
        term: Terminator::Return { has_value: false },
    });
    p.functions[0].blocks.push(Block {
        insts: vec![Inst::ConstBool(false)],
        term: Terminator::SwitchEnum {
            enum_id: EnumId(0),
            arms: vec![BlockId(3)],
            default: None,
        },
    });
    p.functions[0].blocks.push(Block {
        insts: vec![],
        term: Terminator::Return { has_value: false },
    });
    let e = validate(&p).unwrap_err();
    assert!(
        e.message
            .contains("SwitchEnum arms 1 个 != enum 0 case 2 个"),
        "实际：{e}"
    );

    // 补齐 arms 后通过。
    p.functions[0].blocks[2].term = Terminator::SwitchEnum {
        enum_id: EnumId(0),
        arms: vec![BlockId(3), BlockId(3)],
        default: None,
    };
    assert_eq!(validate(&p), Ok(()));
}

#[test]
fn try_requires_valid_err_local_and_fail_block() {
    let mut p = prog_add();
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0].insts.push(Inst::ConstI64(1));
    p.functions[0].blocks[0].insts.push(Inst::Try {
        err: LocalId(0),
        fail: BlockId(9),
    });
    p.functions[0].blocks[0].term = Terminator::Return { has_value: true };
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("LocalId 0 越界"), "实际：{e}");

    p.functions[0].locals.push(LocalDecl {
        name: "err".into(),
        mutable: true,
        type_id: None,
    });
    let e = validate(&p).unwrap_err();
    assert!(e.message.contains("BlockId 9 越界"), "实际：{e}");

    // fail 块合法后整体通过：Try 1→1，两块 Return 各自平衡。
    p.functions[0].blocks.push(Block {
        insts: vec![Inst::LocalGet(LocalId(0))],
        term: Terminator::Return { has_value: true },
    });
    p.functions[0].blocks[0].insts[1] = Inst::Try {
        err: LocalId(0),
        fail: BlockId(1),
    };
    assert_eq!(validate(&p), Ok(()));
}

// —— 红场景：局部表不足 ——

#[test]
fn locals_shorter_than_fixed_slots_rejected() {
    let mut p = prog_add();
    p.functions[0].capture_count = 1;
    p.functions[0].sig.param_count = 2;
    p.functions[0].locals.clear(); // 需要 ≥ 3 个槽位
    let e = validate(&p).unwrap_err();
    assert!(
        e.message.contains("局部表 0 个槽位 < 捕获 1 + 参数 2"),
        "实际：{e}"
    );
}

// —— 错误定位 ——

#[test]
fn error_carries_function_block_inst_location() {
    let mut p = prog_add();
    p.functions[0].locals.push(LocalDecl {
        name: "x".into(),
        mutable: true,
        type_id: None,
    });
    p.functions[0].blocks[0].insts.clear();
    p.functions[0].blocks[0]
        .insts
        .push(Inst::LocalSet(LocalId(42)));
    p.functions[0].blocks[0].term = Terminator::Return { has_value: false };
    let e: IrError = validate(&p).unwrap_err();
    assert_eq!(e.function, FunctionId(0));
    assert_eq!(e.block, Some(BlockId(0)));
    assert_eq!(e.inst, Some(0));
    let shown = e.to_string();
    assert!(shown.contains("fn#0"), "实际：{shown}");
    assert!(shown.contains("block#0"), "实际：{shown}");
    assert!(shown.contains("inst#0"), "实际：{shown}");
}
