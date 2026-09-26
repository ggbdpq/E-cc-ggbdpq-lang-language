//! 静态符号模型（NAME-* 的承载结构）。
//!
//! 每个符号拥有编译内稳定的 [`SymbolId`]（按声明/绑定顺序编号，含预载符号）。
//! 符号自带声明注解的 owned 快照（TypeEx clone），检查器不回读 AST 声明节点。

use std::collections::BTreeMap;

use lang_core::{Span, SymbolId};
use lang_syntax::TypeEx;
use lang_types::{EnumDefId, StructDefId};

/// 符号类别（诊断名与 Go/TS 证据 kindName 逐字一致）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum SymbolKind {
    Fn,
    Method,
    Var,
    Param,
    Struct,
    Enum,
    Ctor,
}

impl SymbolKind {
    /// 类别的诊断名（「绑定」「参数」等中文串为消息契约面）。
    #[must_use]
    pub fn kind_name(self) -> &'static str {
        match self {
            SymbolKind::Fn => "fn",
            SymbolKind::Method => "方法",
            SymbolKind::Var => "绑定",
            SymbolKind::Param => "参数",
            SymbolKind::Struct => "struct",
            SymbolKind::Enum => "enum",
            SymbolKind::Ctor => "构造器",
        }
    }
}

/// 名义类型的 def 身份（符号 → TypeTables def 表项）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum NominalRef {
    Struct(StructDefId),
    Enum(EnumDefId),
}

/// 静态符号。
#[derive(Clone, Debug)]
pub struct Symbol {
    pub id: SymbolId,
    pub kind: SymbolKind,
    pub name: String,
    pub span: Span,
    /// var：是否可重绑（`var`）；其余类别恒 false。
    pub mutable: bool,
    /// fn/method 返回注解、let 绑定注解。
    pub type_ex: Option<TypeEx>,
    /// fn/method 参数注解（保序）。
    pub param_annos: Vec<Option<TypeEx>>,
    /// fn/method 参数名（保序，与 param_annos 对齐；诊断消息用）。
    pub param_names: Vec<String>,
    /// method：接收者 struct 名。
    pub recv_name: Option<String>,
    /// fn/method 参数个数；ctor 载荷个数。
    pub arity: usize,
    /// 声明的泛型参数名（用户泛型 fn；预载 Option/Result 之外的用户泛型
    /// struct/enum 属冻结不支持面，类型参数按擦除占位处理）。
    pub type_params: Vec<String>,
    /// ctor：所属 enum 名（预载为 "Option"/"Result"）。
    pub owner_name: String,
    /// ctor：case 序号（Option: Some=0/None=1；Result: Ok=0/Err=1）。
    pub case_index: u32,
    /// ctor：载荷类型注解（保序）。
    pub payload_annos: Vec<Option<TypeEx>>,
    /// struct：字段名（保序）。
    pub fields: Vec<String>,
    /// struct：字段注解（与 fields 对齐）。
    pub field_annos: Vec<Option<TypeEx>>,
    /// struct：方法表（方法名 → 方法符号）。
    pub methods: BTreeMap<String, SymbolId>,
    /// struct/enum：def 表身份。
    pub nominal: Option<NominalRef>,
    /// extern 声明（无函数体）。
    pub is_extern: bool,
    /// 预载符号（内建函数/预载 struct/预载构造器）。
    pub is_builtin: bool,
}

/// 符号 arena（只增不减，[`SymbolId`] 即下标）。
#[derive(Clone, Debug)]
pub struct SymbolArena {
    items: Vec<Symbol>,
}

impl SymbolArena {
    #[must_use]
    pub fn new() -> Self {
        Self { items: Vec::new() }
    }

    pub(crate) fn push(&mut self, mut s: Symbol) -> SymbolId {
        let id = SymbolId(self.items.len() as u32);
        s.id = id;
        self.items.push(s);
        id
    }

    /// 按身份取符号（arena 只增不减，id 恒有效）。
    #[must_use]
    pub fn get(&self, id: SymbolId) -> &Symbol {
        &self.items[id.0 as usize]
    }

    pub(crate) fn get_mut(&mut self, id: SymbolId) -> &mut Symbol {
        &mut self.items[id.0 as usize]
    }

    #[must_use]
    pub fn len(&self) -> usize {
        self.items.len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    /// 按声明顺序遍历（含预载符号）。
    pub fn iter(&self) -> impl Iterator<Item = &Symbol> {
        self.items.iter()
    }

    /// 首个同名 struct/enum 符号（顶层先于嵌套创建；与证据 arena 扫描序一致）。
    #[must_use]
    pub fn first_type_named(&self, name: &str) -> Option<SymbolId> {
        self.items
            .iter()
            .position(|s| {
                (s.kind == SymbolKind::Struct || s.kind == SymbolKind::Enum) && s.name == name
            })
            .map(|p| SymbolId(p as u32))
    }
}

impl Default for SymbolArena {
    fn default() -> Self {
        Self::new()
    }
}
