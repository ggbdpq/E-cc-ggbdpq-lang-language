//! 语义类型模型（specs/type-system.md TYPE-KINDS-001..TYPE-FN-004）。
//!
//! `TypeData` 是类型的结构描述，经 [`TypeTables`] intern 为稳定 [`TypeId`]；
//! struct/enum 是名义类型（TYPE-STRUCT-003）：身份由声明符号的 def id 决定，
//! 字段完全相同的两个 struct 不兼容。泛型类型参数是擦除占位（GEN-DECL-001）。
//!
//! 预载面（与 lang-runtime 冻结契约对齐，docs/lang-rs/status/A4-runtime.md）：
//! `error` = [`ERROR_DEF`](StructDefId 0)、`Option` = [`OPTION_DEF`](EnumDefId 0)、
//! `Result` = [`RESULT_DEF`](EnumDefId 1)。

use lang_core::TypeId;

/// 名义 struct 定义的身份（TypeTables::structs 的下标）。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub struct StructDefId(pub u32);

/// 名义 enum 定义的身份（TypeTables::enums 的下标）。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub struct EnumDefId(pub u32);

/// 预载 `error` struct 的 def id（A4 冻结：error=StructDefId(0)）。
pub const ERROR_DEF: StructDefId = StructDefId(0);
/// 预载 `Option` enum 的 def id（A4 冻结：Option=EnumDefId(0)）。
pub const OPTION_DEF: EnumDefId = EnumDefId(0);
/// 预载 `Result` enum 的 def id（A4 冻结：Result=EnumDefId(1)）。
pub const RESULT_DEF: EnumDefId = EnumDefId(1);

/// 预载 `error.msg` 字段名。
pub const ERROR_MSG_FIELD: &str = "msg";

/// 类型的结构描述（intern 键；[`TypeId`] 是它的 arena 身份）。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub enum TypeData {
    /// 错误传播占位（已有诊断，下游静默）。
    Invalid,
    /// `int`（64 位有符号）。
    Int,
    /// `float`（64 位）。
    Float,
    /// `bool`。
    Bool,
    /// `string`（不可变，按码点索引）。
    Str,
    /// 预载 `error` struct 的静态类型。
    Error,
    /// unit `()`。
    Unit,
    /// 区间 `lo..hi`（半开，int 端点）。
    Range,
    /// `any`：动态边界（jsonParse/extern），双向放行（TYPE-ANY-002）。
    Any,
    /// `Unknown`：推断不出的内部状态，保守放行，非逃生口（TYPE-ANY-002）。
    Unknown,
    /// 不可达/无值类型（保留）。
    Never,
    /// `[T]` 数组（引用语义）。
    Slice(TypeId),
    /// `map[K]V`（K ∈ int/string/bool，保插入序）。
    Map { key: TypeId, val: TypeId },
    /// `&T`（仅变量可取）。
    Ref(TypeId),
    /// `chan[T]`。
    Chan(TypeId),
    /// `fn(T...): R`（结构兼容，TYPE-FN-004）。
    Fn { params: Vec<TypeId>, ret: TypeId },
    /// 名义 struct。
    Struct(StructDefId),
    /// 名义 enum。
    Enum(EnumDefId),
    /// `Option[T]`（预载代数类型）。
    Option(TypeId),
    /// `Result[T, E]`（E 侧冻结为 error，见 GEN-SUBST-003）。
    Result { ok: TypeId, err: TypeId },
    /// 泛型类型参数（如 `fn[T]` 里的 `T`；擦除占位，彼此同一）。
    TypeParam { name: String },
}

/// struct 字段定义。
#[derive(Clone, Debug)]
pub struct FieldDef {
    pub name: String,
    pub ty: TypeId,
}

/// 名义 struct 定义。
#[derive(Clone, Debug)]
pub struct StructDef {
    pub name: String,
    pub fields: Vec<FieldDef>,
}

/// enum case 定义（载荷按位置）。
#[derive(Clone, Debug)]
pub struct EnumCaseDef {
    pub name: String,
    pub payloads: Vec<TypeId>,
}

/// 名义 enum 定义。
#[derive(Clone, Debug)]
pub struct EnumDef {
    pub name: String,
    pub cases: Vec<EnumCaseDef>,
}

/// 类型 arena：intern 结构类型 + 名义 def 表。
///
/// 预载单例占据 `TypeId(0..=10)`（顺序见 [`TypeTables::new`]），
/// 结构类型 intern 去重；struct/enum def 只增不减。
#[derive(Debug)]
pub struct TypeTables {
    data: Vec<TypeData>,
    structs: Vec<StructDef>,
    enums: Vec<EnumDef>,
}

impl Default for TypeTables {
    fn default() -> Self {
        Self::new()
    }
}

