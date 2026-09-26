//! →JS 后端 E2E：手构 IR 夹具 → 生成 → node 执行 → 断言 stdout。
//!
//! 夹具与期望值同 lang-backend-go/tests/e2e.rs（跨后端差分的同一语义面）：
//! 算术 / 字符串 / 闭包捕获计数器 / 引用写穿 / slice-map 共享 / struct 拷贝 /
//! map 插入序打印 / chan FIFO / Option-Result / ? 传播 / spawn 快照，
//! 另加 for-in、SwitchInt、全局 init、一元、字符串切片索引。
//! node 不可用时 skip 并打印原因（不红 CI）。

use std::path::{Path, PathBuf};
use std::process::Command;

use lang_backend_js::codegen::generate;
use lang_core::ids::{FunctionId, LocalId};
use lang_ir::{
    Binop, Block, BlockId, EnumCase, EnumDecl, EnumId, FieldDecl, FnSig, GlobalDecl, GlobalId,
    Inst, IrFunction, IrProgram, LocalDecl, StructDecl, StructId, Terminator, Unop, validate,
};

// ———— 构造辅助 ————

fn c64(v: i64) -> Inst {
    Inst::ConstI64(v)
}
fn cstr(s: &str) -> Inst {
    Inst::ConstStr(s.into())
}
fn ld(i: u32) -> Inst {
    Inst::LocalGet(LocalId(i))
}
fn st(i: u32) -> Inst {
    Inst::LocalSet(LocalId(i))
}
fn bin(op: Binop) -> Inst {
    Inst::Binary(op)
}
fn println_n(n: u16) -> Inst {
    Inst::CallBuiltin {
        name: "println".into(),
        argc: n,
        has_ret: false,
    }
}
fn builtin(name: &str, argc: u16, has_ret: bool) -> Inst {
    Inst::CallBuiltin {
        name: name.into(),
        argc,
        has_ret,
    }
}
fn blk(insts: Vec<Inst>, term: Terminator) -> Block {
    Block { insts, term }
}
fn retv() -> Terminator {
    Terminator::Return { has_value: true }
}
fn ret0() -> Terminator {
    Terminator::Return { has_value: false }
}
fn goto(t: u32) -> Terminator {
    Terminator::Goto { target: BlockId(t) }
}
fn local(name: &str) -> LocalDecl {
    LocalDecl {
        name: name.into(),
        mutable: true,
        type_id: None,
    }
}

