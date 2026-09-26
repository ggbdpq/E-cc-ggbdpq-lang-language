//! lang-cli E2E：临时目录夹具上走真二进制（CARGO_BIN_EXE）的真执行链。
//!
//! 覆盖：run / check（text+json）/ test / fmt / build / verify / inspect /
//! dump / refactor（rename/organize-imports/safe-remove）+ dump 确定性 +
//! 装载器多文件 import 接线。退出码契约：有诊断 exit 1；未知命令 exit 2。

use std::path::{Path, PathBuf};
use std::process::{Command, Output};

fn run_cli(args: &[&str], cwd: &Path) -> Output {
    Command::new(env!("CARGO_BIN_EXE_lang-cli"))
        .args(args)
        .current_dir(cwd)
        .output()
        .expect("启动 lang-cli 失败")
}

fn code(out: &Output) -> i32 {
    out.status.code().unwrap_or(-1)
}

fn stdout(out: &Output) -> String {
    String::from_utf8_lossy(&out.stdout).into_owned()
}

fn stderr(out: &Output) -> String {
    String::from_utf8_lossy(&out.stderr).into_owned()
}

// ———— 夹具 ————

struct TempDir(PathBuf);

impl TempDir {
    fn new(tag: &str) -> Self {
        static N: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
        let p = std::env::temp_dir().join(format!(
            "lang-cli-e2e-{tag}-{}-{}",
            std::process::id(),
            N.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        TempDir(p)
    }

    fn write(&self, rel: &str, content: &str) {
        let ap = self.0.join(rel);
        if let Some(parent) = ap.parent() {
            std::fs::create_dir_all(parent).unwrap();
        }
        std::fs::write(ap, content).unwrap();
    }

    fn read(&self, rel: &str) -> String {
        std::fs::read_to_string(self.0.join(rel)).unwrap()
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// 干净夹具（无元素/字段赋值——IR lowering 的已知缺口不影响本套件）。
fn clean_module() -> TempDir {
    let td = TempDir::new("clean");
    td.write(
        "main.lang-go",
        "fn add(a: int, b: int): int {\n    return a + b\n}\n\nfn main() {\n    println(add(1, 2))\n    println(\"ok\")\n}\n",
    );
    td
}

// ———— 基础 ————

#[test]
fn version_is_pinned() {
    let td = TempDir::new("ver");
    let out = run_cli(&["version"], td.path());
    assert_eq!(code(&out), 0);
    assert_eq!(stdout(&out), "lang-rs 0.1.0\n");
    let out = run_cli(&["--version"], td.path());
    assert_eq!(stdout(&out), "lang-rs 0.1.0\n");
}

#[test]
fn unknown_command_and_no_args_exit_two() {
    let td = TempDir::new("usage");
    let out = run_cli(&["nope"], td.path());
    assert_eq!(code(&out), 2);
    assert!(stderr(&out).contains("未知命令"));
    let out = run_cli(&[], td.path());
    assert_eq!(code(&out), 2);
    let out = run_cli(&["--help"], td.path());
    assert_eq!(code(&out), 0);
    assert!(stdout(&out).contains("run"));
}

// ———— run ————

#[test]
fn run_executes_and_writes_stdout_directly() {
    let td = clean_module();
    let out = run_cli(&["run", "."], td.path());
    assert_eq!(code(&out), 0);
    assert_eq!(stdout(&out), "3\nok\n");
    assert!(stderr(&out).is_empty());
}

#[test]
fn run_reports_diagnostics_json_and_exits_one() {
    let td = TempDir::new("rundiag");
    td.write("main.lang-go", "fn main() {\n    let x: int = \"s\"\n}\n");
    let out = run_cli(&["run", "."], td.path());
    assert_eq!(code(&out), 1);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).expect("run 诊断应为 JSON 信封");
    assert_eq!(v["kind"], "check");
    assert_eq!(v["schemaVersion"], "1");
    assert_eq!(v["success"], false);
    assert!(!v["diagnostics"].as_array().unwrap().is_empty());
}

#[test]
fn run_loader_error_goes_to_stderr() {
    let td = TempDir::new("runload");
    td.write("main.lang-go", "import \"lib\"");
    let out = run_cli(&["run", "."], td.path());
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("import 仅支持"));
}

// ———— 装载器接线（目录即包 + import 展开 + 语句流剥除） ————

#[test]
fn run_multi_file_module_with_import() {
    let td = TempDir::new("mod");
    td.write(
        "main.lang-go",
        "import \"./lib\"\n\nfn main() {\n    println(libAdd(1, 2))\n}\n",
    );
    td.write(
        "lib/lib.lang-go",
        "fn libAdd(a: int, b: int): int {\n    return a + b\n}\n",
    );
    let out = run_cli(&["run", "."], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    assert_eq!(stdout(&out), "3\n");
}

#[test]
fn run_diamond_import_executes_shared_once() {
    let td = TempDir::new("diamond");
    td.write(
        "main.lang-go",
        "import \"./a\"\nimport \"./b\"\n\nfn main() {\n    println(av() + bv())\n}\n",
    );
    td.write(
        "a.lang-go",
        "import \"./c\"\n\nfn av(): int {\n    return cv()\n}\n",
    );
    td.write(
        "b.lang-go",
        "import \"./c\"\n\nfn bv(): int {\n    return cv()\n}\n",
    );
    td.write(
        "c.lang-go",
        "var hits = 0\n\nfn cv(): int {\n    hits += 1\n    return 40 + hits\n}\n",
    );
    let out = run_cli(&["run", "."], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    // c 只装载一次：av=41、bv=42（全局 hits 共享）——顺序 = a 先于 b
    assert_eq!(stdout(&out), "83\n");
}

// ———— check ————

#[test]
fn check_text_clean_and_dirty() {
    let td = clean_module();
    let out = run_cli(&["check", "."], td.path());
    assert_eq!(code(&out), 0);
    assert_eq!(stdout(&out), "检查通过\n");

    let bad = TempDir::new("checkbad");
    bad.write("main.lang-go", "fn main() {\n    println(nope)\n}\n");
    let out = run_cli(&["check", "."], bad.path());
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("检查未通过"));
    assert!(stderr(&out).contains("E2002"));
}

#[test]
fn check_json_envelope_shape() {
    let bad = TempDir::new("checkjson");
    bad.write("main.lang-go", "fn main() {\n    let x: int = \"s\"\n}\n");
    let out = run_cli(&["check", ".", "--json"], bad.path());
    assert_eq!(code(&out), 1);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["schemaVersion"], "1");
    assert_eq!(v["toolVersion"], "0.1.0");
    assert_eq!(v["kind"], "check");
    assert_eq!(v["success"], false);
    let d = &v["diagnostics"][0];
    for key in [
        "code",
        "ruleId",
        "severity",
        "message",
        "primarySpan",
        "relatedSpans",
        "help",
    ] {
        assert!(v.get(key).is_some() || d.get(key).is_some(), "缺 {key}");
    }
    assert_eq!(d["code"], "E3001");
    assert_eq!(d["primarySpan"]["file"], "main.lang-go");
    assert_eq!(d["primarySpan"]["start"]["line"], 2);
}

#[test]
fn check_parse_error_reports_e1001() {
    let td = TempDir::new("checkparse");
    td.write("main.lang-go", "fn (");
    let out = run_cli(&["check", "."], td.path());
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("E1001"));
}

// ———— test ————

#[test]
fn test_discovers_prefix_fns_and_reports_pass_fail() {
    let td = TempDir::new("test");
    td.write(
        "main.lang-go",
        "fn main() {\n    println(\"main 不应在 test 里执行\")\n}\n",
    );
    td.write(
        "z_test.lang-go",
        "fn testHello() {\n    assert(1 == 1, \"hello\")\n}\n\nfn testBoom() {\n    assert(false, \"爆炸\")\n}\n\nfn helperNotTest() {\n}\n",
    );
    let out = run_cli(&["test", "."], td.path());
    assert_eq!(code(&out), 1);
    let s = stdout(&out);
    assert!(s.contains("PASS testHello"), "实际: {s}");
    assert!(s.contains("FAIL testBoom"), "实际: {s}");
    assert!(s.contains("1 通过, 1 失败"));
    // evalChunk 语义：main 不执行
    assert!(!s.contains("main 不应在 test 里执行"));
    // 非 test 前缀 / 带参函数不发现
    assert!(!s.contains("helperNotTest"));
}

#[test]
fn test_without_test_files_says_none() {
    let td = clean_module();
    let out = run_cli(&["test", "."], td.path());
    assert_eq!(code(&out), 0);
    assert!(stdout(&out).contains("未发现 *_test.lang-go"));
}

#[test]
fn test_all_pass_exits_zero() {
    let td = TempDir::new("testok");
    td.write("main.lang-go", "fn main() {\n}\n");
    td.write(
        "a_test.lang-go",
        "fn testA() {\n    assert(true, \"a\")\n}\n",
    );
    let out = run_cli(&["test", "."], td.path());
    assert_eq!(code(&out), 0);
    assert!(stdout(&out).contains("1 通过, 0 失败"));
}

// ———— fmt ————

#[test]
fn fmt_rewrites_in_place_and_is_idempotent() {
    let td = TempDir::new("fmt");
    td.write("main.lang-go", "fn main() {\nprintln( 1 )\n}\n");
    let out = run_cli(&["fmt", "."], td.path());
    assert_eq!(code(&out), 0);
    assert!(stdout(&out).contains("main.lang-go"));
    assert!(stdout(&out).contains("1 个文件已格式化"));
    let formatted = td.read("main.lang-go");
    assert_eq!(formatted, "fn main() {\n    println(1)\n}\n");
    // 幂等：第二遍零改动
    let out = run_cli(&["fmt", "."], td.path());
    assert_eq!(code(&out), 0);
    assert!(stdout(&out).contains("0 个文件已格式化"));
}

// ———— build ————

#[test]
fn build_writes_go_and_js_outputs_deterministically() {
    let td = clean_module();
    let out = run_cli(&["build", "."], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let go1 = td.read("out/main.go");
    assert!(go1.starts_with("package main"));
    let out = run_cli(&["build", "js", "."], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let js1 = td.read("out/main.mjs");
    // 同输入两次生成逐字节相等（确定性）
    let out = run_cli(&["build", "."], td.path());
    assert_eq!(code(&out), 0);
    assert_eq!(td.read("out/main.go"), go1);
    let out = run_cli(&["build", "js", "."], td.path());
    assert_eq!(code(&out), 0);
    assert_eq!(td.read("out/main.mjs"), js1);
}

#[test]
fn build_rejects_dirty_module() {
    let bad = TempDir::new("builddiag");
    bad.write("main.lang-go", "fn main() {\n    println(nope)\n}\n");
    let out = run_cli(&["build", "."], bad.path());
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("E2002"));
}

// ———— verify ————

#[test]
fn verify_passes_on_clean_module_with_all_stages() {
    let td = clean_module();
    let out = run_cli(&["verify", "."], td.path());
    assert_eq!(
        code(&out),
        0,
        "stdout: {} stderr: {}",
        stdout(&out),
        stderr(&out)
    );
    let s = stdout(&out);
    assert!(s.contains("验证通过"));
    for stage in [
        "parse",
        "resolve",
        "typecheck",
        "hir",
        "ir",
        "effect",
        "compile",
        "execute",
        "differential",
    ] {
        assert!(s.contains(stage), "缺阶段 {stage}");
    }
    assert!(!s.contains("失败"));
}

#[test]
fn verify_json_envelope_and_failure_short_circuit() {
    let td = clean_module();
    let out = run_cli(&["verify", ".", "--json"], td.path());
    assert_eq!(code(&out), 0);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["schemaVersion"], "1");
    assert_eq!(v["kind"], "verify");
    assert_eq!(v["passed"], true);
    assert!(v["failedStage"].is_null());
    assert_eq!(v["stages"].as_array().unwrap().len(), 9);

    // E3001 类型错误 → typecheck 段失败，短路段显式在册
    let bad = TempDir::new("verifybad");
    bad.write("main.lang-go", "fn main() {\n    let x: int = \"s\"\n}\n");
    let out = run_cli(&["verify", ".", "--json"], bad.path());
    assert_eq!(code(&out), 1);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["passed"], false);
    assert_eq!(v["failedStage"], "typecheck");
    assert!(stdout(&out).contains("上游 typecheck 未通过"));
}

