"""机械可证明安全的自动修复（v0.5；fix.go 语义等价）。

只允许语义零变化的修复：
1. duplicate-import 同文件重复 import 去重；
2. unused-import 未使用 import 删除（保守判定：宁可漏删不误删）；
3. trailing-ws 行尾空白去除；
4. eof-newline 文件尾确保恰好一个换行。
偏移一律 0 基 rune 偏移，依偏移降序（apply 约定）。
"""

from __future__ import annotations

import os

from ..edit.edit import Precondition, TextEdit, WorkspaceEdit, hash_bytes


def list_dir(root: str) -> list[str]:
    """目录内全部 .lang-go 文件名（依文件名序）。"""
    entries = sorted(os.listdir(root))
    return [e for e in entries if e.endswith(".lang-go") and os.path.isfile(os.path.join(root, e))]


def plan_file(src: str) -> list[TextEdit]:
    """单文件 safe fix 的 TextEdit 列表（rune 偏移，降序）。"""
    edits: list[TextEdit] = []

    # 行切分（rune 基；text 不含换行）
    lines: list[tuple[str, int]] = []  # (text, 行首 rune 偏移)
    rs = list(src)
    cur_text: list[str] = []
    cur_start = 0
    for r in rs:
        if r == "\n":
            lines.append(("".join(cur_text), cur_start))
            cur_start += len(cur_text) + 1
            cur_text = []
        else:
            cur_text.append(r)
    if cur_text or not lines:
        lines.append(("".join(cur_text), cur_start))
    src_runes = len(rs)

    seen_imports: set[str] = set()
    removals: list[tuple[int, int]] = []

    for text, start in lines:
        trimmed_right = text.rstrip(" \t\r")
        trimmed_all = text.strip()

        # 行尾空白
        if trimmed_right != text:
            removals.append((start + len(trimmed_right), start + len(text)))

        # import：重复 → 删；未使用 → 删（同一行只命中一条，避免区间重叠）
        if trimmed_all.startswith("import "):
            dup = trimmed_all in seen_imports
            seen_imports.add(trimmed_all)
            unused = False
            name = _import_name(trimmed_all)
            if name and not _referenced_in(src, name):
                unused = True
            if dup or unused:
                removals.append((start, start + len(text) + 1))  # 含换行

    for r0, r1 in removals:
        if r1 > src_runes:
            r1 = src_runes  # 末行无换行时收缩到 EOF
        if r0 < r1:
            edits.append(TextEdit(file="", start_offset=r0, end_offset=r1))

    # EOF 换行：缺失且无触及 EOF 的删除时插入
    if not src.endswith("\n") and src:
        touch_eof = any(e.end_offset == src_runes for e in edits)
        if not touch_eof:
            edits.append(TextEdit(start_offset=src_runes, end_offset=src_runes, replacement="\n"))

    edits.sort(key=lambda e: e.start_offset, reverse=True)
    return edits


def _import_name(line: str) -> str:
    i = line.find('"')
    j = line.rfind('"')
    if i < 0 or j <= i:
        return ""
    target = line[i + 1:j]
    if not target.startswith("./"):
        return ""
    p = target[2:]
    k = p.rfind("/")
    if k >= 0:
        p = p[k + 1:]
    return p


def _referenced_in(src: str, name: str) -> bool:
    """保守判定：源码（除 import 行自身）任何位置出现该名字即视为被引用。"""
    for line in src.split("\n"):
        if line.strip().startswith("import "):
            continue
        if name in line:
            return True
    return False


def plan_dir(root: str) -> WorkspaceEdit:
    """目录内全部 .lang-go 计算 safe fix WorkspaceEdit。"""
    ws = WorkspaceEdit()
    for rel in list_dir(root):
        with open(os.path.join(root, rel), "rb") as f:
            bs = f.read()
        edits = plan_file(bs.decode("utf-8"))
        if not edits:
            continue
        for e in edits:
            e.file = rel
        ws.edits.extend(edits)
        ws.preconditions.append(Precondition(file=rel, file_hash=hash_bytes(bs)))
    return ws
