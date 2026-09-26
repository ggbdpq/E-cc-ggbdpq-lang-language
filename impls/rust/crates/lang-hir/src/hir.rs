//! Typed HIR（语义事实层，第三层）：AST 降糖、已解析、已标注类型的语义树。
//!
//! 每个重要节点携带 [`NodeInfo`]：`NodeId`（编译内稳定）、`Span`（列级，出 dump hir 用）、
//! 可选 `SymbolId`（resolver 结论）、可选 `TypeId`（checker 结论）。
//! HIR 表达程序的语义事实；后端与 IR 消费结论，**禁止重新推断**，不再回读 AST。
//!
//! HIR 消除的语法糖（与 Go/TS 参考实现一致）：
//! 裸构造器引用 → [`HirExpr::Ctor`]（0 参）、`obj.m(args)` → [`HirExpr::MethodCall`]、
//! `go f(args)` → [`HirStmt::Spawn`]、`?` → [`HirExpr::Try`]（checker 已约束位置）。
//!
//! 本 crate 自带归一化的算符枚举（[`UnaryOp`]/[`BinaryOp`]/[`AssignOp`]/[`MatchKind`]），
//! 不依赖 lang-syntax 的 token 形态：HIR 是语义层，算符在此归一为封闭集合。
//! 本层不携带类型结构（只有 `TypeId` 句柄）——类型格由 lang-types 拥有。

use lang_core::ids::{NodeId, SymbolId, TypeId};
use lang_core::span::Span;

/// 节点元数据：所有重要 HIR 节点内嵌一份。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct NodeInfo {
    /// 编译内稳定节点编号（先序递增，由 lower 分配）。
    pub id: NodeId,
    /// 列级源码区间（继承自 AST 盖章；IR 不携带 span，契约层从这里取）。
    pub span: Span,
    /// resolver 结论：绑定的符号（无符号节点为 None）。
    pub sym: Option<SymbolId>,
    /// checker 结论：节点语义类型（void/未标注为 None）。
    pub type_id: Option<TypeId>,
}

impl NodeInfo {
    pub fn new(id: NodeId, span: Span) -> Self {
        Self {
            id,
            span,
            sym: None,
            type_id: None,
        }
    }

    pub fn with_sym(mut self, sym: SymbolId) -> Self {
        self.sym = Some(sym);
        self
    }

    pub fn with_type(mut self, type_id: TypeId) -> Self {
        self.type_id = Some(type_id);
        self
    }
}

// —— 程序 ——

/// 已标注的程序。`main` 为顶层 main 函数（可缺省）。
#[derive(Clone, PartialEq, Debug)]
pub struct HirProg {
    pub structs: Vec<HirStructDecl>,
    pub enums: Vec<HirEnumDecl>,
    /// 顶层 let/global 绑定，按声明序。
    pub globals: Vec<HirLet>,
    /// 函数/方法/extern 声明（方法 recv 非空）。
    pub fns: Vec<HirFn>,
    pub main: Option<Box<HirFn>>,
}

/// 函数/方法/extern 声明。
#[derive(Clone, PartialEq, Debug)]
pub struct HirFn {
    pub meta: NodeInfo,
    pub name: String,
    /// 值接收者（方法非空）。
    pub recv: Option<HirParam>,
    pub type_params: Vec<String>,
    pub params: Vec<HirParam>,
    /// 无注解（void）时为 None：决定 →Go 是否写返回类型。
    pub ret: Option<TypeId>,
    pub body: Vec<HirStmt>,
    pub is_extern: bool,
    /// extern 宿主名（`extern "go"` 的宿主侧）。
    pub extern_host: String,
}

/// 参数/接收者。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct HirParam {
    pub name: String,
    pub type_id: Option<TypeId>,
}

/// struct 声明。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct HirStructDecl {
    pub meta: NodeInfo,
    pub name: String,
    pub type_params: Vec<String>,
    pub fields: Vec<HirFieldDecl>,
}

#[derive(Clone, PartialEq, Eq, Debug)]
pub struct HirFieldDecl {
    pub name: String,
    pub type_id: Option<TypeId>,
}

/// enum 声明。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct HirEnumDecl {
    pub meta: NodeInfo,
    pub name: String,
    pub type_params: Vec<String>,
    pub cases: Vec<HirEnumCase>,
}

#[derive(Clone, PartialEq, Eq, Debug)]
pub struct HirEnumCase {
    /// case 符号（NAME-CTOR-005 全局唯一身份）。
    pub sym: Option<SymbolId>,
    pub name: String,
    pub payloads: Vec<Option<TypeId>>,
}

