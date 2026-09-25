"""结构化编辑（v0.5 Safe Editing；edit.go 语义等价）。

TextEdit/WorkspaceEdit 是 Agent 结构化修改的唯一载体；
apply 执行前置校验（哈希防误写）→ 重叠检测 → 偏移降序拼接 → 原子写回。
偏移一律 0 基 rune 偏移（中文安全）。
"""

from __future__ import annotations

import hashlib
import os
from dataclasses import dataclass, field


@dataclass
class TextEdit:
    file: str = ""  # plan_file 产出的编辑在 plan_dir 统一盖章
    start_offset: int = 0
    end_offset: int = 0
    replacement: str = ""


@dataclass
class Precondition:
    file: str
    file_hash: str  # sha256 hex（原文件字节）


@dataclass
class WorkspaceEdit:
    edits: list[TextEdit] = field(default_factory=list)
    preconditions: list[Precondition] = field(default_factory=list)


def hash_bytes(bs: bytes) -> str:
    return hashlib.sha256(bs).hexdigest()


def hash_file(path: str) -> str:
    with open(path, "rb") as f:
        return hash_bytes(f.read())


class EditError(Exception):
    pass


def apply(ws: WorkspaceEdit, root: str) -> None:
    """校验前置条件并应用全部编辑。任一校验失败或区间重叠 → EditError 且不写任何文件。"""
    if not ws.edits:
        return
    pre = {p.file: p.file_hash for p in ws.preconditions}
    # 按文件分组（确定性：文件路径排序处理）
    by_file: dict[str, list[TextEdit]] = {}
    for e in ws.edits:
        by_file.setdefault(e.file, []).append(e)
    files = sorted(by_file)
    for file in files:
        if file not in pre:
            raise EditError(f"edit: 文件 {file} 缺少前置校验（fileHash）")

    new_contents: dict[str, str] = {}
    for file in files:
        path = os.path.join(root, *file.split("/"))
        with open(path, "rb") as f:
            bs = f.read()
        if hash_bytes(bs) != pre[file]:
            raise EditError(f"edit: {file} 自分析后已变化（哈希不匹配），拒绝写入")
        runes = list(bs.decode("utf-8"))
        edits = sorted(by_file[file], key=lambda e: e.start_offset, reverse=True)
        for i, e in enumerate(edits):
            if e.start_offset < 0 or e.end_offset < e.start_offset or e.end_offset > len(runes):
                raise EditError(
                    f"edit: {file} 第 {i} 个编辑区间越界 [{e.start_offset},{e.end_offset})（长度 {len(runes)}）")
            if i + 1 < len(edits) and edits[i + 1].end_offset > e.start_offset:
                raise EditError(f"edit: {file} 存在重叠编辑区间")
        for e in edits:
            runes[e.start_offset:e.end_offset] = list(e.replacement)
        new_contents[file] = "".join(runes)
    for file in files:
        path = os.path.join(root, *file.split("/"))
        with open(path, "wb") as f:
            f.write(new_contents[file].encode("utf-8"))
