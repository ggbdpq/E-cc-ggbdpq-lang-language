//! SourceText：源码所有权 + 码点/字节/行三重索引。
//!
//! 这是 byte ↔ code-point 映射层（计划 §R1 / specs/source-span.md）：
//! parser 内部可走字节偏移；任何对外（诊断 / Agent Contract / 编辑）必须走码点。
//! 换行只认 `\n`；`\r` 属于上一行内容（CRLF 的 `\r` 计入列数之前）。

/// 一份源文件及其索引。构造即建索引，之后不可变。
#[derive(Clone, Debug)]
pub struct SourceText {
    path: String,
    text: String,
    /// 每行首字符的码点偏移（行 1 从 0 起）。
    line_starts_cp: Vec<u32>,
    /// 第 i 个码点的字节偏移（长度 = 码点总数）。
    cp_index: Vec<u32>,
}

impl SourceText {
    pub fn new(path: impl Into<String>, text: impl Into<String>) -> Self {
        let text = text.into();
        let mut line_starts_cp = vec![0u32];
        let mut cp_index = Vec::with_capacity(text.len());
        for (cp, (byte_off, ch)) in (0_u32..).zip(text.char_indices()) {
            cp_index.push(byte_off as u32);
            if ch == '\n' {
                // 下一行始于当前码点之后
                line_starts_cp.push(cp + 1);
            }
        }
        Self {
            path: path.into(),
            text,
            line_starts_cp,
            cp_index,
        }
    }

    pub fn path(&self) -> &str {
        &self.path
    }

    pub fn text(&self) -> &str {
        &self.text
    }

    /// 码点总数。
    pub fn len_cp(&self) -> u32 {
        self.cp_index.len() as u32
    }

    /// UTF-8 字节总数。
    pub fn len_bytes(&self) -> usize {
        self.text.len()
    }

    /// 字节偏移 → 码点偏移。边界字节取所在码点；非边界字节向下取整到包含它的码点。
    pub fn byte_to_cp(&self, byte: usize) -> u32 {
        // `<=`：边界字节（cp_index[i] == byte）必须命中第 i 个码点本身
        let n = self.cp_index.partition_point(|&b| (b as usize) <= byte);
        n.saturating_sub(1) as u32
    }

    /// 码点偏移 → 字节偏移。越界回到文末。
    pub fn cp_to_byte(&self, cp: u32) -> usize {
        match self.cp_index.get(cp as usize) {
            Some(&b) => b as usize,
            None => self.text.len(),
        }
    }

    /// 码点偏移 → (line, column)，均 1 基码点计数。
    pub fn line_col(&self, cp: u32) -> (u32, u32) {
        let line_idx = self
            .line_starts_cp
            .partition_point(|&start| start <= cp)
            .saturating_sub(1);
        let col = cp - self.line_starts_cp[line_idx] + 1;
        (line_idx as u32 + 1, col)
    }

    /// 行数（≥1；空文本为 1 行）。
    pub fn line_count(&self) -> u32 {
        self.line_starts_cp.len() as u32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ascii_passthrough() {
        let s = SourceText::new("main.lang-go", "fn main() {}\n");
        assert_eq!(s.len_cp() as usize, s.text().chars().count());
        assert_eq!(s.byte_to_cp(3), 3);
        assert_eq!(s.cp_to_byte(3), 3);
    }

    #[test]
    fn cjk_counted_in_code_points() {
        // "中文ab"：4 个码点，8 字节
        let s = SourceText::new("m.lang-go", "中文ab");
        assert_eq!(s.len_cp(), 4);
        assert_eq!(s.len_bytes(), 8);
        assert_eq!(s.byte_to_cp(4), 1); // byte 4 在「文」(bytes 3..6) 内部，取整到 cp 1
        assert_eq!(s.byte_to_cp(6), 2); // 「文」结束边界 = cp 2（a 的起点）
        assert_eq!(s.cp_to_byte(1), 3); // 「文」起点
        assert_eq!(s.cp_to_byte(2), 6); // a 起点
        assert_eq!(s.line_col(3), (1, 4));
    }

    #[test]
    fn emoji_is_one_code_point() {
        // "🦀x"：emoji 4 字节 = 1 码点
        let s = SourceText::new("m.lang-go", "🦀x");
        assert_eq!(s.len_cp(), 2);
        assert_eq!(s.byte_to_cp(4), 1);
        assert_eq!(s.cp_to_byte(1), 4);
    }

    #[test]
    fn crlf_and_multiline() {
        // "a\r\n中\n"：3 行；第 2 行首是 cp 3，第 3 行（空行）首是 cp 5
        let s = SourceText::new("m.lang-go", "a\r\n中\n");
        assert_eq!(s.line_count(), 3);
        assert_eq!(s.line_col(0), (1, 1));
        assert_eq!(s.line_col(1), (1, 2)); // \r 在第 1 行第 2 列
        assert_eq!(s.line_col(3), (2, 1)); // 中
        assert_eq!(s.line_col(4), (2, 2)); // 行尾 \n 属于它所在行
    }

    #[test]
    fn non_boundary_byte_floors() {
        let s = SourceText::new("m.lang-go", "中");
        assert_eq!(s.byte_to_cp(1), 0); // 汉字中间的字节归到该码点
        assert_eq!(s.byte_to_cp(3), 0);
        assert_eq!(s.byte_to_cp(99), 0); // 越界回到最后一个码点
        assert_eq!(s.cp_to_byte(99), 3); // 码点越界回到文末
    }
}
