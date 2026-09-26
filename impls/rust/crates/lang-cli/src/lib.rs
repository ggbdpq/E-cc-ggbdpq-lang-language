//! lang-cli —— lang-rs 命令行工具（组合层：装载/语义/解释/后端/重构/验证全接线）。
//!
//! 命令面（无 clap，手写 arg 分派，零新依赖——serde_json 为 workspace 统一源）：
//! run / check / test / fmt / build / verify / inspect / dump / refactor / version。
#![forbid(unsafe_code)]

mod commands;
mod dump;
mod inspect;
mod load;
mod out;
mod refactor;
mod verify;

pub use commands::CollectSink;

/// 语义化版本（随 workspace `0.1.0`）。
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// `--version` 的规范输出行：`lang-rs <版本>`。
pub fn version_line() -> String {
    format!("lang-rs {VERSION}")
}

/// 用法文本（stdout/stderr 共用）。
pub fn usage() -> String {
    "lang-rs 工具链\n\n用法: lang-cli <命令> [参数]\n\n命令:\n  run [dir]                装载目录并执行（目录即包，默认当前目录）\n  check [dir] [--json]     语义检查（稳定诊断码；有诊断 exit 1）\n  test [dir]               执行 *_test.lang-go 里的 test* 函数（前缀、无参）\n  fmt [dir]                就地格式化目录内 .lang-go（幂等）\n  build [go|js] [dir]      编译并写 out/main.go 或 out/main.mjs\n  verify [dir] [--json]    九段全管线验证（parse→…→differential）\n  inspect symbols|refs|type|effects|callers|callees|impact [dir] [--json]\n                           语义查询（SymbolId/graph 事实面）\n  dump ast|hir|ir [dir]    编译器内部树调试输出（确定性）\n  refactor rename|organize-imports|safe-remove [--symbol N] [--to NAME] [--apply] [--json]\n                           语义级重构（默认 dry-run，--apply 落盘 + 验证闭环）\n  version, --version, -V   打印版本\n  help, --help, -h         打印本帮助\n"
        .to_string()
}

/// 入口分派（返回进程退出码）。
pub fn cli_main(args: &[String]) -> i32 {
    match args.first().map(String::as_str) {
        None => {
            eprint!("{}", usage());
            2
        }
        Some("run") => commands::cmd_run(&args[1..]),
        Some("check") => commands::cmd_check(&args[1..]),
        Some("test") => commands::cmd_test(&args[1..]),
        Some("fmt") => commands::cmd_fmt(&args[1..]),
        Some("build") => commands::cmd_build(&args[1..]),
        Some("verify") => verify::cmd_verify(&args[1..]),
        Some("inspect") => inspect::cmd_inspect(&args[1..]),
        Some("dump") => dump::cmd_dump(&args[1..]),
        Some("refactor") => refactor::cmd_refactor(&args[1..]),
        Some("version" | "--version" | "-V") => commands::cmd_version(),
        Some("help" | "--help" | "-h") => {
            print!("{}", usage());
            0
        }
        Some(other) => commands::cmd_unknown(other),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_line_is_pinned() {
        // 任务冻结：`--version` 输出 "lang-rs 0.1.0"
        assert_eq!(version_line(), "lang-rs 0.1.0");
    }

    #[test]
    fn usage_lists_all_verbs() {
        let u = usage();
        for verb in [
            "run", "check", "test", "fmt", "build", "verify", "inspect", "dump", "refactor",
            "version",
        ] {
            assert!(u.contains(verb), "usage 缺命令 {verb}");
        }
    }

    #[test]
    fn unknown_command_exits_two() {
        assert_eq!(cli_main(&["nope".to_string()]), 2);
        assert_eq!(cli_main(&[]), 2);
    }

    #[test]
    fn version_command_exits_zero() {
        assert_eq!(cli_main(&["version".to_string()]), 0);
        assert_eq!(cli_main(&["--version".to_string()]), 0);
    }
}
