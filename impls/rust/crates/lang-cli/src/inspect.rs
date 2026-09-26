//! `inspect symbols|refs|type|effects|callers|callees|impact [dir] [--json]`。
//!
//! 最小实现面（契约 §2 的本波子集，简化处已在 status 登记）：
//! - symbols：非预载符号全列（SymbolId 升序 = canonical 序）；
//! - refs：`lang_refactor::walk` + `symbol_of_ref` 事实（read/write/call/type 四类；
//!   pattern 类引用 facts 层未开放，不输出）；
//! - type：`--file/--line/--column` → 最内层包含该点的表达式节点；
//! - effects：`lang_verify::effects::solve_effects`（AST 事实 → CallGraph 不动点）；
//! - callers/callees/impact：`lang_graph::from_analysis` 图（callers/callees = 沿
//!   calls/spawn 边的闭包，`--depth` 限深、缺省不限；impact = direct 入边 + 传递闭包）。

use std::collections::BTreeSet;
use std::path::Path;

use lang_contract::{Effect, FileResolver, SpanDto};
use lang_core::{Span, SymbolId};
use lang_graph::{EdgeKind, Graph, NodeKind};
use lang_semantic::SymbolKind;
use lang_syntax::{Expr, Stmt};
use lang_types::ops;
use serde_json::{Value, json};

use crate::load::{LoadFailure, Module, load_module};
use crate::out::{envelope, error_envelope, print_json};

/// inspect 参数（子命令之后）：位置参数 + 旗标。
struct InspectArgs {
    positionals: Vec<String>,
    file: Option<String>,
    line: Option<u32>,
    column: Option<u32>,
    depth: Option<usize>,
    json: bool,
}

fn parse_args(rest: &[String]) -> InspectArgs {
    let mut out = InspectArgs {
        positionals: Vec::new(),
        file: None,
        line: None,
        column: None,
        depth: None,
        json: false,
    };
    let flags_with_value = ["--file", "--line", "--column", "--depth", "--symbol"];
    let mut i = 0;
    while i < rest.len() {
        let a = &rest[i];
        if a == "--json" {
            out.json = true;
        } else if flags_with_value.contains(&a.as_str()) {
            if let Some(v) = rest.get(i + 1) {
                match a.as_str() {
                    "--file" => out.file = Some(v.clone()),
                    "--line" => out.line = v.parse().ok(),
                    "--column" => out.column = v.parse().ok(),
                    "--depth" => out.depth = v.parse().ok(),
                    _ => {}
                }
            }
            i += 1; // 跳过旗标值
        } else if !a.starts_with('-') {
            out.positionals.push(a.clone());
        }
        i += 1;
    }
    out
}

/// inspect 子命令分发。
pub fn cmd_inspect(args: &[String]) -> i32 {
    let Some(sub) = args.first() else {
        print_json(&error_envelope(
            "inspect",
            "缺少子命令：symbols|refs|type|effects|callers|callees|impact",
        ));
        return 2;
    };
    let sub = sub.as_str();
    let kind = format!("inspect.{sub}");
    let a = parse_args(&args[1..]);
    // 位置参数：refs/callers/callees/impact 取 <sym> [dir]，其余取 [dir]
    let takes_sym = matches!(
        sub,
        "refs" | "references" | "callers" | "callees" | "impact"
    );
    let (sym, dir) = if takes_sym {
        let dir = a
            .positionals
            .get(1)
            .cloned()
            .unwrap_or_else(|| ".".to_string());
        (a.positionals.first().cloned(), dir)
    } else {
        (
            None,
            a.positionals
                .first()
                .cloned()
                .unwrap_or_else(|| ".".to_string()),
        )
    };
    let module = match load_module(Path::new(&dir), false) {
        Ok(m) => m,
        Err(e) => return report_failure(&kind, &e, a.json),
    };
    if module.has_diagnostics() {
        return report_error(
            &kind,
            &format!(
                "模块存在 {} 条诊断，语义面不可查询",
                module.diagnostics().len()
            ),
            a.json,
            1,
        );
    }
    match sub {
        "symbols" => inspect_symbols(&module, &kind, a.json),
        "refs" | "references" => inspect_refs(&module, &kind, sym.as_deref(), a.json),
        "type" => inspect_type(&module, &kind, &a, a.json),
        "effects" => inspect_effects(&module, &kind, a.json),
        "callers" | "callees" | "impact" => {
            inspect_graph(&module, sub, &kind, sym.as_deref(), a.depth, a.json)
        }
        _ => {
            print_json(&error_envelope(&kind, &format!("未知子命令 {sub}")));
            2
        }
    }
}

