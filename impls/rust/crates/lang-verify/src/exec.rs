//! 执行段注入面（specs/runner-contract.md verify 动词 + 通用约束 5）。
//!
//! 管线的四个执行段（AST 解释 / IR 解释 / →Go 生成+执行 / →JS 生成+执行）
//! 与 HIR→IR lowering 经 [`ExecDriver`] 注入：A3/A4/A5 的实现方认领各自段，
//! 管线只认本 trait。工具链（go/node）缺失必须 [`ExecError::Skipped`]
//! 显式呈现——不静默跳过、不谎报通过。
//!
//! 内建实现：
//! - [`StubExec`]：全段 Skipped 桩（编排/短路测试用）；
//! - [`DefaultExec`]：五段全接——A3（`lang_ir::IrLowerer`，语义查询面经
//!   `&Analysis` 注入）、A4（`lang_interp` AST/IR 双解释器）、A5
//!   （`lang_backend_go`/`lang_backend_js` codegen + 宿主工具链执行）。

use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicU32, Ordering};

use lang_interp::Interp;
use lang_interp::ir::IrMachine;
use lang_ir::{IrProgram, LowerIr};
use lang_runtime::{Machine, OutputSink};
use lang_semantic::Analysis;
use lang_syntax::Stmt;

/// 一路执行的产物（差分比对面：stdout 逐字节 + exitCode + stderr 为空）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RunOutput {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
}

/// 执行段失败形态：Skipped = 环境性跳过（不算失败），Failed = 真实失败。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ExecError {
    Skipped(String),
    Failed(String),
}

impl std::fmt::Display for ExecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ExecError::Skipped(m) => write!(f, "skipped: {m}"),
            ExecError::Failed(m) => write!(f, "failed: {m}"),
        }
    }
}

/// AST 执行段的输入：合并语句序列 + 同一棵树的语义分析产物。
pub struct ExecContext<'a> {
    pub stmts: &'a [Stmt],
    pub analysis: &'a Analysis,
}

