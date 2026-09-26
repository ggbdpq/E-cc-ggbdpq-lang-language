//! struct/enum 定义表：运行时只消费「输出与相等判定」所需的最小定义面。
//!
//! 方法分派在静态期完成（CALL-RESOLVE-001），故运行时定义表不含方法；
//! 定义登记由前级（语义/IR 层）在装载时完成，id 顺序稳定。

/// struct 定义身份。预载 `error` 结构固定为 0 号。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct StructDefId(pub u32);

impl From<StructDefId> for usize {
    fn from(v: StructDefId) -> usize {
        v.0 as usize
    }
}

/// enum 定义身份。预载 `Option` 固定为 0 号、`Result` 固定为 1 号。
///
/// enum 相等要求 def 一致（evidence：`y.Def != x.Def → false`），
/// 因此身份即语义，不做名字比较。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct EnumDefId(pub u32);

impl From<EnumDefId> for usize {
    fn from(v: EnumDefId) -> usize {
        v.0 as usize
    }
}

/// struct 定义：名字 + 字段名表（输出 `Name{field: value}` 用）。
#[derive(Clone, Debug)]
pub struct StructDef {
    pub name: Rc<str>,
    pub fields: Rc<[String]>,
}

/// enum 定义：名字 + case 名表（输出 `Tag(载荷)` 用）。
#[derive(Clone, Debug)]
pub struct EnumDef {
    pub name: Rc<str>,
    pub cases: Rc<[String]>,
}

use std::rc::Rc;

use crate::error::{RtError, RtResult};

/// 定义表。`with_prelude` 预载语言内建类型（specs/option-result.md）：
/// `enum Option[T] { Some(T) None }`、`enum Result[T, E] { Ok(T) Err(E) }`、
/// `struct error { msg: string }`（`err(msg)` 构造，业务错误统一 `Result[T, error]`）。
#[derive(Debug)]
pub struct DefTables {
    structs: Vec<StructDef>,
    enums: Vec<EnumDef>,
    error_def: StructDefId,
    option_def: EnumDefId,
    result_def: EnumDefId,
}

impl DefTables {
    /// 空表 + 预载定义。id 分配顺序为契约的一部分：
    /// `error` → `StructDefId(0)`，`Option` → `EnumDefId(0)`，`Result` → `EnumDefId(1)`。
    pub fn with_prelude() -> DefTables {
        let mut t = DefTables {
            structs: Vec::new(),
            enums: Vec::new(),
            error_def: StructDefId(0),
            option_def: EnumDefId(0),
            result_def: EnumDefId(1),
        };
        t.error_def = t.register_struct("error", vec!["msg".to_string()]);
        t.option_def = t.register_enum("Option", vec!["Some".to_string(), "None".to_string()]);
        t.result_def = t.register_enum("Result", vec!["Ok".to_string(), "Err".to_string()]);
        t
    }

    /// 登记 struct 定义（重复定义的静态拒绝在语义层，运行时只登记）。
    pub fn register_struct(&mut self, name: &str, fields: Vec<String>) -> StructDefId {
        let id = StructDefId(self.structs.len() as u32);
        self.structs.push(StructDef {
            name: Rc::from(name),
            fields: fields.into(),
        });
        id
    }

    /// 登记 enum 定义。
    pub fn register_enum(&mut self, name: &str, cases: Vec<String>) -> EnumDefId {
        let id = EnumDefId(self.enums.len() as u32);
        self.enums.push(EnumDef {
            name: Rc::from(name),
            cases: cases.into(),
        });
        id
    }

    /// 读取 struct 定义。越界 = 内部不变量破坏（id 只能由 `register_struct` 产生）。
    pub fn struct_def(&self, id: StructDefId) -> RtResult<&StructDef> {
        self.structs
            .get(usize::from(id))
            .ok_or_else(|| RtError::internal(format!("StructDefId({}) 越界", usize::from(id))))
    }

    /// 读取 enum 定义。越界 = 内部不变量破坏。
    pub fn enum_def(&self, id: EnumDefId) -> RtResult<&EnumDef> {
        self.enums
            .get(usize::from(id))
            .ok_or_else(|| RtError::internal(format!("EnumDefId({}) 越界", usize::from(id))))
    }

    /// 预载 `error` struct（`err(msg)` 构造用）。
    pub fn error_def(&self) -> StructDefId {
        self.error_def
    }

    /// 预载 `Option` enum。
    pub fn option_def(&self) -> EnumDefId {
        self.option_def
    }

    /// 预载 `Result` enum。
    pub fn result_def(&self) -> EnumDefId {
        self.result_def
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prelude_ids_are_stable() {
        let defs = DefTables::with_prelude();
        assert_eq!(defs.error_def(), StructDefId(0));
        assert_eq!(defs.option_def(), EnumDefId(0));
        assert_eq!(defs.result_def(), EnumDefId(1));
        // 手算真值：Option 恰有 Some/None 两 case，None 为 1 号（isNone 语义依赖此序）。
        assert_eq!(
            defs.enum_def(defs.option_def()).unwrap().cases.as_ref(),
            ["Some", "None"]
        );
        assert_eq!(
            defs.enum_def(defs.result_def()).unwrap().cases.as_ref(),
            ["Ok", "Err"]
        );
        assert_eq!(
            defs.struct_def(defs.error_def()).unwrap().fields.as_ref(),
            ["msg"]
        );
    }

    #[test]
    fn register_appends_stable_ids() {
        let mut defs = DefTables::with_prelude();
        let s = defs.register_struct("P", vec!["v".to_string()]);
        let e = defs.register_enum("Color", vec!["Red".to_string()]);
        assert_eq!(s, StructDefId(1));
        assert_eq!(e, EnumDefId(2));
        assert_eq!(defs.struct_def(s).unwrap().name.as_ref(), "P");
        assert_eq!(defs.enum_def(e).unwrap().name.as_ref(), "Color");
    }
}