impl TypeTables {
    /// 建表并注册原始单例与预载 def（error/Option/Result）。
    pub fn new() -> Self {
        let mut t = Self {
            data: Vec::new(),
            structs: Vec::new(),
            enums: Vec::new(),
        };
        for d in [
            TypeData::Invalid,
            TypeData::Int,
            TypeData::Float,
            TypeData::Bool,
            TypeData::Str,
            TypeData::Error,
            TypeData::Unit,
            TypeData::Range,
            TypeData::Any,
            TypeData::Unknown,
            TypeData::Never,
        ] {
            t.data.push(d);
        }
        // 预载面（RESULT-PRELUDE-001）：id 与 lang-runtime DefTables 冻结一致
        t.structs.push(StructDef {
            name: "error".into(),
            fields: vec![FieldDef {
                name: ERROR_MSG_FIELD.into(),
                ty: Self::STR,
            }],
        });
        t.enums.push(EnumDef {
            name: "Option".into(),
            cases: vec![
                // 载荷类型随实例化变化：登记 Unknown 占位（与 A4 prelude 口径一致）
                EnumCaseDef {
                    name: "Some".into(),
                    payloads: vec![Self::UNKNOWN],
                },
                EnumCaseDef {
                    name: "None".into(),
                    payloads: vec![],
                },
            ],
        });
        t.enums.push(EnumDef {
            name: "Result".into(),
            cases: vec![
                EnumCaseDef {
                    name: "Ok".into(),
                    payloads: vec![Self::UNKNOWN],
                },
                EnumCaseDef {
                    name: "Err".into(),
                    payloads: vec![Self::UNKNOWN],
                },
            ],
        });
        t
    }

    // —— 预载单例（TypeId 即 new() 的注册序）——

    pub const INVALID: TypeId = TypeId(0);
    pub const INT: TypeId = TypeId(1);
    pub const FLOAT: TypeId = TypeId(2);
    pub const BOOL: TypeId = TypeId(3);
    pub const STR: TypeId = TypeId(4);
    pub const ERROR: TypeId = TypeId(5);
    pub const UNIT: TypeId = TypeId(6);
    pub const RANGE: TypeId = TypeId(7);
    pub const ANY: TypeId = TypeId(8);
    pub const UNKNOWN: TypeId = TypeId(9);
    pub const NEVER: TypeId = TypeId(10);

    /// 类型结构（arena 只增不减，id 恒有效）。
    pub fn data(&self, id: TypeId) -> &TypeData {
        &self.data[id.0 as usize]
    }

    /// intern 一个结构类型（结构相同 → 同一 TypeId）。
    pub fn intern(&mut self, d: TypeData) -> TypeId {
        // 单例类型直接命中常量位
        let simple = match &d {
            TypeData::Invalid => Some(Self::INVALID),
            TypeData::Int => Some(Self::INT),
            TypeData::Float => Some(Self::FLOAT),
            TypeData::Bool => Some(Self::BOOL),
            TypeData::Str => Some(Self::STR),
            TypeData::Error => Some(Self::ERROR),
            TypeData::Unit => Some(Self::UNIT),
            TypeData::Range => Some(Self::RANGE),
            TypeData::Any => Some(Self::ANY),
            TypeData::Unknown => Some(Self::UNKNOWN),
            TypeData::Never => Some(Self::NEVER),
            _ => None,
        };
        if let Some(id) = simple {
            return id;
        }
        if let Some(pos) = self.data.iter().position(|x| x == &d) {
            return TypeId(pos as u32);
        }
        let id = TypeId(self.data.len() as u32);
        self.data.push(d);
        id
    }

    // —— 名义 def 表 ——

    /// 注册 struct 定义（只增不减；返回稳定 def id）。
    pub fn register_struct(&mut self, def: StructDef) -> StructDefId {
        let id = StructDefId(self.structs.len() as u32);
        self.structs.push(def);
        id
    }

    /// 注册 enum 定义（只增不减；返回稳定 def id）。
    pub fn register_enum(&mut self, def: EnumDef) -> EnumDefId {
        let id = EnumDefId(self.enums.len() as u32);
        self.enums.push(def);
        id
    }

    pub fn struct_def(&self, id: StructDefId) -> &StructDef {
        &self.structs[id.0 as usize]
    }

    pub fn enum_def(&self, id: EnumDefId) -> &EnumDef {
        &self.enums[id.0 as usize]
    }

    /// 字段表回填（注册时先占位、类型注解解析后填充）。
    pub fn struct_def_mut(&mut self, id: StructDefId) -> &mut StructDef {
        &mut self.structs[id.0 as usize]
    }

    /// case 载荷表回填。
    pub fn enum_def_mut(&mut self, id: EnumDefId) -> &mut EnumDef {
        &mut self.enums[id.0 as usize]
    }
}
