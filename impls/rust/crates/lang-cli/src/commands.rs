//! 五个基础动词：run / check / test / fmt / build（version 在 lib.rs）。

use std::cell::RefCell;
use std::path::Path;
use std::rc::Rc;

use lang_core::{Position, SourceId, Span};
use lang_interp::Interp;
use lang_runtime::{Machine, OutputSink};
use lang_syntax::{Expr, Stmt};

use crate::load::{LoadFailure, Module, load_module};
use crate::out::{check_envelope, error_envelope, format_diags, print_json};
use crate::{VERSION, usage};

/// 是否带某旗标。
pub fn has_flag(args: &[String], flag: &str) -> bool {
    args.iter().any(|a| a == flag)
}

/// 第一个非旗标位置参数（默认 "."；`--json` 等旗标被跳过）。
pub fn positional(args: &[String]) -> String {
    args.iter()
        .find(|a| !a.starts_with('-'))
        .cloned()
        .unwrap_or_else(|| ".".to_string())
}

/// 装载模块或报告失败（返回 Some(模块) 为成功；否则已打印并给出退出码）。
fn load_or_report(
    dir: &str,
    include_tests: bool,
    as_json: bool,
    kind: &str,
) -> (Option<Module>, i32) {
    match load_module(Path::new(dir), include_tests) {
        Ok(m) => (Some(m), 0),
        Err(LoadFailure::Load(msg)) => {
            if as_json {
                print_json(&error_envelope(kind, &format!("装载失败: {msg}")));
            } else {
                eprintln!("装载失败: {msg}");
            }
            (None, 1)
        }
        Err(LoadFailure::Parse(diags, names)) => {
            // 解析诊断属诊断面：text 形走 check 文本契约，json 形走 check 信封
            if as_json {
                print_json(&check_envelope(&diags, &names));
            } else {
                eprintln!("检查未通过:\n{}", format_diags(&diags, &names));
            }
            (None, 1)
        }
    }
}

/// `run [dir]`：装载 → analyze（诊断非空即停，诊断打印 JSON 信封）→
/// AST 参考解释器执行，stdout 直出。装载器错误不是 Diagnostic，走 stderr。
pub fn cmd_run(args: &[String]) -> i32 {
    let dir = positional(args);
    let (module, code) = load_or_report(&dir, false, false, "run");
    let Some(module) = module else { return code };
    if module.has_diagnostics() {
        print_json(&check_envelope(&module.diagnostics(), &module.names));
        return 1;
    }
    let mut interp = Interp::new(Machine::new());
    let mut out = Stdout;
    match interp.run(&module.merged, &module.analysis, &mut out) {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("错误: {}", e.message());
            1
        }
    }
}

/// `check [dir] [--json]`：语义检查 + 稳定诊断码（诊断契约：有诊断 exit 1）。
pub fn cmd_check(args: &[String]) -> i32 {
    let dir = positional(args);
    let as_json = has_flag(args, "--json");
    let (module, code) = load_or_report(&dir, false, as_json, "check");
    let Some(module) = module else { return code };
    let diags = module.diagnostics();
    if as_json {
        print_json(&check_envelope(&diags, &module.names));
    } else if !diags.is_empty() {
        eprintln!("检查未通过:\n{}", format_diags(&diags, &module.names));
        return 1;
    } else {
        println!("检查通过");
    }
    if diags.is_empty() { 0 } else { 1 }
}

/// `test [dir]`：扫描 test* 函数执行（test 前缀、无接收者、无参）。
///
/// 执行序对照 TS 证据（cli/main.ts runTests）：evalChunk（不跑 main）→
/// 逐个 callByName。Rust 侧解释器无公开 callByName（A4 冻结面），最小绕行：
/// 先执行「去掉顶层 `fn main`」的合并序列（等价 evalChunk），再逐测试以
/// 合成的单语句调用切片走 `Interp::run`（状态在同一 interp 上共享，
/// gate 传同一份干净 Analysis——解释器只消费 has_errors 门）。
pub fn cmd_test(args: &[String]) -> i32 {
    let dir = positional(args);
    let (module, code) = load_or_report(&dir, true, false, "test");
    let Some(module) = module else { return code };
    if module.has_diagnostics() {
        eprintln!(
            "检查未通过:\n{}",
            format_diags(&module.diagnostics(), &module.names)
        );
        return 1;
    }
    if module.tests.is_empty() {
        println!("未发现 *_test.lang-go");
        return 0;
    }
    // evalChunk 等价：顶层语句全执行、main 不调用（TS 证据同语义）
    let body: Vec<Stmt> = module
        .merged
        .iter()
        .filter(|s| !matches!(s, Stmt::FnDecl { name, recv: None, .. } if name == "main"))
        .cloned()
        .collect();
    let mut interp = Interp::new(Machine::new());
    let mut out = Stdout;
    if let Err(e) = interp.run(&body, &module.analysis, &mut out) {
        eprintln!("装载失败: {}", e.message());
        return 1;
    }
    let mut pass = 0usize;
    let mut fail = 0usize;
    for name in &module.tests {
        let call = test_call_stmt(name);
        let slice = [call];
        match interp.run(&slice, &module.analysis, &mut out) {
            Ok(()) => {
                pass += 1;
                println!("PASS {name}");
            }
            Err(e) => {
                fail += 1;
                println!("FAIL {name}: {}", e.message());
            }
        }
    }
    println!("\n{pass} 通过, {fail} 失败");
    if fail > 0 { 1 } else { 0 }
}

