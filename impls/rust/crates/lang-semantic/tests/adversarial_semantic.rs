//! 对抗专项测试（docs/lang-rs/2026-09-25-lang-rs-对抗验证重点.md 的 Scope/Generic 节
//! + 本 crate 的 spec 修正面）。期望值全部按 specs/ 手算真值书写。

use std::fmt::Write as _;

use lang_core::{Diagnostic, SourceId, SourceText};
use lang_semantic::analyze_stmts;
use lang_syntax::Stmt;

/// 源码 → 诊断 (码, 行) 有序序列（行 0 = 无锚）。
fn diags_of(src: &str) -> Vec<(&'static str, u32)> {
    let source = SourceId(3);
    let text = SourceText::new("a.lang-go", src.to_string());
    let stmts = lang_syntax::parse(&text, source).unwrap_or_default();
    analyze_stmts(&stmts, source)
        .diags
        .iter()
        .map(|d| {
            (
                d.code.as_str(),
                d.primary_span.map(|s| s.start.line).unwrap_or(0),
            )
        })
        .collect()
}

/// 两份文件语句合并后分析（模拟装载器 NAME-MERGE-010）。
fn diags_merged(a: &str, b: &str) -> Vec<(&'static str, u32)> {
    let source = SourceId(3);
    let ta = SourceText::new("a.lang-go", a.to_string());
    let tb = SourceText::new("b.lang-go", b.to_string());
    let mut stmts: Vec<Stmt> = Vec::new();
    if let Ok(mut v) = lang_syntax::parse(&ta, source) {
        stmts.append(&mut v);
    }
    if let Ok(mut v) = lang_syntax::parse(&tb, source) {
        stmts.append(&mut v);
    }
    analyze_stmts(&stmts, source)
        .diags
        .iter()
        .map(|d| {
            (
                d.code.as_str(),
                d.primary_span.map(|s| s.start.line).unwrap_or(0),
            )
        })
        .collect()
}

