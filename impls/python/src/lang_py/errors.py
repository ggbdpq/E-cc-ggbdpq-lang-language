"""错误与控制流信号的统一载体。

分层（不得混用）：
- ParseError / LexError      —— 词法与解析失败（E1xxx 段，REPL 靠 incomplete 续行）
- SemanticDiag / Diagnostic  —— 语言级诊断（结构化，可聚合），不是宿主异常
- LangError                  —— 装载/import 失败等带用户的操作面错误
- LangRuntimeError           —— 语言级运行时失败（RUNTIME-FAIL-001），CLI 转退出码 1
- SignalReturn/Break/Continue/PropagateErr —— 解释器内部控制流，绝不穿越 CLI 边界

普通语言级 Result 语义一律走返回值；宿主异常只承载真正的失败。
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Diagnostic:
    """一条语言级诊断（诊断契约：code + severity + 定位结构是契约，message 允许语言化差异）。"""

    code: str
    message: str
    line: int

    def render(self) -> str:
        return f"第{self.line}行: [{self.code}] {self.message}"


class DiagnosticList(Exception):
    """多诊断聚合（实现 error 语义，可 raise）；可迭代为 Diagnostic 序列；str() 取首条。"""

    def __init__(self, diags: list[Diagnostic]) -> None:
        super().__init__(diags[0].render() if diags else "无诊断")
        self.diags = diags

    def __iter__(self):
        return iter(self.diags)

    def __len__(self) -> int:
        return len(self.diags)

    def __getitem__(self, i):
        return self.diags[i]

    def __str__(self) -> str:
        return self.args[0] if self.args else "无诊断"


class LexError(Exception):
    """词法错误（统一落 E1001）。"""

    def __init__(self, msg: str, line: int, snippet: str = "") -> None:
        super().__init__(msg)
        self.msg = msg
        self.line = line
        self.snippet = snippet

    def __str__(self) -> str:
        if self.snippet:
            return f"第{self.line}行: {self.msg}\n  | {self.snippet}"
        return f"第{self.line}行: {self.msg}"


class ParseError(Exception):
    """解析错误。incomplete=True 表示输入语法中途截断（REPL 续行）。"""

    def __init__(self, msg: str, line: int, incomplete: bool = False, snippet: str = "") -> None:
        super().__init__(msg)
        self.msg = msg
        self.line = line
        self.incomplete = incomplete
        self.snippet = snippet

    def __str__(self) -> str:
        if self.snippet:
            return f"第{self.line}行: {self.msg}\n  | {self.snippet}"
        return f"第{self.line}行: {self.msg}"


class LangError(Exception):
    """装载/工具面失败（import 展开等）。message 已含定位前缀。"""


class LangRuntimeError(Exception):
    """语言级运行时失败（RUNTIME-FAIL-001）。message 为面向用户的完整文本。"""

    def __init__(self, message: str) -> None:
        super().__init__(message)
        self.message = message


# ———— 解释器内部控制流信号（异常承载，捕获边界与 Go 的 asSignal 一致）————

class SignalReturn(Exception):
    """return 信号；value 已按值语义拷贝。"""

    def __init__(self, value: object) -> None:
        super().__init__("return 出现在函数之外")
        self.value = value


class SignalBreak(Exception):
    def __init__(self) -> None:
        super().__init__("break 出现在循环之外")


class SignalContinue(Exception):
    def __init__(self) -> None:
        super().__init__("continue 出现在循环之外")


class SignalPropagateErr(Exception):
    """`?` 传播出函数边界时携带的 error 值。"""

    def __init__(self, value: object) -> None:
        super().__init__("Err 传播出函数之外")
        self.value = value
