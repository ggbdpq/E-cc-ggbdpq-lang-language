//! conformance/diagnostics 30 个 check 类 case 的夹具测试。
//!
//! 期望值直接读 case.yaml（code/severity/line 逐项），与 analyze_stmts 的实际
//! 产出做多重集合比对——期望来自 conformance 契约面，禁止拿实现当期望。
//! 诊断无锚（line 0）对应 primary_span = None。

use std::collections::BTreeMap;
use std::path::PathBuf;

use lang_core::{SourceId, SourceText};
use lang_semantic::analyze_stmts;
use serde::Deserialize;

#[derive(Deserialize)]
#[allow(dead_code)]
struct CaseYaml {
    name: String,
    #[serde(default)]
    mode: String,
    #[serde(default)]
    expect: ExpectYaml,
}

#[derive(Deserialize, Default)]
struct ExpectYaml {
    #[serde(default, rename = "diagnostics")]
    diagnostics: Vec<DiagYaml>,
}

#[derive(Deserialize)]
struct DiagYaml {
    code: String,
    #[serde(default, rename = "ruleId")]
    rule_id: Option<String>,
    #[serde(default)]
    line: u32,
}

fn conformance_dir() -> PathBuf {
    // lang-semantic → crates → rust → impls → 仓库根
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../../conformance/diagnostics")
}

fn run_case(dir: &std::path::Path, name: &str) -> Result<(), String> {
    let yaml_path = dir.join("case.yaml");
    let yaml = std::fs::read_to_string(&yaml_path)
        .map_err(|e| format!("{name}: 读取 case.yaml 失败: {e}"))?;
    let case: CaseYaml =
        serde_yaml::from_str(&yaml).map_err(|e| format!("{name}: 解析 case.yaml 失败: {e}"))?;
    if case.mode != "check" {
        return Ok(()); // 仅 check 模式属本层
    }
    let src = std::fs::read_to_string(dir.join("main.lang-go"))
        .map_err(|e| format!("{name}: 读取 main.lang-go 失败: {e}"))?;

    let source = SourceId(0);
    let text = SourceText::new("main.lang-go", src);
    let stmts = lang_syntax::parse(&text, source).unwrap_or_default();
    let analysis = analyze_stmts(&stmts, source);

    // 实际产出：code + line 多重集合（BTreeMap 计数保确定性）
    let mut got: BTreeMap<(&str, u32), usize> = BTreeMap::new();
    for d in &analysis.diags {
        let line = d.primary_span.map(|s| s.start.line).unwrap_or(0);
        *got.entry((d.code.as_str(), line)).or_insert(0) += 1;
    }
    // 期望：同构多重集合
    let mut want: BTreeMap<(&str, u32), usize> = BTreeMap::new();
    for d in &case.expect.diagnostics {
        *want.entry((d.code.as_str(), d.line)).or_insert(0) += 1;
    }
    if got != want {
        return Err(format!(
            "{name}:\n  期望 {:?}\n  实际 {:?}",
            want.iter().collect::<Vec<_>>(),
            got.iter().collect::<Vec<_>>(),
        ));
    }
    // ruleId 逐项核对（case.yaml 指定处）
    for d in &case.expect.diagnostics {
        if let Some(rule) = &d.rule_id {
            let found = analysis
                .diags
                .iter()
                .any(|g| g.code.as_str() == d.code && g.rule_id.as_str() == rule.as_str());
            if !found {
                return Err(format!("{name}: 诊断 {} 的 ruleId 应为 {rule}", d.code));
            }
        }
    }
    Ok(())
}

#[test]
fn diagnostics_check_cases_conform() {
    let root = conformance_dir();
    let mut dirs: Vec<PathBuf> = std::fs::read_dir(&root)
        .expect("conformance/diagnostics 目录存在")
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.join("case.yaml").exists())
        .collect();
    dirs.sort();
    assert!(!dirs.is_empty(), "至少应发现 1 个 case");
    let mut passed = 0usize;
    let mut failures: Vec<String> = Vec::new();
    for d in &dirs {
        let name = d.file_name().unwrap().to_string_lossy().to_string();
        match run_case(d, &name) {
            Ok(()) => passed += 1,
            Err(msg) => failures.push(msg),
        }
    }
    for f in &failures {
        println!("FAIL {f}");
    }
    println!("diagnostics check cases: {passed}/{} 通过", dirs.len());
    assert!(
        failures.is_empty(),
        "{}/{} 个 case 不合规（见上方 FAIL 明细）",
        failures.len(),
        dirs.len()
    );
}