fn report_failure(kind: &str, e: &LoadFailure, as_json: bool) -> i32 {
    let msg = match e {
        LoadFailure::Load(m) => format!("装载失败: {m}"),
        LoadFailure::Parse(diags, _) => {
            format!("模块存在 {} 条解析诊断，语义面不可查询", diags.len())
        }
    };
    report_error(kind, &msg, as_json, 1)
}

fn report_error(kind: &str, msg: &str, as_json: bool, code: i32) -> i32 {
    if as_json {
        print_json(&error_envelope(kind, msg));
    } else {
        eprintln!("{msg}");
    }
    code
}

/// 契约 kind 字符串（specs §3 Symbol）。
fn contract_kind(kind: SymbolKind) -> &'static str {
    match kind {
        SymbolKind::Fn => "function",
        SymbolKind::Method => "method",
        SymbolKind::Var => "variable",
        SymbolKind::Param => "parameter",
        SymbolKind::Struct => "struct",
        SymbolKind::Enum => "enum",
        SymbolKind::Ctor => "constructor",
    }
}

/// SpanDto 序列化总性成立（String + u32 全量字段），Err 不可达。
fn span_json(span: &Span, files: &lang_contract::SourceNames) -> Value {
    serde_json::to_value(SpanDto::of(span, files)).expect("SpanDto 序列化总性")
}

/// 符号参数解析（协议 `<sym>`：数字 id 或名字；名字多义拒绝）。
fn resolve_symbol(module: &Module, arg: Option<&str>) -> Result<SymbolId, String> {
    let Some(arg) = arg else {
        return Err("缺少符号参数（<sym>：数字 ID 或名字）".to_string());
    };
    if arg.bytes().all(|b| b.is_ascii_digit()) {
        let Ok(n) = arg.parse::<u32>() else {
            return Err(format!("未知符号 {arg}"));
        };
        let id = SymbolId(n);
        if (id.0 as usize) < module.analysis.symbols.len()
            && !module.analysis.symbols.get(id).is_builtin
        {
            return Ok(id);
        }
        return Err(format!("未知符号 {arg}"));
    }
    let hits: Vec<SymbolId> = module
        .analysis
        .symbols
        .iter()
        .filter(|s| !s.is_builtin && s.name == arg)
        .map(|s| s.id)
        .collect();
    match hits.len() {
        1 => Ok(hits[0]),
        0 => Err(format!("未知符号 {arg}")),
        _ => Err(format!("符号名 {arg} 对应多个符号，请用数字 ID")),
    }
}

// ———— symbols ————

fn inspect_symbols(module: &Module, kind: &str, as_json: bool) -> i32 {
    let mut symbols = Vec::new();
    for s in module.analysis.symbols.iter() {
        if s.is_builtin {
            continue;
        }
        let ty = module
            .analysis
            .symbol_type(s.id)
            .map(|t| ops::type_str(&module.analysis.types, t))
            .unwrap_or_else(|| "unknown".to_string());
        symbols.push(json!({
            "symbolId": s.id.0,
            "name": s.name,
            "kind": contract_kind(s.kind),
            "definition": span_json(&s.span, &module.names),
            "type": ty,
            "arity": s.arity,
            "module": module.names.file_of(s.span.source),
        }));
    }
    if as_json {
        let mut v = envelope(kind);
        v["symbols"] = json!(symbols);
        print_json(&v);
    } else {
        for s in &symbols {
            println!(
                "{}\t{}\t{}\t{}",
                s["symbolId"].as_u64().unwrap_or(0),
                s["kind"].as_str().unwrap_or(""),
                s["name"].as_str().unwrap_or(""),
                s["type"].as_str().unwrap_or(""),
            );
        }
        println!("{} 个符号", symbols.len());
    }
    0
}

