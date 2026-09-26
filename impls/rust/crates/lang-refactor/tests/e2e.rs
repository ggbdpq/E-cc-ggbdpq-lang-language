//! lang-refactor E2E：真实夹具目录上的三入口闭环（红→绿矩阵）。
//!
//! 矩阵：
//! 1. rename：跨文件函数改名（定义位 + 全部调用点）→ apply_and_verify 闭环
//!    （precondition → apply → format → reparse → re-analyze → 诊断零回归）
//!    → 落盘重载 → 新名解析为同一形状、旧名消失、分析零诊断。
//! 2. organize-imports：乱序 + 重复 import 的夹具文件 → 计划 → 事务应用
//!    → 排序去重、注释零丢失、其余代码不动。
//! 3. safe-remove：被引用顶层 fn 拒绝（附 refSites 证据）；未被引用的
//!    顶层 fn 给出删除编辑 → 应用后声明消失、重载分析零诊断。
//!
//! 夹具落系统临时目录，Drop 清理（不落仓库）。

use std::path::PathBuf;

use lang_edit::{ApplyError, apply_in_memory};
use lang_refactor::{RefactorInput, apply_and_verify, organize_imports, rename, safe_remove};

// ———— 临时夹具目录 ————

struct FixtureDir {
    path: PathBuf,
}

impl FixtureDir {
    fn new(tag: &str, files: &[(&str, &str)]) -> Self {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let path = std::env::temp_dir().join(format!(
            "lang-refactor-e2e-{tag}-{}-{nanos}",
            std::process::id()
        ));
        std::fs::create_dir_all(&path).expect("建夹具目录");
        for (name, src) in files {
            std::fs::write(path.join(name), src).expect("写夹具文件");
        }
        FixtureDir { path }
    }

    fn read(&self, name: &str) -> String {
        std::fs::read_to_string(self.path.join(name)).expect("读夹具文件")
    }

    fn write(&self, name: &str, text: &str) {
        std::fs::write(self.path.join(name), text).expect("写夹具文件");
    }
}

impl Drop for FixtureDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

// ———— 矩阵 1：rename 跨文件闭环 ————

const UTIL_SRC: &str = "\
fn scale(x: int, k: int): int {
    return x * k
}
fn shrink(x: int): int {
    return scale(x, 1)
}
";

const MAIN_SRC: &str = "\
import \"./util\"
fn main() {
    let r = scale(21, 2)
    println(r)
}
";

#[test]
fn rename_cross_file_closed_loop() {
    let dir = FixtureDir::new(
        "rename",
        &[("main.lang-go", MAIN_SRC), ("util.lang-go", UTIL_SRC)],
    );
    let input = RefactorInput::load(&dir.path).expect("装载夹具目录");
    assert!(input.analysis.diags.is_empty(), "夹具应零诊断");

    let target = input.resolve_target("scale").expect("符号名解析");
    let baseline = input.baseline_codes();

    // dry-run 计划：定义位 + util 内调用点 + main 调用点 = 3 处编辑、跨 2 文件
    let ws = rename(&input, target, "multiply").expect("rename 计划");
    assert_eq!(ws.edits.len(), 3);
    let mut files: Vec<&str> = ws.edits.iter().map(|e| e.file.as_str()).collect();
    files.sort();
    files.dedup();
    assert_eq!(files, vec!["main.lang-go", "util.lang-go"]);
    // 每个涉及文件都有独立 precondition（sha256）
    assert_eq!(ws.preconditions.len(), 2);

    // 闭环：precondition → apply → format → reparse → re-analyze → 零回归
    let new_contents = apply_and_verify(&ws, &input).expect("闭环验证");

    // 落盘（CLI 落盘面的等价动作），再重载验证语义事实
    for (file, text) in &new_contents {
        dir.write(file, text);
    }
    let reloaded = RefactorInput::load(&dir.path).expect("重载");
    assert!(
        reloaded.analysis.diags.is_empty(),
        "编辑后应零诊断: {:?}",
        reloaded.analysis.diags
    );
    assert_eq!(reloaded.baseline_codes(), baseline, "诊断零回归");
    // 新名解析得到同形状符号；旧名消失
    assert!(reloaded.resolve_target("multiply").is_ok());
    assert!(reloaded.resolve_target("scale").is_err());
    // 文本事实：定义与全部调用点已改名（fmt 后语义不变由闭环断言保证）
    let util = dir.read("util.lang-go");
    let main = dir.read("main.lang-go");
    assert!(util.contains("fn multiply(x: int, k: int): int"));
    assert!(util.contains("return multiply(x, 1)"));
    assert!(main.contains("let r = multiply(21, 2)"));
    assert!(!util.contains("scale"));
    assert!(!main.contains("scale"));
}

