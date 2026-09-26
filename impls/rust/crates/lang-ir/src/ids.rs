//! IR 内部身份 newtype（arena 索引）。
//!
//! lang-core 冻结集之外、IR 图内部寻址所需的 id 在此定义；
//! 形状与 lang-core 的 define_id 同构（newtype(u32) + Display 直出数字）。

/// 定义一个 IR 内部身份 newtype。
macro_rules! define_ir_id {
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

define_ir_id!(
    /// 基本块编号（函数内；块 0 为入口块）。
    BlockId
);
define_ir_id!(
    /// 全局变量编号（程序级；按声明序）。
    GlobalId
);
define_ir_id!(
    /// struct 声明编号（程序级）。
    StructId
);
define_ir_id!(
    /// enum 声明编号（程序级；case 以声明序 u16 索引）。
    EnumId
);