fn sorted(mut v: Vec<(&'static str, u32)>) -> Vec<(&'static str, u32)> {
    v.sort();
    v
}

fn assert_diags(src: &str, want: &[(&'static str, u32)]) {
    let got = sorted(diags_of(src));
    let want = sorted(want.to_vec());
    assert_eq!(got, want, "源码:\n{src}");
}

// ———— Scope：for 绑定遮蔽 ————

#[test]
fn for_var_shadows_outer_and_restores() {
    // 循环变量在内层遮蔽同名绑定；循环结束后外层类型恢复（NAME-SHADOW-002）
    let src = r#"
fn f(): string {
    let x: string = "a"
    for x in [1, 2] {
        let y: int = x
    }
    return x
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[]);
}

// ———— Scope：match 臂绑定限本臂（MATCH-BIND-003）———

#[test]
fn match_bind_scoped_to_arm() {
    // v 只在 Some 臂可见；臂外引用即未定义
    let src = r#"
fn f(o: Option[int]): int {
    return match o {
        Some(v) => v
        None => 0
    }
}
fn g(o: Option[int]): int {
    let before = f(o)
    return before + v
}
fn main() {
    println(g(Some(1)))
}
"#;
    // g 里的 v 是未定义名（臂绑定不外泄）；行号 = `return before + v` 所在行
    assert_diags(src, &[("E2002", 10)]);
}

// ———— Scope：嵌套 let 遮蔽（块作用域类型不外泄）———

#[test]
fn nested_let_shadowing_in_block() {
    let src = r#"
fn f(): int {
    let x = 1
    if true {
        let x: string = "s"
        let y: string = x
    }
    let z: int = x
    return z
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[]);
}

// ———— Scope：接收者/参数遮蔽（同作用域重名即 E2001）———

#[test]
fn receiver_shadowed_by_param_is_dup() {
    let src = r#"
struct P {
    v: int
}
fn (p P).dist(p: int): int {
    return p
}
fn main() {
    let p = P{v: 1}
    println(p.dist(2))
}
"#;
    // 接收者 p 与参数 p 同作用域 → E2001（锚在 fn 声明行 5）
    assert_diags(src, &[("E2001", 5)]);
}

#[test]
fn duplicate_params_is_dup() {
    let src = r#"
fn f(a: int, a: int): int {
    return a
}
fn main() {
    println(f(1, 2))
}
"#;
    assert_diags(src, &[("E2001", 2)]);
}

// ———— prelude 冲突 ————

#[test]
fn user_case_conflicts_with_prelude_ctor() {
    // 与预载构造器 Some 重名 → E2003（NAME-CTOR-005）
    let src = r#"
enum Bad {
    Some
}
fn main() {
    println(1)
}
"#;
    assert_diags(src, &[("E2003", 3)]);
}

#[test]
fn user_fn_shadows_builtin_ok() {
    // 用户 fn 遮蔽内建名合法（内建不占值命名空间）
    let src = r#"
fn len(x: string): int {
    return 0
}
fn main() {
    println(len("ab"))
}
"#;
    assert_diags(src, &[]);
}

// ———— 跨文件合并命名空间（NAME-MERGE-010）———

#[test]
fn cross_file_reference_after_merge() {
    let a = r#"
fn usePoint(p: Point): int {
    return p.x
}
fn main() {
    println(usePoint(Point{x: 1, y: 2}))
}
"#;
    let b = r#"
struct Point {
    x: int
    y: int
}
"#;
    let got = sorted(diags_merged(a, b));
    assert_eq!(got, Vec::<(&str, u32)>::new(), "跨文件引用无须 import");
}

#[test]
fn cross_file_duplicate_fn_is_dup() {
    let a = r#"
fn f(): int {
    return 1
}
"#;
    let b = r#"
fn f(): int {
    return 2
}
fn main() {
    println(f())
}
"#;
    let got = sorted(diags_merged(a, b));
    assert_eq!(got, vec![("E2001", 2)]);
}

// ———— Generic：擦除同一性 ————

#[test]
fn generic_fn_erased_identity() {
    // 同一泛型 fn 以 int/string 两种实参实例化；T 端双向放行（GEN-INFER-002 擦除面）
    let src = r#"
fn id[T](x: T): T {
    return x
}
fn main() {
    let a: int = id(1)
    let s: string = id("x")
    println(a)
    println(s)
}
"#;
    assert_diags(src, &[]);
}

#[test]
fn generic_fn_arity_still_checked() {
    let src = r#"
fn id[T](x: T): T {
    return x
}
fn main() {
    println(id(1, 2))
}
"#;
    assert_diags(src, &[("E3003", 6)]);
}

// ———— Generic：Option 链 ————

#[test]
fn nested_option_chain() {
    let src = r#"
fn f(o: Option[Option[int]]): int {
    return match o {
        Some(inner) => match inner {
            Some(v) => v
            None => 0
        }
        None => 0
    }
}
fn main() {
    println(f(Some(Some(1))))
}
"#;
    assert_diags(src, &[]);
}

// ———— Generic：Result 第二参数 ————

#[test]
fn result_second_param_must_be_error() {
    // Result[int, string] → E3009（无节点锚，line 0）；仍按 Result 形检查 return
    let src = r#"
fn f(): Result[int, string] {
    return Ok(1)
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[("E3009", 0)]);
}

#[test]
fn result_missing_second_param() {
    let src = r#"
fn f(): Result[int] {
    return Ok(1)
}
fn main() {
    println(f())
}
"#;
    // fnRet 回退 Invalid → return Ok(1) 走普通路径：E3006（Ok/Err 位置）连带，证据同款
    assert_diags(src, &[("E3001", 0), ("E3006", 3)]);
}

// ———— spec 修正面 1：赋值顶层 ? 合法（RESULT-TRY-003）———

#[test]
fn try_at_assign_top_level_is_allowed() {
    let src = r#"
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f(): Result[int, error] {
    var x = 0
    x = mayFail()?
    return Ok(x)
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[]);
}

#[test]
fn try_outside_statement_top_level_rejected() {
    // ? 混入表达式内部 → E3005（位置非法）
    let src = r#"
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f(): Result[int, error] {
    return (mayFail()?) + 1
}
fn main() {
    println(f())
}
"#;
    // Result fn return 非 Ok/Err → E3006；推断内嵌值时 ? 的位置非法 E3005 也发出
    assert_diags(src, &[("E3005", 6), ("E3006", 6)]);
}

// ———— ADR-0005 扩展（2026-09-27）：表达式语句顶层 ? 合法———

#[test]
fn try_at_expr_stmt_top_level_is_allowed() {
    let src = r#"
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f(): Result[int, error] {
    mayFail()?
    return Ok(0)
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[]);
}

#[test]
fn try_at_expr_stmt_requires_result_anno() {
    // 所在函数未注解 Result → E3005（与 let/return/赋值同规则）
    let src = r#"
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f() {
    mayFail()?
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[("E3005", 6)]);
}

#[test]
fn try_nested_in_expr_stmt_value_rejected() {
    // ? 混入表达式语句的子表达式 → 仍 E3005（只有语句顶层放行）
    let src = r#"
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f(): Result[int, error] {
    mayFail()? + 1
    return Ok(0)
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[("E3005", 6)]);
}

// ———— spec 修正面 2：块作用域类型不外泄（NAME-SCOPE-001）———

#[test]
fn block_let_type_does_not_leak() {
    let src = r#"
fn f(): int {
    if true {
        let q: string = "s"
    }
    let q: int = 1
    return q
}
fn main() {
    println(f())
}
"#;
    assert_diags(src, &[]);
}

// ———— NAME-PLACE-008 补充：continue 同 break ————

#[test]
fn continue_and_break_inside_loop_ok() {
    let src = r#"
fn main() {
    for x in [1, 2, 3] {
        if x == 2 {
            continue
        }
        if x == 3 {
            break
        }
        println(x)
    }
}
"#;
    assert_diags(src, &[]);
}

// ———— Analysis 查询视图 ————

#[test]
fn analysis_query_views() {
    let source = SourceId(5);
    let text = SourceText::new(
        "q.lang-go",
        "fn main() {\n    let x = 1\n    println(x)\n}\n".to_string(),
    );
    let stmts = lang_syntax::parse(&text, source).unwrap_or_default();
    let analysis = analyze_stmts(&stmts, source);
    assert!(!analysis.has_errors());
    assert!(analysis.checked);
    // main 体的 let 类型 = int
    for s in &stmts {
        if let Stmt::FnDecl { body, .. } = s {
            if let Some(Stmt::Let { init, .. }) = body.first() {
                let t = analysis.type_of(init).expect("let init 有类型");
                assert_eq!(analysis.types.data(t), &lang_types::TypeData::Int);
            }
            let sym = analysis.symbol_of_decl(s).expect("fn 声明有符号");
            assert_eq!(analysis.symbols.get(sym).name, "main");
        }
    }
    let _ = Diagnostic::new(lang_core::DiagnosticCode::E1001, "unused", None);
}

// 防止 fmt 辅助导入被裁
#[test]
fn write_helper_compiles() {
    let mut s = String::new();
    let _ = write!(s, "ok");
    assert_eq!(s, "ok");
}
