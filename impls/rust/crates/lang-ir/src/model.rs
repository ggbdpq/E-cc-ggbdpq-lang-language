//! Core IR 模型（第四层）：HIR 降级而成的后端中立执行表示。
//!
//! 形状决策（本波冻结，见 status/A3-hir-ir.md 登记）：
//! - **arena/index 图**：函数表/块表/局部表全部扁平 `Vec` + newtype 索引，
//!   无自引用 struct、无 `Rc`/`Box` 树。
//! - **不含 span**（specs/source-span.md：IR 为精简执行视图；定位回 HIR 取）。
//! - **栈式 + 基本块**：每函数划分为块；块内指令操作操作数栈，
//!   块以显式终接指令（[`Terminator`]）收尾。块入口栈深恒为 0、终接后归零，
//!   因此 validator 可全静态线性校验栈效应（见 validate.rs）。
//! - **显式化**：求值顺序即指令线性序（callee 先、实参左到右）；
//!   `&&`/`||`/break/continue 降为 [`Terminator::Branch`]/[`Terminator::Goto`]；
//!   `?` 为 [`Inst::Try`]；chan send/recv/spawn 为专指令（非内建）。
//! - **引用与捕获**：被 `&` 取址或被闭包捕获的绑定按装箱纪律存储——
//!   槽位持有 Ref 值（[`Inst::BoxNew`] 创建；循环变量每轮重绑 = 每轮新 Box），
//!   读经 [`Inst::Deref`]、写经 [`Inst::RefSet`]（VALUE-REF-003/VALUE-CAPTURE-004）。
//!   闭包捕获以 Ref 值进 [`Inst::ClosureNew`]，在被调函数局部表前
//!   `capture_count` 个槽位落地（按引用共享）。

use lang_core::ids::{FunctionId, LocalId, TypeId};

use crate::ids::{BlockId, EnumId, GlobalId, StructId};

/// 后端可执行/可转译的程序。
#[derive(Clone, PartialEq, Debug)]
pub struct IrProgram {
    pub structs: Vec<StructDecl>,
    pub enums: Vec<EnumDecl>,
    /// 全局变量表（按声明序；初始化写在 `init` 函数体内，声明序 GlobalSet）。
    pub globals: Vec<GlobalDecl>,
    /// 函数表；`FunctionId` 即下标。含方法与 extern。
    pub functions: Vec<IrFunction>,
    /// 全局初始化函数（运行 main 前执行一次；无全局时可缺省）。
    pub init: Option<FunctionId>,
    /// 顶层 main（可缺省）。
    pub main: Option<FunctionId>,
}

/// struct 声明。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct StructDecl {
    pub name: String,
    pub type_params: Vec<String>,
    /// 字段按声明序（StructNew 按此序弹栈）。
    pub fields: Vec<FieldDecl>,
}

#[derive(Clone, PartialEq, Eq, Debug)]
pub struct FieldDecl {
    pub name: String,
    pub type_id: Option<TypeId>,
}

/// enum 声明。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct EnumDecl {
    pub name: String,
    pub type_params: Vec<String>,
    /// case 按声明序（SwitchEnum arms 与此平行；EnumNew 按此序弹载荷）。
    pub cases: Vec<EnumCase>,
}

#[derive(Clone, PartialEq, Eq, Debug)]
pub struct EnumCase {
    pub name: String,
    pub payload_types: Vec<Option<TypeId>>,
}

/// 全局变量声明。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct GlobalDecl {
    pub name: String,
    pub mutable: bool,
    pub type_id: Option<TypeId>,
}

/// 局部槽位声明。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct LocalDecl {
    pub name: String,
    pub mutable: bool,
    pub type_id: Option<TypeId>,
}

/// 函数签名要素（栈效应校验所需的最小集）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct FnSig {
    /// 参数个数。
    pub param_count: u16,
    /// 是否产生返回值（void = false）。
    pub has_ret: bool,
}

/// 函数/方法/extern/闭包体的 IR。
#[derive(Clone, PartialEq, Debug)]
pub struct IrFunction {
    pub name: String,
    /// 闭包捕获槽个数：`locals[0..capture_count)` 为捕获槽（按引用），
    /// `[capture_count..capture_count+param_count)` 为参数，其后是普通局部/临时。
    pub capture_count: u16,
    pub sig: FnSig,
    /// 全部局部槽位（捕获槽 + 参数 + 局部 + lowering 临时），按分配序。
    pub locals: Vec<LocalDecl>,
    /// 基本块；块 0 为入口。extern 函数必须为空。
    pub blocks: Vec<Block>,
    /// extern 宿主函数名；非 extern 为 None。
    pub extern_host: Option<String>,
}

