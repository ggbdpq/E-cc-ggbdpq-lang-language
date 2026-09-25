// 语义级重构（v0.7 P0）：rename / safe-remove / organize-imports。
// 基于 SymbolId + 语义引用（禁止 Regex Rename）；定位全部基于 token 扫描。

import type { FnDecl, Stmt, LetStmt } from "../syntax/ast.js";
import type { Span } from "../syntax/span.js";
import { LexError, lex } from "../syntax/lexer.js";
import { toCodePoints } from "../syntax/span.js";
import type { Token, TokenKind } from "../syntax/token.js";
import * as walkPkg from "../syntax/walk.js";

const walkMod = (): typeof walkPkg => walkPkg;
import type { ResolveResult } from "../semantic/resolver.js";
import type { Symbol, SymbolId } from "../semantic/symbols.js";
import type { HirProg, HirStmt, HirExpr } from "../hir/model.js";
import type { TextEdit } from "../edit/edit.js";

// —— Sources ——

export interface Sources {
  /** 单文件场景源码（Sp.File 为空时使用） */
  readonly default: string;
  /** loader 相对路径 → 源码 */
  readonly byFile: ReadonlyMap<string, string>;
}

function sourceOf(s: Sources, file: string): string {
  if (file === "") return s.default;
  return s.byFile.get(file) ?? s.default;
}

// —— ResolveTarget ——

export function resolveTarget(res: ResolveResult, arg: string): Symbol {
  if (arg === "") throw new Error("缺少 --symbol 参数");
  if (/^[0-9]+$/.test(arg)) {
    const n = Number.parseInt(arg, 10);
    for (const s of res.symbols) {
      if (!s.builtinMark && s.id === n) return s;
    }
    throw new Error(`未知符号 ID ${arg}`);
  }
  const hits = res.symbols.filter((s) => !s.builtinMark && s.name === arg);
  if (hits.length === 1) return hits[0]!;
  if (hits.length === 0) throw new Error(`未知符号 ${arg}`);
  throw new Error(`符号名 ${arg} 对应多个符号，请用 --symbol <id>`);
}

// —— ValidateNewName ——

const reservedNames: readonly string[] = [
  "let", "var", "fn", "return", "if", "else", "while", "for", "in",
  "match", "struct", "enum", "true", "false", "break", "continue",
  "go", "chan", "import", "extern", "interface",
  "println", "print", "len", "str", "int", "float", "keys", "values",
  "has", "push", "assert", "err", "bytes", "char", "jsonParse",
  "jsonStringify", "isNone", "makeChan", "send", "recv",
  "Some", "None", "Ok", "Err", "http",
];

export function validateNewName(name: string): void {
  if (name === "") throw new Error("新名字为空");
  const cps = Array.from(name);
  for (let i = 0; i < cps.length; i++) {
    const r = cps[i]!;
    const first = i === 0;
    const ok = r === "_" || (r >= "a" && r <= "z") || (r >= "A" && r <= "Z") || (!first && r >= "0" && r <= "9");
    if (!ok) throw new Error(`新名字含非法字符 ${JSON.stringify(r)}`);
  }
  for (const kw of reservedNames) {
    if (name === kw) throw new Error(`新名字 ${name} 与关键字/保留字/内建名冲突`);
  }
}

// —— Rename ——