// ———— refs ————

/// 目标符号的全部引用点（read/write/call/type 分类；file+offset 序）。
fn classify_refs(module: &Module, target: SymbolId) -> Vec<(Span, &'static str)> {
    let mut calls: BTreeSet<(u32, u32)> = BTreeSet::new();
    let mut writes: BTreeSet<(u32, u32)> = BTreeSet::new();
    for s in &module.merged {
        if let Stmt::Assign {
            target: Expr::Ident { span, .. },
            ..
        } = s
        {
            writes.insert((span.source.0, span.start.offset));
        }
    }
    collect_all(module, &mut |e| {
        if let Expr::Call { callee, .. } = e {
            if let Expr::Ident { span, .. } = callee.as_ref() {
                calls.insert((span.source.0, span.start.offset));
            }
        }
    });
    let mut out = Vec::new();
    collect_all(module, &mut |e| {
        if module.analysis.symbol_of_ref(e) != Some(target) {
            return;
        }
        let span = e.span();
        let key = (span.source.0, span.start.offset);
        let k = match e {
            Expr::Ident { .. } if calls.contains(&key) => "call",
            Expr::Ident { .. } if writes.contains(&key) => "write",
            Expr::Ident { .. } => "read",
            Expr::StructLit { .. } => "type",
            _ => "read",
        };
        out.push((span, k));
    });
    out.sort_by_key(|(span, _)| (span.source.0, span.start.offset));
    out
}

/// 全树表达式走查（复用 lang-refactor 的冻结 walk 面）。
fn collect_all<'a, F>(module: &'a Module, f: &mut F)
where
    F: FnMut(&'a Expr),
{
    let mut noop_stmt = |_| {};
    let mut noop_pat = |_| {};
    lang_refactor::walk::walk(&module.merged, &mut noop_stmt, f, &mut noop_pat);
}

fn inspect_refs(module: &Module, kind: &str, sym: Option<&str>, as_json: bool) -> i32 {
    let target = match resolve_symbol(module, sym) {
        Ok(id) => id,
        Err(m) => return report_error(kind, &m, as_json, 1),
    };
    let refs = classify_refs(module, target);
    if as_json {
        let mut v = envelope(kind);
        v["references"] = json!(
            refs.iter()
                .map(|(span, k)| json!({
                    "symbolId": target.0,
                    "span": span_json(span, &module.names),
                    "referenceKind": k,
                }))
                .collect::<Vec<_>>()
        );
        print_json(&v);
    } else {
        for (span, k) in &refs {
            println!(
                "{}:{}:{}\t{}",
                module.names.file_of(span.source),
                span.start.line,
                span.start.column,
                k
            );
        }
        println!("{} 处引用", refs.len());
    }
    0
}

// ———— type ————

/// (line, column) 1 基 → 0 基码点 offset（列越界取行尾；行越界取文末）。
fn offset_at(text: &str, line: u32, column: u32) -> Option<u32> {
    if line == 0 {
        return Some(0);
    }
    let mut cur_line = 1u32;
    let mut col = 1u32;
    let mut offset = 0u32;
    for ch in text.chars() {
        if cur_line == line {
            if col >= column {
                return Some(offset);
            }
            if ch == '\n' {
                return Some(offset); // 行尾兜底
            }
        }
        if ch == '\n' {
            cur_line += 1;
            col = 1;
        } else {
            col += 1;
        }
        offset += 1;
    }
    if cur_line == line {
        return Some(offset);
    }
    None
}

/// 最内层包含该点的表达式（span 最短优先；同长取先序）。
fn innermost_expr_at<'a>(
    module: &'a Module,
    sid: lang_core::SourceId,
    offset: u32,
) -> Option<(&'a Expr, Span)> {
    let mut best: Option<(&'a Expr, Span)> = None;
    collect_all(module, &mut |e| {
        let span = e.span();
        if span.source != sid {
            return;
        }
        let inside = span.start.offset <= offset
            && (offset < span.end.offset
                || (span.start.offset == span.end.offset && offset == span.start.offset));
        if !inside {
            return;
        }
        let shorter = match &best {
            None => true,
            Some((_, b)) => span.end.offset - span.start.offset < b.end.offset - b.start.offset,
        };
        if shorter {
            best = Some((e, span));
        }
    });
    best
}

fn expr_kind_name(e: &Expr) -> &'static str {
    match e {
        Expr::IntLit { .. } => "int_lit",
        Expr::FloatLit { .. } => "float_lit",
        Expr::StrLit { .. } => "str_lit",
        Expr::BoolLit { .. } => "bool_lit",
        Expr::Ident { .. } => "ident",
        Expr::Unary { .. } => "unary",
        Expr::Binary { .. } => "binary",
        Expr::Range { .. } => "range",
        Expr::Call { .. } => "call",
        Expr::Index { .. } => "index",
        Expr::Slice { .. } => "slice",
        Expr::Field { .. } => "field",
        Expr::Try { .. } => "try",
        Expr::StructLit { .. } => "struct_lit",
        Expr::MapLit { .. } => "map_lit",
        Expr::ArrLit { .. } => "array_lit",
        Expr::Match { .. } => "match",
        Expr::FnLit { .. } => "fn_lit",
    }
}