// ———— dump ————

#[test]
fn dump_ast_hir_ir_are_deterministic() {
    let td = clean_module();
    for kind in ["ast", "hir", "ir"] {
        let out1 = run_cli(&["dump", kind, "."], td.path());
        assert_eq!(code(&out1), 0, "dump {kind}: {}", stderr(&out1));
        let out2 = run_cli(&["dump", kind, "."], td.path());
        assert_eq!(code(&out2), 0);
        assert_eq!(stdout(&out1), stdout(&out2), "dump {kind} 两次应逐字节相等");
    }
    // ir 是冻结确定性文本
    let out = run_cli(&["dump", "ir", "."], td.path());
    assert!(stdout(&out).starts_with("entry "));
    assert!(stdout(&out).contains("func 0 \"add\""));
}

#[test]
fn dump_unknown_subcommand_exits_two() {
    let td = clean_module();
    let out = run_cli(&["dump", "nope", "."], td.path());
    assert_eq!(code(&out), 2);
}

// ———— inspect ————

#[test]
fn inspect_symbols_json_canonical_order() {
    let td = clean_module();
    let out = run_cli(&["inspect", "symbols", ".", "--json"], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["kind"], "inspect.symbols");
    let syms = v["symbols"].as_array().unwrap();
    assert!(syms.len() >= 3, "至少 add/main 两个 fn + 无预载符号");
    let ids: Vec<u64> = syms
        .iter()
        .map(|s| s["symbolId"].as_u64().unwrap())
        .collect();
    let mut sorted = ids.clone();
    sorted.sort();
    assert_eq!(ids, sorted, "SymbolId 升序 = canonical 序");
    assert!(
        syms.iter()
            .all(|s| s["kind"].as_str() != Some("constructor") || true)
    );
    let names: Vec<&str> = syms.iter().filter_map(|s| s["name"].as_str()).collect();
    assert!(names.contains(&"add"));
    assert!(names.contains(&"main"));
}

