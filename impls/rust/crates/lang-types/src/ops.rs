//! 类型运算：结构同一、可赋值性、诊断名、成员查找、调用兼容、泛型替换、迭代协议。
//!
//! 语义逐条对照 TS 证据（impls/typescript/src/types/model.ts，其自称按 Go 逐字复刻）：
//! - 宽松铁律：任一侧为 any/Unknown/Invalid/TypeParam 时 `assignable` 双向放行
//!   （TYPE-ANY-002 的保守面：Unknown 不制造误杀，也不得当逃生口新增能力）；
//! - 容器与代数类型按元素协变（运行时容器是擦除的）；fn 参数逆协变 + 返回协变；
//! - struct/enum 名义同一（TYPE-STRUCT-003）；type_param 彼此同一（GEN-DECL-001 擦除）。

use crate::model::{TypeData, TypeTables};
use lang_core::TypeId;

/// 是否数值类型（int/float）。
#[must_use]
pub fn is_numeric(t: &TypeTables, id: TypeId) -> bool {
    matches!(t.data(id), TypeData::Int | TypeData::Float)
}

/// 是否具体类型（非 any/unknown/invalid/type_param 参与判断的宽松位）。
#[must_use]
pub fn is_concrete(t: &TypeTables, id: TypeId) -> bool {
    !matches!(
        t.data(id),
        TypeData::Any | TypeData::Unknown | TypeData::Invalid
    )
}

/// 宽松类型：any/unknown/invalid/type_param —— 参与即放行。
#[must_use]
pub fn is_lax(t: &TypeTables, id: TypeId) -> bool {
    matches!(
        t.data(id),
        TypeData::Any | TypeData::Unknown | TypeData::Invalid | TypeData::TypeParam { .. }
    )
}

/// Result 的 T 侧（非 result → Unknown）。
#[must_use]
pub fn result_ok(t: &TypeTables, id: TypeId) -> TypeId {
    match t.data(id) {
        TypeData::Result { ok, .. } => *ok,
        _ => TypeTables::UNKNOWN,
    }
}

/// Result 的 E 侧（非 result → Unknown）。
#[must_use]
pub fn result_err(t: &TypeTables, id: TypeId) -> TypeId {
    match t.data(id) {
        TypeData::Result { err, .. } => *err,
        _ => TypeTables::UNKNOWN,
    }
}

/// Option 的 T 侧（非 option → Unknown）。
#[must_use]
pub fn option_elem(t: &TypeTables, id: TypeId) -> TypeId {
    match t.data(id) {
        TypeData::Option(elem) => *elem,
        _ => TypeTables::UNKNOWN,
    }
}

/// 穿透 `&T` 引用链。
#[must_use]
pub fn deref(t: &TypeTables, id: TypeId) -> TypeId {
    let mut cur = id;
    while let TypeData::Ref(elem) = t.data(cur) {
        cur = *elem;
    }
    cur
}

/// 结构同一（TYPE-FN-004 fn 结构兼容、TYPE-STRUCT-003 名义同一、擦除 type_param）。
#[must_use]
pub fn equal(t: &TypeTables, a: TypeId, b: TypeId) -> bool {
    if a == b {
        return true;
    }
    match (t.data(a), t.data(b)) {
        (TypeData::Slice(x), TypeData::Slice(y))
        | (TypeData::Ref(x), TypeData::Ref(y))
        | (TypeData::Chan(x), TypeData::Chan(y))
        | (TypeData::Option(x), TypeData::Option(y)) => equal(t, *x, *y),
        (TypeData::Map { key: kx, val: vx }, TypeData::Map { key: ky, val: vy }) => {
            equal(t, *kx, *ky) && equal(t, *vx, *vy)
        }
        (
            TypeData::Fn {
                params: px,
                ret: rx,
            },
            TypeData::Fn {
                params: py,
                ret: ry,
            },
        ) => {
            px.len() == py.len()
                && px.iter().zip(py.iter()).all(|(x, y)| equal(t, *x, *y))
                && equal(t, *rx, *ry)
        }
        (TypeData::Result { ok: ox, err: ex }, TypeData::Result { ok: oy, err: ey }) => {
            equal(t, *ox, *oy) && equal(t, *ex, *ey)
        }
        (TypeData::Struct(sx), TypeData::Struct(sy)) => sx == sy,
        (TypeData::Enum(ex), TypeData::Enum(ey)) => ex == ey,
        (TypeData::TypeParam { .. }, TypeData::TypeParam { .. }) => true, // 擦除同一
        _ => false,
    }
}