/// rename 的 precondition 失败：夹具在计划后被外部改动 → 盘上事务拒绝写入。
#[test]
fn rename_precondition_failure_rejects() {
    let dir = FixtureDir::new(
        "rename-stale",
        &[("main.lang-go", MAIN_SRC), ("util.lang-go", UTIL_SRC)],
    );
    let input = RefactorInput::load(&dir.path).expect("装载夹具目录");
    let target = input.resolve_target("scale").expect("符号名解析");
    let ws = rename(&input, target, "multiply").expect("rename 计划");

    // 外部改动 main.lang-go（源码自分析后已变化）：盘上事务读当前内容对哈希
    dir.write("main.lang-go", &format!("{MAIN_SRC}\nlet stale = 1\n"));
    let err = lang_edit::apply(&ws, &dir.path).unwrap_err();
    assert!(matches!(err, ApplyError::PreconditionFailed(_)));
    // 文件未被半写：外部改动原样保留，计划编辑未落任何一字节
    assert!(dir.read("main.lang-go").contains("let stale = 1"));
    assert!(!dir.read("main.lang-go").contains("multiply"));
    assert!(!dir.read("util.lang-go").contains("multiply"));
}

/// rename 的保守拒绝：struct/enum 目标列 P1；新名与已有符号冲突。
#[test]
fn rename_conservative_refusals() {
    let dir = FixtureDir::new(
        "rename-refuse",
        &[(
            "main.lang-go",
            "struct P {\n    x: int\n}\nenum E {\n    A\n}\nfn alpha(): int {\n    return 1\n}\nfn beta(): int {\n    return 2\n}\nfn main() {\n    println(alpha())\n    println(beta())\n}\n",
        )],
    );
    let input = RefactorInput::load(&dir.path).expect("装载夹具目录");
    // struct → 显式拒绝
    let p = input.resolve_target("P").expect("符号名解析");
    let err = rename(&input, p, "Q").unwrap_err();
    assert!(matches!(
        err,
        lang_refactor::RenameRefusal::UnsupportedKind(_)
    ));
    let e = input.resolve_target("E").expect("符号名解析");
    assert!(matches!(
        rename(&input, e, "F").unwrap_err(),
        lang_refactor::RenameRefusal::UnsupportedKind(_)
    ));
    // 新名冲突（beta 已存在）→ 拒绝
    let a = input.resolve_target("alpha").expect("符号名解析");
    assert!(matches!(
        rename(&input, a, "beta").unwrap_err(),
        lang_refactor::RenameRefusal::Conflict(_)
    ));
}

// ———— 矩阵 2：organize-imports ————

const MESSY_IMPORTS: &str = "\
import \"./zeta\"
import \"./alpha\" // 首组
import \"./zeta\"
import \"./mid\"