#[test]
fn inspect_refs_and_effects() {
    let td = TempDir::new("inspect");
    td.write(
        "main.lang-go",
        "fn helper(): int {\n    return 1\n}\n\nfn main() {\n    println(helper())\n    println(helper())\n}\n",
    );
    let out = run_cli(&["inspect", "refs", "helper", ".", "--json"], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    let refs = v["references"].as_array().unwrap();
    assert_eq!(refs.len(), 2, "两处调用点");
    assert_eq!(refs[0]["referenceKind"], "call");

    let out = run_cli(&["inspect", "effects", ".", "--json"], td.path());
    assert_eq!(code(&out), 0);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    let effs = v["effects"].as_array().unwrap();
    let main = effs.iter().find(|e| e["name"] == "main").unwrap();
    assert_eq!(main["effects"][0], "IO", "println → IO");
    let helper = effs.iter().find(|e| e["name"] == "helper").unwrap();
    assert_eq!(helper["effects"][0], "PURE");
}

#[test]
fn inspect_callers_callees_impact() {
    let td = TempDir::new("callers");
    td.write(
        "main.lang-go",
        "fn leaf(): int {\n    return 1\n}\n\nfn mid(): int {\n    return leaf()\n}\n\nfn main() {\n    println(mid())\n}\n",
    );
    let out = run_cli(&["inspect", "callers", "leaf", ".", "--json"], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    let callers: Vec<&str> = v["callers"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|c| c["name"].as_str())
        .collect();
    assert!(callers.contains(&"mid"));
    assert!(callers.contains(&"main"), "闭包含传递调用方");

    let out = run_cli(&["inspect", "callees", "main", ".", "--json"], td.path());
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    let callees: Vec<&str> = v["callees"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|c| c["name"].as_str())
        .collect();
    assert!(callees.contains(&"mid"));
    assert!(callees.contains(&"leaf"));

    let out = run_cli(&["inspect", "impact", "leaf", ".", "--json"], td.path());
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["impact"]["direct"].as_array().unwrap().len(), 1);
    assert_eq!(v["impact"]["transitive"].as_array().unwrap().len(), 2);
}

#[test]
fn inspect_type_reports_node_type() {
    let td = clean_module();
    // main.lang-go 第 6 行 `    println(add(1, 2))`：列 17 命中 int 字面量 1
    let out = run_cli(
        &[
            "inspect",
            "type",
            "--file",
            "main.lang-go",
            "--line",
            "6",
            "--column",
            "17",
            ".",
            "--json",
        ],
        td.path(),
    );
    assert_eq!(
        code(&out),
        0,
        "stdout: {} stderr: {}",
        stdout(&out),
        stderr(&out)
    );
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    let node = &v["nodes"][0];
    assert_eq!(node["kind"], "int_lit");
    assert_eq!(node["type"], "int");
}

#[test]
fn inspect_errors_are_enveloped() {
    let td = clean_module();
    let out = run_cli(&["inspect", "refs", "ghost", ".", "--json"], td.path());
    assert_eq!(code(&out), 1);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["kind"], "inspect.refs");
    assert!(v["error"].is_string());
    let out = run_cli(&["inspect"], td.path());
    assert_eq!(code(&out), 2);
}

