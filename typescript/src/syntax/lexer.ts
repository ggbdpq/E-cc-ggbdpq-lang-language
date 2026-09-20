// 词法器（LEX-001..008）。在码点数组上扫描，偏移即 0 基 rune 偏移。
// 换行即语句结束；续行只看上一记号（isCont）。错误消息按 Go 参考实现逐字复刻。

import type { Token, TokenKind } from "./token.js";
import { isCont, keywords } from "./token.js";

export class LexError extends Error {
  constructor(
    readonly msg: string,
    readonly line: number,
    readonly snippet: string,
  ) {
    super(formatWithSnippet(`第${line}行: ${msg}`, snippet));
    this.name = "LexError";
  }
}

export function formatWithSnippet(base: string, snippet: string): string {
  if (snippet === "") return base;
  return `${base}\n  | ${snippet}`;
}

const isAsciiDigit = (r: string): boolean => r >= "0" && r <= "9";
const isHexDigit = (r: string): boolean =>
  (r >= "0" && r <= "9") || (r >= "a" && r <= "f") || (r >= "A" && r <= "F");

// Go unicode.IsLetter / IsDigit：JS Unicode 属性闭包同源（L* 与 Nd）
const RE_LETTER = /\p{L}/u;
const RE_DIGIT = /\p{Nd}/u;
const isIdentStart = (r: string): boolean => r === "_" || RE_LETTER.test(r);
const isIdentPart = (r: string): boolean => r === "_" || RE_LETTER.test(r) || RE_DIGIT.test(r);
// 词法循环与数字消费用 Go unicode.IsDigit（Nd 全集）；非 ASCII 数字最终由
// parse 阶段的 ParseInt/ParseFloat 等价校验报错（与 Go 一致）
const isDigit = RE_DIGIT.test.bind(RE_DIGIT);

class Lexer {
  private i = 0;
  private line = 1;
  private last: Token = { kind: "eof", lit: "", line: 0, start: 0, end: 0 };
  private toks: Token[] = [];

  constructor(private readonly cps: readonly string[]) {}

  private peek(n: number): string {
    return this.i + n < this.cps.length ? this.cps[this.i + n]! : "\0";
  }

  private emitAt(kind: TokenKind, lit: string, start: number): void {
    const t: Token = { kind, lit, line: this.line, start, end: this.i };
    this.toks.push(t);
    this.last = t;
  }

  /** 构造带当前行摘录的词法错误（输出末尾报错时摘录前一行，与 Go lexErrAt 一致） */
  private errAt(msg: string): LexError {
    let line = this.line;
    if (line > 1 && this.i >= this.cps.length) line--;
    let text = "";
    if (line >= 1) {
      let start = 0;
      let cur = 1;
      for (let i = 0; i < this.cps.length && cur < line; i++) {
        if (this.cps[i] === "\n") {
          cur++;
          start = i + 1;
        }
      }
      let end = this.cps.length;
      for (let i = start; i < this.cps.length; i++) {
        if (this.cps[i] === "\n") {
          end = i;
          break;
        }
      }
      text = this.cps.slice(start, end).join("").trim();
    }
    return new LexError(msg, this.line, text);
  }

  /** 落实续行规则：续行记号之后、行首的换行不产记号 */
  private emitNewline(): void {
    if (this.toks.length === 0 || isCont(this.last.kind)) return;
    if (this.last.kind === "newline") return;
    this.toks.push({ kind: "newline", lit: "\n", line: this.line - 1, start: this.i - 1, end: this.i });
    this.last = { kind: "newline", lit: "\n", line: this.line, start: this.i, end: this.i };
  }

  private number(start: number): void {
    // 十六进制 0x/0X 前缀（Q28）：记号文本统一为十进制，fmt 无损输出
    const c0 = this.cps[this.i]!;
    if (c0 === "0" && this.i + 1 < this.cps.length && (this.cps[this.i + 1] === "x" || this.cps[this.i + 1] === "X")) {
      this.i += 2;
      const digits = this.i;
      while (this.i < this.cps.length && isHexDigit(this.cps[this.i]!)) this.i++;
      if (this.i === digits) throw this.errAt("十六进制字面量缺少数字（如 0xFF）");
      const text = this.cps.slice(start, this.i).join("");
      const val = BigInt(text);
      if (val > 0x7fffffffffffffffn || val < -0x8000000000000000n) {
        throw this.errAt(`十六进制字面量超出 int 范围: ${text}`);
      }
      this.emitAt("int", val.toString(10), start);
      return;
    }

    while (this.i < this.cps.length && isDigit(this.cps[this.i]!)) this.i++;
    let isFloat = false;
    // "1..3" 里第一个 '.' 不属于数字
    if (this.i < this.cps.length && this.cps[this.i] === "." && isDigit(this.peek(1))) {      isFloat = true;
      this.i++;
      while (this.i < this.cps.length && isDigit(this.cps[this.i]!)) this.i++;
    }
    const lit = this.cps.slice(start, this.i).join("");
    this.emitAt(isFloat ? "float" : "int", lit, start);
  }

