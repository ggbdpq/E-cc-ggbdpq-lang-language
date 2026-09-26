//! 抽象语法树（specs/grammar.md PARSE-001..006，接口契约 §3）。
//!
//! 变体全集：Expr 18 / Pat 4 / TypeEx 5 / Stmt 15 + UnaryOp/BinaryOp/AssignOp。
//! 每个节点带 `span: Span`（0 基码点、半开区间）；FnLit/FnDecl/StructDecl/EnumDecl
//! 带 `type_params: Vec<String>`；FnDecl 带 `recv: Option<Param>`。
//! 与 TS 证据的差异（契约强制的超集）：TS 中 Arm/EnumCase 的 span 为零值、
//! TypeEx/Param 无 span——本实现一律填真实 span。

use lang_core::Span;

// —— 运算符 ——

/// 一元运算符：`- ! &`。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum UnaryOp {
    Neg,
    Not,
    Ref,
}

/// 二元运算符（18 个；契约文本「17 个」为笔误，与 TS ast.ts 全列表一致）。
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
    AndAnd,
    OrOr,
}

/// 赋值运算符（5 个）：`= += -= *= /=`。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum AssignOp {
    Assign,
    AddAssign,
    SubAssign,
    MulAssign,
    DivAssign,
}

// —— 表达式（18） ——

/// 结构体字面量字段：`name: 值`。
#[derive(Clone, PartialEq, Debug)]
pub struct FieldLit {
    pub span: Span,
    pub name: String,
    pub value: Expr,
}

/// 映射字面量键值对：`key: val`。
#[derive(Clone, PartialEq, Debug)]
pub struct MapItem {
    pub span: Span,
    pub key: Expr,
    pub value: Expr,
}

/// match 臂：`模式 => 表达式`。
#[derive(Clone, PartialEq, Debug)]
pub struct Arm {
    pub span: Span,
    pub pat: Pat,
    pub body: Expr,
}

/// 表达式（18 变体，exhaustive match）。
#[derive(Clone, PartialEq, Debug)]
pub enum Expr {
    /// 整数字面量（i64，类型恒 int）。
    IntLit { span: Span, value: i64 },
    /// 浮点字面量（f64，类型恒 float）。
    FloatLit { span: Span, value: f64 },
    /// 字符串字面量（解码后的值；不可变，按码点索引）。
    StrLit { span: Span, value: String },
    /// 布尔字面量。
    BoolLit { span: Span, value: bool },
    /// 标识符引用。
    Ident { span: Span, name: String },
    /// 一元 `- ! &`。
    Unary {
        span: Span,
        op: UnaryOp,
        operand: Box<Expr>,
    },
    /// 二元运算。
    Binary {
        span: Span,
        op: BinaryOp,
        lhs: Box<Expr>,
        rhs: Box<Expr>,
    },
    /// 区间 `lo..hi`（半开）。
    Range {
        span: Span,
        lo: Box<Expr>,
        hi: Box<Expr>,
    },
    /// 调用 `callee(args)`。
    Call {
        span: Span,
        callee: Box<Expr>,
        args: Vec<Expr>,
    },
    /// 索引 `obj[idx]`。
    Index {
        span: Span,
        obj: Box<Expr>,
        idx: Box<Expr>,
    },
    /// 切片 `obj[lo:hi]`（lo/hi 必填，v0 无开区间切片）。
    Slice {
        span: Span,
        obj: Box<Expr>,
        lo: Box<Expr>,
        hi: Box<Expr>,
    },
    /// 字段访问 `obj.name`。
    Field {
        span: Span,
        obj: Box<Expr>,
        name: String,
    },
    /// `e?` —— Result 传播。
    Try { span: Span, operand: Box<Expr> },
    /// 结构体字面量 `Type{...}`。
    StructLit {
        span: Span,
        type_name: String,
        fields: Vec<FieldLit>,
    },
    /// 映射字面量 `{k: v, ...}`。
    MapLit { span: Span, items: Vec<MapItem> },
    /// 数组字面量 `[a, b, ...]`。
    ArrLit { span: Span, items: Vec<Expr> },
    /// match 表达式（PARSE-005）。
    Match {
        span: Span,
        scrutinee: Box<Expr>,
        arms: Vec<Arm>,
    },
    /// 匿名函数表达式；name 恒为空串的 FnDecl 形状展开。
    FnLit {
        span: Span,
        type_params: Vec<String>,
        params: Vec<Param>,
        ret: Option<TypeEx>,
        body: Vec<Stmt>,
    },
}

impl Expr {
    /// 节点 span（起于首记号，止于末记号之后）。
    #[must_use]
    pub fn span(&self) -> Span {
        match self {
            Expr::IntLit { span, .. }
            | Expr::FloatLit { span, .. }
            | Expr::StrLit { span, .. }
            | Expr::BoolLit { span, .. }
            | Expr::Ident { span, .. }
            | Expr::Unary { span, .. }
            | Expr::Binary { span, .. }
            | Expr::Range { span, .. }
            | Expr::Call { span, .. }
            | Expr::Index { span, .. }
            | Expr::Slice { span, .. }
            | Expr::Field { span, .. }
            | Expr::Try { span, .. }
            | Expr::StructLit { span, .. }
            | Expr::MapLit { span, .. }
            | Expr::ArrLit { span, .. }
            | Expr::Match { span, .. }
            | Expr::FnLit { span, .. } => *span,
        }
    }
}

// —— 模式（4） ——

/// 模式（PARSE-005：通配 / 绑定 / 字面量 / 构造）。
#[derive(Clone, PartialEq, Debug)]
pub enum Pat {
    /// `_` 通配。
    Wild { span: Span },
    /// 小写标识符绑定，捕获整个值。
    Bind { span: Span, name: String },
    /// 字面量模式（int/float/string/bool，`==` 语义比较）。
    Lit { span: Span, value: Expr },
    /// 构造模式：大写标识符，可带按位置的载荷子模式。
    Case {
        span: Span,
        name: String,
        subs: Vec<Pat>,
    },
}

