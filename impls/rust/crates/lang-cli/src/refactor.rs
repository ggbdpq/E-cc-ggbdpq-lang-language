//! `refactor rename|organize-imports|safe-remove ... [--apply] [--json] [dir]`。
//!
//! 语义级重构走 lang-refactor 冻结入口（SymbolId 事实，禁 Regex）；默认
//! dry-run 只打印/输出编辑计划，`--apply` 经 `apply_and_verify` 闭环
//! （precondition → apply → format → reparse → 诊断零回归）后落盘。
//! precondition（sha256）由本层按「分析时快照」补齐——与 Go 证据同分工。

use std::collections::BTreeMap;
use std::path::Path;

use lang_edit::{Precondition, sha256_hex};
use lang_refactor::{RefactorInput, apply_and_verify, organize_imports, rename, safe_remove};
use serde_json::{Value, json};

use crate::out::{envelope, error_envelope, print_json};

/// 参数（子命令之后）。
struct RefactorArgs {
    symbol: Option<String>,
    to: Option<String>,
    apply: bool,
    json: bool,
    dir: String,
}

fn parse_args(rest: &[String]) -> RefactorArgs {
    let mut out = RefactorArgs {
        symbol: None,
        to: None,
        apply: false,
        json: false,
        dir: ".".to_string(),
    };
    let mut i = 0;
    while i < rest.len() {
        let a = rest[i].as_str();
        match a {
            "--symbol" => {
                if let Some(v) = rest.get(i + 1) {
                    out.symbol = Some(v.clone());
                }
                i += 1;
            }
            "--to" => {
                if let Some(v) = rest.get(i + 1) {
                    out.to = Some(v.clone());
                }
                i += 1;
            }
            "--apply" => out.apply = true,
            "--json" => out.json = true,
            other => {
                if !other.starts_with('-') {
                    out.dir = other.to_string();
                }
            }
        }
        i += 1;
    }
    out
}

pub fn cmd_refactor(args: &[String]) -> i32 {
    let Some(sub) = args.first().map(String::as_str) else {
        print_json(&error_envelope(
            "refactor",
            "缺少子命令：rename|organize-imports|safe-remove",
        ));
        return 2;
    };
    if !matches!(sub, "rename" | "organize-imports" | "safe-remove") {
        print_json(&error_envelope("refactor", &format!("未知子命令 {sub}")));
        return 2;
    }
    let kind = format!("refactor.{sub}");
    let a = parse_args(&args[1..]);
    let input = match RefactorInput::load(Path::new(&a.dir)) {
        Ok(i) => i,
        Err(m) => return fail(&kind, &m, a.json),
    };
    match sub {
        "rename" => do_rename(&kind, &input, &a),
        "organize-imports" => do_organize(&kind, &input, &a),
        _ => do_remove(&kind, &input, &a),
    }
}

fn fail(kind: &str, msg: &str, as_json: bool) -> i32 {
    if as_json {
        print_json(&error_envelope(kind, msg));
    } else {
        eprintln!("{msg}");
    }
    1
}

/// 编辑涉及文件的 sha256 前置（分析时快照）。lang-refactor 各入口已按声明
/// 文件填过一层 precondition，这里只补缺（按文件去重，不重复添加）。
fn with_preconditions(ws: &mut lang_edit::WorkspaceEdit, input: &RefactorInput) {
    let current = input.current();
    let mut covered: BTreeMap<String, ()> = ws
        .preconditions
        .iter()
        .map(|p| (p.file.clone(), ()))
        .collect();
    for e in &ws.edits {
        if covered.contains_key(&e.file) {
            continue;
        }
        let Some(text) = current.get(&e.file) else {
            continue;
        };
        covered.insert(e.file.clone(), ());
        ws.preconditions.push(Precondition {
            file: e.file.clone(),
            file_hash: sha256_hex(text.as_bytes()),
        });
    }
}

fn ws_json(ws: &lang_edit::WorkspaceEdit) -> Value {
    json!({
        "edits": ws.edits.iter().map(|e| json!({
            "file": e.file,
            "startOffset": e.start_offset,
            "endOffset": e.end_offset,
            "replacement": e.replacement,
        })).collect::<Vec<_>>(),
        "preconditions": ws.preconditions.iter().map(|p| json!({
            "file": p.file,
            "fileHash": p.file_hash,
        })).collect::<Vec<_>>(),
    })
}

/// dry-run 报告（契约 §2：applied/editCount/symbol/workspaceEdit）。
fn report_plan(
    kind: &str,
    input: &RefactorInput,
    args: &RefactorArgs,
    mut ws: lang_edit::WorkspaceEdit,
    symbol: Option<SymbolJson>,
) -> i32 {
    with_preconditions(&mut ws, input);
    if args.json {
        let mut v = envelope(kind);
        v["applied"] = json!(false);
        v["editCount"] = json!(ws.edits.len());
        if let Some(s) = &symbol {
            v["symbol"] = json!({ "id": s.id, "name": s.name, "kind": s.kind });
        }
        v["workspaceEdit"] = ws_json(&ws);
        print_json(&v);
    } else {
        println!(
            "重构计划（dry-run，--apply 落盘）：{} 处编辑",
            ws.edits.len()
        );
        for e in &ws.edits {
            println!(
                "  {}:[{}..{}) → {:?}",
                e.file, e.start_offset, e.end_offset, e.replacement
            );
        }
    }
    0
}