/// 可赋值性（TYPE-ASSIGN-001）：同一类型 / 目标为 any / 源为 Unknown（及宽松位）。
/// 无隐式数值转换：int 与 float 互不相容（E3002 由检查器按运算分别发射）。
#[must_use]
pub fn assignable(t: &TypeTables, to: TypeId, from: TypeId) -> bool {
    if is_lax(t, to) || is_lax(t, from) {
        return true;
    }
    match (t.data(to), t.data(from)) {
        (TypeData::Slice(x), TypeData::Slice(y))
        | (TypeData::Ref(x), TypeData::Ref(y))
        | (TypeData::Chan(x), TypeData::Chan(y))
        | (TypeData::Option(x), TypeData::Option(y)) => assignable(t, *x, *y),
        (TypeData::Map { key: kx, val: vx }, TypeData::Map { key: ky, val: vy }) => {
            assignable(t, *kx, *ky) && assignable(t, *vx, *vy)
        }
        (TypeData::Result { ok: ox, err: ex }, TypeData::Result { ok: oy, err: ey }) => {
            assignable(t, *ox, *oy) && assignable(t, *ex, *ey)
        }
        (
            TypeData::Fn {
                params: px,
                ret: rx,
            },
            TypeData::Fn {
                params: py,
                ret: ry,
            },
        ) => {
            px.len() == py.len()
                && px.iter().zip(py.iter()).all(|(x, y)| assignable(t, *y, *x)) // 参数逆协变
                && assignable(t, *rx, *ry)
        }
        _ => equal(t, to, from),
    }
}

/// 诊断与 dump 用类型名（贴近源码写法，与证据 typeStr 逐字一致）。
#[must_use]
pub fn type_str(t: &TypeTables, id: TypeId) -> String {
    match t.data(id) {
        TypeData::Invalid => "invalid".into(),
        TypeData::Int => "int".into(),
        TypeData::Float => "float".into(),
        TypeData::Bool => "bool".into(),
        TypeData::Str => "string".into(),
        TypeData::Error => "error".into(),
        TypeData::Unit => "()".into(),
        TypeData::Range => "range".into(),
        TypeData::Any => "any".into(),
        TypeData::Unknown => "unknown".into(),
        TypeData::Never => "never".into(),
        TypeData::Slice(elem) => format!("[{}]", type_str(t, *elem)),
        TypeData::Map { key, val } => {
            format!("map[{}]{}", type_str(t, *key), type_str(t, *val))
        }
        TypeData::Ref(elem) => format!("&{}", type_str(t, *elem)),
        TypeData::Chan(elem) => format!("chan[{}]", type_str(t, *elem)),
        TypeData::Fn { params, ret } => {
            let ps: Vec<String> = params.iter().map(|p| type_str(t, *p)).collect();
            let mut s = format!("fn({})", ps.join(", "));
            if t.data(*ret) != &TypeData::Unit {
                s.push_str(": ");
                s.push_str(&type_str(t, *ret));
            }
            s
        }
        TypeData::Struct(def) => t.struct_def(*def).name.clone(),
        TypeData::Enum(def) => t.enum_def(*def).name.clone(),
        TypeData::Option(elem) => format!("Option[{}]", type_str(t, *elem)),
        TypeData::Result { ok, err } => {
            format!("Result[{}, {}]", type_str(t, *ok), type_str(t, *err))
        }
        TypeData::TypeParam { name } => name.clone(),
    }
}

/// 字段查找结论（TYPE-MEMBER-007；诊断由检查器按类别发射）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum FieldLookup {
    /// 命中字段，携带字段类型。
    Found(TypeId),
    /// struct/error 无此字段（E3004，struct 名或 error 专名消息）。
    Missing,
    /// 接收者是非 struct 的具体类型（E3004，通用消息）。
    NotStruct,
    /// 接收者宽松（any/unknown/invalid）→ 保守 Unknown，不报错。
    Lax(TypeId),
}