impl Pat {
    /// 节点 span。
    #[must_use]
    pub fn span(&self) -> Span {
        match self {
            Pat::Wild { span }
            | Pat::Bind { span, .. }
            | Pat::Lit { span, .. }
            | Pat::Case { span, .. } => *span,
        }
    }
}

// —— 参数与类型注解（TypeEx 5） ——

/// 参数 / struct 字段 / 方法接收者共用的 `name: 类型` 形状。
#[derive(Clone, PartialEq, Debug)]
pub struct Param {
    pub span: Span,
    pub name: String,
    pub typ: Option<TypeEx>,
}

/// 类型注解（5 变体）。
#[derive(Clone, PartialEq, Debug)]
pub enum TypeEx {
    /// 命名类型，可带类型实参（`T`、`chan[int]`、`Result[int, string]`）。
    Named {
        span: Span,
        name: String,
        args: Vec<TypeEx>,
    },
    /// 数组类型 `[T]`。
    Arr { span: Span, elem: Box<TypeEx> },
    /// 映射类型 `map[K]V`。
    Map {
        span: Span,
        key: Box<TypeEx>,
        val: Box<TypeEx>,
    },
    /// 指针类型 `&T`。
    Ptr { span: Span, elem: Box<TypeEx> },
    /// 函数类型 `fn(A, B): R`。
    Fn {
        span: Span,
        params: Vec<TypeEx>,
        ret: Option<Box<TypeEx>>,
    },
}

impl TypeEx {
    /// 节点 span。
    #[must_use]
    pub fn span(&self) -> Span {
        match self {
            TypeEx::Named { span, .. }
            | TypeEx::Arr { span, .. }
            | TypeEx::Map { span, .. }
            | TypeEx::Ptr { span, .. }
            | TypeEx::Fn { span, .. } => *span,
        }
    }
}

// —— 语句与声明（15） ——

/// enum case：`Name(载荷类型...)`，载荷按位置。
#[derive(Clone, PartialEq, Debug)]
pub struct EnumCase {
    pub span: Span,
    pub name: String,
    pub payloads: Vec<TypeEx>,
}

/// 语句 / 顶层声明（15 变体，exhaustive match）。
#[derive(Clone, PartialEq, Debug)]
pub enum Stmt {
    /// `let x: T = e`（不可重绑）/ `var x = e`（可重绑）。
    Let {
        span: Span,
        name: String,
        mutable: bool,
        typ: Option<TypeEx>,
        init: Expr,
    },
    /// 赋值 / 复合赋值：目标只能是变量、字段链或索引。
    Assign {
        span: Span,
        target: Expr,
        op: AssignOp,
        value: Expr,
    },
    /// 表达式语句（求值后丢弃）。
    ExprStmt { span: Span, expr: Expr },
    /// `return` / `return e`（bare return 为 unit）。
    Return { span: Span, value: Option<Expr> },
    /// `if c {} else if c2 {} else {}`；else if 展开为单元素块。
    If {
        span: Span,
        cond: Expr,
        then_body: Vec<Stmt>,
        else_body: Option<Vec<Stmt>>,
    },
    /// `while c {}`。
    While {
        span: Span,
        cond: Expr,
        body: Vec<Stmt>,
    },
    /// `for x in iter {}`。
    ForIn {
        span: Span,
        variable: String,
        iter: Expr,
        body: Vec<Stmt>,
    },
    /// `break`。
    Break { span: Span },
    /// `continue`。
    Continue { span: Span },
    /// 函数 / 方法声明：`fn name[T](参数): 返回 { 体 }`、`fn (r Recv).name(...)`。
    FnDecl {
        span: Span,
        name: String,
        recv: Option<Param>,
        type_params: Vec<String>,
        params: Vec<Param>,
        ret: Option<TypeEx>,
        body: Vec<Stmt>,
    },
    /// struct 声明：字段逐行 `name: 类型`。
    StructDecl {
        span: Span,
        name: String,
        type_params: Vec<String>,
        fields: Vec<Param>,
    },
    /// enum 声明：case 逐行，载荷按位置。
    EnumDecl {
        span: Span,
        name: String,
        type_params: Vec<String>,
        cases: Vec<EnumCase>,
    },
    /// `go <具名函数调用>`（TASK-SPAWN-002；实参在 go 语句处求值）。
    Go { span: Span, call: Expr },
    /// `import "./md5"`。
    Import { span: Span, path: String },
    /// `extern "host" { fn 签名... }`：宿主函数手写签名（无函数体）。
    /// `fns` 每项恒为 `Stmt::FnDecl`（body 为空；TS 证据同构复用 FnDecl）。
    Extern {
        span: Span,
        host: String,
        fns: Vec<Stmt>,
    },
}

impl Stmt {
    /// 节点 span。
    #[must_use]
    pub fn span(&self) -> Span {
        match self {
            Stmt::Let { span, .. }
            | Stmt::Assign { span, .. }
            | Stmt::ExprStmt { span, .. }
            | Stmt::Return { span, .. }
            | Stmt::If { span, .. }
            | Stmt::While { span, .. }
            | Stmt::ForIn { span, .. }
            | Stmt::Break { span, .. }
            | Stmt::Continue { span, .. }
            | Stmt::FnDecl { span, .. }
            | Stmt::StructDecl { span, .. }
            | Stmt::EnumDecl { span, .. }
            | Stmt::Go { span, .. }
            | Stmt::Import { span, .. }
            | Stmt::Extern { span, .. } => *span,
        }
    }
}
