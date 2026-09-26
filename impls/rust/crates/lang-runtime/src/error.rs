//! 运行时错误模型（specs/runtime.md RUNTIME-FAIL-001）。
//!
//! 运行时错误 = 向 stderr 输出消息并以非零退出码终止；消息文本允许实现差异，
//! 因此分类（kind）是契约、message 是描述。语言级可恢复结果（如 recv 空返回
//! `Err("channel 空")` 值）不走本模型，见 `chan::RecvOutcome`。

/// 错误分类：与 RUNTIME-FAIL-001 分类表对齐；另含解释器操作面所需的运行时类别。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RtErrorKind {
    /// RUNTIME-INDEX-001：数组/字符串索引越界、切片端点非法、映射取不存在键。
    Index,
    /// RUNTIME-ARITH-002：整数除零/模零。
    Arith,
    /// RUNTIME-EQ-003：`==`/`!=` 作用于数组/映射/struct。
    Eq,
    /// RUNTIME-ASSERT-004：assert 条件为 false。
    Assert,
    /// RUNTIME-CHAN-001：send 到满 channel。
    ChanFull,
    /// RUNTIME-MATCH-001：字面量 match 无臂匹配。
    MatchNoArm,
    /// RUNTIME-TYPE-005：any 边界断言失败、内建实参类型不符等运行时类型错。
    TypeCast,
    /// RUNTIME-JSON-001：jsonParse 非法 JSON（宿主内建实现时使用）。
    JsonParse,
    /// 对 `let` 绑定赋值（静态检漏兜底；正常程序在静态期被 E 码拒绝）。
    ImmutableAssign,
    /// 对不支持迭代的值做迭代（E3010 的运行时面）。
    NotIterable,
    /// 作用域错误（重复定义等；正常程序在静态期被拒绝）。
    Scope,
    /// 由宿主（解释器/后端）实现的内建被路由到运行时调用口。
    UnsupportedHost,
    /// 内部不变量被破坏（非用户源码错误）。
    Internal,
}

/// 运行时错误：分类 + 消息。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RtError {
    kind: RtErrorKind,
    message: String,
}

impl RtError {
    pub fn new(kind: RtErrorKind, message: impl Into<String>) -> RtError {
        RtError {
            kind,
            message: message.into(),
        }
    }

    /// 内部不变量破坏（非用户源码错误）。
    pub fn internal(message: impl Into<String>) -> RtError {
        RtError {
            kind: RtErrorKind::Internal,
            message: message.into(),
        }
    }

    pub fn kind(&self) -> RtErrorKind {
        self.kind
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl std::fmt::Display for RtError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for RtError {}

pub type RtResult<T> = Result<T, RtError>;
