//! `dump ast|hir|ir [dir]`：编译器内部树的调试输出。
//!
//! 确定性面：ast/hir 用 derive Debug 的规范形（同输入逐字节相等），
//! ir 用 lang-ir 冻结的 `dump_program`（同 IrProgram 两次逐字节相等）。
//! 调试视图，非跨版本契约（与 lang_ir::dump 同口径声明）。

use std::path::Path;

use crate::load::load_module;
use crate::out::{error_envelope, print_json};

pub fn cmd_dump(args: &[String]) -> i32 {
    let Some(sub) = args.first().map(String::as_str) else {
        print_json(&error_envelope("dump", "缺少子命令：ast|hir|ir"));
        return 2;
    };
    if !matches!(sub, "ast" | "hir" | "ir") {
        print_json(&error_envelope("dump", &format!("未知子命令 {sub}")));
        return 2;
    }
    let dir = args
        .iter()
        .skip(1)
        .find(|a| !a.starts_with('-'))
        .cloned()
        .unwrap_or_else(|| ".".to_string());
    let mut module = match load_module(Path::new(&dir), false) {
        Ok(m) => m,
        Err(_) => {
            eprintln!("装载失败");
            return 1;
        }
    };
    match sub {
        "ast" => {
            println!("{:#?}", module.merged);
            0
        }
        _ => {
            if module.has_diagnostics() {
                eprintln!(
                    "模块存在 {} 条诊断，hir/ir dump 需要干净程序（先 check）",
                    module.diagnostics().len()
                );
                return 1;
            }
            if sub == "hir" {
                match lang_hir::HirBuilder::new(&mut module.analysis).lower_slice(&module.merged) {
                    Ok(hir) => {
                        println!("{hir:#?}");
                        0
                    }
                    Err(e) => {
                        eprintln!("HIR 构建失败: {}", e.0);
                        1
                    }
                }
            } else {
                match lang_ir::compile(&module.merged, &mut module.analysis) {
                    Ok(prog) => {
                        print!("{}", lang_ir::dump_program(&prog));
                        0
                    }
                    Err(e) => {
                        eprintln!("IR 编译失败: {e}");
                        1
                    }
                }
            }
        }
    }
}
