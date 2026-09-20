"""源码定位（specs/source-span.md）。

Position/Span 全程以 Unicode 码点（rune）计量：offset 0 基，line/column 1 基，区间半开。
"""

from __future__ import annotations

from dataclasses import dataclass, replace


@dataclass(frozen=True, slots=True)
class Position:
    offset: int = 0
    line: int = 1
    col: int = 1


@dataclass(slots=True)
class Span:
    """源码区间 [start, end)。file 由装载器在解析后盖章（可变，仅此一处约定）。"""

    start: Position = Position()
    end: Position = Position()
    file: str = ""

    def with_file(self, file: str) -> Span:
        return replace(self, file=file)


EMPTY_SPAN = Span()
