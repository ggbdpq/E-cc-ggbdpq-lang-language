//! conformance manifest 解析（YAML 受限子集，conformance/README.md「manifest」节）。
//!
//! 形态：顶层 `name/category/mode/ruleIds` + 恰一层 `expect:`（`exitCode/stdout/stderr/
//! diagnostics`），`stdout` 是 `|` 块标量，`diagnostics` 列表项字段 `code/ruleId/severity/line`。
//! 用 serde_yaml + `deny_unknown_fields` 拒绝未知键；本波再把 README 的语义校验落为硬约束：
//! run 模式必须 `exitCode: 0`、stderr 空串、stdout 块标量（末尾恰一个换行、无空行）；
//! check 模式必须声明至少一条诊断，且 `exitCode/stdout/stderr` 不得出现（拒绝模糊输入）。
//!
//! 已知边界（登记 A6-verify.md）：serde_yaml 会放行锚点/流式标量等 YAML 通用形态——
//! 151 个真实 case 均不使用；README 的「任何超集形态一律报错」在锚点这一角未完全落实。

use lang_core::DiagnosticCode;
use serde::Deserialize;

/// case 模式。
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize)]
pub enum Mode {
    /// 执行比对：stdout 逐字节、exitCode 0、stderr 空。
    Run,
    /// 静态诊断：code+ruleId+severity+line 多重集相等。
    Check,
}

/// 期望诊断（多重集的一项）。`rule_id` 已回填：manifest 声明了用声明的，
/// 否则由 code 推导（E 码 ↔ Rule 唯一映射在 lang-core `DiagnosticCode::rule`）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DiagExpect {
    pub code: String,
    pub rule_id: String,
    pub severity: String,
    pub line: u32,
}

/// `expect:` 块的语义模型（run 模式 diagnostics 恒空；check 模式 stdout/stderr 恒空串）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Expect {
    pub exit_code: i64,
    pub stdout: String,
    pub stderr: String,
    pub diagnostics: Vec<DiagExpect>,
}

/// 一个 conformance case 的解析结果。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Case {
    pub name: String,
    pub category: String,
    pub mode: Mode,
    /// case 验证的 Rule ID 清单（出现序；套件元数据，不参与比对）。
    pub rule_ids: Vec<String>,
    pub expect: Expect,
}

/// manifest 解析失败（含 YAML 定位信息或语义校验原因）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ManifestError(pub String);

impl std::fmt::Display for ManifestError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "manifest 解析失败: {}", self.0)
    }
}

impl std::error::Error for ManifestError {}

// —— serde 反序列化目标（与公开模型分离） ——

#[derive(Deserialize, Debug)]
#[serde(deny_unknown_fields)]
struct ManifestRaw {
    name: String,
    category: String,
    #[serde(default = "default_mode")]
    mode: String,
    #[serde(rename = "ruleIds", default)]
    rule_ids: Vec<String>,
    expect: ExpectRaw,
}

fn default_mode() -> String {
    "run".to_string()
}

#[derive(Deserialize, Debug, Default)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct ExpectRaw {
    exit_code: Option<i64>,
    stdout: Option<String>,
    stderr: Option<String>,
    diagnostics: Option<Vec<DiagExpectRaw>>,
}

#[derive(Deserialize, Debug)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct DiagExpectRaw {
    code: String,
    #[serde(default)]
    rule_id: Option<String>,
    severity: String,
    line: u32,
}

/// 解析并校验一份 case.yaml 全文。
pub fn parse_manifest(src: &str) -> Result<Case, ManifestError> {
    let raw: ManifestRaw = serde_yaml::from_str(src)
        .map_err(|e| ManifestError(e.to_string().lines().next().unwrap_or("").to_string()))?;
    validate(raw)
}