export function rename(
  prog: readonly Stmt[],
  srcs: Sources,
  res: ResolveResult,
  h: HirProg,
  target: Symbol,
  newName: string,
): TextEdit[] {
  validateNewName(newName);
  if (target.kind === "struct" || target.kind === "enum" || target.kind === "ctor") {
    throw new Error(`暂不支持对 ${symKindName(target.kind)} 的 rename（列 P1）`);
  }
  if (target.kind !== "fn" && target.kind !== "method" && target.kind !== "var" && target.kind !== "param") {
    throw new Error("该符号类型不支持 rename");
  }
  if (target.name === "_") throw new Error("占位符 _ 不可 rename");
  // 冲突检测（保守）：其他同名用户符号存在即拒绝
  for (const s of res.symbols) {
    if (s.builtinMark || s.id === target.id) continue;
    if (s.name === newName) {
      throw new Error(`新名字 ${newName} 与已有符号冲突（${s.name} #${s.id}）`);
    }
  }

  // 定义位：名字 token 精确区间
  const defSp = declNameSpan(prog, srcs, res, target);
  const edits: TextEdit[] = [
    {
      file: defSp.file,
      startOffset: defSp.start.offset,
      endOffset: defSp.end.offset,
      replacement: newName,
    },
  ];

  // 引用位：Ident（read/write/call 位置的直接标识符引用）
  const { walkStmts } = walkMod();
  walkStmts(prog, {
    stmt: (s) => {
      if (s.kind === "assign" && s.target.kind === "ident") {
        const id = s.target;
        const sym = res.refs.get(id);
        if (sym !== undefined && sym.id === target.id) {
          edits.push({ file: id.span.file, startOffset: id.span.start.offset, endOffset: id.span.end.offset, replacement: newName });
        }
      }
    },
    expr: (e) => {
      if (e.kind === "ident") {
        const sym = res.refs.get(e);
        if (sym !== undefined && sym.id === target.id) {
          edits.push({ file: e.span.file, startOffset: e.span.start.offset, endOffset: e.span.end.offset, replacement: newName });
        }
      }
    },
  });

  // 方法调用点：HIR MethodCall（method == target）
  if (target.kind === "method") {
    for (const st of h.globals) collectHIRMethodRefs(st, target, newName, edits);
    for (const f of h.fns) {
      for (const st of f.body) collectHIRMethodRefs(st, target, newName, edits);
    }
  }

  return dedupeEdits(edits);
}

function dedupeEdits(edits: TextEdit[]): TextEdit[] {
  const out: TextEdit[] = [];
  for (const e of edits) {
    const dup = out.some((x) => x.startOffset === e.startOffset && x.endOffset === e.endOffset && x.file === e.file);
    if (!dup) out.push(e);
  }
  return out.sort((a, b) => b.startOffset - a.startOffset);
}

export const symKindName = (k: string): string =>
  k === "fn" ? "function" : k === "method" ? "method" : k === "var" ? "variable" : k === "param" ? "parameter" : k === "struct" ? "struct" : k === "enum" ? "enum" : k === "ctor" ? "constructor" : "unknown";

// —— safe remove ——

export interface RemoveCheck {
  readonly removable: boolean;
  readonly reason: string;
  readonly refCount: number;
  readonly refSites: readonly string[];
}

export function safeRemoveCheck(prog: readonly Stmt[], res: ResolveResult, target: Symbol): RemoveCheck {
  if (target.kind !== "fn" && target.kind !== "var") {
    throw new Error(`safe remove 仅支持顶层 fn 与全局变量（目标 kind=${symKindName(target.kind)}）`);
  }
  if (target.extern) throw new Error("extern 声明不支持 safe remove");
  if (target.decl === undefined) {
    throw new Error("未找到声明节点");
  }
  void 0;

  // 引用计数：除定义外的全部 Ident 引用
  let refCount = 0;
  const sites: string[] = [];
  const { walkStmts, walkExpr } = walkMod();
  const onExpr = (x: unknown): void => {
    void x;
  };
  void onExpr;
  const visitExpr = (e: import("../syntax/ast.js").Expr): void => {
    walkExpr(e, {
      expr: (x) => {
        if (x.kind === "ident") {
          const sym = res.refs.get(x);
          if (sym !== undefined && sym.id === target.id) {
            refCount++;
            sites.push(formatRefSite(x.span));
          }
        }
      },
    });
  };
  const visitStmt = (st: Stmt): void => {
    switch (st.kind) {
      case "let_stmt":
        visitExpr(st.init);
        return;
      case "assign":
        visitExpr(st.target);
        visitExpr(st.val);
        return;
      case "expr_stmt":
        visitExpr(st.x);
        return;
      case "return":
        if (st.val !== undefined) visitExpr(st.val);
        return;
      case "if":
        visitExpr(st.cond);
        for (const s of st.then) visitStmt(s);
        if (st.els !== undefined) for (const s of st.els) visitStmt(s);
        return;
      case "while":
        visitExpr(st.cond);
        for (const s of st.body) visitStmt(s);
        return;
      case "for_in":
        visitExpr(st.iter);
        for (const s of st.body) visitStmt(s);
        return;
      case "fn_decl":
        for (const s of st.body) visitStmt(s);
        return;
      case "go":
        for (const a of st.call.args) visitExpr(a);
        return;
      default:
        return;
    }
  };
  for (const st of prog) visitStmt(st);
  void walkStmts;
  return { removable: refCount === 0, reason: `${refCount} 处引用`, refCount, refSites: sites };
}