impl IrFunction {
    /// 捕获槽 + 参数所需的槽位下界（validator 用于查 locals 表长度）。
    pub fn fixed_slots(&self) -> usize {
        usize::from(self.capture_count) + usize::from(self.sig.param_count)
    }
}

/// 基本块：线性指令 + 显式终接。入口栈深 0，终接后栈深 0。
#[derive(Clone, PartialEq, Debug)]
pub struct Block {
    pub insts: Vec<Inst>,
    pub term: Terminator,
}

// —— 指令 ——

/// 一元算符。`&` 不在此列：取址经装箱纪律表达（见模块文档）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum Unop {
    Neg,
    Not,
}

/// 二元算符 15 种。`&&`/`||` 不在此列（降为 Branch 控制流）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum Binop {
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Add,
    Sub,
    Mul,
    Div,
    Rem,
    BitAnd,
    BitOr,
    BitXor,
    Shl,
    Shr,
}

/// 栈式指令。注释给出「操作数栈弹压序」（弹序 = 压序逆序）与栈效应 `(in → out)`。
#[derive(Clone, PartialEq, Debug)]
pub enum Inst {
    // —— 常量（0 → 1）——
    /// int 字面量（i64 回绕语义）。
    ConstI64(i64),
    ConstF64(f64),
    ConstBool(bool),
    ConstStr(String),

    // —— 局部/全局读写 ——
    /// 读局部槽位（0 → 1）。
    LocalGet(LocalId),
    /// 写局部槽位（1 → 0）。
    LocalSet(LocalId),
    /// 读全局（0 → 1）。
    GlobalGet(GlobalId),
    /// 写全局（1 → 0）。
    GlobalSet(GlobalId),

    // —— 运算 ——
    /// 一元（1 → 1）。
    Unary(Unop),
    /// 二元：先压左后压右，弹右弹左（2 → 1）。
    Binary(Binop),
    /// 丢弃栈顶（1 → 0；表达式语句结果落栈用）。
    Pop,

    // —— 调用 ——
    /// 直接调用：实参按左到右压栈（in = 目标参数个数，out = 目标 has_ret）。
    Call(FunctionId),
    /// 闭包值调用：先压闭包值再压实参（in = argc + 1，out = 调用点声明 has_ret；
    /// 静态类型保证与闭包签名一致，运行时不符为内部错误）。
    CallIndirect {
        argc: u16,
        has_ret: bool,
    },
    /// 内建调用：实参左到右压栈（in = argc，out = 调用点声明 has_ret）。
    /// 内建身份为名（`push`/`println`/…；makeChan/send/recv 已升为专指令）。
    CallBuiltin {
        name: String,
        argc: u16,
        has_ret: bool,
    },
    /// `go f(args)`：实参在 go 语句处立即求值压栈；本指令启动 Task，返回值丢弃
    /// （in = argc，out = 0；argc 由 validator 对照目标签名）。
    Spawn {
        func: FunctionId,
        argc: u16,
    },
    /// 创建闭包：弹 `captures` 个 Ref 值作为捕获环境（按引用捕获，VALUE-CAPTURE-004）。
    /// `captures` 必须等于目标函数的 capture_count（validator 强制）。
    ClosureNew {
        func: FunctionId,
        captures: u16,
    },

    // —— 索引/切片/字段 ——
    /// `a[i]` 读：先压 obj 后压 idx（2 → 1；EVAL-INDEX-005）。
    IndexGet,
    /// `a[i] = v` 写回容器：压序 obj, idx, val（3 → 0；别名可见，VALUE-SHARE-002）。
    IndexSet,
    /// `a[i:j]`：压序 obj, lo, hi（3 → 1）。
    SliceGet,
    /// `obj.f` 读（1 → 1；字段以名寻址，后端需名映射）。
    FieldGet {
        field: String,
    },
    /// `obj.f = v`：压序 obj, val（2 → 0）。
    FieldSet {
        field: String,
    },
    /// 数组 `+=` 原位 push：压序 arr, val（2 → 0；别名可见，VALUE-SHARE-002）。
    ArrayPush,