fn inspect_type(module: &Module, kind: &str, a: &InspectArgs, as_json: bool) -> i32 {
    let (Some(file), Some(line), Some(column)) = (a.file.clone(), a.line, a.column) else {
        return report_error(kind, "缺少 --file/--line/--column 参数", as_json, 2);
    };
    let Some(unit) = module.files.iter().find(|f| f.rel_path == file) else {
        return report_error(kind, &format!("未知文件 {file}"), as_json, 1);
    };
    let Some(offset) = offset_at(unit.text.text(), line, column) else {
        return report_error(
            kind,
            &format!("位置越界 {file}:{line}:{column}"),
            as_json,
            1,
        );
    };
    match innermost_expr_at(module, unit.sid, offset) {
        Some((e, span)) => {
            let ty = module
                .analysis
                .type_of(e)
                .map(|t| ops::type_str(&module.analysis.types, t))
                .unwrap_or_else(|| "unknown".to_string());
            let name = match e {
                Expr::Ident { name, .. } => Some(name.as_str()),
                _ => None,
            };
            if as_json {
                let mut v = envelope(kind);
                v["nodes"] = json!([{
                    "kind": expr_kind_name(e),
                    "name": name,
                    "type": ty,
                    "span": span_json(&span, &module.names),
                }]);
                print_json(&v);
            } else {
                println!("{}\t{}\t{}", expr_kind_name(e), name.unwrap_or("-"), ty);
            }
            0
        }
        None => report_error(kind, "未命中表达式节点", as_json, 1),
    }
}

// ———— effects ————

fn inspect_effects(module: &Module, kind: &str, as_json: bool) -> i32 {
    match lang_verify::effects::solve_effects(&module.merged, &module.analysis) {
        Ok(entries) => {
            if as_json {
                let mut v = envelope(kind);
                v["effects"] = json!(
                    entries
                        .iter()
                        .map(|e| json!({
                            "symbolId": e.id.0,
                            "name": e.name,
                            "effects": e.effects.iter().map(Effect::as_str).collect::<Vec<_>>(),
                        }))
                        .collect::<Vec<_>>()
                );
                print_json(&v);
            } else {
                for e in &entries {
                    let effs: Vec<&str> = e.effects.iter().map(Effect::as_str).collect();
                    println!("{}\t{}", e.name, effs.join(","));
                }
                println!("{} 个函数", entries.len());
            }
            0
        }
        Err(m) => report_error(kind, &format!("效果求解失败: {m}"), as_json, 1),
    }
}

// ———— callers / callees / impact ————

fn neighbors(graph: &Graph, id: SymbolId, outgoing: bool) -> Vec<SymbolId> {
    let iter = if outgoing {
        graph
            .edges_from(id)
            .filter(|e| matches!(e.kind, EdgeKind::Calls | EdgeKind::Spawn))
            .map(|e| e.to)
            .collect::<Vec<_>>()
    } else {
        graph
            .edges_to(id)
            .filter(|e| matches!(e.kind, EdgeKind::Calls | EdgeKind::Spawn))
            .map(|e| e.from)
            .collect::<Vec<_>>()
    };
    let mut v: Vec<SymbolId> = iter;
    v.sort();
    v.dedup();
    v
}

