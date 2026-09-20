// 前端管线：parse → resolve → typecheck → HIR → IR。
// CLI（check/build）与测试共用；诊断带稳定码。

import { parse, type ParseError } from "../syntax/parser.js";
import { LexError } from "../syntax/lexer.js";
import type { Stmt } from "../syntax/ast.js";
import { resolve, type ResolveResult } from "../semantic/resolver.js";
import { check, type CheckResult } from "../types/checker.js";
import { build } from "../hir/build.js";
import type { HirProg } from "../hir/model.js";
import { lower } from "../ir/lower.js";
import type { IrProg } from "../ir/model.js";
import { E1001, formatDiags, type Diagnostic } from "../common/diagnostic.js";

/** 解析（独立导出供 dump ast 用） */
export function parseSource(src: string, file = ""): Stmt[] {
  try {
    return parse(src, file);
  } catch (e) {
    if (e instanceof LexError) {
      // 词法错误：取整段文本
      throw new DiagList([{ code: E1001, msg: e.message, line: 0 }]);
    }
    const pe = e as ParseError;
    throw new DiagList([{ code: E1001, msg: pe.msg, line: pe.line }]);
  }
}

/** 前端检查：parse → resolve → typecheck。合法返回空诊断。 */
export function checkSource(src: string, file = ""): Diagnostic[] {
  let prog: Stmt[];
  try {
    prog = parseSource(src, file);
  } catch (e) {
    if (e instanceof DiagList) return [...e.diags];
    throw e;
  }
  return checkStmts(prog);
}

/** 前端检查（已解析语句序列） */
export function checkStmts(prog: readonly Stmt[]): Diagnostic[] {
  const res = resolve(prog);
  if (res.diags.length > 0) return [...res.diags];
  const ty = check(prog, res);
  if (ty.diags.length > 0) return [...ty.diags];
  return [];
}

/** 全前端产物（HIR/IR/符号/类型），契约层一次性取用 */
export interface Analyzed {
  readonly ast: readonly Stmt[];
  readonly res: ResolveResult;
  readonly ty: CheckResult;
  readonly hir: HirProg;
  readonly prog: IrProg;
}

/** 已解析语句 → 全前端产物 */
export function analyzeStmts(prog: readonly Stmt[]): Analyzed {
  const res = resolve(prog);
  if (res.diags.length > 0) throw new DiagList([...res.diags]);
  const ty = check(prog, res);
  if (ty.diags.length > 0) throw new DiagList([...ty.diags]);
  const hir = build(prog, res, ty);
  return { ast: prog, res, ty, hir, prog: lower(hir) };
}

/** 源码 → 全前端产物 */
export function analyze(src: string, file = ""): Analyzed {
  return analyzeStmts(parseSource(src, file));
}

/** 已解析语句序列 → IR（装载器合并后的程序用） */
export function compileStmts(prog: readonly Stmt[]): IrProg {
  return analyzeStmts(prog).prog;
}

/** 源码 → IR（检查全过才降级） */
export function compileSource(src: string, file = ""): IrProg {
  return analyze(src, file).prog;
}

/** 多诊断聚合（首个诊断即 Error() 文本，与 Go ErrorList 一致） */
export class DiagList extends Error {
  constructor(readonly diags: readonly Diagnostic[]) {
    super(diags.length === 0 ? "无诊断" : `第${diags[0]!.line}行: [${diags[0]!.code}] ${diags[0]!.msg}`);
    this.name = "DiagList";
  }
}

export { formatDiags };
