//! IR 内建三方对照表 + 直接效果提取（A3↔A4↔A6 对接胶水）。
//!
//! 三方事实源（本模块不新造事实，只把三方钉在同一行）：
//! 1. **runtime 名单**：`lang_runtime::BuiltinRegistry::core()` 冻结 20 名，
//!    `BuiltinId` = 注册序下标（0..=19）；
//! 2. **效果映射**：`lang_contract::builtin_direct_effect`（println/print→IO、
//!    http.get→NET、makeChan/send/recv→CHANNEL；映射未列出的名 = 纯）；
//! 3. **IR 形态**：makeChan/send/recv 已升为专指令
//!    （[`crate::Inst::ChanNew`]/[`crate::Inst::ChanSend`]/[`crate::Inst::ChanRecv`]），
//!    其余 17 名走 [`crate::Inst::CallBuiltin`]（`name` 与注册表名字面逐字节一致）。
//!
//! [`BUILTINS`] 一行 = 一个内建的三方约定；测试
//! （builtins_tests.rs）经 runtime 公共 API（`lookup`/`name`）与 lang_contract
//! 映射逐项断言一致——三方各自演进时，此处测试先红。
//!
//! 决策：`CallBuiltin.name` 不做名单硬校验（validator 放行任意名）——宿主可经
//! `BuiltinRegistry::register_host` 扩展内建（如 http.get），效果提取按
//! lang_contract 映射计算，未知名缺省视为纯（保守正确）。
//!
//! http.get 说明：它不在 core 注册表 20 名内（宿主经 `register_host` 登记，
//! Host 类别），但 lang_contract 的效果映射含 http.get→NET；IR 出现
//! `CallBuiltin{"http.get"}` 时效果提取给出 NET，无需本表登记。

use lang_contract::Effect;

use crate::model::{Inst, IrFunction};

/// 内建在 IR 侧的落地形态。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum IrBuiltinForm {
    /// `Inst::CallBuiltin { name, .. }`。
    Call,
    /// 已升为专指令（makeChan→ChanNew、send→ChanSend、recv→ChanRecv）。
    Inst,
}

/// 对照表条目：「IR 内建名 ↔ runtime BuiltinId ↔ 直接效果」三方锁定。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct BuiltinEntry {
    /// 内建名（`CallBuiltin.name` 与 runtime 注册名逐字节一致）。
    pub name: &'static str,
    /// `BuiltinRegistry::core()` 中的下标（`BuiltinId`）。
    pub runtime_id: u32,
    /// 直接效果（None = 纯）；与 `lang_contract::builtin_direct_effect` 一致。
    pub direct_effect: Option<Effect>,
    /// IR 侧落地形态。
    pub ir_form: IrBuiltinForm,
}

/// 内建三方对照表：runtime core 注册表全量 20 名，顺序 = core() 注册序
/// （即 `BuiltinId` 序）。IR 侧合法 `CallBuiltin` 名 = 表中 `ir_form == Call` 的 17 名。
pub const BUILTINS: &[BuiltinEntry] = &[
    BuiltinEntry {
        name: "println",
        runtime_id: 0,
        direct_effect: Some(Effect::Io),
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "print",
        runtime_id: 1,
        direct_effect: Some(Effect::Io),
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "len",
        runtime_id: 2,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "str",
        runtime_id: 3,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "int",
        runtime_id: 4,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "float",
        runtime_id: 5,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "keys",
        runtime_id: 6,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "values",
        runtime_id: 7,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "has",
        runtime_id: 8,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "push",
        runtime_id: 9,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "assert",
        runtime_id: 10,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "err",
        runtime_id: 11,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "bytes",
        runtime_id: 12,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "char",
        runtime_id: 13,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "makeChan",
        runtime_id: 14,
        direct_effect: Some(Effect::Channel),
        ir_form: IrBuiltinForm::Inst,
    },
    BuiltinEntry {
        name: "send",
        runtime_id: 15,
        direct_effect: Some(Effect::Channel),
        ir_form: IrBuiltinForm::Inst,
    },
    BuiltinEntry {
        name: "recv",
        runtime_id: 16,
        direct_effect: Some(Effect::Channel),
        ir_form: IrBuiltinForm::Inst,
    },
    BuiltinEntry {
        name: "isNone",
        runtime_id: 17,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "jsonParse",
        runtime_id: 18,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
    BuiltinEntry {
        name: "jsonStringify",
        runtime_id: 19,
        direct_effect: None,
        ir_form: IrBuiltinForm::Call,
    },
];

/// 一条指令的直接效果集（specs/agent-protocol.md §3 推导规则的 IR 侧提取；
/// effects 引擎消费方：`lang_contract::CallGraph::set_direct`）。
///
/// 映射（每条指令至多一个直接效果）：
/// - [`Inst::Spawn`] → SPAWN（go 语句；被调效果沿调用边另行保守传播）；
/// - [`Inst::ChanNew`]/[`Inst::ChanSend`]/[`Inst::ChanRecv`] → CHANNEL
///   （makeChan/send/recv 的专指令形态）；
/// - [`Inst::CallBuiltin{name}`] → `lang_contract::builtin_direct_effect(name)`
///   （未列出/纯名 = 空）；
/// - 其余指令 → 空。
///
/// 新增效果面指令必须在此登记；`_` 缺省为纯（保守缺省：漏登记不会虚增效果）。
pub fn inst_direct_effects(inst: &Inst) -> Vec<Effect> {
    match inst {
        Inst::Spawn { .. } => vec![Effect::Spawn],
        Inst::ChanNew | Inst::ChanSend | Inst::ChanRecv => vec![Effect::Channel],
        Inst::CallBuiltin { name, .. } => lang_contract::builtin_direct_effect(name)
            .into_iter()
            .collect(),
        _ => Vec::new(),
    }
}

/// 函数声明的直接效果（IR 结构形状，非指令）：extern 声明 → FFI。
///
/// 普通函数返回空——其函数体内指令的直接效果由调用方遍历
/// `blocks[*].insts` 逐条取 [`inst_direct_effects`] 累积。
pub fn fn_direct_effects(func: &IrFunction) -> Vec<Effect> {
    match func.extern_host {
        Some(_) => vec![Effect::Ffi],
        None => Vec::new(),
    }
}