// —— 语句 ——

#[derive(Clone, PartialEq, Debug)]
pub enum HirStmt {
    Let(HirLet),
    Assign(HirAssign),
    /// 表达式语句：值被丢弃。
    Eval {
        meta: NodeInfo,
        expr: Box<HirExpr>,
    },
    Return {
        meta: NodeInfo,
        /// None = 无值返回（返回 `()`）。
        val: Option<Box<HirExpr>>,
    },
    If {
        meta: NodeInfo,
        cond: Box<HirExpr>,
        then: Vec<HirStmt>,
        els: Vec<HirStmt>,
    },
    While {
        meta: NodeInfo,
        cond: Box<HirExpr>,
        body: Vec<HirStmt>,
    },
    ForIn {
        meta: NodeInfo,
        variable: String,
        var_type: Option<TypeId>,
        iter: Box<HirExpr>,
        body: Vec<HirStmt>,
    },
    Break {
        meta: NodeInfo,
    },
    Continue {
        meta: NodeInfo,
    },
    /// `go f(args)`：目标已被 resolver 约束为顶层 fn。
    Spawn {
        meta: NodeInfo,
        /// 目标函数符号。
        func: Option<SymbolId>,
        args: Vec<HirExpr>,
    },
    /// 函数体内嵌套 fn 声明。
    FnStmt {
        meta: NodeInfo,
        func: Box<HirFn>,
    },
}

/// `let`/顶层 global 绑定。
#[derive(Clone, PartialEq, Debug)]
pub struct HirLet {
    pub meta: NodeInfo,
    pub name: String,
    pub mutable: bool,
    pub init: Box<HirExpr>,
}

/// 赋值/复合赋值。IR 侧会把复合赋值分解为读-算-写（见 lang-ir）。
#[derive(Clone, PartialEq, Debug)]
pub struct HirAssign {
    pub meta: NodeInfo,
    pub target: Box<HirExpr>,
    pub op: AssignOp,
    pub rhs: Box<HirExpr>,
}

// —— 表达式 ——

#[derive(Clone, PartialEq, Debug)]
pub enum HirExpr {
    Lit(HirLit),
    /// 局部/全局绑定读取（写目标复用此节点）。
    LocalRef {
        meta: NodeInfo,
        name: String,
    },
    /// 顶层 fn 引用（一等函数值）。
    FnRef {
        meta: NodeInfo,
        name: String,
    },
    /// 内建引用（println/len/…）。
    BuiltinRef {
        meta: NodeInfo,
        name: String,
    },
    /// 构造器调用（裸零参引用已脱糖为 0 参调用）。
    Ctor {
        meta: NodeInfo,
        /// 构造器 case 符号。
        ctor_sym: Option<SymbolId>,
        args: Vec<HirExpr>,
    },
    /// 用户 fn（func 符号）/ 内建（builtin 名）/ 表达式 callee（callee 节点）三选一。
    Call {
        meta: NodeInfo,
        func: Option<SymbolId>,
        builtin: Option<String>,
        callee: Option<Box<HirExpr>>,
        args: Vec<HirExpr>,
    },
    /// 方法调用（脱糖自 `obj.m(args)`）；method 为空时是字段函数值间接调用。
    MethodCall {
        meta: NodeInfo,
        recv: Box<HirExpr>,
        method: Option<SymbolId>,
        field: Option<String>,
        args: Vec<HirExpr>,
        /// 方法名（或字段名）token 区间，供 rename 消费。
        name_span: Span,
    },
    Unary {
        meta: NodeInfo,
        op: UnaryOp,
        expr: Box<HirExpr>,
    },
    Binary {
        meta: NodeInfo,
        op: BinaryOp,
        lhs: Box<HirExpr>,
        rhs: Box<HirExpr>,
    },
    /// `&&`（短路：左 false 不求值右，EVAL-SHORTCUT-006）。
    AndThen {
        meta: NodeInfo,
        lhs: Box<HirExpr>,
        rhs: Box<HirExpr>,
    },
    /// `||`（短路：左 true 不求值右）。
    OrElse {
        meta: NodeInfo,
        lhs: Box<HirExpr>,
        rhs: Box<HirExpr>,
    },
    Range {
        meta: NodeInfo,
        lo: Box<HirExpr>,
        hi: Box<HirExpr>,
    },
    Index {
        meta: NodeInfo,
        obj: Box<HirExpr>,
        idx: Box<HirExpr>,
    },
    Slice {
        meta: NodeInfo,
        obj: Box<HirExpr>,
        lo: Box<HirExpr>,
        hi: Box<HirExpr>,
    },
    Field {
        meta: NodeInfo,
        obj: Box<HirExpr>,
        name: String,
    },
    /// `?` 传播（checker 已约束：操作数为 Result、位置合法、宿主函数返回 Result）。
    Try {
        meta: NodeInfo,
        expr: Box<HirExpr>,
    },
    StructLit {
        meta: NodeInfo,
        /// struct 符号。
        struct_sym: Option<SymbolId>,
        fields: Vec<(String, HirExpr)>,
    },
    MapLit {
        meta: NodeInfo,
        /// 键值对按源码序（= 插入序，VALUE-MAP-006）。
        items: Vec<HirMapItem>,
    },
    ArrayLit {
        meta: NodeInfo,
        elems: Vec<HirExpr>,
    },
    Match {
        meta: NodeInfo,
        scrutinee: Box<HirExpr>,
        kind: MatchKind,
        arms: Vec<HirMatchArm>,
    },
    /// 匿名函数（闭包）。捕获集由语义期给出（VALUE-CAPTURE-004 按引用）。
    FnLit {
        meta: NodeInfo,
        type_params: Vec<String>,
        params: Vec<HirParam>,
        ret: Option<TypeId>,
        body: Vec<HirStmt>,
        /// 按引用捕获的绑定名（定义处作用域名）。
        captures: Vec<String>,
    },
}