function formatRefSite(sp: Span): string {
  if (sp.file === "") return `${sp.start.line}:${sp.start.col}`;
  return `${sp.file}:${sp.start.line}:${sp.start.col}`;
}

/** 可删除时给出删除编辑：区间吞掉声明后的单个换行，避免残留空行 */
export function safeRemoveEdits(prog: readonly Stmt[], srcs: Sources, res: ResolveResult, target: Symbol): TextEdit[] {
  let span: Span;
  if (target.kind === "fn") {
    if (target.decl === undefined || target.decl.kind !== "fn_decl") throw new Error("未找到函数声明节点");
    span = target.decl.span;
  } else if (target.kind === "var") {
    const st = findLetStmt(prog, res, target);
    if (st === undefined) throw new Error("未找到绑定声明");
    span = st.span;
  } else {
    throw new Error("不支持的定义定位");
  }
  let end = span.end.offset;
  const runes = toCodePoints(sourceOf(srcs, span.file));
  if (end < runes.length && runes[end] === "\n") end++;
  return [
    {
      file: span.file,
      startOffset: span.start.offset,
      endOffset: end,
      replacement: "",
    },
  ];
}

// —— organize imports ——

interface ImportLineInfo {
  path: string;
  comment: string;
  lineIdx: number;
  used: boolean;
}

/** 对单个文件源码做 organize imports，返回 [新源码, 是否变化] */
export function organizeImports(src: string): [string, boolean] {
  let changed = false;
  const lines = src.split("\n");

  const importIdx: number[] = [];
  for (let i = 0; i < lines.length; i++) {
    if ((lines[i] ?? "").trim().startsWith("import ")) importIdx.push(i);
  }
  if (importIdx.length === 0) return [src, false];

  const imps: ImportLineInfo[] = [];
  for (const i of importIdx) {
    const t = (lines[i] ?? "").trim();
    if (!t.startsWith("import ")) continue;
    let rest = t.slice("import ".length).trim();
    let path = "";
    let comment = "";
    const q = rest.indexOf('"');
    if (q >= 0) {
      rest = rest.slice(q + 1);
      const e = rest.indexOf('"');
      if (e < 0) continue;
      path = rest.slice(0, e);
      const tail = rest.slice(e + 1).trim();
      if (tail.startsWith("//")) comment = tail;
    }
    if (path === "") continue;
    const imp: ImportLineInfo = { path, comment, lineIdx: i, used: false };
    // 使用判定：文件内非 import 行出现路径的最后一段名（保守）
    let base = path;
    const k = base.lastIndexOf("/");
    if (k >= 0) base = base.slice(k + 1);
    for (let j = 0; j < lines.length; j++) {
      if (j === i || (lines[j] ?? "").trim().startsWith("import ")) continue;
      if ((lines[j] ?? "").includes(base)) {
        imp.used = true;
        break;
      }
    }
    imps.push(imp);
  }

  // 未使用/重复的 import 行：删除
  const kept: ImportLineInfo[] = [];
  const removedLines: number[] = [];
  const seen = new Set<string>();
  for (const im of imps) {
    if (!im.used) {
      removedLines.push(im.lineIdx);
      changed = true;
      continue;
    }
    if (seen.has(im.path)) {
      removedLines.push(im.lineIdx);
      changed = true;
      continue;
    }
    seen.add(im.path);
    kept.push(im);
  }
  if (!changed && kept.length === imps.length) {
    // 无未使用/无重复——检查是否需要排序
    const paths = kept.map((k) => k.path);
    const sorted = [...paths].sort();
    for (let i = 0; i < paths.length; i++) {
      if (paths[i] !== sorted[i]) {
        changed = true;
        break;
      }
    }
    if (!changed) return [src, false];
  }

  // 输出：非 import 行保持原位；import 行由排序块统一输出
  const out: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    const isImport = importIdx.includes(i);
    const removed = removedLines.includes(i);
    if (isImport && !removed) continue;
    if (removed) continue;
    out.push(lines[i]!);
  }

  // 找到第一个 fn/struct 声明位（跳过注释行），在其前插入排序后的 import 行
  let insertAt = -1;
  for (let i = 0; i < out.length; i++) {
    const t = (out[i] ?? "").trim();
    if (t.startsWith("//")) continue;
    if (t.startsWith("fn ") || t.startsWith("struct ")) {
      insertAt = i;
      break;
    }
  }
  if (insertAt < 0) insertAt = out.length;
  const sorted = [...kept].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  const importLines: string[] = [];
  for (const im of sorted) {
    let l = `import "${im.path}"`;
    if (im.comment !== "") l += " // " + im.comment.replace(/^\/\/ /, "");
    importLines.push(l);
  }
  const res = [...out.slice(0, insertAt), ...importLines, ...out.slice(insertAt)];
  return [res.join("\n"), changed];
}

