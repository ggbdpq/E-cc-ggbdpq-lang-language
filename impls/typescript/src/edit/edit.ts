// 结构化编辑（v0.5 Safe Editing）。
// TextEdit/WorkspaceEdit 是 Agent 结构化修改的唯一载体；
// Apply 执行前置校验（哈希防误写）→ 重叠检测 → 偏移降序拼接 → 原子写回。
// 偏移一律为 0 基 rune（码点）偏移。

import * as fs from "node:fs";
import * as path from "node:path";
import { createHash } from "node:crypto";
import { toCodePoints } from "../syntax/span.js";

export interface TextEdit {
  readonly file: string;
  readonly startOffset: number;
  readonly endOffset: number;
  readonly replacement: string;
}

export interface Precondition {
  readonly file: string;
  /** sha256 hex（原文件字节） */
  readonly fileHash: string;
}

export interface WorkspaceEdit {
  readonly edits: readonly TextEdit[];
  readonly preconditions: readonly Precondition[];
}

export const hashBytes = (bs: Buffer | string): string =>
  createHash("sha256").update(bs).digest("hex");

export const hashFile = (p: string): string => hashBytes(fs.readFileSync(p));

/** 校验前置条件并应用全部编辑。任一前置校验失败或区间重叠 → 抛错且不写任何文件 */
export function apply(ws: WorkspaceEdit, root: string): void {
  if (ws.edits.length === 0) return;
  const pre = new Map<string, string>();
  for (const p of ws.preconditions) pre.set(p.file, p.fileHash);
  // 按文件分组（确定性：文件路径排序处理）
  const byFile = new Map<string, TextEdit[]>();
  for (const e of ws.edits) {
    const list = byFile.get(e.file) ?? [];
    list.push(e);
    byFile.set(e.file, list);
  }
  const files = [...byFile.keys()].sort();
  for (const file of files) {
    if (!pre.has(file)) {
      throw new Error(`edit: 文件 ${file} 缺少前置校验（fileHash）`);
    }
  }

  const newContents = new Map<string, Buffer>();
  for (const file of files) {
    const p = path.join(root, file);
    let bs: Buffer;
    try {
      bs = fs.readFileSync(p);
    } catch (e) {
      throw new Error(`edit: 读 ${file}: ${String(e)}`);
    }
    const h = pre.get(file)!;
    if (hashBytes(bs) !== h) {
      throw new Error(`edit: ${file} 自分析后已变化（哈希不匹配），拒绝写入`);
    }
    const runes = toCodePoints(bs.toString("utf8"));
    const edits = [...byFile.get(file)!].sort((a, b) => b.startOffset - a.startOffset);
    for (let i = 0; i < edits.length; i++) {
      const e = edits[i]!;
      if (e.startOffset < 0 || e.endOffset < e.startOffset || e.endOffset > runes.length) {
        throw new Error(`edit: ${file} 第 ${i} 个编辑区间越界 [${e.startOffset},${e.endOffset})（长度 ${runes.length}）`);
      }
      const next = edits[i + 1];
      if (next !== undefined && next.endOffset > e.startOffset) {
        throw new Error(`edit: ${file} 存在重叠编辑区间`);
      }
    }
    // 逐编辑 splice（降序下每个编辑的区间不受更高偏移编辑影响），与 Go 一致
    let out = runes;
    for (const e of edits) {
      out = [...out.slice(0, e.startOffset), ...toCodePoints(e.replacement), ...out.slice(e.endOffset)];
    }
    newContents.set(file, Buffer.from(out.join(""), "utf8"));
  }
  for (const [file, content] of newContents) {
    try {
      fs.writeFileSync(path.join(root, file), content);
    } catch (e) {
      throw new Error(`edit: 写 ${file}: ${String(e)}`);
    }
  }
}