/// 字面量（kind 区分有效载荷字段；与 TS/Go 参考实现同构）。
#[derive(Clone, PartialEq, Debug)]
pub struct HirLit {
    pub meta: NodeInfo,
    pub kind: LitKind,
    pub i64: i64,
    pub f64: f64,
    pub s: String,
    pub b: bool,
}

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum LitKind {
    Int,
    Float,
    Str,
    Bool,
}

#[derive(Clone, PartialEq, Debug)]
pub struct HirMapItem {
    pub key: HirExpr,
    pub val: HirExpr,
}

#[derive(Clone, PartialEq, Debug)]
pub struct HirMatchArm {
    pub pat: HirPat,
    pub body: Box<HirExpr>,
}

// —— 模式 ——

#[derive(Clone, PartialEq, Debug)]
pub enum HirPat {
    Wild,
    Bind {
        name: String,
        type_id: Option<TypeId>,
    },
    /// 字面量臂（int/string/bool）。
    Lit {
        lit: Box<HirLit>,
    },
    /// 构造器臂（enum/Option/Result），子模式按位置解构载荷。
    Ctor {
        ctor_sym: Option<SymbolId>,
        subs: Vec<HirPat>,
    },
}

// —— 归一算符（语义层封闭集合；A1 的 token 在 lowering 映射到此） ——

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum UnaryOp {
    Neg,
    Not,
    /// `&x`（仅变量，E3012；语义见 VALUE-REF-003）。
    Ref,
}

/// 二元算符 17 种。`&&`/`||` 不在此列——它们是 [`HirExpr::AndThen`]/[`HirExpr::OrElse`]。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum BinaryOp {
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

/// 赋值算符 5 种。复合赋值求值序见 EVAL-ASSIGN-003。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum AssignOp {
    Assign,
    AddAssign,
    SubAssign,
    MulAssign,
    DivAssign,
}

/// match 四分类（MATCH-KIND-001；混用形态由 checker 拒绝）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum MatchKind {
    Literal,
    Enum,
    Result,
    Option,
}

/// 表达式语义类型句柄（无类型节点为 None）。
pub fn type_of(expr: &HirExpr) -> Option<TypeId> {
    match expr {
        HirExpr::Lit(l) => l.meta.type_id,
        HirExpr::LocalRef { meta, .. }
        | HirExpr::FnRef { meta, .. }
        | HirExpr::BuiltinRef { meta, .. }
        | HirExpr::Ctor { meta, .. }
        | HirExpr::Call { meta, .. }
        | HirExpr::MethodCall { meta, .. }
        | HirExpr::Unary { meta, .. }
        | HirExpr::Binary { meta, .. }
        | HirExpr::AndThen { meta, .. }
        | HirExpr::OrElse { meta, .. }
        | HirExpr::Range { meta, .. }
        | HirExpr::Index { meta, .. }
        | HirExpr::Slice { meta, .. }
        | HirExpr::Field { meta, .. }
        | HirExpr::Try { meta, .. }
        | HirExpr::StructLit { meta, .. }
        | HirExpr::MapLit { meta, .. }
        | HirExpr::ArrayLit { meta, .. }
        | HirExpr::Match { meta, .. }
        | HirExpr::FnLit { meta, .. } => meta.type_id,
    }
}
