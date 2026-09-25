"""注释 trivia 扫描（Lossless 层）：词法态扫描收集全部 // 行注释，供 Formatter 100% 保留。"""

from __future__ import annotations

from dataclasses import dataclass

from .span import Position, Span


@dataclass(frozen=True, slots=True)
class Comment:
    span: Span
    text: str  # 原文（含 // 前缀；不含换行与行尾 \r）

    @property
    def line(self) -> int:
        return self.span.start.line

    @property
    def col(self) -> int:
        return self.span.start.col


def scan_comments(src: str) -> list[Comment]:
    runes = src
    out: list[Comment] = []
    line = 1
    line_start = 0
    i = 0
    n = len(runes)
    while i < n:
        r = runes[i]
        if r == "\n":
            i += 1
            line += 1
            line_start = i
        elif r == '"':
            i += 1
            while i < n:
                if runes[i] == "\\":
                    i += 2
                    continue
                if runes[i] == '"' or runes[i] == "\n":
                    i += 1
                    break
                i += 1
        elif r == "/":
            if i + 1 < n and runes[i + 1] == "/":
                start = i
                start_line = line
                start_col = i - line_start + 1
                while i < n and runes[i] != "\n":
                    i += 1
                text = runes[start:i].rstrip("\r")
                out.append(Comment(
                    Span(Position(start, start_line, start_col), Position(i, line, i - line_start + 1)),
                    text,
                ))
                continue
            i += 1
        else:
            i += 1
    return out
