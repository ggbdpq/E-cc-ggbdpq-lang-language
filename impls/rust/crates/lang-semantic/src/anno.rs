//! 源码注解 → 语义类型（TypeEx 5 变体 → [`TypeId`]）。
//!
//! 双通道，与证据保持一致：
//! - [`resolve_anno_quiet`]：声明收集期的静默解析（struct 字段、enum 载荷、
//!   HIR 侧复用）——不发射诊断，坏 Option/Result arity 保守回退；
//! - [`resolve_anno_emit`]：检查器路径（参数/返回/let 注解）——Option arity≠1 →
//!   E3001、Result arity≠2 → E3001、Result 第二参数非 error → E3009（均 line 0，
//!   与 case.yaml 期望的锚定一致）；E3009 后仍返回 Result 形，避免下游把
//!   Result 函数误判为普通函数（E3006 连锁，证据同款处理）。
//!
//! 泛型：`tps` 中的名字解析为擦除占位（[`TypeData::TypeParam`]）。

use lang_core::{DiagnosticCode, SymbolId};
use lang_syntax::TypeEx;
use lang_types::{TypeData, TypeId, TypeTables};

use crate::symbols::{NominalRef, SymbolArena};

/// 注解解析上下文（Resolver / Checker 各自实现）。
pub(crate) trait TypeCtx {
    fn tables_mut(&mut self) -> &mut TypeTables;

    fn arena(&self) -> &SymbolArena;

    /// 具名类型查名（预载 error → 顶层 → arena 首个同名）。
    fn lookup_named(&self, name: &str) -> Option<SymbolId>;

    /// 无节点锚诊断（line 0）。
    fn emit(&mut self, code: DiagnosticCode, msg: String);
}

fn tps_has(tps: &[String], name: &str) -> bool {
    tps.iter().any(|t| t == name)
}

fn named_to_type(ctx: &mut impl TypeCtx, sid: SymbolId) -> TypeId {
    let nominal = ctx.arena().get(sid).nominal;
    match nominal {
        Some(NominalRef::Struct(def)) => ctx.tables_mut().intern(TypeData::Struct(def)),
        Some(NominalRef::Enum(def)) => ctx.tables_mut().intern(TypeData::Enum(def)),
        None => TypeTables::UNKNOWN,
    }
}

/// 静默通道（声明收集期）。
pub(crate) fn resolve_anno_quiet(ctx: &mut impl TypeCtx, t: &TypeEx, tps: &[String]) -> TypeId {
    match t {
        TypeEx::Named { name, args, .. } => match name.as_str() {
            "int" => TypeTables::INT,
            "float" => TypeTables::FLOAT,
            "string" => TypeTables::STR,
            "bool" => TypeTables::BOOL,
            "error" => TypeTables::ERROR,
            "any" => TypeTables::ANY,
            "chan" => {
                let elem = args
                    .first()
                    .map(|a| resolve_anno_quiet(ctx, a, tps))
                    .unwrap_or(TypeTables::UNKNOWN);
                ctx.tables_mut().intern(TypeData::Chan(elem))
            }
            "Option" => {
                let elem = args
                    .first()
                    .map(|a| resolve_anno_quiet(ctx, a, tps))
                    .unwrap_or(TypeTables::UNKNOWN);
                ctx.tables_mut().intern(TypeData::Option(elem))
            }
            "Result" => {
                let ok = args
                    .first()
                    .map(|a| resolve_anno_quiet(ctx, a, tps))
                    .unwrap_or(TypeTables::UNKNOWN);
                ctx.tables_mut().intern(TypeData::Result {
                    ok,
                    err: TypeTables::ERROR,
                })
            }
            _ => {
                if tps_has(tps, name) {
                    return ctx
                        .tables_mut()
                        .intern(TypeData::TypeParam { name: name.clone() });
                }
                match ctx.lookup_named(name) {
                    Some(sid) => named_to_type(ctx, sid),
                    None => TypeTables::UNKNOWN, // 未知注解不阻塞（保守）
                }
            }
        },
        TypeEx::Arr { elem, .. } => {
            let e = resolve_anno_quiet(ctx, elem, tps);
            ctx.tables_mut().intern(TypeData::Slice(e))
        }
        TypeEx::Map { key, val, .. } => {
            let k = resolve_anno_quiet(ctx, key, tps);
            let v = resolve_anno_quiet(ctx, val, tps);
            ctx.tables_mut().intern(TypeData::Map { key: k, val: v })
        }
        TypeEx::Ptr { elem, .. } => {
            let e = resolve_anno_quiet(ctx, elem, tps);
            ctx.tables_mut().intern(TypeData::Ref(e))
        }
        TypeEx::Fn { params, ret, .. } => {
            let ps: Vec<TypeId> = params
                .iter()
                .map(|p| resolve_anno_quiet(ctx, p, tps))
                .collect();
            let r = ret
                .as_ref()
                .map(|r| resolve_anno_quiet(ctx, r, tps))
                .unwrap_or(TypeTables::UNKNOWN);
            ctx.tables_mut().intern(TypeData::Fn { params: ps, ret: r })
        }
    }
}

