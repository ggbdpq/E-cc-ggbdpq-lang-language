//! lang-syntax —— lang-rs 词法/语法前端（接口契约见 docs/lang-rs/interface-contract.md §3）。
//!
//! 分层：
//! - [`token`]：TokenKind 全列表（64 类）+ 中文契约展示名 + `Token { kind, lit, span }`；
//! - [`lexer`]：码点扫描、注释/字符串/转义/续行（LEX-001..008），错误 = E1001 诊断；
//! - [`trivia`]：无损层（注释/空白/换行全保留，供 formatter 与安全编辑消费）；
//! - [`ast`]：Expr 18 / Pat 4 / TypeEx 5 / Stmt 15 + 运算符枚举，节点带码点 span；
//! - [`parser`]：recursive descent + Pratt（PARSE-001..006），错误 = E1001 诊断。
//!
//! 对外 span 一律 0 基 Unicode 码点、半开区间（specs/source-span.md）；
//! 本 crate 禁 panic 处理用户源码错误，词法/解析失败以 `lang_core::Diagnostic` 返回。
#![forbid(unsafe_code)]

pub mod ast;
pub mod lexer;
pub mod parser;
pub mod token;
pub mod trivia;

mod unicode_tables;

pub use ast::{
    Arm, AssignOp, BinaryOp, EnumCase, Expr, FieldLit, MapItem, Param, Pat, Stmt, TypeEx, UnaryOp,
};
pub use lexer::lex;
pub use parser::{parse, parse_tokens};
pub use token::{Token, TokenKind, is_cont, keyword, kind_name};
pub use trivia::{Trivia, TriviaKind, scan_trivia};
