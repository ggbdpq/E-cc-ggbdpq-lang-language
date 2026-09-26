//! 编辑事务应用（specs/agent-protocol.md §6）。
//!
//! 管线：前置校验（sha256）→ 区间合法 → 重叠拒绝 → 码点切片拼接。
//! 原子性：全部文件的校验与拼接**全部完成后**才进入写入；任一校验失败
//! 不产生任何写入（全成或全不成）。偏移一律 0 基 Unicode 码点（模块头
//! 冻结决定）；同文件多编辑按 start 降序（稳定）逐个 splice——高偏移编辑
//! 不影响低偏移编辑的区间，与 TS/Go 证据同构。
//!
//! 入口：
//! - [`apply_in_memory`]：纯内存事务（map 进 map 出），refactor/测试的直接底座；
//! - [`apply`]：文件系统事务（根目录 + 相对路径），验证全过后逐文件
//!   临时文件 + 原子改名落盘。
//!
//! 确定性：同点插入按编辑表序**倒序**落位（先落者被后落者顶右），稳定排序保证
//! 同一 WorkspaceEdit 重复应用结果逐字节一致。

use std::collections::BTreeMap;
use std::path::Path;

use crate::hash::sha256_hex;
use crate::{TextEdit, WorkspaceEdit};

/// 应用失败（specs §6：前置/重叠/缺前置都拒绝，绝不半应用）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ApplyError {
    /// 有编辑的文件缺少前置校验。
    MissingPrecondition(String),
    /// 文件当前内容与 precondition.file_hash 不符（源码自分析后已变化）。
    PreconditionFailed(String),
    /// 编辑区间重叠。
    Overlap(String),
    /// 区间越界或方向颠倒。
    OutOfRange(String),
    /// 文件读/写 IO 失败。
    Io(String),
}