/// E2E 夹具程序（与 →Go 测试同构；每块栈效应手算平衡，过 validate）。
fn fixture_program() -> IrProgram {
    let structs = vec![StructDecl {
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
    }];
    let enums = vec![
        EnumDecl {
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
        },
        EnumDecl {
            name: "Result".into(),
            type_params: vec!["T".into(), "E".into()],
            cases: vec![
                EnumCase {
                    name: "Ok".into(),
                    payload_types: vec![None],
                },
                EnumCase {
                    name: "Err".into(),
                    payload_types: vec![None],
                },
            ],
        },
    ];
    let globals = vec![GlobalDecl {
        name: "counter".into(),
        mutable: true,
        type_id: None,
    }];

    let init = IrFunction {
        name: "init".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 0,
            has_ret: false,
        },
        locals: vec![],
        blocks: vec![blk(vec![c64(7), Inst::GlobalSet(GlobalId(0))], ret0())],
        extern_host: None,
    };

    let inc = IrFunction {
        name: "inc".into(),
        capture_count: 1,
        sig: FnSig {
            param_count: 0,
            has_ret: false,
        },
        locals: vec![local("c0")],
        blocks: vec![blk(
            vec![
                ld(0),
                ld(0),
                Inst::Deref,
                c64(1),
                bin(Binop::Add),
                Inst::RefSet,
            ],
            ret0(),
        )],
        extern_host: None,
    };

    let triple = IrFunction {
        name: "triple".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 1,
            has_ret: true,
        },
        locals: vec![local("x")],
        blocks: vec![blk(vec![ld(0), c64(3), bin(Binop::Mul)], retv())],
        extern_host: None,
    };

    let parse = IrFunction {
        name: "parse".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 1,
            has_ret: true,
        },
        locals: vec![local("x")],
        blocks: vec![
            blk(
                vec![ld(0), c64(0), bin(Binop::Gt)],
                Terminator::Branch {
                    if_true: BlockId(1),
                    if_false: BlockId(2),
                },
            ),
            blk(
                vec![
                    ld(0),
                    Inst::EnumNew {
                        enum_id: EnumId(1),
                        case_id: 0,
                    },
                ],
                retv(),
            ),
            blk(
                vec![
                    cstr("neg"),
                    builtin("err", 1, true),
                    Inst::EnumNew {
                        enum_id: EnumId(1),
                        case_id: 1,
                    },
                ],
                retv(),
            ),
        ],
        extern_host: None,
    };

    let caller = IrFunction {
        name: "caller".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 1,
            has_ret: true,
        },
        locals: vec![local("x"), local("err"), local("v")],
        blocks: vec![
            blk(
                vec![
                    ld(0),
                    Inst::Call(FunctionId(3)),
                    Inst::Try {
                        err: LocalId(1),
                        fail: BlockId(1),
                    },
                    st(2),
                    ld(2),
                    c64(10),
                    bin(Binop::Mul),
                    Inst::EnumNew {
                        enum_id: EnumId(1),
                        case_id: 0,
                    },
                ],
                retv(),
            ),
            blk(
                vec![
                    ld(1),
                    Inst::EnumNew {
                        enum_id: EnumId(1),
                        case_id: 1,
                    },
                ],
                retv(),
            ),
        ],
        extern_host: None,
    };

    let send_task = IrFunction {
        name: "send_task".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 2,
            has_ret: false,
        },
        locals: vec![local("ch"), local("v")],
        blocks: vec![blk(vec![ld(0), ld(1), Inst::ChanSend], ret0())],
        extern_host: None,
    };

    let m0 = vec![
        c64(10),
        c64(5),
        c64(2),
        bin(Binop::Mul),
        bin(Binop::Add),
        c64(4),
        c64(2),
        bin(Binop::Div),
        bin(Binop::Sub),
        st(1),
        Inst::ConstF64(1.5),
        Inst::ConstF64(0.25),
        bin(Binop::Add),
        st(2),
        cstr("a"),
        cstr("b"),
        bin(Binop::Add),
        c64(3),
        builtin("str", 1, true),
        bin(Binop::Add),
        st(3),
        ld(1),
        ld(2),
        ld(3),
        println_n(3), // "18 1.75 ab3"
        ld(3),
        println_n(1), // "ab3"
        c64(5),
        Inst::Call(FunctionId(2)),
        println_n(1), // "15"
        c64(0),
        Inst::BoxNew,
        st(4),
        ld(4),
        Inst::ClosureNew {
            func: FunctionId(1),
            captures: 1,
        },
        st(5),
        ld(5),
        Inst::CallIndirect {
            argc: 0,
            has_ret: false,
        },
        ld(5),
        Inst::CallIndirect {
            argc: 0,
            has_ret: false,
        },
        ld(5),
        Inst::CallIndirect {
            argc: 0,
            has_ret: false,
        },
        ld(4),
        Inst::Deref,
        println_n(1), // "3"
    ];
    let b1 = vec![
        c64(1),
        Inst::BoxNew,
        st(6),
        ld(6),
        st(7),
        ld(6),
        c64(42),
        Inst::RefSet,
        ld(7),
        Inst::Deref,
        println_n(1), // "42"
    ];
    let b2 = vec![
        c64(1),
        Inst::ArrayNew { count: 1 },
        st(8),
        ld(8),
        st(9),
        ld(9),
        c64(2),
        // ArrayPush = 拼接（rhs 须为数组，IrMachine 冻结语义）：压 [2] 拼入
        Inst::ArrayNew { count: 1 },
        Inst::ArrayPush,
        ld(8),
        builtin("len", 1, true),
        println_n(1), // "2"
        Inst::MapNew { count: 0 },
        st(10),
        ld(10),
        st(11),
        ld(11),
        cstr("k"),
        c64(1),
        Inst::IndexSet,
        ld(10),
        cstr("k"),
        builtin("has", 2, true),
        println_n(1), // "true"
    ];
    let b3 = vec![
        c64(1),
        c64(2),
        Inst::StructNew {
            struct_id: StructId(0),
        },
        st(12),
        ld(12),
        st(13),
        ld(13),
        c64(9),
        Inst::FieldSet { field: "x".into() },
        ld(12),
        Inst::FieldGet { field: "x".into() },
        ld(13),
        Inst::FieldGet { field: "x".into() },
        println_n(2), // "1 9"
    ];
    let b4 = vec![
        cstr("b"),
        c64(1),
        cstr("a"),
        c64(2),
        Inst::MapNew { count: 2 },
        st(14),
        ld(14),
        cstr("c"),
        c64(3),
        Inst::IndexSet,
        ld(14),
        println_n(1), // {"b": 1, "a": 2, "c": 3}
    ];
    let b5 = vec![
        Inst::ChanNew,
        st(15),
        ld(15),
        c64(1),
        Inst::ChanSend,
        ld(15),
        c64(2),
        Inst::ChanSend,
        ld(15),
        Inst::ChanRecv,
        Inst::Try {
            err: LocalId(0),
            fail: BlockId(24),
        },
        println_n(1), // "1"
        ld(15),
        Inst::ChanRecv,
        Inst::Try {
            err: LocalId(0),
            fail: BlockId(24),
        },
        println_n(1), // "2"
    ];
    let b6 = vec![
        c64(5),
        Inst::EnumNew {
            enum_id: EnumId(0),
            case_id: 0,
        },
        st(17),
        ld(17),
    ];
    let b7 = vec![ld(17), Inst::EnumPayloadGet { index: 0 }, println_n(1)]; // "5"
    let b8 = vec![cstr("none"), println_n(1)];
    let b9 = vec![c64(5), Inst::Call(FunctionId(4)), st(18), ld(18)];
    let b10 = vec![ld(18), Inst::EnumPayloadGet { index: 0 }, println_n(1)]; // "50"
    let b11 = vec![
        ld(18),
        Inst::EnumPayloadGet { index: 0 },
        Inst::FieldGet {
            field: "msg".into(),
        },
        println_n(1),
    ];
    let b12 = vec![c64(-1), Inst::Call(FunctionId(4)), st(19), ld(19)];
    let b13 = vec![ld(19), Inst::EnumPayloadGet { index: 0 }, println_n(1)];
    let b14 = vec![
        ld(19),
        Inst::EnumPayloadGet { index: 0 },
        Inst::FieldGet {
            field: "msg".into(),
        },
        println_n(1), // "neg"
    ];
    let b15 = vec![
        c64(1),
        st(20),
        Inst::ChanNew,
        st(21),
        ld(21),
        ld(20),
        Inst::Spawn {
            func: FunctionId(5),
            argc: 2,
        },
        c64(2),
        st(20),
        ld(21),
        Inst::ChanRecv,
        Inst::Try {
            err: LocalId(0),
            fail: BlockId(24),
        },
        println_n(1), // "1"
    ];
    let b16 = vec![
        c64(0),
        st(22),
        c64(1),
        c64(4),
        Inst::RangeNew,
        Inst::IterNew,
        st(23),
    ];
    let b17 = vec![
        ld(23),
        Inst::IterNext { done: BlockId(18) },
        ld(22),
        bin(Binop::Add),
        st(22),
    ];
    let b18 = vec![ld(22), println_n(1)]; // "6"
    let b19 = vec![c64(1)];
    let b20 = vec![cstr("zero"), println_n(1)];
    let b21 = vec![cstr("one"), println_n(1)];
    let b22 = vec![cstr("other"), println_n(1)];
    let b23 = vec![
        Inst::GlobalGet(GlobalId(0)),
        c64(1),
        bin(Binop::Add),
        Inst::GlobalSet(GlobalId(0)),
        Inst::GlobalGet(GlobalId(0)),
        println_n(1), // "8"
        c64(3),
        Inst::Unary(Unop::Neg),
        println_n(1), // "-3"
        Inst::ConstBool(true),
        Inst::Unary(Unop::Not),
        println_n(1), // "false"
        cstr("hello"),
        c64(1),
        c64(3),
        Inst::SliceGet,
        println_n(1), // "el"
        cstr("hello"),
        c64(1),
        Inst::IndexGet,
        println_n(1), // "e"
        c64(1),
        c64(2),
        c64(3),
        Inst::ArrayNew { count: 3 },
        c64(0),
        c64(2),
        Inst::SliceGet,
        builtin("len", 1, true),
        println_n(1), // "2"
        c64(0),
    ];
    let b24 = vec![ld(0)];

    let main_fn = IrFunction {
        name: "main".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 0,
            has_ret: true,
        },
        locals: (0..24)
            .map(|i| {
                local(match i {
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
                    _ => "it",
                })
            })
            .collect(),
        blocks: vec![
            blk(m0, goto(1)),
            blk(b1, goto(2)),
            blk(b2, goto(3)),
            blk(b3, goto(4)),
            blk(b4, goto(5)),
            blk(b5, goto(6)),
            blk(
                b6,
                Terminator::SwitchEnum {
                    enum_id: EnumId(0),
                    arms: vec![BlockId(7), BlockId(8)],
                    default: None,
                },
            ),
            blk(b7, goto(9)),
            blk(b8, goto(9)),
            blk(
                b9,
                Terminator::SwitchEnum {
                    enum_id: EnumId(1),
                    arms: vec![BlockId(10), BlockId(11)],
                    default: None,
                },
            ),
            blk(b10, goto(12)),
            blk(b11, goto(12)),
            blk(
                b12,
                Terminator::SwitchEnum {
                    enum_id: EnumId(1),
                    arms: vec![BlockId(13), BlockId(14)],
                    default: None,
                },
            ),
            blk(b13, goto(15)),
            blk(b14, goto(15)),
            blk(b15, goto(16)),
            blk(b16, goto(17)),
            blk(b17, goto(17)),
            blk(b18, goto(19)),
            blk(
                b19,
                Terminator::SwitchInt {
                    arms: vec![(0, BlockId(20)), (1, BlockId(21))],
                    default: Some(BlockId(22)),
                },
            ),
            blk(b20, goto(23)),
            blk(b21, goto(23)),
            blk(b22, goto(23)),
            blk(b23, retv()),
            blk(b24, retv()),
        ],
        extern_host: None,
    };

    IrProgram {
        structs,
        enums,
        globals,
        functions: vec![init, inc, triple, parse, caller, send_task, main_fn],
        init: Some(FunctionId(0)),
        main: Some(FunctionId(6)),
    }
}

