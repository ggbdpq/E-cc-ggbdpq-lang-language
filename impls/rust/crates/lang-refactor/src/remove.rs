//! safe-remove：仅顶层 fn/全局变量、引用计数 0 才给删除编辑（specs §7）。
//!
//! 引用事实来自 `Analysis.refs`（Ident 引用；出现序即证据序）；有引用时
//! 返回 `NotRemovable { reason, ref_sites }`，refSites 形如 `path:line:col`
//! （与证据 formatRefSite 同口径）。删除区间吞掉声明后的单个换行
//! （CRLF 源含 `\r`），避免残留空行。

use lang_core::SymbolId;
use lang_edit::{Precondition, TextEdit, WorkspaceEdit};
use lang_semantic::{Symbol, SymbolKind};

use crate::input::RefactorInput;
use crate::walk::refs_of;

/// 不可删（证据面）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NotRemovable {
    pub reason: String,
    /// `path:line:col`（1 基；与证据同口径）。
    pub ref_sites: Vec<String>,
}

/// safe-remove 错误（符号未知等入口级问题）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SafeRemoveError {
    Unknown(String),
}

impl std::fmt::Display for SafeRemoveError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SafeRemoveError::Unknown(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for SafeRemoveError {}

/// 计划一次 safe-remove（dry-run）。
///
/// 外层 `Err` = 入口级错误；内层 `Err` = 不可删（附证据）；`Ok` = 可删，
/// 给出删除声明的 WorkspaceEdit。
pub fn safe_remove(
    input: &RefactorInput,
    target: SymbolId,
) -> Result<Result<WorkspaceEdit, NotRemovable>, SafeRemoveError> {
    if (target.0 as usize) >= input.analysis.symbols.len() {
        return Err(SafeRemoveError::Unknown(format!("未知符号 ID {target}")));
    }
    let sym = input.analysis.symbols.get(target);
    if sym.is_builtin {
        return Err(SafeRemoveError::Unknown(format!(
            "符号 {target} 是预载符号，不可 remove"
        )));
    }
    // 仅顶层 fn 与全局变量（协议 §7）
    match sym.kind {
        SymbolKind::Fn if !sym.is_extern => {}
        SymbolKind::Var => {}
        SymbolKind::Fn => {
            return Ok(Err(NotRemovable {
                reason: "extern 声明不支持 safe remove".to_string(),
                ref_sites: Vec::new(),
            }));
        }
        other => {
            return Ok(Err(NotRemovable {
                reason: format!(
                    "safe remove 仅支持顶层 fn 与全局变量（目标 kind={}）",
                    kind_name(other)
                ),
                ref_sites: Vec::new(),
            }));
        }
    }
    // 顶层性：声明语句必须出现在合并程序的顶层切片
    let decl_span = find_top_level_decl(input, target, sym);
    let Some(decl_span) = decl_span else {
        return Ok(Err(NotRemovable {
            reason: format!(
                "safe remove 仅支持顶层 fn 与全局变量（{} 非顶层声明）",
                sym.name
            ),
            ref_sites: Vec::new(),
        }));
    };

    // 引用计数（定义位之外的全部 Ident 引用）
    let refs = refs_of(input, target);
    if !refs.is_empty() {
        let sites: Vec<String> = refs
            .iter()
            .map(|e| {
                let sp = e.span();
                let path = input.path_of(&sp);
                if path.is_empty() {
                    format!("{}:{}", sp.start.line, sp.start.column)
                } else {
                    format!("{path}:{}:{}", sp.start.line, sp.start.column)
                }
            })
            .collect();
        return Ok(Err(NotRemovable {
            reason: format!("{} 处引用", refs.len()),
            ref_sites: sites,
        }));
    }

    // 删除编辑：[声明起点, 行尾 + 换行)；吞掉行尾 \r 与声明后的单个 \n
    let file = input.path_of(&decl_span).to_string();
    let text = input
        .sources
        .get(&decl_span.source)
        .map(|f| f.text.text())
        .unwrap_or("");
    let mut end = line_end(text, decl_span.end.offset);
    let cps: Vec<char> = text.chars().collect();
    let at = |cp: u32| -> Option<char> { cps.get(cp as usize).copied() };
    if at(end) == Some('\r') {
        end += 1;
    }
    if at(end) == Some('\n') {
        end += 1;
    }
    Ok(Ok(WorkspaceEdit {
        edits: vec![TextEdit {
            file,
            start_offset: decl_span.start.offset,
            end_offset: end,
            replacement: String::new(),
        }],
        preconditions: vec![Precondition {
            file: input.path_of(&decl_span).to_string(),
            file_hash: lang_edit::sha256_hex(text.as_bytes()),
        }],
    }))
}

/// 顶层声明 span（只在合并程序顶层切片里找）。
fn find_top_level_decl(
    input: &RefactorInput,
    target: SymbolId,
    _sym: &Symbol,
) -> Option<lang_core::Span> {
    for s in &input.merged {
        if input.analysis.symbol_of_decl(s) == Some(target) {
            return Some(s.span());
        }
    }
    None
}

/// cp 所在行的行尾（`\n` 之前）。
fn line_end(src: &str, cp: u32) -> u32 {
    let mut i = 0u32;
    for ch in src.chars() {
        if i >= cp && ch == '\n' {
            return i;
        }
        i += 1;
    }
    i
}

fn kind_name(k: SymbolKind) -> &'static str {
    match k {
        SymbolKind::Fn => "fn",
        SymbolKind::Method => "方法",
        SymbolKind::Var => "绑定",
        SymbolKind::Param => "参数",
        SymbolKind::Struct => "struct",
        SymbolKind::Enum => "enum",
        SymbolKind::Ctor => "构造器",
    }
}