  private stringLit(start: number): void {
    this.i++;
    let b = "";
    for (;;) {
      if (this.i >= this.cps.length || this.cps[this.i] === "\n") throw this.errAt("字符串未闭合");
      const r = this.cps[this.i]!;
      if (r === '"') {
        this.i++;
        break;
      }
      if (r === "\\") {
        this.i++;
        if (this.i >= this.cps.length) throw this.errAt("字符串未闭合");
        const esc = this.cps[this.i]!;
        switch (esc) {
          case "n":
            b += "\n";
            break;
          case "t":
            b += "\t";
            break;
          case "r":
            b += "\r";
            break;
          case '"':
            b += '"';
            break;
          case "\\":
            b += "\\";
            break;
          default:
            throw this.errAt(`不支持的转义 \\${esc}`);
        }
        this.i++;
        continue;
      }
      b += r;
      this.i++;
    }
    this.emitAt("string", b, start);
  }

  private ident(start: number): void {
    while (this.i < this.cps.length && isIdentPart(this.cps[this.i]!)) this.i++;
    const name = this.cps.slice(start, this.i).join("");
    // 单独的下划线是通配符记号
    if (name === "_") {
      this.emitAt("underscore", name, start);
      return;
    }
    const kw = keywords[name];
    if (kw !== undefined) {
      this.emitAt(kw, name, start);
      return;
    }
    this.emitAt("ident", name, start);
  }

  private op(start: number): void {
    const r = this.cps[this.i]!;
    const two = (next: string, k: TokenKind, lit: string): boolean => {
      if (this.peek(1) === next) {
        this.i += 2;
        this.emitAt(k, lit, start);
        return true;
      }
      return false;
    };
    switch (r) {
      case "&":
        if (two("&", "&&", "&&")) return;
        this.i++;
        this.emitAt("&", "&", start);
        return;
      case "|":
        if (two("|", "||", "||")) return;
        this.i++;
        this.emitAt("|", "|", start);
        return;
      case "^":
        this.i++;
        this.emitAt("^", "^", start);
        return;
      case "+":
        if (two("=", "+=", "+=")) return;
        this.i++;
        this.emitAt("+", "+", start);
        return;
      case "-":
        if (two("=", "-=", "-=")) return;
        this.i++;
        this.emitAt("-", "-", start);
        return;
      case "*":
        if (two("=", "*=", "*=")) return;
        this.i++;
        this.emitAt("*", "*", start);
        return;
      case "/":
        if (two("=", "/=", "/=")) return;
        this.i++;
        this.emitAt("/", "/", start);
        return;
      case "=":
        if (two("=", "==", "==")) return;
        if (two(">", "=>", "=>")) return;
        this.i++;
        this.emitAt("=", "=", start);
        return;
      case "!":
        if (two("=", "!=", "!=")) return;
        this.i++;
        this.emitAt("!", "!", start);
        return;
      case "<":
        if (two("<", "<<", "<<")) return;
        if (two("=", "<=", "<=")) return;
        this.i++;
        this.emitAt("<", "<", start);
        return;
      case ">":
        if (two(">", ">>", ">>")) return;
        if (two("=", ">=", ">=")) return;
        this.i++;
        this.emitAt(">", ">", start);
        return;
      case ".":
        if (two(".", "..", "..")) return;
        this.i++;
        this.emitAt(".", ".", start);
        return;
      case "%":
      case "(":
      case ")":
      case "{":
      case "}":
      case "[":
      case "]":
      case ",":
      case ":":
      case "?": {
        this.i++;
        const k: TokenKind =
          r === "%"
            ? "%"
            : r === "("
              ? "("
              : r === ")"
                ? ")"
                : r === "{"
                  ? "{"
                  : r === "}"
                    ? "}"
                    : r === "["
                      ? "["
                      : r === "]"
                        ? "]"
                        : r === ","
                          ? ","
                          : r === ":"
                            ? ":"
                            : "?";
        this.emitAt(k, r, start);
        return;
      }
      default:
        throw this.errAt(`非法字符 ${r}`);
    }
  }

  run(): Token[] {
    while (this.i < this.cps.length) {
      const start = this.i;
      const r = this.cps[this.i]!;
      if (r === " " || r === "\t" || r === "\r") {
        this.i++;
      } else if (r === "\n") {
        this.i++;
        this.line++;
        this.emitNewline();
      } else if (r === "/" && this.peek(1) === "/") {
        while (this.i < this.cps.length && this.cps[this.i] !== "\n") this.i++;
      } else if (isDigit(r)) {
        this.number(start);
      } else if (r === '"') {
        this.stringLit(start);
      } else if (isIdentStart(r)) {
        this.ident(start);
      } else {
        this.op(start);
      }
    }
    this.toks.push({ kind: "eof", lit: "", line: this.line, start: this.i, end: this.i });
    return this.toks;
  }
}

/** 把源码切成记号流，末尾追加 EOF */
export function lex(src: string): Token[] {
  return new Lexer(Array.from(src)).run();
}
