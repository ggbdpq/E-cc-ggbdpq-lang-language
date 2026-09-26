//! conformance/syntax 真实夹具测试（TDD 期望来自 case.yaml，不来自本实现）。
//!
//! 夹具布局：`<repo>/conformance/syntax/<case>/{case.yaml, main.lang-go}`。
//! run 类 case（exitCode: 0）断言能解析通过；check 类（非 0）断言产出 E1001。
//! case.yaml 只做轻量文本判定（exitCode 行），不引入 yaml 依赖。

use std::fs;
use std::path::PathBuf;

use lang_core::{DiagnosticCode, SourceId, SourceText};
use lang_syntax::{Expr, Stmt, parse};

fn syntax_dir() -> PathBuf {
    // crates/lang-syntax → crates → rust → impls → <repo 根>
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../../..")
        .join("conformance")
        .join("syntax")
}

fn expected_exit_zero(case_yaml: &str) -> bool {
    case_yaml.lines().any(|l| {
        let t = l.trim();
        t == "exitCode: 0" || t == "exitCode: '0'" || t == "exitCode: \"0\""
    })
}

#[test]
fn conformance_syntax_cases_parse_as_expected() {
    let dir = syntax_dir();
    assert!(dir.is_dir(), "conformance/syntax 不存在: {}", dir.display());
    let mut cases: Vec<PathBuf> = fs::read_dir(&dir)
        .expect("列出 conformance/syntax")
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.join("case.yaml").is_file() && p.join("main.lang-go").is_file())
        .collect();
    cases.sort();
    assert!(
        cases.len() >= 13,
        "syntax 夹具应至少 13 个 case，实得 {}",
        cases.len()
    );

    let mut failures = Vec::new();
    for case in &cases {
        let name = case
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .to_string();
        let yaml = fs::read_to_string(case.join("case.yaml")).expect("读 case.yaml");
        let src = fs::read_to_string(case.join("main.lang-go")).expect("读 main.lang-go");
        let text = SourceText::new(format!("{name}/main.lang-go"), src);
        let result = parse(&text, SourceId(0));
        match (expected_exit_zero(&yaml), result) {
            (true, Ok(_)) => {}
            (true, Err(d)) => failures.push(format!(
                "{name}: 期望解析通过，得到 {} {}",
                d.code.as_str(),
                d.message
            )),
            (false, Err(d)) => {
                if d.code != DiagnosticCode::E1001 {
                    failures.push(format!(
                        "{name}: check 类期望 E1001，得到 {}",
                        d.code.as_str()
                    ));
                }
            }
            (false, Ok(_)) => failures.push(format!("{name}: 期望解析失败，但通过了")),
        }
    }
    assert!(failures.is_empty(), "夹具失败:\n{}", failures.join("\n"));
}

#[test]
fn hello_world_string_literal_matches_expected_stdout() {
    // 期望来自 hello-world/case.yaml 的 stdout「hello, conformance」：
    // 源码里唯一的字符串字面量必须正是它（AST 值 = 解码后文本）
    let dir = syntax_dir().join("hello-world");
    let src = fs::read_to_string(dir.join("main.lang-go")).expect("读 main.lang-go");
    let text = SourceText::new("hello-world/main.lang-go", src);
    let prog = parse(&text, SourceId(0)).expect("hello-world 应解析通过");
    let mut lits = Vec::new();
    for s in &prog {
        // hello-world 的调用在 fn main 体里
        let stmts: &[Stmt] = match s {
            Stmt::FnDecl { body, .. } => body,
            _ => std::slice::from_ref(s),
        };
        for st in stmts {
            if let Stmt::ExprStmt {
                expr: Expr::Call { args, .. },
                ..
            } = st
            {
                for a in args {
                    if let Expr::StrLit { value, .. } = a {
                        lits.push(value.clone());
                    }
                }
            }
        }
    }
    assert_eq!(lits, vec!["hello, conformance"]);
}
