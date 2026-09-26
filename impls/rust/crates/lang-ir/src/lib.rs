//! lang-ir —— Core IR（后端中立的执行表示，第四层）。
//!
//! 形状：arena/index 图 + 栈式基本块 + 显式终接；不含 span；
//! 求值顺序即指令线性序。消费方：解释器、→Go/→JS 后端、effects 推导。
//! 指令集清单见 status/A3-hir-ir.md「已冻结接口登记」。
#![forbid(unsafe_code)]

pub mod builtins;
pub mod compile;
pub mod dump;
pub mod ids;
pub mod lower;
pub mod model;
pub mod validate;

#[cfg(test)]
mod builtins_tests;
#[cfg(test)]
mod compile_tests;
#[cfg(test)]
mod dump_tests;
#[cfg(test)]
mod lower_tests;
#[cfg(test)]
mod validate_tests;

pub use builtins::{BUILTINS, BuiltinEntry, IrBuiltinForm, fn_direct_effects, inst_direct_effects};
pub use compile::{CompileError, compile};
pub use dump::{dump_function, dump_program};
pub use ids::{BlockId, EnumId, GlobalId, StructId};
pub use lower::{EntryPoints, IrLowerer, LowerError, LowerIr};
pub use model::{
    Binop, Block, EnumCase, EnumDecl, FieldDecl, FnSig, GlobalDecl, Inst, IrFunction, IrProgram,
    LocalDecl, StructDecl, Terminator, Unop,
};
pub use validate::{IrError, validate};
