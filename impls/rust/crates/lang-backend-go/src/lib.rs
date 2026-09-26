//! lang-backend-go —— →Go 转译后端。
//!
//! 面向消费方的三层：
//! - [`fmt`]：输出格式化规则层（RUNTIME-OUTPUT-003/004，打印面）；
//! - [`json`]：jsonStringify 序列化面（RUNTIME-JSON-002 硬契约）；
//! - [`codegen`]：Core IR → 自包含可编译 Go 源码（prelude 内联，`go build` 直接可用），
//!   prelude 文本见 [`prelude`]。
#![forbid(unsafe_code)]

pub mod codegen;
pub mod fmt;
pub mod json;
pub mod prelude;
