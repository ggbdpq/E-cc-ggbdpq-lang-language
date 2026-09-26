//! IR dump golden 测试：期望文本全部手写（禁同义反复），
//! 并断言同一程序两次 dump 逐字节相等（确定性契约）。

use lang_core::ids::{FunctionId, LocalId};

use crate::dump::dump_function;
use crate::ids::{BlockId, EnumId, GlobalId, StructId};
use crate::model::{
    Binop, Block, EnumCase, EnumDecl, FieldDecl, FnSig, GlobalDecl, Inst, IrFunction, IrProgram,
    LocalDecl, StructDecl, Terminator,
};
use crate::validate::validate;

fn local(name: &str, mutable: bool) -> LocalDecl {
    LocalDecl {
        name: name.into(),
        mutable,
        type_id: None,
    }
}

fn plain_fn(name: &str, param_count: u16, has_ret: bool, locals: Vec<LocalDecl>) -> IrFunction {
    IrFunction {
        name: name.into(),
        capture_count: 0,
        sig: FnSig {
            param_count,
            has_ret,
        },
        locals,
        blocks: vec![],
        extern_host: None,
    }
}

/// golden 1：常量/全局/运算/chan 专指令/字符串转义的小程序。
#[test]
fn golden_small_chan_program() {
    let prog = IrProgram {
        structs: vec![StructDecl {
            name: "Point".into(),
            type_params: vec![],
            fields: vec![FieldDecl {
                name: "x".into(),
                type_id: None,
            }],
        }],
        enums: vec![EnumDecl {
            name: "Option".into(),
            type_params: vec![],
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
            name: "counter".into(),
            mutable: true,
            type_id: None,
        }],
        functions: vec![
            {
                let mut f = plain_fn("main", 0, false, vec![local("ch", false)]);
                f.blocks = vec![Block {
                    insts: vec![
                        Inst::GlobalGet(GlobalId(0)),
                        Inst::ConstI64(1),
                        Inst::Binary(Binop::Add),
                        Inst::GlobalSet(GlobalId(0)),
                        Inst::ChanNew,
                        Inst::LocalSet(LocalId(0)),
                        Inst::LocalGet(LocalId(0)),
                        Inst::ConstStr("hi".into()),
                        Inst::ChanSend,
                        Inst::LocalGet(LocalId(0)),
                        Inst::ChanRecv,
                        Inst::Pop,
                    ],
                    term: Terminator::Return { has_value: false },
                }];
                f
            },
            {
                let mut f = plain_fn("init", 0, false, vec![]);
                f.blocks = vec![Block {
                    insts: vec![Inst::ConstI64(0), Inst::GlobalSet(GlobalId(0))],
                    term: Terminator::Return { has_value: false },
                }];
                f
            },
        ],
        init: Some(FunctionId(1)),
        main: Some(FunctionId(0)),
    };
    validate(&prog).expect("golden 1 程序应通过 validate");

    let want = "\
entry init=1
entry main=0
struct 0 \"Point\" fields=1
  field \"x\"
enum 0 \"Option\" cases=2
  case 0 \"Some\" payloads=1
  case 1 \"None\" payloads=0
global 0 \"counter\" mut
func 0 \"main\" captures=0 params=0 ret=0
  block 0:
    global.get 0
    const.i64 1
    binary.add
    global.set 0
    chan.new
    local.set 0
    local.get 0
    const.str \"hi\"
    chan.send
    local.get 0
    chan.recv
    pop
    return 0
func 1 \"init\" captures=0 params=0 ret=0
  block 0:
    const.i64 0
    global.set 0
    return 0
";
    assert_eq!(crate::dump::dump_program(&prog), want);
    // 确定性：同一程序两次 dump 逐字节相等。
    assert_eq!(crate::dump::dump_program(&prog), want);
}

