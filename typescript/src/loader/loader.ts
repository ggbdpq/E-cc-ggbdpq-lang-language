// 目录即包 + import 装载器（specs/modules.md）：
// 把目录内全部非测试 .lang-go 按文件名序合并为一个程序，
// 递归展开 import "./x"（文件或目录），限制在根目录树内；
// 循环导入报错，菱形导入（多处导同一库）静默去重。错误消息与 Go 参考实现逐字对齐。

import * as fs from "node:fs";
import * as path from "node:path";
import { parseSource } from "../pipeline/pipeline.js";
import type { Stmt } from "../syntax/ast.js";

/** 文件装载信息（inspect dependencies 用） */
export interface FileInfo {
  /** 模块根相对路径（/ 分隔） */
  readonly path: string;
  /** 该文件声明的 import 路径（源码原文，依出现序） */
  readonly imports: string[];
}

export interface LoadedProgram {
  readonly stmts: Stmt[];
  readonly files: FileInfo[];
}

export const ENTRY = "main.lang-go";

function listLangGo(dir: string, tests: boolean): string[] {
  const out: string[] = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const n = e.name;
    if (e.isDirectory() || !n.endsWith(".lang-go")) continue;
    const isTest = n.endsWith("_test.lang-go");
    if (isTest === tests) out.push(n);
  }
  return out.sort();
}

class Loader {
  private readonly done = new Set<string>();
  private readonly inProgress = new Set<string>();
  readonly files: FileInfo[] = [];

  constructor(private readonly root: string) {}

  /** 合并目录内非测试 .lang-go 并展开 import */
  loadDir(dir: string): Stmt[] {
    const files = listLangGo(dir, false);
    const raw: Stmt[] = [];
    for (const f of files) {
      const ap = path.join(dir, f);
      const stmts = this.loadFile(ap);
      this.done.add(ap);
      // import 解析键不含扩展名：同目录文件既被根装载又被 import 引用时不得双载
      this.done.add(ap.replace(/\.lang-go$/, ""));
      raw.push(...stmts);
    }
    return this.expand(raw, dir);
  }

  /** 解析单个文件并盖章 Span.File（模块根相对路径） */
  loadFile(ap: string): Stmt[] {
    let src: string;
    try {
      src = fs.readFileSync(ap, "utf8");
    } catch (e) {
      throw new Error(`${path.basename(ap)}: ${String(e)}`);
    }
    let stmts: Stmt[];
    try {
      stmts = parseSource(src, toRel(this.root, ap));
    } catch (e) {
      throw new Error(`${path.basename(ap)}: ${String(e)}`);
    }
    const rel = toRel(this.root, ap);
    // Span.File 已在 parse 时盖章（file 参数），此处补 FileInfo
    this.files.push({ path: rel, imports: collectImports(stmts) });
    return stmts;
  }

  /** 展开语句序列里的 ImportStmt：相对 fromDir 解析 "./x" → x/ 目录或 x.lang-go 文件 */
  private expand(stmts: Stmt[], fromDir: string): Stmt[] {
    const out: Stmt[] = [];
    for (const s of stmts) {
      if (s.kind !== "import") {
        out.push(s);
        continue;
      }
      const imp = s;
      if (!imp.path.startsWith("./")) {
        throw new Error(`第${imp.line}行: import 仅支持 "./相对路径"（得到 ${JSON.stringify(imp.path)}）`);
      }
      const [target, skip] = this.resolve(imp.path, fromDir, imp.line);
      if (skip) continue; // 菱形导入：已装载过
      let sub: Stmt[];
      let isDir = false;
      try {
        isDir = fs.statSync(target).isDirectory();
      } catch {
        isDir = false;
      }
      try {
        if (isDir) {
          sub = this.loadDir(target);
        } else {
          const fileStmts = this.loadFile(target + ".lang-go");
          sub = this.expand(fileStmts, path.dirname(target));
        }
      } catch (e) {
        throw e instanceof Error ? e : new Error(String(e));
      }
      this.done.add(target);
      this.inProgress.delete(target);
      out.push(...sub);
    }
    return out;
  }

  /** 规范化 + containment + 环/去重判定 */
  private resolve(rel: string, fromDir: string, line: number): [string, boolean] {
    const ap = path.resolve(fromDir, rel);
    const relToRoot = toRel(this.root, ap);
    if (relToRoot === ".." || relToRoot.startsWith("../")) {
      throw new Error(`第${line}行: import ${rel} 逃出模块根目录`);
    }
    if (this.done.has(ap)) return [ap, true];
    if (this.inProgress.has(ap)) {
      throw new Error(`第${line}行: 循环 import ${rel}`);
    }
    this.inProgress.add(ap);
    return [ap, false];
  }
}

function toRel(root: string, ap: string): string {
  const r = path.relative(root, ap).split(path.sep).join("/");
  return r === "" ? "." : r;
}

function collectImports(stmts: Stmt[]): string[] {
  const out: string[] = [];
  for (const s of stmts) {
    if (s.kind === "import") out.push(s.path);
  }
  return out;
}

/** 装载 dir 为一个合并程序。includeTests 时 *_test.lang-go 追加在后，
 *  并返回其中全部 test 前缀无参函数名（按文件名序） */
export function loadProgram(dir: string, includeTests: boolean): { stmts: Stmt[]; tests: string[]; files: FileInfo[] } {
  const root = path.resolve(dir);
  const l = new Loader(root);
  const prog = l.loadDir(root);
  const tests: string[] = [];
  if (includeTests) {
    for (const f of listLangGo(root, true)) {
      const stmts = l.loadFile(path.join(root, f));
      prog.push(...stmts);
      for (const s of stmts) {
        if (s.kind === "fn_decl" && s.recv === undefined && s.params.length === 0 && s.name.startsWith("test")) {
          tests.push(s.name);
        }
      }
    }
  }
  return { stmts: prog, tests, files: l.files };
}