fn validate(raw: ManifestRaw) -> Result<Case, ManifestError> {
    if raw.name.trim().is_empty() {
        return Err(ManifestError("name 不得为空".into()));
    }
    if raw.category.trim().is_empty() {
        return Err(ManifestError("category 不得为空".into()));
    }
    if raw.rule_ids.iter().any(|r| r.trim().is_empty()) {
        return Err(ManifestError("ruleIds 列表项不得为空".into()));
    }
    let mode = match raw.mode.as_str() {
        "run" => Mode::Run,
        "check" => Mode::Check,
        other => {
            return Err(ManifestError(format!(
                "mode 须为 run|check（得到 {other:?}）"
            )));
        }
    };
    let e = raw.expect;

    let diagnostics = match e.diagnostics {
        Some(list) => {
            let mut out = Vec::with_capacity(list.len());
            for d in list {
                out.push(validate_diag(d)?);
            }
            out
        }
        None => Vec::new(),
    };

    match mode {
        Mode::Run => {
            if !diagnostics.is_empty() {
                return Err(ManifestError("run 模式不得声明 diagnostics".into()));
            }
            let Some(exit_code) = e.exit_code else {
                return Err(ManifestError("run 模式必须声明 exitCode".into()));
            };
            if exit_code != 0 {
                return Err(ManifestError(
                    "run 模式 exitCode 必须为 0（运行时错误文本不在比对面）".into(),
                ));
            }
            let stderr = e
                .stderr
                .ok_or_else(|| ManifestError("run 模式必须声明 stderr（当前必须为空串）".into()))?;
            if !stderr.is_empty() {
                return Err(ManifestError("run 模式 stderr 必须为空串".into()));
            }
            let stdout = e
                .stdout
                .ok_or_else(|| ManifestError("run 模式必须声明 stdout（| 块标量）".into()))?;
            if !stdout.ends_with('\n') || stdout.contains("\n\n") {
                return Err(ManifestError(
                    "run 模式 stdout 须为 | 块标量：末尾恰一个换行、无空行".into(),
                ));
            }
            Ok(Case {
                name: raw.name,
                category: raw.category,
                mode,
                rule_ids: raw.rule_ids,
                expect: Expect {
                    exit_code,
                    stdout,
                    stderr,
                    diagnostics,
                },
            })
        }
        Mode::Check => {
            if e.exit_code.is_some() || e.stdout.is_some() || e.stderr.is_some() {
                return Err(ManifestError(
                    "check 模式不得声明 exitCode/stdout/stderr（只比对诊断多重集）".into(),
                ));
            }
            if diagnostics.is_empty() {
                return Err(ManifestError("check 模式至少声明一条诊断".into()));
            }
            Ok(Case {
                name: raw.name,
                category: raw.category,
                mode,
                rule_ids: raw.rule_ids,
                expect: Expect {
                    exit_code: 0,
                    stdout: String::new(),
                    stderr: String::new(),
                    diagnostics,
                },
            })
        }
    }
}

fn validate_diag(d: DiagExpectRaw) -> Result<DiagExpect, ManifestError> {
    let code = DiagnosticCodeBridge::parse(&d.code)
        .ok_or_else(|| ManifestError(format!("未知诊断码 {}", d.code)))?;
    if d.severity != "error" && d.severity != "warning" {
        return Err(ManifestError(format!(
            "severity 须为 error|warning（得到 {:?}）",
            d.severity
        )));
    }
    // line 允许 0：套件真值中「无位置诊断」（如 E3009 全局形状错误）以 line: 0 声明，
    // runner 按 primarySpan.start.line 精确比对（README manifest 节）。
    let rule_id = match d.rule_id {
        Some(r) => {
            // 声明了 ruleId 就必须是该码的映射真值（Rule ID 与码同为契约）
            let expect = code.rule().as_str();
            if r != expect {
                return Err(ManifestError(format!(
                    "ruleId {r} 与码 {} 的映射 {expect} 不符",
                    d.code
                )));
            }
            r
        }
        None => code.rule().as_str().to_string(),
    };
    Ok(DiagExpect {
        code: d.code,
        rule_id,
        severity: d.severity,
        line: d.line,
    })
}

/// E 码字符串 → lang-core 枚举桥接（lang-core 是唯一事实源；此处只做字符串→枚举，
/// 映射仍走 `DiagnosticCode::rule()`。lang-core 追加新码时同步此表）。
struct DiagnosticCodeBridge;

