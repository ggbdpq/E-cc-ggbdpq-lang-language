"""装载器：目录即包 + import 展开（MODULE-ROOT/IMPORT/MERGE/CYCLE/INIT）。

行为对齐 Go 参考实现：
- 目录内全部非测试 .lang-go 按文件名序合并；import 递归展开（被导入先于导入者）；
- 只支持 "./相对路径"，目标为 x.lang-go 文件或 x/ 目录；
- 越出模块根 → 装载错误；循环 import → 装载错误；菱形去重；
- include_tests 时 *_test.lang-go 追加在后，并收集 test 前缀无参函数名；
- 解析后给全部节点盖 Span.file 章（模块根相对路径，/ 分隔）。
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field

from ..errors import LangError, ParseError
from ..syntax import ast_nodes as A
from ..syntax.parser import parse
from ..syntax.walk import stamp_file


@dataclass
class FileInfo:
    """文件装载信息（inspect dependencies 用）。"""

    path: str  # 模块根相对路径（/ 分隔）
    imports: list[str] = field(default_factory=list)  # 出现序
    order: int = 0


@dataclass
class Program:
    """装载产物：合并后的语句序列 + 测试函数名 + 文件级依赖信息。"""

    stmts: list[A.Stmt]
    tests: list[str]
    files: list[FileInfo]


class _Loader:
    def __init__(self, root: str) -> None:
        self.root = os.path.abspath(root)
        self.in_progress: set[str] = set()  # 正在展开（真环检测）
        self.done: set[str] = set()  # 已完整装载（菱形去重）
        self.files: list[FileInfo] = []

    def list_lang_go(self, directory: str, tests: bool) -> list[str]:
        try:
            entries = sorted(os.listdir(directory))
        except OSError as e:
            raise LangError(str(e)) from None
        out = []
        for name in entries:
            full = os.path.join(directory, name)
            if not name.endswith(".lang-go") or not os.path.isfile(full):
                continue
            is_test = name.endswith("_test.lang-go")
            if is_test == tests:
                out.append(name)
        return sorted(out)

    def load_dir(self, directory: str) -> list[A.Stmt]:
        files = self.list_lang_go(directory, tests=False)
        raw: list[A.Stmt] = []
        for f in files:
            ap = os.path.abspath(os.path.join(directory, f))
            stmts = self.load_file(ap)
            self.done.add(ap)
            # import 解析键不含扩展名：同目录文件既被根装载又被 import 引用时不双载
            self.done.add(_strip_ext(ap))
            raw.extend(stmts)
        return self.expand(raw, directory)

    def load_file(self, ap: str) -> list[A.Stmt]:
        try:
            with open(ap, "r", encoding="utf-8") as fh:
                src = fh.read()
        except OSError as e:
            raise LangError(str(e)) from None
        try:
            stmts = parse(src)
        except ParseError as e:
            raise LangError(f"{os.path.basename(ap)}: {e}") from None
        rel = os.path.relpath(ap, self.root).replace(os.sep, "/")
        stamp_file(stmts, rel)
        fi = FileInfo(path=rel, order=len(self.files))
        for st in stmts:
            if isinstance(st, A.ImportStmt):
                fi.imports.append(st.path)
        self.files.append(fi)
        return stmts

    def expand(self, stmts: list[A.Stmt], from_dir: str) -> list[A.Stmt]:
        out: list[A.Stmt] = []
        for s in stmts:
            if not isinstance(s, A.ImportStmt):
                out.append(s)
                continue
            if not s.path.startswith("./"):
                raise LangError(f'第{s.line}行: import 仅支持 "./相对路径"（得到 "{s.path}"）')
            target, skip = self._resolve(s.path, from_dir, s.line)
            if skip:
                continue  # 菱形导入：已装载过
            if os.path.isdir(target):
                sub = self.load_dir(target)
            else:
                file_stmts = self.load_file(target + ".lang-go")
                sub = self.expand(file_stmts, os.path.dirname(target))
            self.done.add(target)
            self.in_progress.discard(target)
            out.extend(sub)
        return out

    def _resolve(self, rel: str, from_dir: str, line: int) -> tuple[str, bool]:
        target = os.path.abspath(os.path.normpath(os.path.join(from_dir, rel)))
        rel_to_root = os.path.relpath(target, self.root)
        if rel_to_root == ".." or rel_to_root.replace(os.sep, "/").startswith("../"):
            raise LangError(f"第{line}行: import {rel} 逃出模块根目录")
        if target in self.done:
            return target, True
        if target in self.in_progress:
            raise LangError(f"第{line}行: 循环 import {rel}")
        self.in_progress.add(target)
        return target, False


def _strip_ext(ap: str) -> str:
    return ap[: -len(".lang-go")] if ap.endswith(".lang-go") else ap


def load_program(directory: str, include_tests: bool) -> Program:
    l = _Loader(os.path.abspath(directory))
    stmts = l.load_dir(l.root)
    tests: list[str] = []
    if include_tests:
        for f in l.list_lang_go(l.root, tests=True):
            stmts_ = l.load_file(os.path.join(l.root, f))
            # 测试文件与普通文件同等待遇：import 在装载期展开（v1.0 dogfood 修复）
            stmts.extend(l.expand(stmts_, l.root))
            for s in stmts_:
                if isinstance(s, A.FnDecl) and s.recv is None and not s.params and s.name.startswith("test"):
                    tests.append(s.name)
    return Program(stmts=stmts, tests=tests, files=l.files)
