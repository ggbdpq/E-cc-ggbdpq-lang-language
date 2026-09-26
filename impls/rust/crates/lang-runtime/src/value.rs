//! 运行时值模型（specs/values.md）。
//!
//! # 值语义铁律的模型化
//!
//! - **struct/enum 内联存储**：`Value::clone()` 即 VALUE-COPY-001——顶层字段逐字段
//!   复制、嵌套 struct 递归拷贝、字段中的容器（`ValueId`）保持共享。绑定/传参/返回/
//!   写入容器一律走 clone（VALUE-ELEM-009 读出即副本），别名偏差在结构上不可表示。
//! - **slice/map 堆分配**：`Value::Slice(ValueId)` / `Value::Map(ValueId)` 只持堆索引，
//!   clone 共享底层数据（VALUE-SHARE-002 引用语义），原位变更对全部别名可见。
//! - **字符串不可变**：`Rc<str>`（VALUE-IMMUT-005），clone 廉价。
//! - Option/Result 是预载 enum 实例（specs/option-result.md），不是独立变体；
//!   `Some/None/Ok/Err` 经 `Machine` 的构造器产生（依赖预载 `EnumDefId`）。
//!
//! `PartialEq` 仅供内部/测试做结构比较；语言级 `==` 必须走 [`Value::equals`]
//! （RUNTIME-EQ-003：仅标量与 enum 可比）。

use std::rc::Rc;

use lang_core::FunctionId;

use crate::builtin::BuiltinId;
use crate::chan::ChannelId;
use crate::defs::{DefTables, EnumDefId, StructDefId};
use crate::env::EnvId;
use crate::error::{RtError, RtErrorKind, RtResult};
use crate::heap::ValueId;
use crate::place::Place;

/// struct 实例（内联值；clone = VALUE-COPY-001 深拷贝）。
#[derive(Clone, Debug, PartialEq)]
pub struct StructVal {
    pub def: StructDefId,
    pub fields: Vec<Value>,
}

/// 语言运行时值。
#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    /// `int`（i64，值域与 Go int64 对齐）。
    Int(i64),
    /// `float`（f64）。
    Float(f64),
    /// `bool`。
    Bool(bool),
    /// `string`，不可变（VALUE-IMMUT-005）。
    Str(Rc<str>),
    /// `()`（VALUE-UNIT-008）。
    Unit,
    /// 区间 `lo..hi`，半开 `[lo, hi)`；仅 for-in/len 消费（运行时证据 RangeVal）。
    Range { lo: i64, hi: i64 },
    /// `[T]` 容器——语言唯一线性数组形态，引用语义（VALUE-SHARE-002）。
    Slice(ValueId),
    /// `map[K]V`，插入序（VALUE-MAP-006），引用语义。
    Map(ValueId),
    /// struct 实例，值语义（VALUE-COPY-001）。
    Struct(StructVal),
    /// enum 实例（含预载 Option/Result）：`Tag(载荷...)`。
    Enum {
        def: EnumDefId,
        case: u32,
        payloads: Vec<Value>,
    },
    /// `chan[T]`，channel 表索引（CHAN-CREATE-001）。
    Chan(ChannelId),
    /// 闭包/函数值：函数身份 + 定义处环境（VALUE-CAPTURE-004 按引用捕获）。
    Closure { func: FunctionId, env: EnvId },
    /// 内建函数值。
    Builtin(BuiltinId),
    /// 引用（RefValue）：`&x` 的结果，显式建模，绝不映射为 Rust 引用（VALUE-REF-003）。
    Ref(Place),
}

impl Value {
    /// 字符串值构造。
    pub fn str(s: impl Into<Rc<str>>) -> Value {
        Value::Str(s.into())
    }