    // —— 构造 ——
    /// 数组字面量：元素左到右压栈（in = count，out = 1）。
    ArrayNew {
        count: u32,
    },
    /// 映射字面量：键值对左到右压栈（压序 k1,v1,k2,v2,…；in = 2*count，out = 1；
    /// 插入序 = 压入对序，VALUE-MAP-006）。
    MapNew {
        count: u32,
    },
    /// struct 字面量：字段值按声明序压栈（in = 声明字段数，out = 1）。
    StructNew {
        struct_id: StructId,
    },
    /// enum 构造：载荷按 case 声明序压栈（in = case 载荷数，out = 1）。
    EnumNew {
        enum_id: EnumId,
        case_id: u16,
    },
    /// 区间 `lo..hi`：压序 lo, hi（2 → 1；半开）。
    RangeNew,

    // —— 引用/装箱 ——
    /// 新建 Ref 装入初值（1 → 1）。装箱纪律见模块文档。
    BoxNew,
    /// 读穿透：弹一值，Ref 则压入指称副本，非 Ref 原样回压（1 → 1）。
    Deref,
    /// 写穿透：压序 r#ref, val（2 → 0；所有别名可见）。
    RefSet,

    // —— match 分派取载荷 ——
    /// 取 enum 值第 `index` 个载荷：弹 enum 值，压载荷副本（1 → 1）。
    EnumPayloadGet {
        index: u16,
    },

    // —— for-in 迭代协议（显式化） ——
    /// 由可迭代对象建迭代器（1 → 1；数组索引序/映射插入序/字符串码点序/区间半开）。
    IterNew,
    /// 迭代下一步：弹迭代器，压下一元素（1 → 1）；耗尽则跳 `done`。
    /// 迭代器本体驻留局部槽（不跨块压栈——块入口栈深恒 0）；
    /// 循环变量每轮重绑由 lowering 逐轮 BoxNew 保证（CTRL-FORIN-003）。
    IterNext {
        done: BlockId,
    },

    // —— chan（专指令，非内建） ——
    /// `makeChan()`：缓冲 1024（语言常量，无操作数）（0 → 1）。
    ChanNew,
    /// `send(ch, v)`：压序 ch, val（2 → 0；恒产 `()`，满为运行时错误）。
    ChanSend,
    /// `recv(ch)`：弹 ch，压 `Result[T, error]`（1 → 1；FIFO；空 = Err）。
    ChanRecv,

    // —— `?` 传播 ——
    /// 弹 Result 值：`Ok(v)` 压 v 继续走本块（1 → 1）；
    /// `Err(e)` 将 e 存入 `err` 槽位并跳 `fail` 块（fail 块入口栈深 0，
    /// 从 `err` 槽读取错误值合成 `Err` 返回——函数边界传播，RESULT-TRY-003）。
    Try {
        err: LocalId,
        fail: BlockId,
    },
}

// —— 终接 ——

/// 块终接指令：消费栈顶后块结束，栈深归零；每个块恰一个。
#[derive(Clone, PartialEq, Debug)]
pub enum Terminator {
    /// 无条件跳转（0 → 0；break/continue 的降形）。
    Goto { target: BlockId },
    /// 条件分支：弹 bool（1 → 0）。
    Branch { if_true: BlockId, if_false: BlockId },
    /// 字面量 int match 分派：弹 int（1 → 0）；arms 依源码臂序，首个命中即跳。
    SwitchInt {
        arms: Vec<(i64, BlockId)>,
        default: Option<BlockId>,
    },
    /// enum/Option/Result match 分派：弹 enum 值（1 → 0）；
    /// `arms` 与 enum case 声明序平行（len 必须相等，validator 强制）；
    /// 穷尽 match 可省 default；无臂命中且无 default 为运行时错误（RUNTIME-MATCH-001）。
    SwitchEnum {
        enum_id: EnumId,
        arms: Vec<BlockId>,
        default: Option<BlockId>,
    },
    /// 函数返回（in = has_value；无值即返回 `()`）。
    Return { has_value: bool },
    /// 不可达锚（穷尽 match 兜底；执行到即内部错误）。
    Unreachable,
}

// 注：字符串/bool 字面量 match 分派不设专用 SwitchStr/SwitchBool——
// 字符串臂经 `Binary(Eq)` 链 + Branch 降级（被测值先入临时槽），bool 同理。
