// 语法器（PARSE-001..006）：Pratt 表达式 + 语句/声明，错误消息与 Go 参考实现逐字一致。
// Incomplete 标记输入在语法中途截断（REPL 续行依据）。
// Span.File 由 file 参数在构造时盖章（与 Go 装载器解析后 stampFile 等价）。
// 注意：Go 参考实现中 Arm.Sp 与 EnumCase.Sp 构造时不赋值（零值 Span），此处如实复刻。

import type {
  Arm,
  AssignOp,
  BinaryOp,
  EnumCase,
  EnumDecl,
  Expr,
  ExternDecl,
  FieldLit,
  FnDecl,
  FnLit,
  FnType,
  IfStmt,
  MapItem,
  Param,
  Pat,
  Stmt,
  StructLit,
  TypeEx,
} from "./ast.js";
import { LexError, lex } from "./lexer.js";
import { lineStarts, posAt, toCodePoints, type Span } from "./span.js";
import { kindName, type Token, type TokenKind } from "./token.js";

export class ParseError extends Error {
  constructor(
    readonly msg: string,
    readonly line: number,
    readonly incomplete: boolean,
    readonly snippet: string,
  ) {
    super(snippet === "" ? `第${line}行: ${msg}` : `第${line}行: ${msg}\n  | ${snippet}`);
    this.name = "ParseError";
  }
}

/** Go 零值 Span（未赋值的构造位） */
export const zeroSpan: Span = {
  file: "",
  start: { offset: 0, line: 0, col: 0 },
  end: { offset: 0, line: 0, col: 0 },
};

/** 取 1-based 行号的内容（trim 后）；空行回退前一行 */
function lineSnippet(lines: readonly string[], line: number): string {
  let l = line;
  if (l < 1) l = 1;
  if (l > lines.length) l = lines.length;
  if (l < 1) return "";
  const t = (lines[l - 1] ?? "").trim();
  if (t !== "") return t;
  if (l > 1) return (lines[l - 2] ?? "").trim();
  return "";
}

