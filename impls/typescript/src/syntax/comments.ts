// 注释 trivia 扫描（v0.5 Lossless 层）。
// 词法态扫描：跳过字符串字面量（含转义），收集全部 // 行注释，
// 产出按偏移排序的有序 trivia 流。Formatter 据此做 100% 注释保留。

import { pos, type Span } from "./span.js";

export interface Comment {
  readonly span: Span;
  /** 原文（含 // 前缀；不含换行与行尾 \r） */
  readonly text: string;
}

/** 扫描源码中的全部注释（依出现序）；src 为码点度量下的原文 */
export function scanComments(cps: readonly string[]): Comment[] {
  const out: Comment[] = [];
  let line = 1;
  let lineStart = 0;
  let i = 0;
  while (i < cps.length) {
    const r = cps[i]!;
    if (r === "\n") {
      i++;
      line++;
      lineStart = i;
    } else if (r === '"') {
      i++;
      while (i < cps.length) {
        if (cps[i] === "\\") {
          i += 2;
          continue;
        }
        if (cps[i] === '"' || cps[i] === "\n") {
          i++;
          break;
        }
        i++;
      }
    } else if (r === "/") {
      if (i + 1 < cps.length && cps[i + 1] === "/") {
        const start = i;
        const startLine = line;
        const startCol = i - lineStart + 1;
        while (i < cps.length && cps[i] !== "\n") i++;
        const text = cps.slice(start, i).join("").replace(/\r+$/, "");
        out.push({
          span: {
            file: "",
            start: pos(start, startLine, startCol),
            end: pos(i, line, i - lineStart + 1),
          },
          text,
        });
        continue;
      }
      i++;
    } else {
      i++;
    }
  }
  return out;
}