/// 检查器通道（Option/Result arity 与第二参数错误在此发射）。
pub(crate) fn resolve_anno_emit(ctx: &mut impl TypeCtx, t: &TypeEx, tps: &[String]) -> TypeId {
    if let TypeEx::Named { name, args, .. } = t {
        match name.as_str() {
            "Option" => {
                if args.len() == 1 {
                    let elem = resolve_anno_emit(ctx, &args[0], tps);
                    return ctx.tables_mut().intern(TypeData::Option(elem));
                }
                ctx.emit(DiagnosticCode::E3001, "Option 需要一个类型参数".to_string());
                return TypeTables::INVALID;
            }
            "Result" => {
                if args.len() != 2 {
                    ctx.emit(DiagnosticCode::E3001, "Result 需要两个类型参数".to_string());
                    return TypeTables::INVALID;
                }
                let ok = resolve_anno_emit(ctx, &args[0], tps);
                let err_ok = matches!(&args[1], TypeEx::Named { name, .. } if name == "error");
                if !err_ok {
                    ctx.emit(
                        DiagnosticCode::E3009,
                        "Result 第二类型参数须为 error".to_string(),
                    );
                }
                return ctx.tables_mut().intern(TypeData::Result {
                    ok,
                    err: TypeTables::ERROR,
                });
            }
            _ => {}
        }
    }
    match t {
        TypeEx::Named { name, args, .. } => match name.as_str() {
            "int" => TypeTables::INT,
            "float" => TypeTables::FLOAT,
            "string" => TypeTables::STR,
            "bool" => TypeTables::BOOL,
            "error" => TypeTables::ERROR,
            "any" => TypeTables::ANY,
            "chan" => {
                let elem = args
                    .first()
                    .map(|a| resolve_anno_emit(ctx, a, tps))
                    .unwrap_or(TypeTables::UNKNOWN);
                ctx.tables_mut().intern(TypeData::Chan(elem))
            }
            _ => {
                if tps_has(tps, name) {
                    return ctx
                        .tables_mut()
                        .intern(TypeData::TypeParam { name: name.clone() });
                }
                match ctx.lookup_named(name) {
                    Some(sid) => named_to_type(ctx, sid),
                    None => TypeTables::UNKNOWN,
                }
            }
        },
        TypeEx::Arr { elem, .. } => {
            let e = resolve_anno_emit(ctx, elem, tps);
            ctx.tables_mut().intern(TypeData::Slice(e))
        }
        TypeEx::Map { key, val, .. } => {
            let k = resolve_anno_emit(ctx, key, tps);
            let v = resolve_anno_emit(ctx, val, tps);
            ctx.tables_mut().intern(TypeData::Map { key: k, val: v })
        }
        TypeEx::Ptr { elem, .. } => {
            let e = resolve_anno_emit(ctx, elem, tps);
            ctx.tables_mut().intern(TypeData::Ref(e))
        }
        TypeEx::Fn { params, ret, .. } => {
            let ps: Vec<TypeId> = params
                .iter()
                .map(|p| resolve_anno_emit(ctx, p, tps))
                .collect();
            let r = ret
                .as_ref()
                .map(|r| resolve_anno_emit(ctx, r, tps))
                .unwrap_or(TypeTables::UNKNOWN);
            ctx.tables_mut().intern(TypeData::Fn { params: ps, ret: r })
        }
    }
}