    /// 类型名（输出与错误消息用；struct/enum 取定义名，需定义表）。
    pub fn type_name(&self, defs: &DefTables) -> String {
        match self {
            Value::Int(_) => "int".to_string(),
            Value::Float(_) => "float".to_string(),
            Value::Bool(_) => "bool".to_string(),
            Value::Str(_) => "string".to_string(),
            Value::Unit => "()".to_string(),
            Value::Range { .. } => "区间".to_string(),
            Value::Slice(_) => "数组".to_string(),
            Value::Map(_) => "映射".to_string(),
            Value::Struct(sv) => defs
                .struct_def(sv.def)
                .map(|d| d.name.to_string())
                .unwrap_or_else(|_| "<?>".to_string()),
            Value::Enum { def, .. } => defs
                .enum_def(*def)
                .map(|d| d.name.to_string())
                .unwrap_or_else(|_| "<?>".to_string()),
            Value::Chan(_) => "chan".to_string(),
            Value::Closure { .. } => "函数".to_string(),
            Value::Builtin(_) => "内建函数".to_string(),
            Value::Ref(_) => "引用".to_string(),
        }
    }

    /// 不依赖定义表的类型提示（运行时内部错误路径用；带定义名的正规
    /// 消息由解释器经 `type_name` 构造）。消息文本允许实现差异
    /// （RUNTIME-FAIL-001），错误分类是契约。
    pub fn type_hint(&self) -> String {
        match self {
            Value::Int(_) => "int".to_string(),
            Value::Float(_) => "float".to_string(),
            Value::Bool(_) => "bool".to_string(),
            Value::Str(_) => "string".to_string(),
            Value::Unit => "()".to_string(),
            Value::Range { .. } => "区间".to_string(),
            Value::Slice(_) => "数组".to_string(),
            Value::Map(_) => "映射".to_string(),
            Value::Struct(_) => "struct 值".to_string(),
            Value::Enum { .. } => "enum 值".to_string(),
            Value::Chan(_) => "chan".to_string(),
            Value::Closure { .. } => "函数".to_string(),
            Value::Builtin(_) => "内建函数".to_string(),
            Value::Ref(_) => "引用".to_string(),
        }
    }

    /// 语言级 `==`（RUNTIME-EQ-003）：标量同型比较、enum 同 def 同 case 递归比较；
    /// 容器/struct/函数等不可比 → `RtErrorKind::Eq`。
    pub fn equals(a: &Value, b: &Value) -> RtResult<bool> {
        match (a, b) {
            (Value::Int(x), Value::Int(y)) => Ok(x == y),
            (Value::Float(x), Value::Float(y)) => Ok(x == y),
            (Value::Bool(x), Value::Bool(y)) => Ok(x == y),
            (Value::Str(x), Value::Str(y)) => Ok(x == y),
            (
                Value::Enum {
                    def: da,
                    case: ca,
                    payloads: pa,
                },
                Value::Enum {
                    def: db,
                    case: cb,
                    payloads: pb,
                },
            ) => {
                if da != db || ca != cb {
                    return Ok(false);
                }
                if pa.len() != pb.len() {
                    return Err(RtError::internal("enum 载荷数不一致：内部不变量破坏"));
                }
                for (x, y) in pa.iter().zip(pb.iter()) {
                    if !Value::equals(x, y)? {
                        return Ok(false);
                    }
                }
                Ok(true)
            }
            _ => Err(RtError::new(
                RtErrorKind::Eq,
                format!("{} 不可用 == 比较", a.type_hint()),
            )),
        }
    }

    /// 同型 int/float/string 大小比较；异型或不可比类型返回 `None`
    /// （字符串为 UTF-8 字节序 = 码点序，与 Go strings.Compare 一致）。
    pub fn compare(a: &Value, b: &Value) -> Option<std::cmp::Ordering> {
        use std::cmp::Ordering;
        match (a, b) {
            (Value::Int(x), Value::Int(y)) => Some(x.cmp(y)),
            (Value::Float(x), Value::Float(y)) => x.partial_cmp(y).or(Some(Ordering::Equal)),
            (Value::Str(x), Value::Str(y)) => Some(x.cmp(y)),
            _ => None,
        }
    }

