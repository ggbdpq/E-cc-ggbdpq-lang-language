// SourceSpan：0 基 rune（Unicode 码点）偏移 + 1 基行/列（specs/source-span.md）。
// TS 宿主风险控制：JS 字符串索引是 UTF-16 code unit，本实现统一在词法入口把
// 源码展开为码点数组（Array.from），此后所有 offset/line/column 都是码点度量，
// 与 Go 实现逐一对齐；对外（Agent Contract / TextEdit）只暴露码点偏移。

export interface Position {
  readonly offset: number;
  readonly line: number;
  readonly col: number;
}

export interface Span {
  readonly file: string;
  readonly start: Position;
  readonly end: Position;
}

export const pos = (offset: number, line: number, col: number): Position => ({ offset, line, col });

export const spanOf = (file: string, start: Position, end: Position): Span => ({ file, start, end });

/** 把源码展开为码点数组（每元素恰一个 Unicode 码点），并给出每行起始码点偏移 */
export function toCodePoints(src: string): string[] {
  return Array.from(src);
}

/** 每行起始码点偏移（0 基），首行起始为 0；与 Go lineStarts 同构 */
export function lineStarts(cps: readonly string[]): number[] {
  const starts: number[] = [0];
  for (let i = 0; i < cps.length; i++) {
    if (cps[i] === "\n") starts.push(i + 1);
  }
  return starts;
}

/** 偏移 + 行 → Position（col 1 基；行为 0 或越界时钳制，与 Go posAt 一致） */
export function posAt(starts: readonly number[], offset: number, line: number): Position {
  if (line < 1) line = 1;
  if (line > starts.length) line = starts.length;
  let col = offset - starts[line - 1]! + 1;
  if (col < 1) col = 1;
  return pos(offset, line, col);
}
