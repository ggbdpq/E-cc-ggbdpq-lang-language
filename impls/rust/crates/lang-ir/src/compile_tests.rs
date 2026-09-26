//! conformance 采样测试：run 类 case（expect.exitCode == 0）全量过
//! 「parse → analyze → compile → validate」链。
//!
//! 范围 = 任务登记的 13 个分类（io 需宿主 http、diagnostics 不产 IR，不做）。
//! 多文件 case（module 等）：递归收集源文件，按 import 指令做依赖先序
//! 展开（NAME-MERGE-010 的装载序近似；resolver 对 `import` 语句按
//! 「装载器已展开」处理）。compile 内部已 validate；测试再显式调
//! `validate` 复核（任务判据）。

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use lang_core::ids::SourceId;
use lang_core::source::SourceText;
use lang_semantic::analyze_stmts;

use crate::compile::compile;
use crate::validate::validate;

fn conformance_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../../conformance")
        .canonicalize()
        .expect("conformance 语料应存在")
}

/// case.yaml 是否为 run 类（期望正常退出）。
fn is_run_case(yaml: &str) -> bool {
    yaml.contains("exitCode: 0")
}

/// 收集 case 的全部源文件（递归子目录）。键 = 相对路径（正斜杠、去扩展名），
/// 对应 `import "./util/math"` 的 `util/math`。
fn collect_files(dir: &Path, base: &Path, out: &mut Vec<(String, PathBuf)>) {
    for e in std::fs::read_dir(dir).expect("case 目录可读") {
        let p = e.expect("目录项可读").path();
        if p.is_dir() {
            collect_files(&p, base, out);
        } else if p.extension().is_some_and(|x| x == "lang-go") {
            let rel = p.strip_prefix(base).expect("相对路径").with_extension("");
            let key = rel
                .components()
                .map(|c| c.as_os_str().to_string_lossy().into_owned())
                .collect::<Vec<_>>()
                .join("/");
            out.push((key, p));
        }
    }
}

/// 源码里的 import 相对路径 → 本 case 内的文件键。
fn import_keys_of(text: &str, keys: &[String]) -> Vec<String> {
    let st = SourceText::new("probe.lang-go", text.to_string());
    let Ok(stmts) = lang_syntax::parse(&st, SourceId(0)) else {
        return Vec::new();
    };
    stmts
        .iter()
        .filter_map(|s| match s {
            lang_syntax::Stmt::Import { path, .. } => {
                let key = path.trim_start_matches("./").trim_end_matches(".lang-go");
                keys.iter().any(|k| k == key).then(|| key.to_string())
            }
            _ => None,
        })
        .collect()
}

/// 合并序：依赖先序展开 import（post-order DFS，防环）；根按名字序确定。
fn case_sources(dir: &Path) -> Vec<(String, String)> {
    let mut files = Vec::new();
    collect_files(dir, dir, &mut files);
    files.sort_by(|a, b| a.0.cmp(&b.0));
    let texts: Vec<(String, String)> = files
        .into_iter()
        .map(|(k, p)| {
            let t = std::fs::read_to_string(&p).expect("case 源码可读");
            (k, t)
        })
        .collect();
    let keys: Vec<String> = texts.iter().map(|(k, _)| k.clone()).collect();
    let by_key: HashMap<String, String> = texts.iter().cloned().collect();
    let imported: Vec<String> = texts
        .iter()
        .flat_map(|(_, t)| import_keys_of(t, &keys))
        .collect();
    let roots: Vec<String> = keys
        .iter()
        .filter(|k| !imported.contains(k))
        .cloned()
        .collect();
    let mut order = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for root in &roots {
        expand_imports(root, &by_key, &mut order, &mut seen);
    }
    for k in &keys {
        if !seen.contains(k) {
            order.push(k.clone());
        }
    }
    order
        .into_iter()
        .map(|k| {
            let t = by_key[&k].clone();
            (k, t)
        })
        .collect()
}

fn expand_imports(
    key: &str,
    by_key: &HashMap<String, String>,
    order: &mut Vec<String>,
    seen: &mut std::collections::HashSet<String>,
) {
    if !seen.insert(key.to_string()) {
        return;
    }
    let Some(text) = by_key.get(key) else {
        return;
    };
    for dep in import_keys_of(text, &by_key.keys().cloned().collect::<Vec<_>>()) {
        expand_imports(&dep, by_key, order, seen);
    }
    order.push(key.to_string());
}

/// 单 case：合并源码 → analyze → compile → validate。
fn run_case(dir: &Path) -> Result<(), String> {
    let sources = case_sources(dir);
    let mut stmts = Vec::new();
    for (i, (key, text)) in sources.iter().enumerate() {
        let source = SourceId(i as u32);
        let st = SourceText::new(format!("{key}.lang-go"), text.clone());
        let mut parsed = lang_syntax::parse(&st, source).map_err(|d| format!("parse: {d:?}"))?;
        stmts.append(&mut parsed);
    }
    let mut analysis = analyze_stmts(&stmts, SourceId(0));
    assert!(
        analysis.diags.is_empty(),
        "run case 不应有语义诊断: {:?}",
        analysis.diags
    );
    let prog = compile(&stmts, &mut analysis).map_err(|e| format!("compile: {e}"))?;
    validate(&prog).map_err(|e| format!("validate: {e}"))?;
    Ok(())
}

/// 每分类一个测试：枚举全部 run case 逐个过链；失败即指出 case 名。
fn check_category(cat: &str) {
    let root = conformance_root().join(cat);
    let mut dirs: Vec<PathBuf> = std::fs::read_dir(&root)
        .unwrap_or_else(|e| panic!("分类 {cat} 目录可读: {e}"))
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.is_dir())
        .collect();
    dirs.sort();
    let mut ran = 0;
    for d in &dirs {
        let yaml = std::fs::read_to_string(d.join("case.yaml"))
            .unwrap_or_else(|e| panic!("case.yaml 可读 {d:?}: {e}"));
        if !is_run_case(&yaml) {
            continue;
        }
        let name = d
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned();
        if let Err(e) = run_case(d) {
            panic!("conformance {cat}/{name} 失败: {e}");
        }
        ran += 1;
    }
    assert!(ran > 0, "分类 {cat} 应至少覆盖一个 run case（实跑 {ran}）");
    println!("{cat}: {ran} run cases 全过");
}

#[test]
fn conformance_syntax() {
    check_category("syntax");
}

#[test]
fn conformance_expressions() {
    check_category("expressions");
}

#[test]
fn conformance_values() {
    check_category("values");
}

#[test]
fn conformance_closure() {
    check_category("closure");
}

#[test]
fn conformance_match() {
    check_category("match");
}

#[test]
fn conformance_option() {
    check_category("option");
}

#[test]
fn conformance_result() {
    check_category("result");
}

#[test]
fn conformance_generics() {
    check_category("generics");
}

#[test]
fn conformance_struct() {
    check_category("struct");
}

#[test]
fn conformance_enum() {
    check_category("enum");
}

#[test]
fn conformance_collections() {
    check_category("collections");
}

#[test]
fn conformance_concurrency() {
    check_category("concurrency");
}

#[test]
fn conformance_module() {
    check_category("module");
}