/// golden 2：泛型 struct/SwitchInt/SwitchEnum/extern/闭包/call.indirect/
/// spawn/branch/unreachable/转义字符 + 单函数 dump。
#[test]
fn golden_multi_block_program() {
    let classify = {
        let mut f = plain_fn("classify", 1, true, vec![local("n", false)]);
        f.blocks = vec![
            Block {
                insts: vec![Inst::LocalGet(LocalId(0))],
                term: Terminator::SwitchInt {
                    arms: vec![(0, BlockId(1)), (1, BlockId(2))],
                    default: Some(BlockId(3)),
                },
            },
            Block {
                insts: vec![Inst::ConstStr("zero".into())],
                term: Terminator::Return { has_value: true },
            },
            Block {
                insts: vec![Inst::ConstStr("one".into())],
                term: Terminator::Return { has_value: true },
            },
            Block {
                insts: vec![],
                term: Terminator::Unreachable,
            },
        ];
        f
    };
    let read_config = IrFunction {
        extern_host: Some("fs.read".into()),
        ..plain_fn("readConfig", 0, true, vec![])
    };
    let run = {
        let mut f = plain_fn(
            "run",
            0,
            false,
            vec![
                local("pair", false),
                local("cb", false),
                local("it", false),
                local("ev", false),
            ],
        );
        f.blocks = vec![
            Block {
                insts: vec![
                    Inst::ConstI64(7),
                    Inst::Spawn {
                        func: FunctionId(0),
                        argc: 1,
                    },
                    Inst::ConstI64(1),
                    Inst::ConstF64(2.5),
                    Inst::StructNew {
                        struct_id: StructId(0),
                    },
                    Inst::LocalSet(LocalId(0)),
                    Inst::LocalGet(LocalId(0)),
                    Inst::FieldGet {
                        field: "first".into(),
                    },
                    Inst::EnumNew {
                        enum_id: EnumId(0),
                        case_id: 0,
                    },
                    // 枚举值驻局部槽：SwitchEnum 弹掉栈顶后，臂块从槽取回。
                    Inst::LocalSet(LocalId(3)),
                    Inst::LocalGet(LocalId(3)),
                ],
                term: Terminator::SwitchEnum {
                    enum_id: EnumId(0),
                    arms: vec![BlockId(1), BlockId(2)],
                    default: None,
                },
            },
            Block {
                insts: vec![
                    Inst::LocalGet(LocalId(3)),
                    Inst::EnumPayloadGet { index: 0 },
                    Inst::CallBuiltin {
                        name: "println".into(),
                        argc: 1,
                        has_ret: false,
                    },
                ],
                term: Terminator::Return { has_value: false },
            },
            Block {
                insts: vec![
                    Inst::ClosureNew {
                        func: FunctionId(3),
                        captures: 0,
                    },
                    Inst::LocalSet(LocalId(1)),
                    Inst::LocalGet(LocalId(1)),
                    Inst::ConstI64(4),
                    Inst::CallIndirect {
                        argc: 1,
                        has_ret: true,
                    },
                    Inst::Pop,
                    Inst::ConstBool(true),
                ],
                term: Terminator::Branch {
                    if_true: BlockId(3),
                    if_false: BlockId(4),
                },
            },
            Block {
                insts: vec![
                    Inst::ConstI64(0),
                    Inst::ConstI64(3),
                    Inst::RangeNew,
                    Inst::IterNew,
                    Inst::LocalSet(LocalId(2)),
                    Inst::LocalGet(LocalId(2)),
                    Inst::IterNext { done: BlockId(4) },
                    Inst::Pop,
                ],
                term: Terminator::Goto { target: BlockId(4) },
            },
            Block {
                insts: vec![],
                term: Terminator::Return { has_value: false },
            },
        ];
        f
    };
    let add_one = {
        let mut f = plain_fn("addOne", 1, true, vec![local("n", false)]);
        f.blocks = vec![Block {
            insts: vec![
                Inst::LocalGet(LocalId(0)),
                Inst::ConstI64(1),
                Inst::Binary(Binop::Add),
            ],
            term: Terminator::Return { has_value: true },
        }];
        f
    };

    let prog = IrProgram {
        structs: vec![StructDecl {
            name: "Pair".into(),
            type_params: vec!["T".into()],
            fields: vec![
                FieldDecl {
                    name: "first".into(),
                    type_id: None,
                },
                FieldDecl {
                    name: "second".into(),
                    type_id: None,
                },
            ],
        }],
        enums: vec![EnumDecl {
            name: "Result".into(),
            type_params: vec![],
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
        }],
        globals: vec![],
        functions: vec![classify, read_config, run, add_one],
        init: None,
        main: Some(FunctionId(2)),
    };
    validate(&prog).expect("golden 2 程序应通过 validate");

    let want = "\
entry init=-
entry main=2
struct 0 \"Pair\" <T> fields=2
  field \"first\"
  field \"second\"
enum 0 \"Result\" cases=2
  case 0 \"Ok\" payloads=1
  case 1 \"Err\" payloads=1
func 0 \"classify\" captures=0 params=1 ret=1
  block 0:
    local.get 0
    switch.int 0->1 1->2 default=3
  block 1:
    const.str \"zero\"
    return 1
  block 2:
    const.str \"one\"
    return 1
  block 3:
    unreachable
func 1 \"readConfig\" extern=\"fs.read\"
func 2 \"run\" captures=0 params=0 ret=0
  block 0:
    const.i64 7
    spawn 0 argc=1
    const.i64 1
    const.f64 2.5
    struct.new 0
    local.set 0
    local.get 0
    field.get \"first\"
    enum.new 0 0
    local.set 3
    local.get 3
    switch.enum 0 0->1 1->2 default=-
  block 1:
    local.get 3
    enum.payload.get 0
    call.builtin \"println\" argc=1 ret=0
    return 0
  block 2:
    closure.new 3 captures=0
    local.set 1
    local.get 1
    const.i64 4
    call.indirect argc=1 ret=1
    pop
    const.bool true
    branch 3 4
  block 3:
    const.i64 0
    const.i64 3
    range.new
    iter.new
    local.set 2
    local.get 2
    iter.next 4
    pop
    goto 4
  block 4:
    return 0
func 3 \"addOne\" captures=0 params=1 ret=1
  block 0:
    local.get 0
    const.i64 1
    binary.add
    return 1
";
    assert_eq!(crate::dump::dump_program(&prog), want);
    // 确定性：同一程序两次 dump 逐字节相等。
    assert_eq!(crate::dump::dump_program(&prog), want);
    // 单函数 dump：extern 函数只有声明行；普通函数行带 func 下标。
    assert_eq!(
        dump_function(&prog.functions[1], FunctionId(1)),
        "func 1 \"readConfig\" extern=\"fs.read\"\n"
    );
    assert_eq!(
        dump_function(&prog.functions[3], FunctionId(3)),
        "func 3 \"addOne\" captures=0 params=1 ret=1\n  block 0:\n    local.get 0\n    const.i64 1\n    binary.add\n    return 1\n"
    );
}

/// 字符串转义：引号/反斜杠/换行以 `escape_debug` 形态出现在 dump 中（确定性）。
#[test]
fn dump_escapes_strings_deterministically() {
    let mut f = plain_fn("main", 0, false, vec![]);
    f.blocks = vec![Block {
        insts: vec![Inst::ConstStr("a\"b\\c\nd\te".into())],
        term: Terminator::Return { has_value: false },
    }];
    let text = dump_function(&f, FunctionId(0));
    assert!(
        text.contains("const.str \"a\\\"b\\\\c\\nd\\te\""),
        "转义形态不符: {text}"
    );
    assert_eq!(text, dump_function(&f, FunctionId(0)));
}