/// 成员查找：struct 字段与 error.msg（`&T` 自动解引用，与 interp PtrVal 语义一致）。
#[must_use]
pub fn lookup_field(t: &TypeTables, obj: TypeId, name: &str) -> FieldLookup {
    let ty = deref(t, obj);
    match t.data(ty) {
        TypeData::Struct(def) => {
            for f in &t.struct_def(*def).fields {
                if f.name == name {
                    return FieldLookup::Found(f.ty);
                }
            }
            FieldLookup::Missing
        }
        TypeData::Error => {
            if name == crate::model::ERROR_MSG_FIELD {
                FieldLookup::Found(TypeTables::STR)
            } else {
                FieldLookup::Missing
            }
        }
        _ if !is_concrete(t, ty) => FieldLookup::Lax(TypeTables::UNKNOWN),
        _ => FieldLookup::NotStruct,
    }
}

/// 调用兼容结论（CALL-ARG-002/E3003/E3001 的类型侧判定）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum CallCompat {
    /// 个数与已注解参数全部兼容。
    Compatible,
    /// 个数不符。
    Arity { want: usize, got: usize },
    /// 第 index 个实参（0 基）与形参类型不兼容。
    ArgMismatch { index: usize },
    /// callee 非 fn 类型且不宽松。
    NotCallable,
}

/// 按函数类型检查调用（结构兼容；具体实参 span 由检查器携带）。
#[must_use]
pub fn check_call(t: &TypeTables, callee: TypeId, args: &[TypeId]) -> CallCompat {
    match t.data(callee) {
        TypeData::Fn { params, .. } => {
            if params.len() != args.len() {
                return CallCompat::Arity {
                    want: params.len(),
                    got: args.len(),
                };
            }
            for (i, (p, a)) in params.iter().zip(args.iter()).enumerate() {
                if !assignable(t, *p, *a) {
                    return CallCompat::ArgMismatch { index: i };
                }
            }
            CallCompat::Compatible
        }
        _ if !is_concrete(t, callee) => CallCompat::Compatible, // 宽松 callee 保守放行
        _ => CallCompat::NotCallable,
    }
}

/// 泛型替换（GEN-DECL-001 擦除语义的类型侧）：把 `t` 中名为 `name` 的
/// TypeParam 替换为 `to`。名义 struct/enum 原样保留（成员类型不递归替换）。
pub fn substitute(t: &mut TypeTables, id: TypeId, name: &str, to: TypeId) -> TypeId {
    match t.data(id).clone() {
        TypeData::TypeParam { name: pname } if pname == name => to,
        TypeData::Slice(elem) => {
            let e = substitute(t, elem, name, to);
            t.intern(TypeData::Slice(e))
        }
        TypeData::Map { key, val } => {
            let k = substitute(t, key, name, to);
            let v = substitute(t, val, name, to);
            t.intern(TypeData::Map { key: k, val: v })
        }
        TypeData::Ref(elem) => {
            let e = substitute(t, elem, name, to);
            t.intern(TypeData::Ref(e))
        }
        TypeData::Chan(elem) => {
            let e = substitute(t, elem, name, to);
            t.intern(TypeData::Chan(e))
        }
        TypeData::Option(elem) => {
            let e = substitute(t, elem, name, to);
            t.intern(TypeData::Option(e))
        }
        TypeData::Result { ok, err } => {
            let o = substitute(t, ok, name, to);
            let e = substitute(t, err, name, to);
            t.intern(TypeData::Result { ok: o, err: e })
        }
        TypeData::Fn { params, ret } => {
            let ps: Vec<TypeId> = params.iter().map(|p| substitute(t, *p, name, to)).collect();
            let r = substitute(t, ret, name, to);
            t.intern(TypeData::Fn { params: ps, ret: r })
        }
        _ => id,
    }
}

/// 迭代协议（CTRL-FORIN-003）：`None` = 不可迭代（E3010）；Some(elem) 为循环变量类型。
#[must_use]
pub fn iterable_elem(t: &TypeTables, id: TypeId) -> Option<TypeId> {
    match t.data(deref(t, id)) {
        TypeData::Slice(elem) => Some(*elem),
        TypeData::Map { key, .. } => Some(*key), // 映射迭代产出键
        TypeData::Str => Some(TypeTables::STR),  // 单字符字符串
        TypeData::Range => Some(TypeTables::INT),
        _ if !is_concrete(t, deref(t, id)) => Some(TypeTables::UNKNOWN),
        _ => None,
    }
}