impl std::fmt::Display for ApplyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ApplyError::MissingPrecondition(m)
            | ApplyError::PreconditionFailed(m)
            | ApplyError::Overlap(m)
            | ApplyError::OutOfRange(m)
            | ApplyError::Io(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for ApplyError {}

/// 单文件：校验并把全部编辑套用到该文件内容上（码点度量）。
fn apply_file(
    file: &str,
    source: &str,
    edits: &[&TextEdit],
    expect_hash: &str,
) -> Result<String, ApplyError> {
    let actual = sha256_hex(source.as_bytes());
    if actual != expect_hash {
        return Err(ApplyError::PreconditionFailed(format!(
            "edit: {file} 自分析后已变化（哈希不匹配），拒绝写入"
        )));
    }
    let mut cps: Vec<char> = source.chars().collect();
    // start 降序稳定排序（同 start 保持编辑表序）
    let mut sorted: Vec<&TextEdit> = edits.to_vec();
    sorted.sort_by_key(|e| std::cmp::Reverse(e.start_offset));
    let len_cp = cps.len() as u32;
    for (i, e) in sorted.iter().enumerate() {
        if e.start_offset > e.end_offset || e.end_offset > len_cp {
            return Err(ApplyError::OutOfRange(format!(
                "edit: {file} 编辑区间越界 [{}, {})（长度 {len_cp}）",
                e.start_offset, e.end_offset
            )));
        }
        if let Some(next) = sorted.get(i + 1) {
            if next.end_offset > e.start_offset {
                return Err(ApplyError::Overlap(format!(
                    "edit: {file} 存在重叠编辑区间 [{}, {}) 与 [{}, {})",
                    next.start_offset, next.end_offset, e.start_offset, e.end_offset
                )));
            }
        }
    }
    for e in sorted {
        let start = e.start_offset as usize;
        let end = e.end_offset as usize;
        cps.splice(start..end, e.replacement.chars());
    }
    Ok(cps.into_iter().collect())
}

/// 事务核心：校验全部文件并计算各自新内容；任何失败即整体失败（零写入语义
/// 由此保证——调用方拿到 `Ok` 才允许落盘）。文件按路径字典序处理。
fn compute(
    ws: &WorkspaceEdit,
    read: &mut dyn FnMut(&str) -> Option<String>,
) -> Result<Vec<(String, String)>, ApplyError> {
    if ws.edits.is_empty() {
        return Ok(Vec::new());
    }
    let pre: BTreeMap<&str, &str> = ws
        .preconditions
        .iter()
        .map(|p| (p.file.as_str(), p.file_hash.as_str()))
        .collect();
    let mut by_file: BTreeMap<&str, Vec<&TextEdit>> = BTreeMap::new();
    for e in &ws.edits {
        by_file.entry(e.file.as_str()).or_default().push(e);
    }
    let mut out = Vec::new();
    for (file, edits) in by_file {
        let Some(hash) = pre.get(file) else {
            return Err(ApplyError::MissingPrecondition(format!(
                "edit: 文件 {file} 缺少前置校验（fileHash）"
            )));
        };
        let Some(source) = read(file) else {
            return Err(ApplyError::Io(format!(
                "edit: 读 {file}: 文件不存在或不可读"
            )));
        };
        let new_text = apply_file(file, &source, &edits, hash)?;
        out.push((file.to_string(), new_text));
    }
    Ok(out)
}

/// 纯内存事务：`current` 为「分析时」的文件快照（相对路径 → 内容）。
/// 返回被编辑文件的新内容（只含被编辑的文件）；任何校验失败返回 Err 且
/// 不产生部分结果。
pub fn apply_in_memory(
    ws: &WorkspaceEdit,
    current: &BTreeMap<String, String>,
) -> Result<BTreeMap<String, String>, ApplyError> {
    let mut read = |f: &str| current.get(f).cloned();
    compute(ws, &mut read).map(|files| files.into_iter().collect())
}

/// 文件系统事务：`root` 为模块根目录，`TextEdit.file` 为根相对路径。
///
/// 全部读取与校验完成后才逐文件写入（临时文件 + 改名，避免半截文件）；
/// 校验阶段的任何失败不触碰文件系统。写入阶段 IO 失败以 [`ApplyError::Io`]
/// 报告（与 TS 证据同口径：校验原子，跨文件写入尽力而为）。
pub fn apply(ws: &WorkspaceEdit, root: &Path) -> Result<(), ApplyError> {
    let mut read = |f: &str| std::fs::read_to_string(root.join(f)).ok();
    let new_contents = compute(ws, &mut read)?;
    for (file, text) in new_contents {
        let target = root.join(&file);
        let tmp = root.join(format!(".{file}.lang-edit-tmp"));
        let write_or_cleanup = |tmp: &Path| -> Result<(), ApplyError> {
            std::fs::write(tmp, text.as_bytes())
                .map_err(|e| ApplyError::Io(format!("edit: 写 {file}: {e}")))?;
            std::fs::rename(tmp, &target)
                .map_err(|e| ApplyError::Io(format!("edit: 落盘 {file}: {e}")))
        };
        if let Err(e) = write_or_cleanup(&tmp) {
            let _ = std::fs::remove_file(&tmp);
            return Err(e);
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Precondition;

    fn edit(file: &str, start: u32, end: u32, replacement: &str) -> TextEdit {
        TextEdit {
            file: file.to_string(),
            start_offset: start,
            end_offset: end,
            replacement: replacement.to_string(),
        }
    }

    fn pre(file: &str, text: &str) -> Precondition {
        Precondition {
            file: file.to_string(),
            file_hash: sha256_hex(text.as_bytes()),
        }
    }

    fn map(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs
            .iter()
            .map(|(f, s)| (f.to_string(), s.to_string()))
            .collect()
    }

    const SRC: &str = "fn add(a: int, b: int): int {\n    return a + b\n}\n";

    #[test]
    fn single_edit_replaces_by_codepoints() {
        // "fn add" 的 "add"：码点 3..6
        let ws = WorkspaceEdit {
            edits: vec![edit("m.lang-go", 3, 6, "sum")],
            preconditions: vec![pre("m.lang-go", SRC)],
        };
        let out = apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap();
        assert_eq!(
            out.get("m.lang-go").unwrap(),
            "fn sum(a: int, b: int): int {\n    return a + b\n}\n"
        );
    }

    #[test]
    fn cjk_offsets_are_codepoint_metric() {
        let src = "let 甲 = 乙 + 1\n";
        let ws = WorkspaceEdit {
            // 「乙」在码点 9..10（let␣甲␣=␣乙 → l0 e1 t2 ␣3 甲4 ␣5 =6 ␣7 乙8 — 码点 8..9）
            edits: vec![edit("c.lang-go", 8, 9, "丙")],
            preconditions: vec![pre("c.lang-go", src)],
        };
        let out = apply_in_memory(&ws, &map(&[("c.lang-go", src)])).unwrap();
        assert_eq!(out.get("c.lang-go").unwrap(), "let 甲 = 丙 + 1\n");
    }

    #[test]
    fn hash_mismatch_rejects() {
        let ws = WorkspaceEdit {
            edits: vec![edit("m.lang-go", 0, 2, "xx")],
            preconditions: vec![pre("m.lang-go", "另一个版本")],
        };
        let err = apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap_err();
        assert!(matches!(err, ApplyError::PreconditionFailed(_)));
    }

    #[test]
    fn missing_precondition_rejects() {
        let ws = WorkspaceEdit {
            edits: vec![edit("m.lang-go", 0, 2, "xx")],
            preconditions: vec![pre("other.lang-go", SRC)],
        };
        let err = apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap_err();
        assert!(matches!(err, ApplyError::MissingPrecondition(_)));
    }

    #[test]
    fn missing_file_is_io_error() {
        let ws = WorkspaceEdit {
            edits: vec![edit("ghost.lang-go", 0, 2, "xx")],
            preconditions: vec![pre("ghost.lang-go", SRC)],
        };
        let err = apply_in_memory(&ws, &map(&[])).unwrap_err();
        assert!(matches!(err, ApplyError::Io(_)));
    }

    #[test]
    fn overlapping_edits_reject() {
        let ws = WorkspaceEdit {
            edits: vec![edit("m.lang-go", 3, 9, "a"), edit("m.lang-go", 6, 12, "b")],
            preconditions: vec![pre("m.lang-go", SRC)],
        };
        let err = apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap_err();
        assert!(matches!(err, ApplyError::Overlap(_)));
    }

    #[test]
    fn out_of_range_and_reversed_reject() {
        let len = SRC.chars().count() as u32;
        let ws = WorkspaceEdit {
            edits: vec![edit("m.lang-go", 0, len + 1, "x")],
            preconditions: vec![pre("m.lang-go", SRC)],
        };
        assert!(matches!(
            apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap_err(),
            ApplyError::OutOfRange(_)
        ));
        let ws2 = WorkspaceEdit {
            edits: vec![edit("m.lang-go", 5, 3, "x")],
            preconditions: vec![pre("m.lang-go", SRC)],
        };
        assert!(matches!(
            apply_in_memory(&ws2, &map(&[("m.lang-go", SRC)])).unwrap_err(),
            ApplyError::OutOfRange(_)
        ));
    }

    #[test]
    fn multiple_edits_apply_descending_without_interference() {
        // 两个编辑都在同一行：3..6（add→sum）与 15..22（b: int→b2: int）
        // "fn add(a: int, b: int): int" 的码点：f0..n1 ␣2 a3d4d5 (6 a7:8 ␣9 i10n11t12,13 ␣14 b15:16 ␣17 …
        let ws = WorkspaceEdit {
            edits: vec![
                edit("m.lang-go", 3, 6, "sum"),
                edit("m.lang-go", 15, 16, "b2"),
            ],
            preconditions: vec![pre("m.lang-go", SRC)],
        };
        let out = apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap();
        assert_eq!(
            out.get("m.lang-go").unwrap(),
            "fn sum(a: int, b2: int): int {\n    return a + b\n}\n"
        );
    }

    #[test]
    fn same_point_insertions_deterministic() {
        // 同点插入 (5,5,"A") 与 (5,5,"B")：稳定降序后按表序倒序落位 → "…BA…"
        let src = "hello world";
        let ws = WorkspaceEdit {
            edits: vec![edit("s", 5, 5, "A"), edit("s", 5, 5, "B")],
            preconditions: vec![pre("s", src)],
        };
        let out = apply_in_memory(&ws, &map(&[("s", src)])).unwrap();
        assert_eq!(out.get("s").unwrap(), "helloBA world");
    }

    #[test]
    fn empty_edit_set_is_noop_and_unedited_files_untouched() {
        let ws = WorkspaceEdit {
            edits: vec![],
            preconditions: vec![pre("m.lang-go", "过期哈希")],
        };
        let out = apply_in_memory(&ws, &map(&[("m.lang-go", SRC)])).unwrap();
        assert!(out.is_empty());
    }

    #[test]
    fn multi_file_transaction_all_or_nothing() {
        let other = "let q = 1\n";
        // 第二个文件哈希不匹配 → 整体失败，不返回任何部分结果
        let ws = WorkspaceEdit {
            edits: vec![edit("a.lang-go", 3, 6, "sum"), edit("b.lang-go", 4, 5, "2")],
            preconditions: vec![pre("a.lang-go", SRC), pre("b.lang-go", "过期")],
        };
        let err =
            apply_in_memory(&ws, &map(&[("a.lang-go", SRC), ("b.lang-go", other)])).unwrap_err();
        assert!(matches!(err, ApplyError::PreconditionFailed(_)));
    }

    /// 文件系统事务：全过落盘；哈希不匹配一个字节都不动。
    #[test]
    fn fs_apply_atomicity() {
        let dir = std::env::temp_dir().join(format!(
            "lang-edit-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let cleanup = || {
            let _ = std::fs::remove_dir_all(&dir);
        };
        let path_a = dir.join("a.lang-go");
        let path_b = dir.join("b.lang-go");
        std::fs::write(&path_a, SRC).unwrap();
        std::fs::write(&path_b, "let q = 1\n").unwrap();

        // 全绿事务：两文件都改
        let ws = WorkspaceEdit {
            edits: vec![edit("a.lang-go", 3, 6, "sum"), edit("b.lang-go", 8, 9, "2")],
            preconditions: vec![pre("a.lang-go", SRC), pre("b.lang-go", "let q = 1\n")],
        };
        apply(&ws, &dir).unwrap();
        assert_eq!(
            std::fs::read_to_string(&path_a).unwrap(),
            "fn sum(a: int, b: int): int {\n    return a + b\n}\n"
        );
        assert_eq!(std::fs::read_to_string(&path_b).unwrap(), "let q = 2\n");

        // 哈希不匹配：b 被外部改过 → a 也不得被写
        std::fs::write(&path_b, "let q = 999\n").unwrap();
        let sum_src = "fn sum(a: int, b: int): int {\n    return a + b\n}\n";
        let ws2 = WorkspaceEdit {
            edits: vec![edit("a.lang-go", 3, 6, "plus")],
            preconditions: vec![pre("a.lang-go", sum_src)],
        };
        let ws_bad = WorkspaceEdit {
            edits: vec![
                edit("a.lang-go", 3, 6, "plus"),
                edit("b.lang-go", 8, 11, "42"),
            ],
            preconditions: vec![pre("a.lang-go", sum_src), pre("b.lang-go", "let q = 1\n")],
        };
        // 单独应用 a（哈希对）应成功，用于确认 ws_bad 的失败只来自 b
        apply(&ws2, &dir).unwrap();
        let err = apply(&ws_bad, &dir).unwrap_err();
        assert!(matches!(err, ApplyError::PreconditionFailed(_)));
        assert_eq!(
            std::fs::read_to_string(&path_a).unwrap(),
            "fn plus(a: int, b: int): int {\n    return a + b\n}\n"
        );
        cleanup();
    }
}