struct SymbolJson {
    id: u32,
    name: String,
    kind: &'static str,
}

/// 应用 + 闭环验证 + 落盘。
fn apply_flow(
    kind: &str,
    input: &RefactorInput,
    args: &RefactorArgs,
    mut ws: lang_edit::WorkspaceEdit,
    dir: &str,
) -> i32 {
    with_preconditions(&mut ws, input);
    match apply_and_verify(&ws, input) {
        Ok(contents) => {
            let mut written = 0usize;
            for (file, text) in &contents {
                if input.current().get(file) == Some(text) {
                    continue; // 未被编辑的文件不重写
                }
                let path = Path::new(dir).join(file);
                if let Err(e) = std::fs::write(&path, text) {
                    return fail(kind, &format!("写 {file}: {e}"), args.json);
                }
                written += 1;
            }
            if args.json {
                let mut v = envelope(kind);
                v["applied"] = json!(true);
                v["editCount"] = json!(ws.edits.len());
                v["writtenFiles"] = json!(written);
                print_json(&v);
            } else {
                println!(
                    "已应用 {} 处编辑（写 {written} 个文件），验证通过",
                    ws.edits.len()
                );
            }
            0
        }
        Err(e) => fail(kind, &e.to_string(), args.json),
    }
}

fn do_rename(kind: &str, input: &RefactorInput, args: &RefactorArgs) -> i32 {
    let Some(sym_arg) = args.symbol.clone() else {
        return fail(kind, "缺少 --symbol 参数", args.json);
    };
    let Some(to) = args.to.clone() else {
        return fail(kind, "缺少 --to 参数", args.json);
    };
    let target = match input.resolve_target(&sym_arg) {
        Ok(id) => id,
        Err(m) => return fail(kind, &m, args.json),
    };
    let sym = input.analysis.symbols.get(target);
    let sym_json = SymbolJson {
        id: target.0,
        name: sym.name.clone(),
        kind: sym.kind.kind_name(),
    };
    match rename(input, target, &to) {
        Ok(ws) => {
            if args.apply {
                apply_flow(kind, input, args, ws, &args.dir)
            } else {
                report_plan(kind, input, args, ws, Some(sym_json))
            }
        }
        Err(e) => fail(kind, &e.to_string(), args.json),
    }
}

fn do_organize(kind: &str, input: &RefactorInput, args: &RefactorArgs) -> i32 {
    // 每文件独立整理；变化文件以整文件替换编辑表达（Go 证据同口径）
    let mut ws = lang_edit::WorkspaceEdit::default();
    for f in input.sources.values() {
        match organize_imports(&f.path, f.text.text()) {
            Ok(per) => {
                ws.edits.extend(per.edits);
                ws.preconditions.extend(per.preconditions);
            }
            Err(e) => return fail(kind, &e.to_string(), args.json),
        }
    }
    if args.apply {
        apply_flow(kind, input, args, ws, &args.dir)
    } else {
        report_plan(kind, input, args, ws, None)
    }
}

fn do_remove(kind: &str, input: &RefactorInput, args: &RefactorArgs) -> i32 {
    let Some(sym_arg) = args.symbol.clone() else {
        return fail(kind, "缺少 --symbol 参数", args.json);
    };
    let target = match input.resolve_target(&sym_arg) {
        Ok(id) => id,
        Err(m) => return fail(kind, &m, args.json),
    };
    let sym = input.analysis.symbols.get(target);
    let sym_json = json!({
        "id": target.0,
        "name": sym.name,
        "kind": sym.kind.kind_name(),
    });
    match safe_remove(input, target) {
        Ok(Ok(ws)) => {
            if args.apply {
                apply_flow(kind, input, args, ws, &args.dir)
            } else {
                report_plan(kind, input, args, ws, None)
            }
        }
        Ok(Err(nr)) => {
            // 不可删：证据面（removable:false + reason + refSites），退出码 1
            if args.json {
                let mut v = envelope(kind);
                v["removable"] = json!(false);
                v["reason"] = json!(nr.reason);
                v["refSites"] = json!(nr.ref_sites);
                v["symbol"] = sym_json;
                print_json(&v);
            } else {
                eprintln!("不可删：{}", nr.reason);
                for s in &nr.ref_sites {
                    eprintln!("  引用点 {s}");
                }
            }
            1
        }
        Err(e) => fail(kind, &e.to_string(), args.json),
    }
}