/// 合成 `name()` 单语句调用（test 运行器的 callByName 等价物）。
/// span 为合成锚点（不参与语义分析——gate 用既有干净 Analysis）。
fn test_call_stmt(name: &str) -> Stmt {
    let span = Span::point(SourceId(0), Position::new(0, 1, 1));
    Stmt::ExprStmt {
        span,
        expr: Expr::Call {
            span,
            callee: Box::new(Expr::Ident {
                span,
                name: name.to_string(),
            }),
            args: Vec::new(),
        },
    }
}

/// `fmt [dir]`：就地格式化目录内全部 .lang-go（不递归，Go 证据同口径）。
pub fn cmd_fmt(args: &[String]) -> i32 {
    let dir = positional(args);
    let entries = match std::fs::read_dir(&dir) {
        Ok(e) => e,
        Err(e) => {
            eprintln!("读目录失败: {e}");
            return 2;
        }
    };
    let mut names: Vec<String> = entries
        .filter_map(|e| e.ok())
        .filter(|e| e.path().is_file())
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| n.ends_with(".lang-go"))
        .collect();
    names.sort();
    let mut changed = 0usize;
    for name in &names {
        let path = Path::new(&dir).join(name);
        let src = match std::fs::read_to_string(&path) {
            Ok(s) => s,
            Err(e) => {
                eprintln!("读文件失败: {e}");
                return 1;
            }
        };
        let formatted = match lang_formatter::format_source(&src) {
            Ok(f) => f,
            Err(d) => {
                eprintln!(
                    "格式化 {name} 失败: 第{}行: {}",
                    d.primary_span.as_ref().map(|s| s.start.line).unwrap_or(0),
                    d.message
                );
                return 1;
            }
        };
        if formatted != src {
            if let Err(e) = std::fs::write(&path, formatted) {
                eprintln!("写文件失败: {e}");
                return 1;
            }
            println!("{name}");
            changed += 1;
        }
    }
    println!("{changed} 个文件已格式化");
    0
}

/// `build [go|js] [dir]`：检查通过后编译 → codegen 写 `{dir}/out/main.{go,mjs}`。
pub fn cmd_build(args: &[String]) -> i32 {
    let mut target = "go";
    let mut dir = ".";
    let mut seen_pos = 0usize;
    for a in args {
        if a.starts_with('-') {
            continue;
        }
        if seen_pos == 0 && (a == "go" || a == "js") {
            target = a;
        } else {
            dir = a;
        }
        seen_pos += 1;
    }
    let (module, code) = load_or_report(dir, false, false, "build");
    let Some(mut module) = module else {
        return code;
    };
    if module.has_diagnostics() {
        eprintln!(
            "检查未通过:\n{}",
            format_diags(&module.diagnostics(), &module.names)
        );
        return 1;
    }
    let prog = match lang_ir::compile(&module.merged, &mut module.analysis) {
        Ok(p) => p,
        Err(e) => {
            eprintln!("编译失败: {e}");
            return 1;
        }
    };
    let generated: Result<(String, &str, &str), String> = if target == "go" {
        lang_backend_go::codegen::generate(&prog)
            .map(|s| {
                (
                    s,
                    "main.go",
                    "out/main.go（go build ./out 或 go run ./out/main.go 执行）",
                )
            })
            .map_err(|e| e.0)
    } else {
        lang_backend_js::codegen::generate(&prog)
            .map(|s| (s, "main.mjs", "out/main.mjs（node out/main.mjs 执行）"))
            .map_err(|e| e.0)
    };
    let (src, file, hint) = match generated {
        Ok(t) => t,
        Err(msg) => {
            eprintln!("→{target} 转译失败: {msg}");
            return 1;
        }
    };
    let out_dir = Path::new(dir).join("out");
    if let Err(e) = std::fs::create_dir_all(&out_dir) {
        eprintln!("建 out/ 失败: {e}");
        return 1;
    }
    if let Err(e) = std::fs::write(out_dir.join(file), src) {
        eprintln!("写产物失败: {e}");
        return 1;
    }
    println!("{hint}");
    0
}

/// `version` / `--version`。
pub fn cmd_version() -> i32 {
    println!("lang-rs {VERSION}");
    0
}

/// 未知命令（usage 到 stderr + 退出码 2）。
pub fn cmd_unknown(name: &str) -> i32 {
    eprintln!("未知命令 {name:?}\n");
    eprint!("{}", usage());
    2
}

// ———— 输出汇 ————

/// stdout 直出（run/test 的 println 通道）。
struct Stdout;

impl OutputSink for Stdout {
    fn write(&mut self, s: &str) {
        print!("{s}");
    }
}

/// 收集型输出汇（库内复用）。
#[derive(Clone, Debug, Default)]
pub struct CollectSink(pub Rc<RefCell<String>>);

impl OutputSink for CollectSink {
    fn write(&mut self, s: &str) {
        self.0.borrow_mut().push_str(s);
    }
}