const reserved: Partial<Record<TokenKind, string>> = {
  chan: "chan",
  interface: "interface",
};
function precOf(k: TokenKind): number {
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

/** 赋值目标只能是变量、字段链或索引 */
function checkTarget(e: Expr): void {
  switch (e.kind) {
    case "ident":
      return;
    case "field":
      checkTarget(e.obj);
      return;
    case "index":
      checkTarget(e.obj);
      return;
    default:
      throw new ParseError("赋值目标不合法", e.line, false, "");
  }
}

/** 复刻 Go `unicode.IsUpper(rune(lit[0]))`：取 UTF-8 首字节并按其码点判 Lu */
function firstByteIsUpper(lit: string): boolean {
  if (lit.length === 0) return false;
  const cp = lit.codePointAt(0)!;
  let firstByte: number;
  if (cp < 0x80) firstByte = cp;
  else if (cp < 0x800) firstByte = 0xc0 | (cp >> 6);
  else if (cp < 0x10000) firstByte = 0xe0 | (cp >> 12);
  else firstByte = 0xf0 | (cp >> 18);
  if (firstByte >= 0x41 && firstByte <= 0x5a) return true;
  if (firstByte >= 0x80) return /\p{Lu}/u.test(String.fromCodePoint(firstByte));
  return false;
}

class Parser {
  private i = 0;
  /** >0 时处于控制头位置，禁止裸 struct 字面量（PARSE-006） */
  private noStruct = 0;
  private readonly lines: string[];
  private readonly starts: number[];
  private prevEnd = 0;
  private prevLine = 1;

  constructor(
    private readonly toks: readonly Token[],
    src: string,
    private readonly file: string,
  ) {
    this.lines = src.split("\n");
    this.starts = lineStarts(toCodePoints(src));
  }

  private cur(): Token {
    return this.toks[this.i]!;
  }

  private next(): Token {
    const t = this.toks[this.i]!;
    this.prevEnd = t.end;
    this.prevLine = t.line;
    if (this.i < this.toks.length - 1) this.i++;
    return t;
  }

  private spanOf(startTok: Token): Span {
    return {
      file: this.file,
      start: posAt(this.starts, startTok.start, startTok.line),
      end: posAt(this.starts, this.prevEnd, this.prevLine),
    };
  }

  private at(k: TokenKind): boolean {
    return this.toks[this.i]!.kind === k;
  }

  private skipNl(): void {
    while (this.at("newline")) this.i++;
  }

  private accept(k: TokenKind): boolean {
    if (this.at(k)) {
      const t = this.toks[this.i]!;
      this.prevEnd = t.end;
      this.prevLine = t.line;
      this.i++;
      return true;
    }
    return false;
  }

  private errAt(t: Token, msg: string): ParseError {
    return new ParseError(msg, t.line, t.kind === "eof", lineSnippet(this.lines, t.line));
  }

  private expect(k: TokenKind): Token {
    if (this.at(k)) return this.next();
    const t = this.cur();
    throw this.errAt(t, `期望 ${kindName(k)}，得到 ${kindName(t.kind)}`);
  }

  private expectName(): string {
    if (this.at("ident")) return this.next().lit;
    if (this.at("underscore")) {
      this.next();
      return "_";
    }
    const t = this.cur();
    throw this.errAt(t, `期望标识符，得到 ${kindName(t.kind)}`);
  }

  // —— 顶层 ——

  private parseTop(): Stmt {
    const t = this.cur();
    const res = reserved[t.kind];
    if (res !== undefined) throw this.errAt(t, `保留字 ${res} 尚未启用（规划于 M3+）`);
    switch (t.kind) {
      case "import": {
        this.next();
        const path = this.expect("string");
        return { kind: "import", path: path.lit, line: t.line, span: this.spanOf(t) };
      }
      case "extern":
        return this.parseExtern();
      default:
        return this.parseStmt();
    }
  }

  /** extern "host" { fn 签名（无函数体）... }；Sp 在 `{` 消费后即定（与 Go 一致） */
  private parseExtern(): Stmt {
    const t = this.next(); // extern
    if (!this.at("string")) {
      throw this.errAt(t, `extern 后须是宿主名字符串，如 extern "go"`);
    }
    const host = this.next().lit;
    this.expect("{");
    const fns: FnDecl[] = [];
    const decl: ExternDecl = { kind: "extern", host, fns, line: t.line, span: this.spanOf(t) };
    for (;;) {
      this.skipNl();
      if (this.accept("}")) return decl;
      if (this.at("eof")) throw this.errAt(this.cur(), "extern 块未闭合");
      const fnTok = this.expect("fn");
      const name = this.expectName();
      const tps = this.parseTypeParams();
      const params = this.parseParams();
      let ret: TypeEx | undefined;
      if (this.accept(":")) ret = this.parseType();
      fns.push({
        kind: "fn_decl",
        name,
        recv: undefined,
        typeParams: tps,
        params,
        ret,
        body: [],
        line: fnTok.line,
        span: this.spanOf(fnTok),
      });
    }
  }

  // —— 语句 ——

  private parseStmt(): Stmt {
    const t = this.cur();
    switch (t.kind) {
      case "let":
      case "var": {
        this.next();
        const name = this.expectName();
        let typ: TypeEx | undefined;
        if (this.accept(":")) typ = this.parseType();
        this.expect("=");
        const init = this.parseExpr(0);
        return { kind: "let_stmt", name, mutable: t.kind === "var", typ, init, line: t.line, span: this.spanOf(t) };
      }

      case "return": {
        this.next();
        let val: Expr | undefined;
        if (!this.at("newline") && !this.at("}") && !this.at("eof")) {
          val = this.parseExpr(0);
        }
        return { kind: "return", val, line: t.line, span: this.spanOf(t) };
      }

      case "if":
        return this.parseIf();

      case "while": {
        this.next();
        this.noStruct++;
        let cond: Expr;
        try {
          cond = this.parseExpr(0);
        } finally {
          this.noStruct--;
        }
        const body = this.parseBlock();
        return { kind: "while", cond, body, line: t.line, span: this.spanOf(t) };
      }

      case "for": {
        this.next();
        const name = this.expectName();
        this.expect("in");
        this.noStruct++;
        let iter: Expr;
        try {
          iter = this.parseExpr(0);
        } finally {
          this.noStruct--;
        }
        const body = this.parseBlock();
        return { kind: "for_in", variable: name, iter, body, line: t.line, span: this.spanOf(t) };
      }

      case "go": {
        this.next();
        const callee = this.parseExpr(0);
        if (callee.kind !== "call") throw this.errAt(t, "go 的目标须是函数调用");
        return { kind: "go", call: callee, line: t.line, span: this.spanOf(t) };
      }

      case "break":
        this.next();
        return { kind: "break", line: t.line, span: this.spanOf(t) };

      case "continue":
        this.next();
        return { kind: "continue", line: t.line, span: this.spanOf(t) };

      case "fn":
        return this.parseFnDecl();

      case "struct":
        return this.parseStruct();

      case "enum":
        return this.parseEnum();

      default: {
        const e = this.parseExpr(0);
        if (this.accept("=")) {
          checkTarget(e);
          const v = this.parseExpr(0);
          return this.assignStmt(t, e, v, "=");
        }
        for (const op of ["+=", "-=", "*=", "/="] as const) {
          if (this.accept(op)) {
            checkTarget(e);
            const v = this.parseExpr(0);
            return this.assignStmt(t, e, v, op);
          }
        }
        return { kind: "expr_stmt", x: e, line: t.line, span: this.spanOf(t) };
      }
    }
  }

  private assignStmt(t: Token, target: Expr, val: Expr, op: AssignOp): Stmt {
    return { kind: "assign", target, val, op, line: t.line, span: this.spanOf(t) };
  }

  /** Sp 末次重算（覆盖 else 分支），与 Go 两次 spanOf 等价 */
  private parseIf(): Stmt {
    const t = this.next(); // if
    this.noStruct++;
    let cond: Expr;
    try {
      cond = this.parseExpr(0);
    } finally {
      this.noStruct--;
    }
    const then = this.parseBlock();
    let els: Stmt[] | undefined;
    this.skipNl();
    if (this.accept("else")) {
      if (this.at("if")) {
        els = [this.parseIf()];
      } else {
        els = this.parseBlock();
      }
    }
    const st: IfStmt = { kind: "if", cond, then, els, line: t.line, span: this.spanOf(t) };
    return st;
  }

  private parseBlock(): Stmt[] {
    this.expect("{");
    const stmts: Stmt[] = [];
    for (;;) {
      this.skipNl();
      if (this.accept("}")) return stmts;
      if (this.at("eof")) throw this.errAt(this.cur(), "块未闭合，缺少 }");
      stmts.push(this.parseStmt());
    }
  }

  // —— 声明 ——

  private parseFnDecl(): Stmt {
    const t = this.next(); // fn
    let recv: Param | undefined;
    if (this.accept("(")) {
      const rname = this.expectName();
      if (this.at("&")) throw this.errAt(this.cur(), "v0 暂不支持指针接收者");
      const rtyp = this.parseType();
      this.expect(")");
      this.expect(".");
      recv = { name: rname, typ: rtyp };
    }
    const name = this.expectName();
    return this.parseFuncRest(t, recv, name);
  }

  private parseFuncRest(startTok: Token, recv: Param | undefined, name: string): FnDecl {
    const tps = this.parseTypeParams();
    const params = this.parseParams();
    let ret: TypeEx | undefined;
    if (this.accept(":")) ret = this.parseType();
    const body = this.parseBlock();
    return {
      kind: "fn_decl",
      name,
      recv,
      typeParams: tps,
      params,
      ret,
      body,
      line: startTok.line,
      span: this.spanOf(startTok),
    };
  }

  private parseTypeParams(): string[] {
    if (!this.accept("[")) return [];
    const tps: string[] = [];
    for (;;) {
      tps.push(this.expectName());
      if (!this.accept(",")) break;
    }
    this.expect("]");
    return tps;
  }

  private parseParams(): Param[] {
    this.expect("(");
    const params: Param[] = [];
    while (!this.at(")")) {
      const name = this.expectName();
      let typ: TypeEx | undefined;
      if (this.accept(":")) typ = this.parseType();
      params.push({ name, typ });
      if (!this.accept(",")) break;
    }
    this.expect(")");
    return params;
  }

  private parseStruct(): Stmt {
    const t = this.next(); // struct
    const name = this.expectName();
    const tps = this.parseTypeParams();
    this.expect("{");
    const fields: Param[] = [];
    for (;;) {
      this.skipNl();
      if (this.accept("}")) break;
      if (this.at("eof")) throw this.errAt(this.cur(), "struct 未闭合");
      const fname = this.expectName();
      this.expect(":");
      const typ = this.parseType();
      fields.push({ name: fname, typ });
      this.skipNl();
      this.accept(",");
    }
    return { kind: "struct_decl", name, typeParams: tps, fields, line: t.line, span: this.spanOf(t) };
  }

  private parseEnum(): Stmt {
    const t = this.next(); // enum
    const name = this.expectName();
    const tps = this.parseTypeParams();
    this.expect("{");
    const cases: EnumCase[] = [];
    for (;;) {
      this.skipNl();
      if (this.accept("}")) break;
      if (this.at("eof")) throw this.errAt(this.cur(), "enum 未闭合");
      const cn = this.expectName();
      const case_: { name: string; payloads: TypeEx[]; line: number; span: Span } = {
        name: cn,
        payloads: [],
        line: this.cur().line,
        span: zeroSpan, // Go 构造时不赋 Sp（零值）
      };
      if (this.accept("(")) {
        const payloads: TypeEx[] = [];
        while (!this.at(")")) {
          payloads.push(this.parseType());
          if (!this.accept(",")) break;
        }
        this.expect(")");
        case_.payloads = payloads;
      }
      cases.push(case_);
      this.skipNl();
      this.accept(",");
    }
    const decl: EnumDecl = { kind: "enum_decl", name, typeParams: tps, cases, line: t.line, span: this.spanOf(t) };
    return decl;
  }

  // —— 类型注解 ——

  private parseType(): TypeEx {
    const t = this.cur();
    switch (t.kind) {
      case "chan": {
        this.next();
        if (!this.accept("[")) throw this.errAt(t, "chan 需要 elem 类型参数，如 chan[int]");
        const elem = this.parseType();
        this.expect("]");
        return { kind: "named_type", name: "chan", args: [elem] };
      }
      case "&": {
        this.next();
        const elem = this.parseType();
        return { kind: "ptr_type", elem };
      }
      case "fn": {
        this.next();
        this.expect("(");
        const params: TypeEx[] = [];
        while (!this.at(")")) {
          params.push(this.parseType());
          if (!this.accept(",")) break;
        }
        this.expect(")");
        let ret: TypeEx | undefined;
        if (this.accept(":")) ret = this.parseType();
        const ft: FnType = { kind: "fn_type", params, ret };
        return ft;
      }
      case "[": {
        this.next();
        const elem = this.parseType();
        this.expect("]");
        return { kind: "arr_type", elem };
      }
      case "ident": {
        this.next();
        if (t.lit === "map" && this.at("[")) {
          this.next();
          const k = this.parseType();
          this.expect("]");
          const v = this.parseType();
          return { kind: "map_type", key: k, val: v };
        }
        const args: TypeEx[] = [];
        if (this.accept("[")) {
          for (;;) {
            args.push(this.parseType());
            if (!this.accept(",")) break;
          }
          this.expect("]");
        }
        return { kind: "named_type", name: t.lit, args };
      }
      default:
        throw this.errAt(t, `期望类型，得到 ${kindName(t.kind)}`);
    }
  }

  // —— 表达式（Pratt） ——

  private parseExpr(minPrec: number): Expr {
    let left = this.parseUnary();
    for (;;) {
      const t = this.cur();
      const prec = precOf(t.kind);
      if (prec === 0 || prec < minPrec) return left;
      this.next();
      const right = this.parseExpr(prec + 1);
      if (t.kind === "..") {
        left = { kind: "range", lo: left, hi: right, line: t.line, span: this.spanOf(t) };
      } else {
        const op = t.kind as BinaryOp; // precOf 已保证是二元运算符
        left = { kind: "binary", op, l: left, r: right, line: t.line, span: this.spanOf(t) };
      }
    }
  }

  private parseUnary(): Expr {
    const t = this.cur();
    switch (t.kind) {
      case "-":
      case "!":
      case "&": {
        this.next();
        const x = this.parseUnary();
        return { kind: "unary", op: t.kind, x, line: t.line, span: this.spanOf(t) };
      }
      default:
        return this.parsePostfix();
    }
  }

  private parsePostfix(): Expr {
    let x = this.parsePrimary();
    for (;;) {
      const t = this.cur();
      switch (t.kind) {
        case "(": {
          this.next();
          const args: Expr[] = [];
          for (;;) {
            this.skipNl();
            if (this.at(")")) break;
            args.push(this.parseExpr(0));
            this.skipNl();
            if (!this.accept(",")) break;
          }
          this.skipNl();
          this.expect(")");
          x = { kind: "call", callee: x, args, line: t.line, span: this.spanOf(t) };
          break;
        }
        case "[": {
          this.next();
          this.skipNl();
          const lo = this.parseExpr(0);
          if (this.accept(":")) {
            if (this.at("]")) throw this.errAt(this.cur(), "切片需要上界（v0 不支持开区间切片）");
            const hi = this.parseExpr(0);
            this.skipNl();
            this.expect("]");
            x = { kind: "slice", obj: x, lo, hi, line: t.line, span: this.spanOf(t) };
          } else {
            this.skipNl();
            this.expect("]");
            x = { kind: "index", obj: x, idx: lo, line: t.line, span: this.spanOf(t) };
          }
          break;
        }
        case ".": {
          this.next();
          const name = this.expect("ident");
          x = { kind: "field", obj: x, name: name.lit, line: t.line, span: this.spanOf(t) };
          break;
        }
        case "?": {
          this.next();
          x = { kind: "try", x, line: t.line, span: this.spanOf(t) };
          break;
        }
        default:
          return x;
      }
    }
  }

  private parsePrimary(): Expr {
    const t = this.cur();
    switch (t.kind) {
      case "int": {
        this.next();
        // Go ParseInt(s, 10, 64)：语法非法与溢出统一报「整数溢出」
        if (!/^[0-9]+$/.test(t.lit) || BigInt(t.lit) > 0x7fffffffffffffffn) {
          throw this.errAt(t, `整数溢出: ${t.lit}`);
        }
        return { kind: "int_lit", value: BigInt(t.lit), line: t.line, span: this.spanOf(t) };
      }
      case "float": {
        this.next();
        if (!/^[0-9]+\.[0-9]+$/.test(t.lit) || !Number.isFinite(Number(t.lit))) {
          throw this.errAt(t, `非法浮点数: ${t.lit}`);
        }
        return { kind: "float_lit", value: Number(t.lit), line: t.line, span: this.spanOf(t) };
      }
      case "string":
        this.next();
        return { kind: "str_lit", value: t.lit, line: t.line, span: this.spanOf(t) };
      case "true":
      case "false":
        this.next();
        return { kind: "bool_lit", value: t.kind === "true", line: t.line, span: this.spanOf(t) };
      case "ident": {
        this.next();
        if (this.at("{") && this.noStruct === 0) return this.parseStructLit(t);
        return { kind: "ident", name: t.lit, line: t.line, span: this.spanOf(t) };
      }
      case "(": {
        this.next();
        const saved = this.noStruct; // 括号内恢复 struct 字面量
        this.noStruct = 0;
        let x: Expr;
        try {
          x = this.parseExpr(0);
        } finally {
          this.noStruct = saved;
        }
        this.skipNl();
        this.expect(")");
        return x;
      }
      case "[": {
        this.next();
        const items: Expr[] = [];
        for (;;) {
          this.skipNl();
          if (this.at("]")) break;
          items.push(this.parseExpr(0));
          this.skipNl();
          if (!this.accept(",")) break;
        }
        this.skipNl();
        this.expect("]");
        return { kind: "arr_lit", items, line: t.line, span: this.spanOf(t) };
      }
      case "{": {
        this.next();
        const items: MapItem[] = [];
        for (;;) {
          this.skipNl();
          if (this.accept("}")) break;
          if (this.at("eof")) throw this.errAt(this.cur(), "映射字面量未闭合");
          const key = this.parseExpr(0);
          this.expect(":");
          const val = this.parseExpr(0);
          items.push({ key, val });
          this.skipNl();
          this.accept(",");
        }
        return { kind: "map_lit", items, line: t.line, span: this.spanOf(t) };
      }
      case "fn": {
        this.next();
        const fn = this.parseFuncRest(t, undefined, "");
        const fl: FnLit = {
          kind: "fn_lit",
          typeParams: fn.typeParams,
          params: fn.params,
          ret: fn.ret,
          body: fn.body,
          line: t.line,
          span: this.spanOf(t),
        };
        return fl;
      }
      case "match":
        return this.parseMatch();
      case "eof":
        throw this.errAt(t, "表达式意外结束");
      default:
        throw this.errAt(t, `意外的记号 ${kindName(t.kind)}`);
    }
  }

  private parseStructLit(startTok: Token): Expr {
    const name = startTok.lit;
    const line = startTok.line;
    this.next(); // {
    const fields: FieldLit[] = [];
    for (;;) {
      this.skipNl();
      if (this.accept("}")) break;
      if (this.at("eof")) throw this.errAt(this.cur(), "struct 字面量未闭合");
      const fname = this.expect("ident");
      this.expect(":");
      const val = this.parseExpr(0);
      fields.push({ name: fname.lit, val });
      this.skipNl();
      this.accept(",");
    }
    const sl: StructLit = { kind: "struct_lit", typeName: name, fields, line, span: this.spanOf(startTok) };
    return sl;
  }

  private parseMatch(): Expr {
    const t = this.next(); // match
    this.noStruct++;
    let scrut: Expr;
    try {
      scrut = this.parseExpr(0);
    } finally {
      this.noStruct--;
    }
    this.expect("{");
    const arms: Arm[] = [];
    for (;;) {
      this.skipNl();
      if (this.accept("}")) break;
      if (this.at("eof")) throw this.errAt(this.cur(), "match 未闭合");
      const armLine = this.cur().line;
      const pat = this.parsePattern();
      this.expect("=>");
      const body = this.parseExpr(0);
      arms.push({ pat, body, line: armLine, span: zeroSpan }); // Go 构造时不赋 Sp（零值）
      if (this.at("newline") || this.at("}")) continue;
      throw this.errAt(this.cur(), "match 分支之间需要换行");
    }
    return { kind: "match", scrut, arms, line: t.line, span: this.spanOf(t) };
  }

  private parsePattern(): Pat {
    const t = this.cur();
    switch (t.kind) {
      case "underscore":
        this.next();
        return { kind: "wild_pat", line: t.line, span: this.spanOf(t) };
      case "int":
      case "float":
      case "string":
      case "true":
      case "false": {
        const e = this.parsePrimary();
        return { kind: "lit_pat", val: e, line: t.line, span: this.spanOf(t) };
      }
      case "ident": {
        this.next();
        if (this.accept("(")) {
          const subs: Pat[] = [];
          while (!this.at(")")) {
            subs.push(this.parsePattern());
            if (!this.accept(",")) break;
          }
          this.expect(")");
          return { kind: "case_pat", name: t.lit, subs, line: t.line, span: this.spanOf(t) };
        }
        // 大写开头按 case 引用，小写为绑定（PARSE-005）
        if (firstByteIsUpper(t.lit)) {
          return { kind: "case_pat", name: t.lit, subs: [], line: t.line, span: this.spanOf(t) };
        }
        return { kind: "bind_pat", name: t.lit, line: t.line, span: this.spanOf(t) };
      }
      default:
        throw this.errAt(t, `非法模式，得到 ${kindName(t.kind)}`);
    }
  }

  /** 整个源文件/REPL 片段 → 语句序列 */
  parseProgram(): Stmt[] {
    const prog: Stmt[] = [];
    for (;;) {
      this.skipNl();
      if (this.at("eof")) return prog;
      prog.push(this.parseTop());
    }
  }
}

/** 解析整个源文件/REPL 片段，返回语句序列（解析/词法错误以 ParseError/LexError 抛出） */
export function parse(src: string, file = ""): Stmt[] {
  let toks: Token[];
  try {
    toks = lex(src);
  } catch (e) {
    if (e instanceof LexError) throw e;
    throw e;
  }
  return new Parser(toks, src, file).parseProgram();
}
