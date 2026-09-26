//! 内建对照表与直接效果提取测试。
//!
//! 对照表断言经 runtime 公共 API 读取（`BuiltinRegistry::core` 的
//! `lookup`/`name`），不复制 runtime 事实；效果列断言与
//! `lang_contract::builtin_direct_effect` 一致。

use lang_contract::Effect;
use lang_core::ids::{FunctionId, LocalId};

use crate::builtins::{BUILTINS, IrBuiltinForm, fn_direct_effects, inst_direct_effects};
use crate::ids::{GlobalId, StructId};
use crate::model::{Binop, FnSig, Inst, IrFunction, LocalDecl, Unop};

/// 对照表 ↔ runtime 注册表：名字/下标双向一致，且注册表恰 20 项。
#[test]
fn table_matches_runtime_registry() {
    let reg = lang_runtime::BuiltinRegistry::core();
    for entry in BUILTINS {
        assert_eq!(
            reg.lookup(entry.name),
            Some(lang_runtime::BuiltinId(entry.runtime_id)),
            "{} 的 runtime_id 漂移",
            entry.name
        );
        assert_eq!(
            reg.name(lang_runtime::BuiltinId(entry.runtime_id)),
            Ok(entry.name),
            "BuiltinId({}) 反查名字漂移",
            entry.runtime_id
        );
    }
    // 注册表规模恰 20：id 0..=19 全部可反查，20 越界（防 runtime 静默加名）。
    for id in 0..20u32 {
        assert!(reg.name(lang_runtime::BuiltinId(id)).is_ok());
    }
    assert!(reg.name(lang_runtime::BuiltinId(20)).is_err());
}

/// 对照表效果列 ↔ lang_contract 映射逐项一致。
#[test]
fn table_effects_match_contract_mapping() {
    for entry in BUILTINS {
        assert_eq!(
            entry.direct_effect,
            lang_contract::builtin_direct_effect(entry.name),
            "{} 的效果列与 lang_contract 映射不一致",
            entry.name
        );
    }
}

/// 表规模与形态分布：20 名唯一；恰 makeChan/send/recv 为专指令，其余 17 名 Call。
#[test]
fn table_shape_and_ir_forms() {
    assert_eq!(BUILTINS.len(), 20);
    let mut names: Vec<&str> = BUILTINS.iter().map(|e| e.name).collect();
    names.sort_unstable();
    let unique = names.len();
    names.dedup();
    assert_eq!(unique, names.len(), "对照表名字重复");
    for entry in BUILTINS {
        let want_inst = matches!(entry.name, "makeChan" | "send" | "recv");
        assert_eq!(
            entry.ir_form == IrBuiltinForm::Inst,
            want_inst,
            "{} 的 IR 形态漂移",
            entry.name
        );
    }
    let call_count = BUILTINS
        .iter()
        .filter(|e| e.ir_form == IrBuiltinForm::Call)
        .count();
    assert_eq!(call_count, 17);
}

/// 每条带效果指令的映射：Spawn→SPAWN、chan 三专指令→CHANNEL、
/// CallBuiltin 按映射（含 IO/NET/CHANNEL 与纯名）。
#[test]
fn effect_insts_map_to_effects() {
    assert_eq!(
        inst_direct_effects(&Inst::Spawn {
            func: FunctionId(0),
            argc: 0
        }),
        vec![Effect::Spawn]
    );
    for inst in [Inst::ChanNew, Inst::ChanSend, Inst::ChanRecv] {
        assert_eq!(inst_direct_effects(&inst), vec![Effect::Channel]);
    }
    let cb = |name: &str, argc, has_ret| Inst::CallBuiltin {
        name: name.to_string(),
        argc,
        has_ret,
    };
    assert_eq!(
        inst_direct_effects(&cb("println", 1, false)),
        vec![Effect::Io]
    );
    assert_eq!(
        inst_direct_effects(&cb("print", 1, false)),
        vec![Effect::Io]
    );
    assert_eq!(
        inst_direct_effects(&cb("http.get", 1, true)),
        vec![Effect::Net]
    );
    assert_eq!(
        inst_direct_effects(&cb("makeChan", 0, true)),
        vec![Effect::Channel]
    );
    assert_eq!(
        inst_direct_effects(&cb("send", 2, false)),
        vec![Effect::Channel]
    );
    assert_eq!(
        inst_direct_effects(&cb("recv", 1, true)),
        vec![Effect::Channel]
    );
    // 纯名与未知名（宿主扩展面）都为空。
    assert!(inst_direct_effects(&cb("len", 1, true)).is_empty());
    assert!(inst_direct_effects(&cb("no.suchBuiltin", 0, false)).is_empty());
}

/// 无效果指令逐条枚举全为零（活文档：新增 Inst 变体时提醒核对该映射）。
#[test]
fn effectless_insts_return_empty() {
    let cases: Vec<Inst> = vec![
        Inst::ConstI64(1),
        Inst::ConstF64(1.5),
        Inst::ConstBool(true),
        Inst::ConstStr("s".into()),
        Inst::LocalGet(LocalId(0)),
        Inst::LocalSet(LocalId(0)),
        Inst::GlobalGet(GlobalId(0)),
        Inst::GlobalSet(GlobalId(0)),
        Inst::Unary(Unop::Neg),
        Inst::Binary(Binop::Add),
        Inst::Pop,
        Inst::Call(FunctionId(0)),
        Inst::CallIndirect {
            argc: 1,
            has_ret: true,
        },
        Inst::ClosureNew {
            func: FunctionId(0),
            captures: 0,
        },
        Inst::IndexGet,
        Inst::IndexSet,
        Inst::SliceGet,
        Inst::FieldGet { field: "f".into() },
        Inst::FieldSet { field: "f".into() },
        Inst::ArrayPush,
        Inst::ArrayNew { count: 1 },
        Inst::MapNew { count: 1 },
        Inst::StructNew {
            struct_id: StructId(0),
        },
        Inst::EnumNew {
            enum_id: crate::ids::EnumId(0),
            case_id: 0,
        },
        Inst::RangeNew,
        Inst::BoxNew,
        Inst::Deref,
        Inst::RefSet,
        Inst::EnumPayloadGet { index: 0 },
        Inst::IterNew,
        Inst::IterNext {
            done: crate::ids::BlockId(0),
        },
        Inst::Try {
            err: LocalId(0),
            fail: crate::ids::BlockId(0),
        },
    ];
    assert_eq!(
        cases.len(),
        32,
        "37 条 Inst 变体 - 5 条带效果（Spawn/Chan 三件套/CallBuiltin）= 32 条无效果全列"
    );
    for inst in cases {
        assert!(
            inst_direct_effects(&inst).is_empty(),
            "{inst:?} 不应有直接效果"
        );
    }
}

/// 函数级：extern 声明 → FFI；普通函数空（体内指令效果由调用方逐条累积）。
#[test]
fn fn_direct_effects_marks_extern_ffi() {
    let plain = IrFunction {
        name: "f".into(),
        capture_count: 0,
        sig: FnSig {
            param_count: 0,
            has_ret: false,
        },
        locals: Vec::<LocalDecl>::new(),
        blocks: vec![],
        extern_host: None,
    };
    let ext = IrFunction {
        extern_host: Some("fs.read".into()),
        ..plain.clone()
    };
    assert!(fn_direct_effects(&plain).is_empty());
    assert_eq!(fn_direct_effects(&ext), vec![Effect::Ffi]);
}
