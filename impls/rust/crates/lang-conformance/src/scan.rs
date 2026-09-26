//! conformance 套件目录扫描器（conformance/README.md「布局」节）。
//!
//! 布局：`<root>/<category>/<case-name>/case.yaml`（入口 main.lang-go 同目录）。
//! 扫描规则：root 下的每个目录是一个 category，category 下的每个目录必须含
//! case.yaml（缺文件视为套件错误，不是静默跳过）；解析失败收集全部失败一次性报告。
//! 输出按 (category, case 目录名) 排序——确定性，禁文件系统序外泄。

use std::fs;
use std::path::{Path, PathBuf};

use crate::manifest::{Case, ManifestError, parse_manifest};

/// 扫描出的一个 case：解析结果 + 所在目录（执行器用目录装载程序）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ScannedCase {
    pub case: Case,
    pub dir: PathBuf,
}

/// 一条扫描失败（目录缺 case.yaml / manifest 解析失败 / IO）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ScanFailure {
    pub path: PathBuf,
    pub reason: String,
}

impl std::fmt::Display for ScanFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.path.display(), self.reason)
    }
}

/// 扫描整个套件。任一失败则 Err（收集全部失败，不只报第一个）。
pub fn scan_suite(root: &Path) -> Result<Vec<ScannedCase>, Vec<ScanFailure>> {
    let mut failures = Vec::new();
    let mut out = Vec::new();

    let mut categories = match list_dirs(root) {
        Err(reason) => {
            failures.push(ScanFailure {
                path: root.to_path_buf(),
                reason,
            });
            return Err(failures);
        }
        Ok(cats) => cats,
    };
    categories.sort();
    for cat in categories {
        let cat_name = file_name_of(&cat);
        let mut case_dirs = match list_dirs(&cat) {
            Ok(dirs) => dirs,
            Err(reason) => {
                failures.push(ScanFailure { path: cat, reason });
                continue;
            }
        };
        case_dirs.sort();
        if case_dirs.is_empty() {
            failures.push(ScanFailure {
                path: cat.clone(),
                reason: format!("category {cat_name} 下没有任何 case 目录"),
            });
            continue;
        }
        for case_dir in case_dirs {
            let manifest = case_dir.join("case.yaml");
            if !manifest.is_file() {
                failures.push(ScanFailure {
                    path: case_dir.clone(),
                    reason: "缺 case.yaml".into(),
                });
                continue;
            }
            match fs::read_to_string(&manifest) {
                Err(e) => failures.push(ScanFailure {
                    path: manifest,
                    reason: e.to_string(),
                }),
                Ok(text) => match parse_manifest(&text) {
                    Err(ManifestError(reason)) => failures.push(ScanFailure {
                        path: manifest,
                        reason,
                    }),
                    Ok(case) => {
                        // manifest 声明的 category/name 必须与目录位置一致（防 case 搬家后失配）
                        if case.category != cat_name {
                            failures.push(ScanFailure {
                                path: manifest,
                                reason: format!(
                                    "manifest category {:?} 与目录名 {cat_name:?} 不一致",
                                    case.category
                                ),
                            });
                            continue;
                        }
                        if case.name != file_name_of(&case_dir) {
                            failures.push(ScanFailure {
                                path: manifest,
                                reason: format!(
                                    "manifest name {:?} 与目录名 {:?} 不一致",
                                    case.name,
                                    file_name_of(&case_dir)
                                ),
                            });
                            continue;
                        }
                        out.push(ScannedCase {
                            case,
                            dir: case_dir,
                        });
                    }
                },
            }
        }
    }

    if failures.is_empty() {
        Ok(out)
    } else {
        Err(failures)
    }
}

fn list_dirs(dir: &Path) -> Result<Vec<PathBuf>, String> {
    let entries = fs::read_dir(dir).map_err(|e| format!("读取目录失败: {e}"))?;
    let mut dirs = Vec::new();
    for entry in entries {
        let entry = entry.map_err(|e| format!("遍历目录失败: {e}"))?;
        let path = entry.path();
        if path.is_dir() {
            dirs.push(path);
        }
    }
    Ok(dirs)
}

fn file_name_of(path: &Path) -> String {
    path.file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::manifest::Mode;

    /// 仓库真值：conformance/ 恰好 151 个 case（仓库实测数；套件扩容时同步更新本断言）。
    #[test]
    fn real_suite_has_exactly_151_parsable_cases() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("..")
            .join("..")
            .join("conformance");
        let cases = scan_suite(&root).unwrap_or_else(|f| {
            panic!(
                "套件扫描应零失败，实际 {} 条：{}",
                f.len(),
                f.iter()
                    .take(5)
                    .map(|x| x.to_string())
                    .collect::<Vec<_>>()
                    .join("; ")
            )
        });
        assert_eq!(cases.len(), 151);
        // 确定性：两次扫描逐项相等（含目录路径与解析内容）
        let again = scan_suite(&root).unwrap();
        assert_eq!(cases, again);
        // 排序 = (category, case 目录名)
        let mut sorted = cases.clone();
        sorted.sort_by(|a, b| {
            (&a.case.category, &a.case.name).cmp(&(&b.case.category, &b.case.name))
        });
        assert_eq!(cases, sorted);
        // run/check 两种模式都真实存在（防比较逻辑退化为死代码）
        assert!(cases.iter().any(|c| c.case.mode == Mode::Run));
        assert!(cases.iter().any(|c| c.case.mode == Mode::Check));
    }

    /// 缺 case.yaml 与 manifest 失败都会被收集并一次报出。
    #[test]
    fn collects_all_failures() {
        let tmp = std::env::temp_dir().join(format!("lang-cf-scan-{}", std::process::id()));
        let good = tmp.join("types").join("good-case");
        fs::create_dir_all(&good).unwrap();
        fs::write(
            good.join("case.yaml"),
            "name: good-case\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    ok\n  stderr: \"\"\n",
        )
        .unwrap();
        let missing = tmp.join("types").join("missing-manifest");
        fs::create_dir_all(&missing).unwrap();
        let bad = tmp.join("syntax").join("bad-manifest");
        fs::create_dir_all(&bad).unwrap();
        fs::write(bad.join("case.yaml"), "name: bad\nunknown_key: 1\n").unwrap();
        let empty_cat = tmp.join("empty-category");
        fs::create_dir_all(&empty_cat).unwrap();

        let failures = scan_suite(&tmp).unwrap_err();
        assert_eq!(failures.len(), 3, "三条失败应一次收集：{failures:?}");

        let _ = fs::remove_dir_all(&tmp);
    }
}