// —— 定位（token 扫描） ——

interface DeclInfo {
  readonly decl: FnDecl;
  readonly sym: Symbol;
}

function collectFnDecls(prog: readonly Stmt[], res: ResolveResult): DeclInfo[] {
  const out: DeclInfo[] = [];
  const seen = new Set<Stmt>();
  const { walkStmt, walkStmts } = walkMod();
  const walkS = (s: Stmt): void => {
    if (seen.has(s)) return;
    seen.add(s);
    if (s.kind === "fn_decl") {
      const sym = res.declSyms.get(s);
      if (sym !== undefined) out.push({ decl: s, sym });
    }
    walkStmt(s, { stmt: walkS });
  };
  walkStmts(prog, { stmt: walkS });
  return out;
}

function declNameSpan(prog: readonly Stmt[], srcs: Sources, res: ResolveResult, target: Symbol): Span {
  switch (target.kind) {
    case "fn":
    case "method": {
      if (target.decl === undefined || target.decl.kind !== "fn_decl") throw new Error("未找到函数声明节点");
      return scanNameAfterFn(target.decl.span, srcs, target.kind === "method");
    }
    case "var": {
      const st = findLetStmt(prog, res, target);
      if (st === undefined) throw new Error("未找到绑定声明");
      return firstIdentSpan(st.span, srcs);
    }
    case "param": {
      const owner = paramOwner(res, target);
      if (owner === undefined) throw new Error("未找到参数所属函数");
      const d = findFnDeclBySym(prog, res, owner);
      if (d === undefined) throw new Error("未找到函数声明节点");
      const idx = paramIndex(res, owner, target);
      return paramIdentSpan(d.span, srcs, d.recv !== undefined ? 1 : 0, idx);
    }
    default:
      throw new Error("不支持的定义定位");
  }
}

function findLetStmt(prog: readonly Stmt[], res: ResolveResult, target: Symbol): LetStmt | undefined {
  let found: LetStmt | undefined;
  const { walkStmts } = walkMod();
  walkStmts(prog, {
    stmt: (s) => {
      if (s.kind === "let_stmt" && found === undefined) {
        const sym = res.declSyms.get(s);
        if (sym === target) found = s;
      }
    },
  });
  return found;
}

function paramOwner(res: ResolveResult, target: Symbol): Symbol | undefined {
  for (const [fnId, params] of res.fnParams) {
    for (const p of params) {
      if (p.id === target.id) {
        return res.symbols.find((s) => s.id === fnId);
      }
    }
  }
  return undefined;
}

function paramIndex(res: ResolveResult, owner: Symbol, target: Symbol): number {
  const params = res.fnParams.get(owner.id) ?? [];
  for (let i = 0; i < params.length; i++) {
    if (params[i]!.id === target.id) return i;
  }
  return 0;
}

function findFnDeclBySym(prog: readonly Stmt[], res: ResolveResult, owner: Symbol): FnDecl | undefined {
  for (const d of collectFnDecls(prog, res)) {
    if (d.sym === owner) return d.decl;
  }
  if (owner.decl !== undefined && owner.decl.kind === "fn_decl") return owner.decl;
  return undefined;
}

// —— token 扫描 ——

interface SpanTok {
  readonly kind: TokenKind;
  readonly absStart: number;
  readonly absEnd: number;
  readonly absLine: number;
  readonly absCol: number;
}