impl DiagnosticCodeBridge {
    fn parse(s: &str) -> Option<DiagnosticCode> {
        use DiagnosticCode as C;
        Some(match s {
            "E1001" => C::E1001,
            "E2001" => C::E2001,
            "E2002" => C::E2002,
            "E2003" => C::E2003,
            "E2004" => C::E2004,
            "E2005" => C::E2005,
            "E2006" => C::E2006,
            "E2007" => C::E2007,
            "E2008" => C::E2008,
            "E2010" => C::E2010,
            "E3001" => C::E3001,
            "E3002" => C::E3002,
            "E3003" => C::E3003,
            "E3004" => C::E3004,
            "E3005" => C::E3005,
            "E3006" => C::E3006,
            "E3007" => C::E3007,
            "E3008" => C::E3008,
            "E3009" => C::E3009,
            "E3010" => C::E3010,
            "E3011" => C::E3011,
            "E3012" => C::E3012,
            "E3013" => C::E3013,
            "E3014" => C::E3014,
            "E4001" => C::E4001,
            "E4002" => C::E4002,
            "E4003" => C::E4003,
            _ => return None,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// README manifest 节的 run 形态逐字段手抄。
    #[test]
    fn parses_readme_run_shape() {
        let src = "name: result-propagation-basic\ncategory: result\nmode: run\nexpect:\n  exitCode: 0\n  stdout: |\n    Ok(4)\n    Err(error{msg: \"div by zero\"})\n  stderr: \"\"\n";
        let c = parse_manifest(src).unwrap();
        assert_eq!(c.name, "result-propagation-basic");
        assert_eq!(c.category, "result");
        assert_eq!(c.mode, Mode::Run);
        assert_eq!(c.expect.exit_code, 0);
        assert_eq!(c.expect.stdout, "Ok(4)\nErr(error{msg: \"div by zero\"})\n");
        assert_eq!(c.expect.stderr, "");
        assert!(c.expect.diagnostics.is_empty());
    }

    /// mode 缺省 = run。
    #[test]
    fn mode_defaults_to_run() {
        let src = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    hi\n  stderr: \"\"\n";
        let c = parse_manifest(src).unwrap();
        assert_eq!(c.mode, Mode::Run);
    }

    /// check 形态：ruleId 缺省由码推导（E3012 → VALUE-REF-003）。
    #[test]
    fn check_diag_rule_id_falls_back_to_mapping() {
        let src = "name: amp\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3012\n      severity: error\n      line: 2\n";
        let c = parse_manifest(src).unwrap();
        assert_eq!(c.mode, Mode::Check);
        assert_eq!(c.expect.diagnostics.len(), 1);
        assert_eq!(c.expect.diagnostics[0].rule_id, "VALUE-REF-003");
    }

    /// 声明 ruleId 时必须是映射真值；写错即拒绝（Rule ID 与码同为契约）。
    #[test]
    fn check_diag_rule_id_must_match_code() {
        let ok = "name: amp\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3012\n      ruleId: VALUE-REF-003\n      severity: error\n      line: 2\n";
        assert!(parse_manifest(ok).is_ok());
        let bad = "name: amp\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3012\n      ruleId: NAME-UNDEF-004\n      severity: error\n      line: 2\n";
        assert!(parse_manifest(bad).is_err());
    }

    /// 未知键 / 深层嵌套 / 未知码 / 非法 severity / 非法 mode 一律拒绝。
    #[test]
    fn rejects_superset_and_garbage() {
        let unknown_key = "name: t\ncategory: types\noops: 1\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n";
        assert!(parse_manifest(unknown_key).is_err());
        let deep = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n  nested:\n    a: 1\n";
        assert!(parse_manifest(deep).is_err());
        let bad_code = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E9999\n      severity: error\n      line: 1\n";
        assert!(parse_manifest(bad_code).is_err());
        let bad_sev = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E2002\n      severity: fatal\n      line: 1\n";
        assert!(parse_manifest(bad_sev).is_err());
        let bad_mode = "name: t\ncategory: types\nmode: fmt\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n";
        assert!(parse_manifest(bad_mode).is_err());
    }

    /// run 模式的语义约束：exitCode 非 0、stderr 非空、stdout 非块标量都拒绝。
    #[test]
    fn rejects_bad_run_semantics() {
        let nonzero = "name: t\ncategory: types\nexpect:\n  exitCode: 2\n  stdout: |\n    x\n  stderr: \"\"\n";
        assert!(parse_manifest(nonzero).is_err());
        let noisy = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"boom\"\n";
        assert!(parse_manifest(noisy).is_err());
        let no_block =
            "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: \"x\"\n  stderr: \"\"\n";
        assert!(parse_manifest(no_block).is_err());
        let blank_line = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n\n    y\n  stderr: \"\"\n";
        assert!(parse_manifest(blank_line).is_err());
    }

    /// check 模式的语义约束：必须声明诊断；混入 run 字段拒绝。
    #[test]
    fn rejects_bad_check_semantics() {
        let no_diag = "name: t\ncategory: diagnostics\nmode: check\nexpect: {}\n";
        assert!(parse_manifest(no_diag).is_err());
        let mixed = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  stdout: |\n    x\n  diagnostics:\n    - code: E2002\n      severity: error\n      line: 1\n";
        assert!(parse_manifest(mixed).is_err());
    }

    /// ruleIds 列表保出现序。
    #[test]
    fn rule_ids_keep_order() {
        let src = "name: two-rules\ncategory: match\nruleIds:\n  - MATCH-EXHAUSTIVE-002\n  - MATCH-KIND-001\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n";
        let c = parse_manifest(src).unwrap();
        assert_eq!(c.rule_ids, vec!["MATCH-EXHAUSTIVE-002", "MATCH-KIND-001"]);
    }
}