/// 预期 stdout（与 →Go 完全一致：差分矩阵的语义面）。
const EXPECTED: &str = "\
18 1.75 ab3
ab3
15
3
42
2
true
1 9
{\"b\": 1, \"a\": 2, \"c\": 3}
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
";

// ———— 工具探测（存在性守卫） ————

fn probe(exe: &Path) -> bool {
    Command::new(exe)
        .arg("--version")
        .output()
        .is_ok_and(|o| o.status.success())
}

fn find_node() -> Option<PathBuf> {
    if let Ok(p) = std::env::var("LANG_RS_NODE") {
        let path = PathBuf::from(p);
        if probe(&path) {
            return Some(path);
        }
    }
    if probe(Path::new("node")) {
        return Some(PathBuf::from("node"));
    }
    None
}

struct TempDir(PathBuf);

impl TempDir {
    fn new(tag: &str) -> Self {
        let dir =
            std::env::temp_dir().join(format!("lang_rs_js_e2e_{}_{}", std::process::id(), tag));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).expect("建临时目录");
        Self(dir)
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        if std::env::var("LANG_RS_KEEP").is_ok() {
            eprintln!("LANG_RS_KEEP 已设，保留 {}", self.0.display());
            return;
        }
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

// ———— 测试 ————

#[test]
fn fixture_is_valid_and_generation_is_deterministic() {
    let prog = fixture_program();
    assert_eq!(validate(&prog), Ok(()));
    let a = generate(&prog).unwrap();
    let b = generate(&prog).unwrap();
    assert_eq!(a, b, "同 IR 两次生成必须逐字节相等");
    assert!(a.contains("function lf6_main(...as) {"));
    assert!(a.contains("let __pc = 0;"));
    assert!(a.contains("lf0_init();"));
    assert!(a.contains("lf6_main();"));
}

#[test]
fn emission_covers_fixture_shape() {
    let src = generate(&fixture_program()).unwrap();
    // 状态机发射形
    assert!(src.contains("__pc = 1; break;"));
    // struct 标签对象 + 动态字段
    assert!(src.contains("__langGoStruct: \"P\""));
    assert!(src.contains("langGoSetField(l13, \"x\""));
    // spawn：同步调用 + 实参拷贝快照
    assert!(src.contains("lf5_send_task(langGoCopy(l21), langGoCopy(l20));"));
    // Try：Err 存槽跳 fail 块
    assert!(src.contains("if (langGoIsErr(_t"));
    // BigInt int 字面量
    assert!(src.contains("42n"));
    assert!(src.contains("langGoEnumVal(\"Option\", \"Some\""));
}

#[test]
fn e2e_node_run_matches_expected_stdout() {
    let Some(node) = find_node() else {
        eprintln!("SKIP：未找到 node（设 LANG_RS_NODE 或加入 PATH 后重跑）");
        return;
    };
    let prog = fixture_program();
    assert_eq!(validate(&prog), Ok(()));
    let src = generate(&prog).unwrap();

    let tmp = TempDir::new("full");
    let src_path = tmp.0.join("main.mjs");
    std::fs::write(&src_path, &src).expect("写 main.mjs");

    let run = Command::new(&node)
        .arg(&src_path)
        .output()
        .expect("启动 node");
    assert!(
        run.status.success(),
        "产物执行失败:\nstderr: {}",
        String::from_utf8_lossy(&run.stderr)
    );
    let stdout = String::from_utf8_lossy(&run.stdout);
    assert_eq!(
        stdout,
        EXPECTED,
        "stdout 与手算期望逐行一致\nstderr: {}",
        String::from_utf8_lossy(&run.stderr)
    );
}
