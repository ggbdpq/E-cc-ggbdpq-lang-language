//! 重构输入：模块文件（源码 + 词元 + 语句）+ 合并语句序列 + 语义分析。
//!
//! 构造纪律（lang-semantic 冻结契约）：`analyze_stmts` 的查询视图按 AST
//! 节点指针取事实——[`RefactorInput::from_files`] 先建合并语句序列，再对
//! **同一份缓冲** 跑分析，之后不再改动该序列。SourceId 按文件路径字典序
//! 分配（确定性），span → 文件反查走 [`ModuleSources`]。

use std::collections::BTreeMap;
use std::path::Path;

use lang_core::{DiagnosticCode, SourceId, SourceText, Span, SymbolId};
use lang_loader::{ImportRef, load_program};
use lang_semantic::Analysis;
use lang_syntax::{Stmt, Token, parse};

/// 模块中的一个文件：相对路径 + 源文本 + 全量词元 + 语句序列。
pub struct SourceFile {
    pub path: String,
    pub text: SourceText,
    pub tokens: Vec<Token>,
    pub stmts: Vec<Stmt>,
}

/// 文件集合（SourceId → 文件；BTreeMap 保证迭代确定性）。
pub type ModuleSources = BTreeMap<SourceId, SourceFile>;

/// 重构输入（一次构造，rename/safe_remove/apply 共享）。
pub struct RefactorInput {
    /// SourceId → 文件（含 span 反查所需的路径与源码）。
    pub sources: ModuleSources,
    /// 合并后的语句序列（NAME-MERGE-010；分析所消费的同一缓冲）。
    pub merged: Vec<Stmt>,
    /// 语义分析产物（refs/decl_syms/symbols 查询面）。
    pub analysis: Analysis,
}

impl RefactorInput {
    /// 从 (相对路径, 源码) 列表构造；任一文件解析失败返回 Err（重构只对
    /// 可解析模块负责；解析面归 verify 管线报告）。
    pub fn from_files(files: Vec<(String, String)>) -> Result<RefactorInput, String> {
        // 路径字典序分配 SourceId（确定性）
        let mut sorted = files;
        sorted.sort_by(|a, b| a.0.cmp(&b.0));
        let mut sources: ModuleSources = BTreeMap::new();
        let mut merged: Vec<Stmt> = Vec::new();
        for (i, (path, text)) in sorted.into_iter().enumerate() {
            let sid = SourceId(i as u32);
            let st = SourceText::new(path.clone(), text);
            let stmts = parse(&st, sid).map_err(|d| {
                format!(
                    "load: {} 第 {} 行: {}",
                    path,
                    d.primary_span.map(|s| s.start.line).unwrap_or(0),
                    d.message
                )
            })?;
            let tokens = lang_syntax::lex(&st, sid).unwrap_or_default();
            merged.extend(stmts.iter().cloned());
            sources.insert(
                sid,
                SourceFile {
                    path,
                    text: st,
                    tokens,
                    stmts,
                },
            );
        }
        let analysis = lang_semantic::analyze_stmts(&merged, SourceId(0));
        Ok(RefactorInput {
            sources,
            merged,
            analysis,
        })
    }

    /// 从目录装载（lang-loader：目录即包 + import 展开）。
    pub fn load(dir: &Path) -> Result<RefactorInput, String> {
        let mut imports_of = |rel: &str, src: &str| -> Vec<ImportRef> {
            let text = SourceText::new(rel, src.to_string());
            match parse(&text, SourceId(0)) {
                Ok(stmts) => stmts
                    .iter()
                    .filter_map(|s| match s {
                        Stmt::Import { path, span } => Some(ImportRef {
                            path: path.clone(),
                            line: span.start.line,
                        }),
                        _ => None,
                    })
                    .collect(),
                Err(_) => Vec::new(), // 装载仅抽 import；解析面错误由 from_files 报
            }
        };
        let loaded = load_program(dir, false, &mut imports_of).map_err(|e| e.message)?;
        let files = loaded
            .files
            .into_iter()
            .map(|f| (f.rel_path, f.source))
            .collect();
        Self::from_files(files)
    }

    /// 「分析时」的文件内容快照（apply 事务的 precondition 比对基线）。
    pub fn current(&self) -> BTreeMap<String, String> {
        self.sources
            .values()
            .map(|f| (f.path.clone(), f.text.text().to_string()))
            .collect()
    }

    /// span → 文件相对路径（未登记 → ""，与契约面一致）。
    pub fn path_of(&self, span: &Span) -> &str {
        self.sources
            .get(&span.source)
            .map(|f| f.path.as_str())
            .unwrap_or("")
    }

    /// 基线诊断码多重集（apply 闭环的零回归断言基线；按码字符串序）。
    pub fn baseline_codes(&self) -> Vec<DiagnosticCode> {
        let mut codes: Vec<DiagnosticCode> = self.analysis.diags.iter().map(|d| d.code).collect();
        codes.sort_by_key(|c| c.as_str());
        codes
    }

    /// 目标解析（协议 `<sym>`：数字 SymbolId 或符号名；预载符号不可指名）。
    /// 名字命中多个 → 拒绝要求用 id（与证据一致）。
    pub fn resolve_target(&self, arg: &str) -> Result<SymbolId, String> {
        if arg.is_empty() {
            return Err("缺少 --symbol 参数".to_string());
        }
        if arg.bytes().all(|b| b.is_ascii_digit()) {
            let Ok(n) = arg.parse::<u32>() else {
                return Err(format!("未知符号 ID {arg}"));
            };
            let id = SymbolId(n);
            if (id.0 as usize) < self.analysis.symbols.len()
                && !self.analysis.symbols.get(id).is_builtin
            {
                return Ok(id);
            }
            return Err(format!("未知符号 ID {arg}"));
        }
        let hits: Vec<SymbolId> = self
            .analysis
            .symbols
            .iter()
            .filter(|s| !s.is_builtin && s.name == arg)
            .map(|s| s.id)
            .collect();
        match hits.len() {
            1 => Ok(hits[0]),
            0 => Err(format!("未知符号 {arg}")),
            _ => Err(format!("符号名 {arg} 对应多个符号，请用 --symbol <id>")),
        }
    }
}