function spanTokens(sp: Span, src: string): SpanTok[] {
  const runes = toCodePoints(src);
  const lo = sp.start.offset;
  const hi = sp.end.offset;
  if (lo < 0 || hi > runes.length || lo >= hi) return [];
  const lineStarts: number[] = [lo];
  for (let i = lo; i < hi; i++) {
    if (runes[i] === "\n") lineStarts.push(i + 1);
  }
  let toks: Token[];
  try {
    toks = lex(runes.slice(lo, hi).join(""));
  } catch {
    return [];
  }
  const out: SpanTok[] = [];
  for (const t of toks) {
    if (t.kind === "eof" || t.kind === "newline") continue;
    const lineIdx = t.line - 1;
    if (lineIdx < 0 || lineIdx >= lineStarts.length) continue;
    const off = lo + t.start;
    out.push({
      kind: t.kind,
      absStart: lo + t.start,
      absEnd: lo + t.end,
      absLine: sp.start.line + (t.line - 1),
      absCol: off - lineStarts[lineIdx]! + 1,
    });
  }
  return out;
}

function scanNameAfterFn(sp: Span, srcs: Sources, hasRecv: boolean): Span {
  const toks = spanTokens(sp, sourceOf(srcs, sp.file));
  let i = 0;
  while (i < toks.length && toks[i]!.kind !== "fn") i++;
  if (i >= toks.length) throw new Error("未找到 fn");
  i++; // 越过 fn
  if (hasRecv) {
    let depth = 1;
    i++;
    while (i < toks.length && depth > 0) {
      const k = toks[i]!.kind;
      if (k === "(") depth++;
      else if (k === ")") depth--;
      if (depth > 0) i++;
    }
    i++;
    while (i < toks.length && toks[i]!.kind === ".") i++;
  }
  if (i >= toks.length || toks[i]!.kind !== "ident") throw new Error("未找到名字 token");
  return spanTokAbs(sp, toks[i]!);
}

function firstIdentSpan(sp: Span, srcs: Sources): Span {
  const toks = spanTokens(sp, sourceOf(srcs, sp.file));
  for (const t of toks) {
    if (t.kind === "ident") return spanTokAbs(sp, t);
  }
  throw new Error("未找到标识符");
}

function paramIdentSpan(sp: Span, srcs: Sources, group: number, seg: number): Span {
  const toks = spanTokens(sp, sourceOf(srcs, sp.file));
  let gi = 0;
  let i = 0;
  while (i < toks.length) {
    if (toks[i]!.kind !== "(") {
      i++;
      continue;
    }
    if (gi === group) return segIdent(sp, toks, i, seg);
    let depth = 1;
    i++;
    while (i < toks.length && depth > 0) {
      const k = toks[i]!.kind;
      if (k === "(" || k === "[") depth++;
      else if (k === ")" || k === "]") depth--;
      if (depth > 0) i++;
    }
    gi++;
  }
  throw new Error("未找到参数组");
}

function segIdent(sp: Span, toks: readonly SpanTok[], lp: number, seg: number): Span {
  let depth = 1;
  let i = lp + 1;
  let segIdx = 0;
  let start = i;
  while (i < toks.length && depth > 0) {
    const k = toks[i]!.kind;
    if (k === "(" || k === "[") depth++;
    else if (k === ")" || k === "]") depth--;
    if (depth === 0) break;
    if (k === "," && depth === 1) {
      if (segIdx === seg) return firstIdentInSeg(sp, toks.slice(start, i));
      segIdx++;
      start = i + 1;
    }
    i++;
  }
  if (segIdx === seg) return firstIdentInSeg(sp, toks.slice(start, i));
  throw new Error(`参数段 ${seg} 不存在`);
}

function firstIdentInSeg(sp: Span, toks: readonly SpanTok[]): Span {
  for (const t of toks) {
    if (t.kind === "ident") return spanTokAbs(sp, t);
  }
  throw new Error("参数段无标识符");
}

function spanTokAbs(sp: Span, t: SpanTok): Span {
  return {
    file: sp.file,
    start: { offset: t.absStart, line: t.absLine, col: t.absCol },
    end: { offset: t.absEnd, line: t.absLine, col: t.absCol + (t.absEnd - t.absStart) },
  };
}

