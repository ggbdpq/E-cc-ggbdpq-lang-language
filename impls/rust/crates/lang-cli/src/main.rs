//! lang-cli 二进制入口（arg 分派在 lib::cli_main，无 clap）。
#![forbid(unsafe_code)]

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    std::process::exit(lang_cli::cli_main(&args));
}