    /// 条件真值：仅 bool 可作条件（无隐式真值转换）。
    pub fn truthy(&self) -> RtResult<bool> {
        match self {
            Value::Bool(b) => Ok(*b),
            other => Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("条件须为 bool，得到 {}", other.type_hint()),
            )),
        }
    }
}

/// 映射键：语言层仅允许 int/string/bool（E3014）。
///
/// `Ord` 派生序为「变体序 + 值序」，仅供内部；JSON 键字母序（RUNTIME-JSON-002）
/// 由宿主内建按 [`MapKey::plain`] 文本排序实现。
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum MapKey {
    Int(i64),
    Str(Rc<str>),
    Bool(bool),
}

impl MapKey {
    /// 从值提取合法映射键；其余类型返回 `None`（由调用方报 E3014/RUNTIME 错）。
    pub fn from_value(v: &Value) -> Option<MapKey> {
        match v {
            Value::Int(i) => Some(MapKey::Int(*i)),
            Value::Str(s) => Some(MapKey::Str(s.clone())),
            Value::Bool(b) => Some(MapKey::Bool(*b)),
            _ => None,
        }
    }

    /// 键的纯文本形（int 十进制 / bool `true|false` / string 原文）。
    /// 映射输出 `{k: v}` 与 JSON 键字母序共用此形。
    pub fn plain(&self) -> String {
        match self {
            MapKey::Int(i) => i.to_string(),
            MapKey::Str(s) => s.to_string(),
            MapKey::Bool(b) => b.to_string(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn defs() -> DefTables {
        DefTables::with_prelude()
    }

    // —— type_name（输出面/错误消息真值，来自证据 typeName 表）——

    #[test]
    fn type_name_scalar_and_containers() {
        let d = defs();
        assert_eq!(Value::Int(1).type_name(&d), "int");
        assert_eq!(Value::Float(1.0).type_name(&d), "float");
        assert_eq!(Value::Bool(true).type_name(&d), "bool");
        assert_eq!(Value::str("x").type_name(&d), "string");
        assert_eq!(Value::Unit.type_name(&d), "()");
        assert_eq!(Value::Range { lo: 0, hi: 1 }.type_name(&d), "区间");
        assert_eq!(Value::Slice(ValueId(0)).type_name(&d), "数组");
        assert_eq!(Value::Map(ValueId(0)).type_name(&d), "映射");
        assert_eq!(
            Value::Ref(Place::Var {
                env: EnvId(0),
                slot: 0
            })
            .type_name(&d),
            "引用"
        );
    }

    #[test]
    fn type_name_struct_and_enum_use_def_name() {
        let mut d = defs();
        let s = d.register_struct("P", vec!["v".to_string()]);
        let e = d.register_enum("Color", vec!["Red".to_string()]);
        assert_eq!(
            Value::Struct(StructVal {
                def: s,
                fields: vec![]
            })
            .type_name(&d),
            "P"
        );
        assert_eq!(
            Value::Enum {
                def: e,
                case: 0,
                payloads: vec![]
            }
            .type_name(&d),
            "Color"
        );
    }

    // —— equals（RUNTIME-EQ-003：仅标量与 enum 可比）——

    #[test]
    fn equals_scalars_same_type_only() {
        assert!(Value::equals(&Value::Int(1), &Value::Int(1)).unwrap());
        assert!(!Value::equals(&Value::Int(1), &Value::Int(2)).unwrap());
        // 无隐式转换：int 与 float 不可比较
        assert_eq!(
            Value::equals(&Value::Int(1), &Value::Float(1.0))
                .unwrap_err()
                .kind(),
            RtErrorKind::Eq
        );
        assert!(Value::equals(&Value::str("a"), &Value::str("a")).unwrap());
        assert!(Value::equals(&Value::Bool(true), &Value::Bool(true)).unwrap());
    }

    #[test]
    fn equals_enum_requires_same_def_and_case() {
        let mut d = defs();
        let e1 = d.register_enum("Color", vec!["Red".to_string(), "Pair".to_string()]);
        let e2 = d.register_enum("Other", vec!["Red".to_string()]);
        let red1 = Value::Enum {
            def: e1,
            case: 0,
            payloads: vec![],
        };
        let red1_again = Value::Enum {
            def: e1,
            case: 0,
            payloads: vec![],
        };
        let red_other_def = Value::Enum {
            def: e2,
            case: 0,
            payloads: vec![],
        };
        let pair = Value::Enum {
            def: e1,
            case: 1,
            payloads: vec![Value::Int(1), Value::Int(2)],
        };
        let pair_eq = Value::Enum {
            def: e1,
            case: 1,
            payloads: vec![Value::Int(1), Value::Int(2)],
        };
        let pair_ne = Value::Enum {
            def: e1,
            case: 1,
            payloads: vec![Value::Int(1), Value::Int(3)],
        };
        assert!(Value::equals(&red1, &red1_again).unwrap());
        assert!(!Value::equals(&red1, &red_other_def).unwrap()); // def 身份不同 → false
        assert!(!Value::equals(&red1, &pair).unwrap()); // case 不同 → false
        assert!(Value::equals(&pair, &pair_eq).unwrap()); // 载荷递归比较
        assert!(!Value::equals(&pair, &pair_ne).unwrap());
    }

    #[test]
    fn equals_containers_rejected() {
        let err = Value::equals(&Value::Slice(ValueId(0)), &Value::Slice(ValueId(0))).unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::Eq);
        let s = StructVal {
            def: StructDefId(0),
            fields: vec![],
        };
        assert_eq!(
            Value::equals(&Value::Struct(s.clone()), &Value::Struct(s))
                .unwrap_err()
                .kind(),
            RtErrorKind::Eq
        );
    }

    // —— compare / truthy ——

    #[test]
    fn compare_same_type_only() {
        assert_eq!(
            Value::compare(&Value::Int(1), &Value::Int(2)),
            Some(std::cmp::Ordering::Less)
        );
        assert_eq!(
            Value::compare(&Value::Float(2.0), &Value::Float(1.0)),
            Some(std::cmp::Ordering::Greater)
        );
        // 字符串 = 码点序（UTF-8 字节序）
        assert_eq!(
            Value::compare(&Value::str("a"), &Value::str("b")),
            Some(std::cmp::Ordering::Less)
        );
        assert_eq!(
            Value::compare(&Value::Int(1), &Value::Str("1".into())),
            None
        );
    }

    #[test]
    fn truthy_bool_only() {
        assert!(Value::Bool(true).truthy().unwrap());
        assert!(!Value::Bool(false).truthy().unwrap());
        assert_eq!(
            Value::Int(0).truthy().unwrap_err().kind(),
            RtErrorKind::TypeCast
        );
    }

    // —— MapKey（E3014：键须 int/string/bool）——

    #[test]
    fn map_key_from_value() {
        assert_eq!(MapKey::from_value(&Value::Int(-3)), Some(MapKey::Int(-3)));
        assert_eq!(
            MapKey::from_value(&Value::str("k")),
            Some(MapKey::Str("k".into()))
        );
        assert_eq!(
            MapKey::from_value(&Value::Bool(true)),
            Some(MapKey::Bool(true))
        );
        assert_eq!(MapKey::from_value(&Value::Float(1.0)), None);
        assert_eq!(MapKey::from_value(&Value::Unit), None);
    }

    #[test]
    fn map_key_plain_form() {
        assert_eq!(MapKey::Int(42).plain(), "42");
        assert_eq!(MapKey::Bool(false).plain(), "false");
        assert_eq!(MapKey::Str("k".into()).plain(), "k");
    }
}