fn main() {
    println(\"x\")
}
";

#[test]
fn organize_imports_e2e_sorts_and_dedupes() {
    // 被 import 的模块必须真实存在（重载走 lang-loader 的 import 展开）
    let dir = FixtureDir::new(
        "imports",
        &[
            ("main.lang-go", MESSY_IMPORTS),
            ("alpha.lang-go", "let a = 1\n"),
            ("mid.lang-go", "let m = 2\n"),
            ("zeta.lang-go", "let z = 3\n"),
        ],
    );
    let source = dir.read("main.lang-go");
    let ws = organize_imports("main.lang-go", &source).expect("organize 计划");
    // 单点区间替换编辑 + 独立 precondition
    assert_eq!(ws.edits.len(), 1);
    assert_eq!(ws.preconditions.len(), 1);

    let mut cur = std::collections::BTreeMap::new();
    cur.insert("main.lang-go".to_string(), source.clone());
    let out = apply_in_memory(&ws, &cur).expect("内存事务");
    let got = out.get("main.lang-go").expect("编辑后内容");
    let lines: Vec<&str> = got.lines().collect();
    assert_eq!(
        lines,
        vec![
            "import \"./alpha\" // 首组",
            "import \"./mid\"",
            "import \"./zeta\"",
            "",
            "fn main() {",
            "    println(\"x\")",
            "}",
        ],
        "排序 + 去重 + 行尾注释随行走 + 空行与函数体不动"
    );

    // 落盘后重载分析仍零诊断（import 重排不引入语义变化）
    dir.write("main.lang-go", got);
    let reloaded = RefactorInput::load(&dir.path).expect("重载");
    assert!(reloaded.analysis.diags.is_empty());
}

/// organize-imports：区间内夹代码 → 保守拒绝（绝不猜测语义）。
#[test]
fn organize_imports_refuses_scattered_code() {
    let src = "import \"./b\"\nlet x = 1\nimport \"./a\"\nfn main() {}\n";
    let err = organize_imports("main.lang-go", src).unwrap_err();
    assert!(matches!(err, lang_refactor::OrganizeRefusal::Scattered(_)));
}

// ———— 矩阵 3：safe-remove ————

const REMOVABLE_SRC: &str = "\
fn used(): int {
    return 1
}
fn unused(): int {
    return 2
}
fn main() {
    println(used())
}
";

#[test]
fn safe_remove_refuses_referenced_fn_with_sites() {
    let dir = FixtureDir::new("remove-used", &[("main.lang-go", REMOVABLE_SRC)]);
    let input = RefactorInput::load(&dir.path).expect("装载夹具目录");
    let target = input.resolve_target("used").expect("符号名解析");
    let verdict = safe_remove(&input, target).expect("入口级 OK");
    let not_removable = verdict.expect_err("被引用 → 不可删");
    assert!(not_removable.reason.contains("引用"));
    assert!(!not_removable.ref_sites.is_empty(), "refSites 证据面非空");
    // 证据形态 path:line:col（1 基）
    let site = &not_removable.ref_sites[0];
    assert!(site.starts_with("main.lang-go:"), "实际: {site}");
}

#[test]
fn safe_remove_removes_unreferenced_fn() {
    let dir = FixtureDir::new("remove-unused", &[("main.lang-go", REMOVABLE_SRC)]);
    let input = RefactorInput::load(&dir.path).expect("装载夹具目录");
    let target = input.resolve_target("unused").expect("符号名解析");
    let ws = safe_remove(&input, target)
        .expect("入口级 OK")
        .expect("未被引用 → 可删");
    assert_eq!(ws.edits.len(), 1);

    let new_contents = apply_and_verify(&ws, &input).expect("闭环验证");
    dir.write("main.lang-go", &new_contents["main.lang-go"]);
    let got = dir.read("main.lang-go");
    assert!(!got.contains("unused"), "声明已删除: {got}");
    assert!(got.contains("fn used"), "其余声明保留");
    // 删除不残留空行（吞掉声明后换行）
    assert!(!got.contains("\n\n\n"));
    let reloaded = RefactorInput::load(&dir.path).expect("重载");
    assert!(reloaded.analysis.diags.is_empty());
    assert!(reloaded.resolve_target("unused").is_err());
    assert!(reloaded.resolve_target("used").is_ok());
}

/// safe-remove：非顶层/非 fn-var 目标拒绝（方法不在此列）。
#[test]
fn safe_remove_refuses_method_and_local() {
    let src = "\
struct P {
    x: int
}
fn (p P).m(): int {
    let local = 1
    return p.x + local
}
fn main() {
    let q = P { x: 3 }
    println(q.m())
}
";
    let dir = FixtureDir::new("remove-kind", &[("main.lang-go", src)]);
    let input = RefactorInput::load(&dir.path).expect("装载夹具目录");
    let m = input.resolve_target("m").expect("方法符号");
    let verdict = safe_remove(&input, m).expect("入口级 OK");
    let not_removable = verdict.expect_err("方法 → 不可删");
    assert!(not_removable.reason.contains("仅支持顶层 fn 与全局变量"));
}
