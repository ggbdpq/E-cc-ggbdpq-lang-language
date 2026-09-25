// langGo 格式化器（v0.5 Safe Editing）。
// 渲染为行缓冲；注释按 trivia 流插回：
//   - 行首注释：独立行输出，保留注释间空行分组（最多一空行）；
//   - 行尾注释：追加到语句渲染输出的最后一行（两空格间隔）；
//   - 表达式内注释：语句边界冲刷（位置后移，文本零丢失）；
//   - 块尾注释：块内最后冲刷，原位保留在 `}` 之前。
// 硬契约：100% 注释保留、幂等、语义等价、确定性输出。

import { parse } from "../syntax/parser.js";
import { scanComments, type Comment } from "../syntax/comments.js";
import { toCodePoints } from "../syntax/span.js";
import type {
  Expr, IfStmt, Pat, Stmt, TypeEx,
} from "../syntax/ast.js";
import { LexError } from "../syntax/lexer.js";
import { ParseError } from "../syntax/parser.js";

export class FormatError extends Error {
  constructor(
    message: string,
    readonly cause0: unknown,
  ) {
    super(message);
    this.name = "FormatError";
  }
}

class CommentTracker {
  private pos = 0;
  constructor(private readonly all: readonly Comment[]) {}

  peek(): Comment | undefined {
    return this.pos < this.all.length ? this.all[this.pos] : undefined;
  }

  advance(): void {
    this.pos++;
  }
}

class Fmtter {
  private lines: string[] = [];
  private lastSrcLine = 0;

  constructor(private readonly cm: CommentTracker) {}

  /** 冲刷 start 之前的整行注释（行尾注释留在流里给 flushTrailing） */
  flushLeading(start: number, indent: number): void {
    for (;;) {
      const c = this.cm.peek();
      if (c === undefined || c.span.start.offset >= start) return;
      this.cm.advance();
      if (c.span.start.line - this.lastSrcLine >= 2) this.lines.push("");
      this.lines.push("    ".repeat(indent) + c.text);
      this.lastSrcLine = c.span.start.line;
    }
  }

  /** 冲刷语句末行上的行尾注释（追加到最近输出行） */
  flushTrailing(endOffset: number, endLine: number): void {
    for (;;) {
      const c = this.cm.peek();
      if (c === undefined || c.span.start.line !== endLine || c.span.start.offset < endOffset) return;
      this.cm.advance();
      if (this.lines.length === 0) this.lines.push("");
      this.lines[this.lines.length - 1] += "  " + c.text;
      this.lastSrcLine = c.span.start.line;
    }
  }

  /** 冲刷全部挂起注释（块尾/文件尾），整行输出 */
  flushRemaining(indent: number): void {
    for (;;) {
      const c = this.cm.peek();
      if (c === undefined) return;
      this.cm.advance();
      if (c.span.start.line - this.lastSrcLine >= 2) this.lines.push("");
      this.lines.push("    ".repeat(indent) + c.text);
      this.lastSrcLine = c.span.start.line;
    }
  }

  w(indent: number, s: string): void {
    const parts = s.split("\n");
    for (let i = 0; i < parts.length; i++) {
      if (i === 0) this.lines.push("    ".repeat(indent) + parts[i]);
      else this.lines.push(parts[i]!);
    }
  }

  out(): string {
    return this.lines.length === 0 ? "" : this.lines.join("\n") + "\n";
  }

  stmts(list: readonly Stmt[], indent: number): void {
    for (const s of list) {
      const sp = s.span;
      this.flushLeading(sp.start.offset, indent);
      this.lastSrcLine = sp.end.line;
      this.stmt(s, indent);
      this.flushTrailing(sp.end.offset, sp.end.line);
    }
    this.flushRemaining(indent);
  }

