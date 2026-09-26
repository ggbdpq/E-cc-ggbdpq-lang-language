//! 稳定身份模型（计划 §R1）：编译器对外身份一律 newtype，禁止裸 usize/String 当身份。
//!
//! u32 内部索引 + arena 使用；`Display` 直出数字便于 JSON 契约输出。

/// 定义一个身份 newtype。
macro_rules! define_id {
    ($(#[$doc:meta])* $name:ident) => {
        $(#[$doc])*
        #[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
        pub struct $name(pub u32);

        impl From<$name> for usize {
            fn from(v: $name) -> usize {
                v.0 as usize
            }
        }

        impl std::fmt::Display for $name {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                write!(f, "{}", self.0)
            }
        }
    };
}

define_id!(
    /// 源文件身份（loader 分配；Span 经它反查路径）。
    SourceId
);
define_id!(
    /// 语法/语义节点身份（Agent Contract 的稳定范围载体）。
    NodeId
);
define_id!(
    /// 符号身份（resolver 分配；refactor/graph 消费）。
    SymbolId
);
define_id!(
    /// 类型身份（TypeArena 内索引）。
    TypeId
);
define_id!(
    /// 函数身份（HIR/IR/interp 共享的函数寻址）。
    FunctionId
);
define_id!(
    /// 局部变量槽位（IR/运行时帧内寻址）。
    LocalId
);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn display_prints_index() {
        assert_eq!(SymbolId(7).to_string(), "7");
        assert_eq!(usize::from(SourceId(0)), 0);
    }

    #[test]
    fn sorts_by_inner_index() {
        let mut v = vec![NodeId(3), NodeId(1), NodeId(2)];
        v.sort();
        assert_eq!(v, vec![NodeId(1), NodeId(2), NodeId(3)]);
    }
}