// —— HIR 方法调用点收集 ——

function collectHIRMethodRefs(st: HirStmt, target: Symbol, newName: string, edits: TextEdit[]): void {
  switch (st.kind) {
    case "let":
      collectHIREdit(st.init, target, newName, edits);
      return;
    case "assign":
      collectHIREdit(st.target, target, newName, edits);
      collectHIREdit(st.rhs, target, newName, edits);
      return;
    case "eval":
      collectHIREdit(st.x, target, newName, edits);
      return;
    case "return":
      if (st.val !== undefined) collectHIREdit(st.val, target, newName, edits);
      return;
    case "if":
      collectHIREdit(st.cond, target, newName, edits);
      for (const s2 of st.then) collectHIRMethodRefs(s2, target, newName, edits);
      for (const s2 of st.els) collectHIRMethodRefs(s2, target, newName, edits);
      return;
    case "while":
      collectHIREdit(st.cond, target, newName, edits);
      for (const s2 of st.body) collectHIRMethodRefs(s2, target, newName, edits);
      return;
    case "for_in":
      collectHIREdit(st.iter, target, newName, edits);
      for (const s2 of st.body) collectHIRMethodRefs(s2, target, newName, edits);
      return;
    case "spawn":
      for (const a of st.args) collectHIREdit(a, target, newName, edits);
      return;
    case "fn_stmt":
      for (const s2 of st.fn.body) collectHIRMethodRefs(s2, target, newName, edits);
      return;
    default:
      return;
  }
}

function collectHIREdit(e: HirExpr, target: Symbol, newName: string, edits: TextEdit[]): void {
  switch (e.kind) {
    case "method_call":
      if (e.method !== undefined && e.method.id === target.id) {
        edits.push({
          file: e.nameSp.file,
          startOffset: e.nameSp.start.offset,
          endOffset: e.nameSp.end.offset,
          replacement: newName,
        });
      }
      collectHIREdit(e.recv, target, newName, edits);
      for (const a of e.args) collectHIREdit(a, target, newName, edits);
      return;
    case "call":
      if (e.callee !== undefined) collectHIREdit(e.callee, target, newName, edits);
      for (const a of e.args) collectHIREdit(a, target, newName, edits);
      return;
    case "ctor":
      for (const a of e.args) collectHIREdit(a, target, newName, edits);
      return;
    case "unary":
      collectHIREdit(e.x, target, newName, edits);
      return;
    case "binary":
      collectHIREdit(e.l, target, newName, edits);
      collectHIREdit(e.r, target, newName, edits);
      return;
    case "and_then":
      collectHIREdit(e.l, target, newName, edits);
      collectHIREdit(e.r, target, newName, edits);
      return;
    case "or_else":
      collectHIREdit(e.l, target, newName, edits);
      collectHIREdit(e.r, target, newName, edits);
      return;
    case "range":
      collectHIREdit(e.lo, target, newName, edits);
      collectHIREdit(e.hi, target, newName, edits);
      return;
    case "index":
      collectHIREdit(e.obj, target, newName, edits);
      collectHIREdit(e.idx, target, newName, edits);
      return;
    case "slice":
      collectHIREdit(e.obj, target, newName, edits);
      collectHIREdit(e.lo, target, newName, edits);
      collectHIREdit(e.hi, target, newName, edits);
      return;
    case "field":
      collectHIREdit(e.obj, target, newName, edits);
      return;
    case "try":
      collectHIREdit(e.x, target, newName, edits);
      return;
    case "struct_lit":
      for (const f of e.fields) collectHIREdit(f.val, target, newName, edits);
      return;
    case "map_lit":
      for (let i = 0; i < e.keys.length; i++) {
        collectHIREdit(e.keys[i]!, target, newName, edits);
        collectHIREdit(e.vals[i]!, target, newName, edits);
      }
      return;
    case "slice_lit":
      for (const el of e.elems) collectHIREdit(el, target, newName, edits);
      return;
    case "match":
      collectHIREdit(e.scrut, target, newName, edits);
      for (const arm of e.arms) collectHIREdit(arm.body, target, newName, edits);
      return;
    case "fn_lit":
      for (const st of e.body) collectHIRMethodRefs(st, target, newName, edits);
      return;
    default:
      return;
  }
}

export type { SymbolId };