  stmt(s: Stmt, indent: number): void {
    switch (s.kind) {
      case "let_stmt": {
        const kw = s.mutable ? "var" : "let";
        const anno = s.typ !== undefined ? ": " + typ(s.typ) : "";
        this.w(indent, `${kw} ${s.name}${anno} = ${this.expr(s.init, 0, indent)}`);
        return;
      }

      case "assign":
        this.w(indent, `${this.expr(s.target, 0, indent)} ${s.op} ${this.expr(s.val, 0, indent)}`);
        return;

      case "expr_stmt":
        this.w(indent, this.expr(s.x, 0, indent));
        return;

      case "return":
        if (s.val === undefined) this.w(indent, "return");
        else this.w(indent, "return " + this.expr(s.val, 0, indent));
        return;

      case "if":
        this.ifChain(s, indent);
        return;

      case "while":
        this.w(indent, `while ${this.expr(s.cond, 0, indent)} {`);
        this.stmts(s.body, indent + 1);
        this.w(indent, "}");
        return;

      case "for_in":
        this.w(indent, `for ${s.variable} in ${this.expr(s.iter, 0, indent)} {`);
        this.stmts(s.body, indent + 1);
        this.w(indent, "}");
        return;

      case "break":
        this.w(indent, "break");
        return;
      case "continue":
        this.w(indent, "continue");
        return;

      case "go":
        this.w(indent, "go " + this.expr(s.call, 0, indent));
        return;

      case "fn_decl":
        this.fnDecl(s, indent);
        return;

      case "struct_decl": {
        this.w(indent, `struct ${s.name}${genericParams(s.typeParams)} {`);
        for (const fd of s.fields) {
          this.w(indent + 1, `${fd.name}: ${typ(fd.typ)}`);
        }
        this.w(indent, "}");
        return;
      }

      case "enum_decl": {
        this.w(indent, `enum ${s.name}${genericParams(s.typeParams)} {`);
        for (const c of s.cases) {
          if (c.payloads.length === 0) {
            this.w(indent + 1, c.name);
          } else {
            const ps = c.payloads.map((p) => typ(p));
            this.w(indent + 1, `${c.name}(${ps.join(", ")})`);
          }
        }
        this.w(indent, "}");
        return;
      }

      case "import":
        this.w(indent, `import ${goQuoteStr(s.path)}`);
        return;

      case "extern": {
        this.w(indent, `extern ${goQuoteStr(s.host)} {`);
        for (const fn of s.fns) {
          this.w(indent + 1, fnSig(fn));
        }
        this.w(indent, "}");
        return;
      }

      default: {
        const _never: never = s;
        return _never;
      }
    }
  }

  /** if / else if / else 扁平化输出 */
  private ifChain(st: IfStmt, indent: number): void {
    this.w(indent, `if ${this.expr(st.cond, 0, indent)} {`);
    this.stmts(st.then, indent + 1);
    this.elseTail(st, indent);
  }

  private elseTail(st: IfStmt, indent: number): void {
    if (st.els !== undefined && st.els.length === 1 && st.els[0]!.kind === "if") {
      const nested = st.els[0] as IfStmt;
      this.w(indent, `} else if ${this.expr(nested.cond, 0, indent)} {`);
      this.stmts(nested.then, indent + 1);
      this.elseTail(nested, indent);
      return;
    }
    if (st.els !== undefined && st.els.length > 0) {
      this.w(indent, "} else {");
      this.stmts(st.els, indent + 1);
    }
    this.w(indent, "}");
  }

  private fnDecl(st: Extract<Stmt, { kind: "fn_decl" }>, indent: number): void {
    if (st.recv !== undefined) {
      // 接收者无冒号（Go 风格）
      const recv = `(${st.recv.name} ${typ(st.recv.typ)})`;
      const ps = st.params.map((p) => (p.typ === undefined ? p.name : `${p.name}: ${typ(p.typ)}`));
      let sig = `fn ${recv}.${st.name}(${ps.join(", ")})`;
      if (st.ret !== undefined) sig += `: ${typ(st.ret)}`;
      this.w(indent, sig + " {");
    } else {
      this.w(indent, fnSig(st) + " {");
    }
    this.stmts(st.body, indent + 1);
    this.w(indent, "}");
  }

  // —— 表达式（优先级感知加括号） ——

  /** 以 minPrec 门槛输出表达式；低于门槛的子式加括号 */
  expr(e: Expr, minPrec: number, indent: number): string {
    const s = this.exprRaw(e, indent);
    if (exprPrec(e) < minPrec) return `(${s})`;
    return s;
  }