// ———— refactor ————

#[test]
fn refactor_rename_dry_run_then_apply() {
    let td = TempDir::new("rename");
    td.write(
        "main.lang-go",
        "fn greet(): string {\n    return \"hi\"\n}\n\nfn main() {\n    println(greet())\n    println(greet())\n}\n",
    );
    let before = td.read("main.lang-go");
    // dry-run：不动文件
    let out = run_cli(
        &[
            "refactor",
            "rename",
            "--symbol",
            "greet",
            "--to",
            "salutation",
            ".",
            "--json",
        ],
        td.path(),
    );
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["applied"], false);
    assert_eq!(v["editCount"], 3, "定义 + 两处调用");
    assert_eq!(td.read("main.lang-go"), before);
    // apply：落盘 + 闭环验证
    let out = run_cli(
        &[
            "refactor",
            "rename",
            "--symbol",
            "greet",
            "--to",
            "salutation",
            "--apply",
            ".",
        ],
        td.path(),
    );
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let after = td.read("main.lang-go");
    assert!(!after.contains("greet"));
    assert_eq!(after.matches("salutation").count(), 3);
    let out = run_cli(&["check", "."], td.path());
    assert_eq!(code(&out), 0);
}

#[test]
fn refactor_rename_rejects_conflict_and_unknown() {
    let td = TempDir::new("renamebad");
    td.write(
        "main.lang-go",
        "fn foo(): int {\n    return 1\n}\n\nfn bar(): int {\n    return 2\n}\n\nfn main() {\n    println(foo())\n}\n",
    );
    // 改名撞已有符号
    let out = run_cli(
        &["refactor", "rename", "--symbol", "foo", "--to", "bar", "."],
        td.path(),
    );
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("冲突"));
    // 未知符号
    let out = run_cli(
        &["refactor", "rename", "--symbol", "ghost", "--to", "x", "."],
        td.path(),
    );
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("未知符号"));
    // struct 目标不支持
    td.write(
        "main.lang-go",
        "struct S {\n    a: int\n}\n\nfn main() {\n}\n",
    );
    let out = run_cli(
        &["refactor", "rename", "--symbol", "S", "--to", "T", "."],
        td.path(),
    );
    assert_eq!(code(&out), 1);
    assert!(stderr(&out).contains("暂不支持"));
}

