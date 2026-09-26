//! lang-backend-js —— →JS 转译后端。
//!
//! 面向消费方的三层：
//! - [`fmt`]：输出格式化规则层（形状与 →Go 同构；嵌套字符串走 JSON.stringify 语义）；
//! - [`json`]：jsonStringify 序列化面（与 lang_backend_go::json 同文，硬契约）；
//! - [`codegen`]：Core IR → 自包含可执行 ES Module（prelude 内联，`node <file>.mjs`
//!   直接可用），prelude 文本见 [`prelude`]。
#![forbid(unsafe_code)]

pub mod codegen;
pub mod fmt;
pub mod json;
pub mod prelude;