  private exprRaw(e: Expr, indent: number): string {
    switch (e.kind) {
      case "int_lit":
        return e.value.toString(10);
      case "float_lit": {
        let s = goFormatFloat(e.value);
        // 保住 float 身份：整数值的浮点字面量补 ".0"（否则重解析变 int）
        if (!/[.eE]/.test(s)) s += ".0";
        return s;
      }
      case "str_lit":
        return quoteStr(e.value);
      case "bool_lit":
        return e.value ? "true" : "false";
      case "ident":
        return e.name;
      case "unary": {
        const op = e.op;
        return op + this.expr(e.x, 8, indent);
      }
      case "binary": {
        const p = precOf(e.op);
        return `${this.expr(e.l, p, indent)} ${e.op} ${this.expr(e.r, p + 1, indent)}`;
      }
      case "range":
        return `${this.expr(e.lo, 5, indent)}..${this.expr(e.hi, 6, indent)}`;
      case "call": {
        const as = e.args.map((a) => this.expr(a, 0, indent));
        return `${this.expr(e.callee, 9, indent)}(${as.join(", ")})`;
      }
      case "index":
        return `${this.expr(e.obj, 9, indent)}[${this.expr(e.idx, 0, indent)}]`;
      case "slice":
        return `${this.expr(e.obj, 9, indent)}[${this.expr(e.lo, 0, indent)}:${this.expr(e.hi, 0, indent)}]`;
      case "field":
        return `${this.expr(e.obj, 9, indent)}.${e.name}`;
      case "try":
        return `${this.expr(e.x, 9, indent)}?`;
      case "struct_lit": {
        const fs = e.fields.map((fl) => `${fl.name}: ${this.expr(fl.val, 0, indent)}`);
        return `${e.typeName}{${fs.join(", ")}}`;
      }
      case "map_lit": {
        const kvs = e.items.map((it) => `${this.expr(it.key, 0, indent)}: ${this.expr(it.val, 0, indent)}`);
        return `{${kvs.join(", ")}}`;
      }
      case "arr_lit": {
        const is = e.items.map((el) => this.expr(el, 0, indent));
        return `[${is.join(", ")}]`;
      }
      case "match": {
        let b = "match " + this.expr(e.scrut, 0, indent) + " {\n";
        for (const arm of e.arms) {
          b += "    ".repeat(indent + 1) + this.pat(arm.pat) + " => " + this.expr(arm.body, 0, indent + 1) + "\n";
        }
        b += "    ".repeat(indent) + "}";
        return b;
      }
      case "fn_lit": {
        const ps = e.params.map((p) => (p.typ === undefined ? p.name : `${p.name}: ${typ(p.typ)}`));
        let sig = `fn(${ps.join(", ")})`;
        if (e.ret !== undefined) sig += `: ${typ(e.ret)}`;
        const f2 = new Fmtter(this.cm);
        f2.w(0, sig + " {");
        f2.stmts(e.body, indent + 1);
        f2.w(indent, "}");
        return f2.linesInternal().join("\n");
      }
      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  private linesInternal(): string[] {
    return this["lines" as never] as string[];
  }

  pat(p: Pat): string {
    switch (p.kind) {
      case "wild_pat":
        return "_";
      case "bind_pat":
        return p.name;
      case "lit_pat":
        return this.expr(p.val, 0, 0);
      case "case_pat": {
        if (p.subs.length === 0) return p.name;
        const ss = p.subs.map((s) => this.pat(s));
        return `${p.name}(${ss.join(", ")})`;
      }
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }
}

// —— 辅助 ——

function genericParams(tps: readonly string[]): string {
  return tps.length === 0 ? "" : `[${tps.join(", ")}]`;
}

function fnSig(d: Extract<Stmt, { kind: "fn_decl" }>): string {
  const ps = d.params.map((p) => (p.typ === undefined ? p.name : `${p.name}: ${typ(p.typ)}`));
  let sig = `fn ${d.name}${genericParams(d.typeParams)}(${ps.join(", ")})`;
  if (d.ret !== undefined) sig += `: ${typ(d.ret)}`;
  return sig;
}

function typ(t0: TypeEx | undefined): string {
  if (t0 === undefined) return "?";
  const t: TypeEx = t0;
  switch (t.kind) {
    case "named_type":
      if (t.args.length === 0) return t.name;
      return `${t.name}[${t.args.map(typ).join(", ")}]`;
    case "arr_type":
      return `[${typ(t.elem)}]`;
    case "map_type":
      return `map[${typ(t.key)}]${typ(t.val)}`;
    case "ptr_type":
      return `&${typ(t.elem)}`;
    case "fn_type": {
      const ps = t.params.map(typ);
      let sig = `fn(${ps.join(", ")})`;
      if (t.ret !== undefined) sig += `: ${typ(t.ret)}`;
      return sig;
    }
    default: {
      const _never: never = t;
      return _never;
    }
  }
}

function precOf(k: string): number {
  switch (k) {
    case "||":
      return 1;
    case "&&":
      return 2;
    case "==":
    case "!=":
      return 3;
    case "<":
    case "<=":
    case ">":
    case ">=":
      return 4;
    case "..":
      return 5;
    case "+":
    case "-":
    case "|":
    case "^":
      return 6;
    case "*":
    case "/":
    case "%":
    case "&":
    case "<<":
    case ">>":
      return 7;
    default:
      return 0;
  }
}

function exprPrec(e: Expr): number {
  if (e.kind === "binary") return precOf(e.op);
  if (e.kind === "unary") return 8;
  return 9;
}

/** 按 langGo 转义规则输出字符串字面量（仅 \n \t \r \" \\ 五种） */
function quoteStr(s: string): string {
  let b = '"';
  for (const r of s) {
    switch (r) {
      case "\n":
        b += "\\n";
        break;
      case "\t":
        b += "\\t";
        break;
      case "\r":
        b += "\\r";
        break;
      case '"':
        b += '\\"';
        break;
      case "\\":
        b += "\\\\";
        break;
      default:
        b += r;
    }
  }
  return b + '"';
}

/** Go strconv.Quote 兼容（import 路径输出用） */
function goQuoteStr(s: string): string {
  let b = '"';
  for (const r of s) {
    const cp = r.codePointAt(0)!;
    if (cp === 0x22) b += '\\"';
    else if (cp === 0x5c) b += "\\\\";
    else if (cp >= 0x20 && cp < 0x7f) b += r;
    else if (cp === 0x0a) b += "\\n";
    else if (cp === 0x09) b += "\\t";
    else if (cp === 0x0d) b += "\\r";
    else b += r;
  }
  return b + '"';
}

/** Go strconv.FormatFloat(f,'g',-1,64) 等价（shortest eprec=6） */
export function goFormatFloat(f: number): string {
  if (Number.isNaN(f)) return "NaN";
  if (f === Number.POSITIVE_INFINITY) return "+Inf";
  if (f === Number.NEGATIVE_INFINITY) return "-Inf";
  if (f === 0) return Object.is(f, -0) ? "-0" : "0";
  const neg = f < 0;
  const a = Math.abs(f);
  const m = /^(-?)(\d)(?:\.(\d+))?[eE]([+-]\d+)$/.exec(a.toExponential());
  const sign = m![1]!;
  let digits = m![2]! + (m![3] ?? "");
  digits = digits.replace(/0+$/, "") || "0";
  const exp = Number(m![4]);
  const dp = exp + 1;
  let body: string;
  if (exp < -4 || exp >= 6) {
    body = digits[0]! + (digits.length > 1 ? "." + digits.slice(1) : "") + "e" + (exp < 0 ? "-" : "+") + String(Math.abs(exp)).padStart(2, "0");
  } else if (dp <= 0) {
    body = "0." + "0".repeat(-dp) + digits;
  } else if (dp >= digits.length) {
    body = digits + "0".repeat(dp - digits.length);
  } else {
    body = digits.slice(0, dp) + "." + digits.slice(dp);
  }
  return neg ? "-" + body : body;
}

/** 把源码格式化为规范形态 */
export function format(src: string): string {
  let prog: Stmt[];
  try {
    prog = parse(src);
  } catch (e) {
    if (e instanceof ParseError) throw e;
    if (e instanceof LexError) throw e;
    throw e;
  }
  const cm = new CommentTracker(scanComments(toCodePoints(src)));
  const f = new Fmtter(cm);
  f.stmts(prog, 0);
  f.flushRemaining(0);
  return f.out();
}
