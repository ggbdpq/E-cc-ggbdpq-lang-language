//! verify 输入：模块目录（runner-contract：输入一律为程序目录）或内存文件集。

use std::collections::BTreeMap;
use std::path::Path;

use lang_core::{SourceId, SourceText};
use lang_loader::{ImportRef, load_program};
use lang_syntax::Stmt;

/// 单文件解析失败形态：文件相对路径 + 装箱诊断（Diagnostic 体积大）。
pub type ParseFailure = (String, Box<lang_core::Diagnostic>);

/// verify 的输入文件集：相对路径 → 源码（路径字典序 = 确定性）。
#[derive(Clone, Debug, Default)]
pub struct VerifyInput {
    pub files: Vec<(String, String)>,
}

impl VerifyInput {
    /// 内存文件集（测试/宿主直供）。
    pub fn from_files(files: Vec<(String, String)>) -> Self {
        let mut files = files;
        files.sort_by(|a, b| a.0.cmp(&b.0));
        VerifyInput { files }
    }

    /// 从目录装载（lang-loader：目录即包 + import 展开；与 run/check 同一装载面）。
    pub fn from_dir(dir: &Path) -> Result<Self, String> {
        let mut imports_of = |rel: &str, src: &str| -> Vec<ImportRef> {
            let text = SourceText::new(rel, src.to_string());
            match lang_syntax::parse(&text, SourceId(0)) {
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
                Err(_) => Vec::new(), // 装载仅抽 import；解析面错误由管线 Parse 段报告
            }
        };
        let loaded = load_program(dir, false, &mut imports_of).map_err(|e| e.message)?;
        Ok(VerifyInput::from_files(
            loaded
                .files
                .into_iter()
                .map(|f| (f.rel_path, f.source))
                .collect(),
        ))
    }

    /// 逐文件解析（路径字典序分配 SourceId）；任一文件解析失败即 Err。
    /// 失败负载 = (文件路径, 装箱诊断)——Diagnostic 体积大，装箱控错误负载。
    pub fn parse_all(&self) -> Result<Vec<(String, Vec<Stmt>)>, ParseFailure> {
        let mut out = Vec::new();
        for (i, (path, text)) in self.files.iter().enumerate() {
            let st = SourceText::new(path.clone(), text.clone());
            match lang_syntax::parse(&st, SourceId(i as u32)) {
                Ok(stmts) => out.push((path.clone(), stmts)),
                Err(d) => return Err((path.clone(), Box::new(d))),
            }
        }
        Ok(out)
    }

    /// 「分析时」的内容快照（差分/复查用）。
    pub fn snapshot(&self) -> BTreeMap<String, String> {
        self.files.iter().cloned().collect()
    }
}
