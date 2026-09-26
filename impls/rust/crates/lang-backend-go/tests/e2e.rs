//! →Go 后端 E2E：手构 IR 夹具 → 生成 → go build → 执行 → 断言 stdout。
//!
//! 夹具形状参照 lang-ir 的 full_feature_program，语义覆盖对抗矩阵：
//! 算术 / 字符串 / 闭包捕获计数器 / 引用写穿 / slice-map 共享 / struct 拷贝 /
//! map 插入序打印 / chan FIFO / Option-Result / ? 传播 / spawn 快照，
//! 另加 for-in、SwitchInt、全局 init、一元、字符串切片索引。
//! 外部工具不可用时 skip 并打印原因（不红 CI）。

use std::path::{Path, PathBuf};
use std::process::Command;

use lang_backend_go::codegen::generate;
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

/// E2E 夹具程序（语义见文件头注释；每块栈效应手算平衡，过 validate）。
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

    // fn0 init：counter = 7
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

    // fn1 inc（闭包体）：捕获槽 0（Ref）写穿 +1
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

    // fn2 triple(x) = x*3
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

    // fn3 parse(x) -> Result：x>0 ? Ok(x) : Err(err("neg"))
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

    // fn4 caller(x) -> Result：let v = parse(x)?; Ok(v*10)
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

    // fn5 send_task(ch, v)：send(ch, v)
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

    // fn6 main：矩阵全覆盖（块布局见各段注释）
    let m0 = vec![
        // a = 10 + 5*2 - 4/2 = 18
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
        // f = 1.5 + 0.25
        Inst::ConstF64(1.5),
        Inst::ConstF64(0.25),
        bin(Binop::Add),
        st(2),
        // s = "a" + "b" + str(3)
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
        // 闭包捕获计数器：cbox 捕获进 inc，调 3 次后读穿 = 3
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
        // 引用别名写穿：r2 = r1（同一 Box），RefSet(r1, 42) → Deref(r2) = 42
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
        // 数组/映射引用共享
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
        // struct 值语义：q = p 拷贝，q.x = 9 不影响 p.x
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
        // 映射插入序打印（b、a、c ≠ 任何排序序）
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
        // chan FIFO（先 send 后 recv，缓冲 1024 不阻塞）
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
        // Option match
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
        // spawn 快照：实参在 go 处取值（x=1），随后改 x=2 不影响 Task
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
        // for-in sum(1..4) = 6
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
    let b21 = vec![cstr("one"), println_n(1)]; // 命中臂
    let b22 = vec![cstr("other"), println_n(1)];
    let b23 = vec![
        // 全局（init 初始化 + 读改写）、一元、字符串/数组切片与索引
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
    let b24 = vec![ld(0)]; // `?` fail 落点：Err(e) → return e（夹具中不可达）

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

/// 预期 stdout（夹具内 println 顺序）。
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
        .arg("version")
        .output()
        .is_ok_and(|o| o.status.success())
}

fn find_go() -> Option<PathBuf> {
    if let Ok(p) = std::env::var("LANG_RS_GO") {
        let path = PathBuf::from(p);
        if probe(&path) {
            return Some(path);
        }
    }
    if probe(Path::new("go")) {
        return Some(PathBuf::from("go"));
    }
    for candidate in [
        "D:\\DevTools\\Languages\\Backend\\Golang\\Go\\bin\\go.exe",
        "/usr/local/go/bin/go",
        "/usr/bin/go",
    ] {
        let path = PathBuf::from(candidate);
        if path.exists() && probe(&path) {
            return Some(path);
        }
    }
    None
}

struct TempDir(PathBuf);

impl TempDir {
    fn new(tag: &str) -> Self {
        let dir =
            std::env::temp_dir().join(format!("lang_rs_go_e2e_{}_{}", std::process::id(), tag));
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
    assert!(a.starts_with("package main"));
    assert!(a.contains("func lf6_main(as ...any) any {"));
    assert!(a.contains("langGoWaitAll()"));
}

#[test]
fn emission_covers_fixture_shape() {
    let src = generate(&fixture_program()).unwrap();
    // 函数名下标化（防撞名/关键字）
    assert!(src.contains("func lf2_triple(as ...any) any {"));
    // struct：Go 类型改名 + 原名打印 + 动态字段接口
    assert!(src.contains("type S0_P struct {"));
    assert!(src.contains("\"P\" + \"{\" + strings.Join"));
    assert!(src.contains("case \"x\":"));
    // 全局 var + init
    assert!(src.contains("var g0_counter any"));
    assert!(src.contains("lf0_init()"));
    // enum 构造经 langGoEnumVal
    assert!(src.contains("langGoEnumVal(\"Option\", \"Some\""));
    // spawn：实参快照 []any
    assert!(src.contains("langGoGo(func(sa ...any) { lf5_send_task(sa...) }, []any{"));
    // Try：Err 存槽跳 fail 块
    assert!(src.contains("if langGoIsErr(_t"));
}

#[test]
fn e2e_go_build_run_matches_expected_stdout() {
    let Some(go) = find_go() else {
        eprintln!("SKIP：未找到 go 工具（设 LANG_RS_GO 或加入 PATH 后重跑）");
        return;
    };
    let prog = fixture_program();
    assert_eq!(validate(&prog), Ok(()));
    let src = generate(&prog).unwrap();

    let tmp = TempDir::new("full");
    let src_path = tmp.0.join("main.go");
    std::fs::write(&src_path, &src).expect("写 main.go");
    let bin_path = tmp.0.join("app.exe");

    let out = Command::new(&go)
        .args(["build", "-o"])
        .arg(&bin_path)
        .arg(&src_path)
        .output()
        .expect("启动 go build");
    assert!(
        out.status.success(),
        "go build 失败:\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&out.stdout),
        String::from_utf8_lossy(&out.stderr)
    );

    let run = Command::new(&bin_path).output().expect("运行生成产物");
    assert!(
        run.status.success(),
        "产物执行失败:\nstderr: {}",
        String::from_utf8_lossy(&run.stderr)
    );
    let stdout = String::from_utf8_lossy(&run.stdout);
    assert_eq!(
        stdout,
        EXPECTED,
        "stdout 与手算期望逐行一致
stderr: {}
退出码: {}",
        String::from_utf8_lossy(&run.stderr),
        run.status
    );
}