/// 执行段注入 trait（A3/A4/A5 实现方认领；管线只依赖本面）。
pub trait ExecDriver {
    /// AST 参考解释器（A4）。
    fn run_ast(&mut self, ctx: &ExecContext<'_>) -> Result<RunOutput, ExecError>;
    /// HIR → Core IR lowering（A3；未接入 → Skipped）。`analysis` 供
    /// 需要语义查询面（TypeTables/SymbolArena）的实现方使用。
    fn lower_ir(
        &mut self,
        hir: &lang_hir::HirProg,
        analysis: &Analysis,
    ) -> Result<IrProgram, ExecError>;
    /// IR 解释器（A4）。
    fn run_ir(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError>;
    /// →Go 生成 + 宿主执行（A5 + go 工具链；缺失 → Skipped）。
    fn run_go(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError>;
    /// →JS 生成 + 宿主执行（A5 + node 工具链；缺失 → Skipped）。
    fn run_js(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError>;
}

/// 全段 Skipped 桩：编排/短路测试用（管线对 Skipped 的可见性即本桩的验收）。
#[derive(Default)]
pub struct StubExec;

impl ExecDriver for StubExec {
    fn run_ast(&mut self, _ctx: &ExecContext<'_>) -> Result<RunOutput, ExecError> {
        Err(ExecError::Skipped("桩：执行段未注入".to_string()))
    }
    fn lower_ir(
        &mut self,
        _hir: &lang_hir::HirProg,
        _analysis: &Analysis,
    ) -> Result<IrProgram, ExecError> {
        Err(ExecError::Skipped("桩：执行段未注入".to_string()))
    }
    fn run_ir(&mut self, _prog: &IrProgram) -> Result<RunOutput, ExecError> {
        Err(ExecError::Skipped("桩：执行段未注入".to_string()))
    }
    fn run_go(&mut self, _prog: &IrProgram) -> Result<RunOutput, ExecError> {
        Err(ExecError::Skipped("桩：执行段未注入".to_string()))
    }
    fn run_js(&mut self, _prog: &IrProgram) -> Result<RunOutput, ExecError> {
        Err(ExecError::Skipped("桩：执行段未注入".to_string()))
    }
}

/// 捕获解释器输出的 sink。
struct VecSink(String);

impl OutputSink for VecSink {
    fn write(&mut self, s: &str) {
        self.0.push_str(s);
    }
}

/// 内建执行驱动：A3 lowering + AST/IR 双解释器（A4）+ 双后端生成 +
/// 宿主工具链执行（A5），五段全接。
///
/// 工具链缺失 → [`ExecDriver::run_go`]/[`ExecDriver::run_js`] 如实 Skipped；
/// 需要定制执行面的宿主可自带 [`ExecDriver`] 实现（tests/e2e.rs 的
/// TestDriver 即记录/篡改产物的包装示例）。
#[derive(Default)]
pub struct DefaultExec {
    go: Option<bool>,
    node: Option<bool>,
}

impl DefaultExec {
    pub fn new() -> Self {
        Self::default()
    }

    /// 工具链可用性探测（每进程至多一次；结果缓存）。
    fn probe(&mut self, which: &str) -> bool {
        let slot = match which {
            "go" => &mut self.go,
            "node" => &mut self.node,
            _ => return false,
        };
        if let Some(v) = *slot {
            return v;
        }
        // go 的版本子命令无连字符；node 需 --version（`node version` 会当脚本跑）
        let flag = if which == "go" {
            "version"
        } else {
            "--version"
        };
        let ok = Command::new(which)
            .arg(flag)
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false);
        *slot = Some(ok);
        ok
    }
}

impl ExecDriver for DefaultExec {
    fn run_ast(&mut self, ctx: &ExecContext<'_>) -> Result<RunOutput, ExecError> {
        let mut interp = Interp::new(Machine::new());
        let mut sink = VecSink(String::new());
        match interp.run(ctx.stmts, ctx.analysis, &mut sink) {
            Ok(()) => Ok(RunOutput {
                stdout: sink.0,
                stderr: String::new(),
                exit_code: 0,
            }),
            Err(e) => Err(ExecError::Failed(format!("AST 解释器: {}", e.message()))),
        }
    }

    fn lower_ir(
        &mut self,
        hir: &lang_hir::HirProg,
        analysis: &Analysis,
    ) -> Result<IrProgram, ExecError> {
        // A3 的 IrLowerer 构造期注入语义查询面（TypeId→名 / SymbolId→函数等映射）
        let mut lowerer = lang_ir::IrLowerer::new(&analysis.types, &analysis.symbols);
        lowerer
            .lower(hir)
            .map_err(|e| ExecError::Failed(format!("HIR→IR lowering: {}", e.0)))
    }

    fn run_ir(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError> {
        let mut m = IrMachine::new(prog);
        let mut sink = VecSink(String::new());
        match m.run(&mut sink) {
            Ok(()) => Ok(RunOutput {
                stdout: sink.0,
                stderr: String::new(),
                exit_code: 0,
            }),
            Err(e) => Err(ExecError::Failed(format!("IR 解释器: {}", e.message()))),
        }
    }

    fn run_go(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError> {
        let src = lang_backend_go::codegen::generate(prog)
            .map_err(|e| ExecError::Failed(format!("→Go 生成: {e}")))?;
        if !self.probe("go") {
            return Err(ExecError::Skipped("宿主缺 go 工具链".to_string()));
        }
        let tmp = TempDir::new("lang-verify-go")?;
        let src_path = tmp.path.join("main.go");
        write_file(&src_path, src.as_bytes())?;
        let bin = tmp.path.join(if cfg!(windows) {
            "main_bin.exe"
        } else {
            "main_bin"
        });
        let build = Command::new("go")
            .args(["build", "-o"])
            .arg(&bin)
            .arg(&src_path)
            .output()
            .map_err(|e| ExecError::Failed(format!("go build 启动失败: {e}")))?;
        if !build.status.success() {
            return Err(ExecError::Failed(format!(
                "go build 失败: {}",
                String::from_utf8_lossy(&build.stderr)
            )));
        }
        run_process(&bin)
    }

    fn run_js(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError> {
        let src = lang_backend_js::codegen::generate(prog)
            .map_err(|e| ExecError::Failed(format!("→JS 生成: {e}")))?;
        if !self.probe("node") {
            return Err(ExecError::Skipped("宿主缺 node 工具链".to_string()));
        }
        let tmp = TempDir::new("lang-verify-js")?;
        let src_path = tmp.path.join("main.mjs");
        write_file(&src_path, src.as_bytes())?;
        // .mjs 不能被 Windows 直接执行（os error 193）——必须经 node 启动
        let out = Command::new("node")
            .arg(&src_path)
            .output()
            .map_err(|e| ExecError::Failed(format!("node 执行失败: {e}")))?;
        Ok(collect_output(&out))
    }
}

/// 运行产物文件并采集三要素。
fn run_process(bin: &std::path::Path) -> Result<RunOutput, ExecError> {
    let out = Command::new(bin)
        .output()
        .map_err(|e| ExecError::Failed(format!("执行产物失败: {e}")))?;
    Ok(collect_output(&out))
}

/// 进程产物 → 三要素（stdout/stderr/exit）。
fn collect_output(out: &std::process::Output) -> RunOutput {
    RunOutput {
        stdout: String::from_utf8_lossy(&out.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&out.stderr).into_owned(),
        exit_code: out.status.code().unwrap_or(-1),
    }
}

fn write_file(path: &std::path::Path, bytes: &[u8]) -> Result<(), ExecError> {
    std::fs::write(path, bytes)
        .map_err(|e| ExecError::Failed(format!("写 {} 失败: {e}", path.display())))
}

/// 系统临时目录下的独占工作目录（Drop 清理；纪律：临时文件不落仓库）。
struct TempDir {
    path: PathBuf,
}

static TEMP_SEQ: AtomicU32 = AtomicU32::new(0);

impl TempDir {
    fn new(tag: &str) -> Result<Self, ExecError> {
        let seq = TEMP_SEQ.fetch_add(1, Ordering::Relaxed);
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let path = std::env::temp_dir().join(format!("{tag}-{}-{seq}-{nanos}", std::process::id()));
        std::fs::create_dir_all(&path)
            .map_err(|e| ExecError::Failed(format!("建临时目录失败: {e}")))?;
        Ok(TempDir { path })
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stub_exec_skips_every_segment() {
        let mut d = StubExec;
        let prog = lang_ir::IrProgram {
            structs: Vec::new(),
            enums: Vec::new(),
            globals: Vec::new(),
            functions: Vec::new(),
            init: None,
            main: None,
        };
        let empty_hir = lang_hir::HirProg {
            structs: Vec::new(),
            enums: Vec::new(),
            globals: Vec::new(),
            fns: Vec::new(),
            main: None,
        };
        let empty_analysis = lang_semantic::analyze_stmts(&[], lang_core::SourceId(0));
        assert!(matches!(
            d.lower_ir(&empty_hir, &empty_analysis),
            Err(ExecError::Skipped(_))
        ));
        assert!(matches!(d.run_ir(&prog), Err(ExecError::Skipped(_))));
        assert!(matches!(d.run_go(&prog), Err(ExecError::Skipped(_))));
        assert!(matches!(d.run_js(&prog), Err(ExecError::Skipped(_))));
    }

    #[test]
    fn temp_dir_is_removed_on_drop() {
        let path;
        {
            let tmp = TempDir::new("lang-verify-selftest").unwrap();
            path = tmp.path.clone();
            assert!(path.exists());
        }
        assert!(!path.exists());
    }
}