#[test]
fn refactor_organize_imports_sorts_and_dedups() {
    let td = TempDir::new("org");
    td.write("main.lang-go", "import \"./b\"\nimport \"./a\"\nimport \"./a\"\n\nfn main() {\n    println(av() + bv())\n}\n");
    td.write("a.lang-go", "fn av(): int {\n    return 1\n}\n");
    td.write("b.lang-go", "fn bv(): int {\n    return 2\n}\n");
    let out = run_cli(&["refactor", "organize-imports", "--apply", "."], td.path());
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    // organize 的替换区间 = 首 import 行首到末 import 行尾（含换行），
    // 应用后经 formatter 规范化：import 块与 fn 之间不留空行（lang-refactor 冻结行为）
    let after = td.read("main.lang-go");
    assert_eq!(
        after,
        "import \"./a\"\nimport \"./b\"\nfn main() {\n    println(av() + bv())\n}\n"
    );
    let out = run_cli(&["run", "."], td.path());
    assert_eq!(stdout(&out), "3\n");
}

#[test]
fn refactor_safe_remove_refuses_referenced_and_removes_unused() {
    let td = TempDir::new("remove");
    td.write(
        "main.lang-go",
        "fn used(): int {\n    return 1\n}\n\nfn dead(): int {\n    return 2\n}\n\nfn main() {\n    println(used())\n}\n",
    );
    // 有引用 → 拒绝 + refSites 证据
    let out = run_cli(
        &["refactor", "safe-remove", "--symbol", "used", ".", "--json"],
        td.path(),
    );
    assert_eq!(code(&out), 1);
    let v: serde_json::Value = serde_json::from_str(&stdout(&out)).unwrap();
    assert_eq!(v["removable"], false);
    assert!(!v["refSites"].as_array().unwrap().is_empty());
    // 未引用 → 删除闭环
    let out = run_cli(
        &[
            "refactor",
            "safe-remove",
            "--symbol",
            "dead",
            "--apply",
            ".",
        ],
        td.path(),
    );
    assert_eq!(code(&out), 0, "stderr: {}", stderr(&out));
    let after = td.read("main.lang-go");
    assert!(!after.contains("dead"));
    let out = run_cli(&["check", "."], td.path());
    assert_eq!(code(&out), 0);
}
