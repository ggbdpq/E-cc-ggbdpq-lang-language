// 机械可证明安全的自动修复（v0.5 Safe Editing）。
// 只允许语义零变化的修复，每条都有安全性论证：
//   1. duplicate-import  同文件重复 import 去重
//   2. unused-import     未使用 import 删除（声明名字与文件内引用无交集）
//   3. trailing-ws       行尾空白去除
//   4. eof-newline       文件尾确保恰好一个换行
// 偏移一律为 0 基 rune（码点）偏移，依偏移降序——Apply 约定。

import * as fs from "node:fs";
import * as path from "node:path";
import { toCodePoints } from "../syntax/span.js";
import type { WorkspaceEdit } from "../edit/edit.js";
import { hashBytes, type Precondition, type TextEdit } from "../edit/edit.js";

/** 无文件归属的编辑（planFile 阶段） */
type FileEdit = Omit<TextEdit, "file">;

/** 列出目录内全部 .lang-go 文件（依文件名序） */
export function listDir(root: string): string[] {
  return fs
    .readdirSync(root, { withFileTypes: true })
    .filter((e) => !e.isDirectory() && e.name.endsWith(".lang-go"))
    .map((e) => e.name)
    .sort();
}

interface LineR {
  text: string[];
  start: number;
}

/** 对单文件计算 safe fix 的 TextEdit 列表（rune 偏移，依偏移降序） */
export function planFile(src: string): FileEdit[] {
  const edits: FileEdit[] = [];
  const rs = toCodePoints(src);
  const lines: LineR[] = [];
  let cur: LineR = { text: [], start: 0 };
  for (const r of rs) {
    if (r === "\n") {
      lines.push(cur);
      cur = { text: [], start: cur.start + cur.text.length + 1 };
    } else {
      cur.text.push(r);
    }
  }
  if (cur.text.length > 0 || lines.length === 0) lines.push(cur);
  const srcRunes = rs.length;

  const seenImports = new Set<string>();
  const removals: [number, number][] = [];

  for (const ln of lines) {
    const text = ln.text.join("");
    const trimmedRight = text.replace(/[ \t\r]+$/, "");
    const trimmedAll = text.trim();

    // 行尾空白
    if (trimmedRight !== text) {
      removals.push([ln.start + Array.from(trimmedRight).length, ln.start + ln.text.length]);
    }

    // import：重复 → 删；未使用 → 删（同一行只命中一条，避免区间重叠）
    if (trimmedAll.startsWith("import ")) {
      const dup = seenImports.has(trimmedAll);
      seenImports.add(trimmedAll);
      let unused = false;
      const name = importName(trimmedAll);
      if (name !== "" && !referencedIn(src, name)) unused = true;
      if (dup || unused) {
        removals.push([ln.start, ln.start + ln.text.length + 1]); // 含换行
      }
    }
  }

  for (const [s0, e0] of removals) {
    let start = s0;
    let end = e0;
    if (end > srcRunes) end = srcRunes; // 末行无换行时收缩到 EOF
    if (start < end) {
      edits.push({ startOffset: start, endOffset: end, replacement: "" });
    }
  }

  // EOF 换行：缺失且无触及 EOF 的删除时插入
  if (!src.endsWith("\n") && src !== "") {
    let touchEOF = false;
    for (const e of edits) {
      if (e.endOffset === srcRunes) touchEOF = true;
    }
    if (!touchEOF) {
      edits.push({ startOffset: srcRunes, endOffset: srcRunes, replacement: "\n" });
    }
  }

  // Apply 约定：按 StartOffset 降序
  return edits.sort((a, b) => b.startOffset - a.startOffset);
}

/** 从 import 行提取 "./x" 的 x */
function importName(line: string): string {
  const i = line.indexOf('"');
  const j = line.lastIndexOf('"');
  if (i < 0 || j <= i) return "";
  const target = line.slice(i + 1, j);
  if (!target.startsWith("./")) return "";
  let p = target.slice(2);
  const k = p.lastIndexOf("/");
  if (k >= 0) p = p.slice(k + 1);
  return p;
}

/** 保守判定：源码（除 import 行自身）任何位置出现该名字即视为被引用。宁可漏删不误删 */
function referencedIn(src: string, name: string): boolean {
  for (const line of src.split("\n")) {
    if (line.trim().startsWith("import ")) continue;
    if (line.includes(name)) return true;
  }
  return false;
}

/** 对目录内全部 .lang-go 计算 safe fix WorkspaceEdit */
export function planDir(root: string): WorkspaceEdit {
  const files = listDir(root);
  const edits: TextEdit[] = [];
  const preconditions: Precondition[] = [];
  for (const rel of files) {
    let bs: Buffer;
    try {
      bs = fs.readFileSync(path.join(root, rel));
    } catch (e) {
      throw new Error(`读 ${rel}: ${String(e)}`);
    }
    const fileEdits = planFile(bs.toString("utf8"));
    if (fileEdits.length === 0) continue;
    for (const e of fileEdits) {
      edits.push({ ...e, file: rel });
    }
    preconditions.push({ file: rel, fileHash: hashBytes(bs) });
  }
  return { edits, preconditions };
}
