//! HIR 模型测试：确定性（同构造两次逐字段相等）+ 节点元数据承载。

use lang_core::ids::{NodeId, SourceId, SymbolId, TypeId};
use lang_core::span::{Position, Span};

use crate::hir::*;

fn sp() -> Span {
    Span::new(
        SourceId(0),
        Position::new(0, 1, 1),
        Position::new(12, 1, 13),
    )
}

fn lit_int(id: u32, v: i64) -> HirExpr {
    HirExpr::Lit(HirLit {
        meta: NodeInfo::new(NodeId(id), sp()).with_type(TypeId(7)),
        kind: LitKind::Int,
        i64: v,
        f64: 0.0,
        s: String::new(),
        b: false,
    })
}

/// `fn main() -> int { return 1 + 2 }` 的 HIR 手构。
fn prog() -> HirProg {
    let body = vec![HirStmt::Return {
        meta: NodeInfo::new(NodeId(4), sp()).with_type(TypeId(7)),
        val: Some(Box::new(HirExpr::Binary {
            meta: NodeInfo::new(NodeId(5), sp()).with_type(TypeId(7)),
            op: BinaryOp::Add,
            lhs: Box::new(lit_int(6, 1)),
            rhs: Box::new(lit_int(7, 2)),
        })),
    }];
    HirProg {
        structs: vec![],
        enums: vec![],
        globals: vec![],
        fns: vec![],
        main: Some(Box::new(HirFn {
            meta: NodeInfo::new(NodeId(0), sp())
                .with_sym(SymbolId(3))
                .with_type(TypeId(7)),
            name: "main".into(),
            recv: None,
            type_params: vec![],
            params: vec![],
            ret: Some(TypeId(7)),
            body,
            is_extern: false,
            extern_host: String::new(),
        })),
    }
}

#[test]
fn same_hir_built_twice_is_field_identical() {
    assert_eq!(prog(), prog());
}

#[test]
fn node_info_carries_id_span_sym_type() {
    let p = prog();
    let main = p.main.expect("main 存在");
    assert_eq!(main.meta.id, NodeId(0));
    assert_eq!(main.meta.span, sp());
    assert_eq!(main.meta.sym, Some(SymbolId(3)));
    assert_eq!(main.meta.type_id, Some(TypeId(7)));

    match &main.body[0] {
        HirStmt::Return { val: Some(v), .. } => match v.as_ref() {
            HirExpr::Binary { meta, lhs, .. } => {
                assert_eq!(meta.id, NodeId(5));
                assert_eq!(meta.type_id, Some(TypeId(7)));
                match lhs.as_ref() {
                    HirExpr::Lit(l) => {
                        assert_eq!(l.meta.id, NodeId(6));
                        assert_eq!(l.meta.sym, None);
                        assert_eq!(l.meta.type_id, Some(TypeId(7)));
                        assert_eq!(l.i64, 1);
                    }
                    other => panic!("应为 Lit，实际 {other:?}"),
                }
            }
            other => panic!("应为 Binary，实际 {other:?}"),
        },
        other => panic!("应为 Return，实际 {other:?}"),
    }
}

#[test]
fn one_field_difference_breaks_equality() {
    let mut a = prog();
    let mut b = prog();
    assert_eq!(a, b);
    if let HirProg { main: Some(m), .. } = &mut b {
        m.name = "not_main".into();
    }
    assert_ne!(a, b);
    let _ = &mut a;
}

#[test]
fn type_of_reads_meta_type() {
    let p = prog();
    let main = p.main.expect("main 存在");
    match &main.body[0] {
        HirStmt::Return { val: Some(v), .. } => {
            assert_eq!(type_of(v), Some(TypeId(7)));
        }
        _ => unreachable!(),
    }
}

#[test]
fn match_arm_pattern_shapes() {
    // match x { Some(v) => v, None => 0 }（Option 分派事实）。
    let arms = vec![
        HirMatchArm {
            pat: HirPat::Ctor {
                ctor_sym: Some(SymbolId(10)),
                subs: vec![HirPat::Bind {
                    name: "v".into(),
                    type_id: None,
                }],
            },
            body: Box::new(lit_int(20, 0)),
        },
        HirMatchArm {
            pat: HirPat::Ctor {
                ctor_sym: Some(SymbolId(11)),
                subs: vec![],
            },
            body: Box::new(lit_int(21, 0)),
        },
    ];
    let m = HirExpr::Match {
        meta: NodeInfo::new(NodeId(30), sp()),
        scrutinee: Box::new(lit_int(31, 1)),
        kind: MatchKind::Option,
        arms: arms.clone(),
    };
    assert_eq!(m.clone(), m);
    assert_eq!(arms.len(), 2);
}