/// 沿 calls/spawn 边的闭包（depth None = 不限；不含起点）。
fn closure(graph: &Graph, start: SymbolId, outgoing: bool, depth: Option<usize>) -> Vec<SymbolId> {
    let mut seen: BTreeSet<SymbolId> = BTreeSet::new();
    let mut frontier = vec![start];
    let mut d = 0usize;
    while !frontier.is_empty() && depth.is_none_or(|max| d < max) {
        let mut next = Vec::new();
        for id in &frontier {
            for t in neighbors(graph, *id, outgoing) {
                if t != start && seen.insert(t) {
                    next.push(t);
                }
            }
        }
        frontier = next;
        d += 1;
    }
    seen.into_iter().collect()
}

fn node_json(graph: &Graph, id: SymbolId) -> Value {
    match graph.node(id) {
        Some(n) => json!({
            "symbolId": id.0,
            "name": n.name,
            "kind": node_kind_str(n.kind),
        }),
        None => json!({ "symbolId": id.0 }),
    }
}

fn node_kind_str(kind: NodeKind) -> &'static str {
    match kind {
        NodeKind::Module => "module",
        NodeKind::File => "file",
        NodeKind::Function => "function",
        NodeKind::Variable => "variable",
        NodeKind::Parameter => "parameter",
        NodeKind::Struct => "struct",
        NodeKind::Field => "field",
        NodeKind::Method => "method",
        NodeKind::Enum => "enum",
        NodeKind::Variant => "variant",
        NodeKind::Type => "type",
    }
}

fn names_of(graph: &Graph, ids: &[SymbolId]) -> Vec<String> {
    ids.iter()
        .map(|id| graph.node(*id).map(|n| n.name.clone()).unwrap_or_default())
        .collect()
}

fn inspect_graph(
    module: &Module,
    sub: &str,
    kind: &str,
    sym: Option<&str>,
    depth: Option<usize>,
    as_json: bool,
) -> i32 {
    let graph = match lang_graph::fill::from_analysis(&module.merged, &module.analysis, sub) {
        Ok(g) => g,
        Err(e) => return report_error(kind, &format!("图构建失败: {e:?}"), as_json, 1),
    };
    let target = match resolve_symbol(module, sym) {
        Ok(id) => id,
        Err(m) => return report_error(kind, &m, as_json, 1),
    };
    match sub {
        "callers" => {
            let ids = closure(&graph, target, false, depth);
            if as_json {
                let mut v = envelope(kind);
                v["callers"] = json!(
                    ids.iter()
                        .map(|id| node_json(&graph, *id))
                        .collect::<Vec<_>>()
                );
                print_json(&v);
            } else {
                for n in names_of(&graph, &ids) {
                    println!("{n}");
                }
                println!("{} 个调用方", ids.len());
            }
            0
        }
        "callees" => {
            let ids = closure(&graph, target, true, depth);
            if as_json {
                let mut v = envelope(kind);
                v["callees"] = json!(
                    ids.iter()
                        .map(|id| node_json(&graph, *id))
                        .collect::<Vec<_>>()
                );
                print_json(&v);
            } else {
                for n in names_of(&graph, &ids) {
                    println!("{n}");
                }
                println!("{} 个被调方", ids.len());
            }
            0
        }
        _ => {
            let direct = neighbors(&graph, target, false);
            let transitive = closure(&graph, target, false, None);
            if as_json {
                let mut v = envelope(kind);
                v["impact"] = json!({
                    "symbol": node_json(&graph, target),
                    "direct": direct.iter().map(|id| node_json(&graph, *id)).collect::<Vec<_>>(),
                    "transitive": transitive.iter().map(|id| node_json(&graph, *id)).collect::<Vec<_>>(),
                    "note": "证据集为保守估计，非完整影响证明",
                });
                print_json(&v);
            } else {
                println!("direct: {:?}", names_of(&graph, &direct));
                println!("transitive: {:?}", names_of(&graph, &transitive));
            }
            0
        }
    }
}
