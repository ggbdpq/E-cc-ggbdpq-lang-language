//! CLI 统一装载面：lang-loader 装载 → 逐文件 parse（SourceId 按装载序分配）→
//! 合并语句序列 → `analyze_stmts` 语义分析 → 诊断聚合。
//!
//! SourceId 约定（与 lang-refactor::RefactorInput::from_files 同一口径）：
//! 每个文件按**装载序**分配 `SourceId(i)`（span 随 AST 节点保留原 id，
//! DTO 反查经 [`Module::names`]）；合并序列用 `SourceId(0)` 作分析锚
//! （无锚诊断如 E3009 落在首文件行 0）。
//!
//! 测试发现规则（对照 TS loader.ts loadProgram）：仅扫描 `*_test.lang-go`
//! 的顶层语句，`fn` 声明且无接收者、无参数、名字 `test` 前缀。

use std::collections::HashSet;
use std::path::Path;

use lang_contract::SourceNames;
use lang_core::{Diagnostic, SourceId, SourceText};
use lang_loader::load_program;
use lang_semantic::{Analysis, analyze_stmts};
use lang_syntax::{Stmt, parse};

/// 单个已解析文件。
pub struct FileUnit {
    pub rel_path: String,
    pub text: SourceText,
    pub sid: SourceId,
    pub stmts: Vec<Stmt>,
    /// 解析失败时的 E1001 诊断（stmts 为空；成功为 None）。
    pub parse_diag: Option<Diagnostic>,
}

/// 装载失败形态：装载器错误（stderr 直出，非 Diagnostic）或解析诊断聚合。
pub enum LoadFailure {
    /// 装载器错误（文件缺失/import 环等；消息文本已含位置前缀）。
    Load(String),
    /// 任一文件解析失败：报告全部解析诊断 + SourceId 反查表（不做语义分析，
    /// 避免缺文件的连锁噪声）。
    Parse(Vec<Diagnostic>, SourceNames),
}

/// 已装载并分析的一个模块。
pub struct Module {
    /// 装载序文件（被导入先于导入者，同目录文件名序）。
    pub files: Vec<FileUnit>,
    /// 合并语句序列（NAME-MERGE-010；`analysis` 消费的同一份缓冲）。
    pub merged: Vec<Stmt>,
    pub analysis: Analysis,
    /// SourceId → 文件相对路径（DTO `file` 反查）。
    pub names: SourceNames,
    /// 测试函数名（发现序 = 测试文件文件名序 × 语句序）。
    pub tests: Vec<String>,
}

impl Module {
    /// 全部聚合诊断（解析诊断在前，语义诊断在后）。
    pub fn diagnostics(&self) -> Vec<Diagnostic> {
        let mut out: Vec<Diagnostic> = self
            .files
            .iter()
            .filter_map(|f| f.parse_diag.clone())
            .collect();
        out.extend(self.analysis.diags.iter().cloned());
        out
    }

    pub fn has_diagnostics(&self) -> bool {
        self.files.iter().any(|f| f.parse_diag.is_some()) || !self.analysis.diags.is_empty()
    }
}

/// 装载 `dir` 为一个模块。`include_tests` 时追加 `*_test.lang-go`（run/check
/// 传 false，test 传 true——MODULE-TEST-007）。
pub fn load_module(dir: &Path, include_tests: bool) -> Result<Module, LoadFailure> {
    let loaded = load_program(dir, include_tests, &mut lang_loader::parse_imports)
        .map_err(|e| LoadFailure::Load(e.message))?;

    let mut files: Vec<FileUnit> = Vec::with_capacity(loaded.files.len());
    let mut names = SourceNames::new();
    let mut merged: Vec<Stmt> = Vec::new();
    let mut parse_diags: Vec<Diagnostic> = Vec::new();
    for (i, f) in loaded.files.into_iter().enumerate() {
        let sid = SourceId(i as u32);
        let text = SourceText::new(f.rel_path.clone(), f.source);
        let (stmts, parse_diag) = match parse(&text, sid) {
            Ok(stmts) => (stmts, None),
            Err(d) => (Vec::new(), Some(d)),
        };
        if let Some(d) = &parse_diag {
            parse_diags.push(d.clone());
        } else {
            // import 语句剥除（装载器的文件级合并已完成依赖先序；语句流上
            // import 是空操作标记——解释器拒绝执行 Import，参考实现
            // expand 在语句位替换后也不保留 import 语句）
            merged.extend(
                stmts
                    .iter()
                    .filter(|s| !matches!(s, Stmt::Import { .. }))
                    .cloned(),
            );
        }
        files.push(FileUnit {
            rel_path: f.rel_path.clone(),
            text,
            sid,
            stmts,
            parse_diag,
        });
        names.insert(sid, f.rel_path);
    }
    if !parse_diags.is_empty() {
        return Err(LoadFailure::Parse(parse_diags, names));
    }

    // 测试发现（仅 *_test.lang-go 的顶层 fn；test 前缀、无接收者、无参数）
    let test_set: HashSet<&str> = loaded.test_files.iter().map(String::as_str).collect();
    let mut tests = Vec::new();
    for f in &files {
        if !test_set.contains(f.rel_path.as_str()) {
            continue;
        }
        for s in &f.stmts {
            if let Stmt::FnDecl {
                name, recv, params, ..
            } = s
            {
                if recv.is_none() && params.is_empty() && name.starts_with("test") {
                    tests.push(name.clone());
                }
            }
        }
    }

    let analysis = analyze_stmts(&merged, SourceId(0));
    Ok(Module {
        files,
        merged,
        analysis,
        names,
        tests,
    })
}
