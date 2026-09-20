// 目录即包装载器（specs/modules.md）：
// 当前目录全部 .lang-go 按文件名序合并；import "./x" 递归展开（文件或目录），
// 循环导入报错、菱形去重、越出模块根报错、非 ./ 前缀拒绝。

import * as fs from "node:fs";
import * as path from "node:path";
import { parseSource } from "../pipeline/pipeline.js";
import type { Stmt } from "../syntax/ast.js";

export interface FileInfo {
  readonly path: string; // 模块根相对路径（/ 分隔）
  readonly imports: string[];
}

export interface LoadedProgram {
  readonly stmts: Stmt[];
  readonly files: FileInfo[];
}

const ENTRY = "main.lang-go";

export function loadProgram(dir: string, includeTests = false): LoadedProgram {
  const root = path.resolve(dir);
  const l = new Loader(root);
  const stmts = l.loadDir(root);
  if (includeTests) {
    for (const f of listLangGo(root, true)) {
      stmts.push(...l.loadFile(path.join(root, f)));
    }
  }
  return { stmts, files: l.files };
}

function listLangGo(dir: string, testsOnly: boolean): string[] {
  const out: string[] = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (!e.isFile() || !e.name.endsWith(".lang-go")) continue;
    const isTest = e.name.endsWith("_test.lang-go");
    if (testsOnly === isTest) out.push(e.name);
  }
  return out.sort();
}

class Loader {
  readonly files: FileInfo[] = [];
  private readonly done = new Set<string>();
  private readonly inProgress = new Set<string>();

  constructor(private readonly root: string) {}

  loadDir(dir: string): Stmt[] {
    const out: Stmt[] = [];
    for (const f of listLangGo(dir, false)) {
      const abs = path.join(dir, f);
      out.push(...this.loadFile(abs));
      // import 解析键不含扩展名：同目录文件既被根装载又被 import 引用时不得双载
      this.done.add(abs);
      this.done.add(abs.replace(/\.lang-go$/, ""));
    }
    return this.expandImports(out, dir);
  }

  loadFile(abs: string): Stmt[] {
    const src = fs.readFileSync(abs, "utf8");
    const rel = path.relative(this.root, abs).split(path.sep).join("/");
    const stmts = parseSource(src, rel);
    this.stampFile(stmts, rel);
    this.files.push({ path: rel, imports: collectImports(stmts) });
    return stmts;
  }

  // expandImports 展开语句序列里的 ImportStmt（相对 fromDir 解析）
  private expandImports(stmts: Stmt[], fromDir: string): Stmt[] {
    const out: Stmt[] = [];
    for (const s of stmts) {
      const imp = importPath(s);
      if (imp === undefined) {
        out.push(s);
        continue;
      }
      if (!imp.startsWith("./")) {
        throw new Error(`import 仅支持 "./相对路径"（得到 ${JSON.stringify(imp)}）`);
      }
      const base = path.resolve(fromDir, imp);
      if (!base.startsWith(this.root + path.sep) && base !== this.root) {
        throw new Error(`import 越出模块根: ${imp}`);
      }
      const asFile = base.endsWith(".lang-go") ? base : base + ".lang-go";
      const asDir = base;
      let target: string;
      if (this.done.has(asFile) || fs.existsSync(asFile)) target = asFile;
      else target = asDir;
      if (this.done.has(target)) continue;
      if (this.inProgress.has(target)) {
        throw new Error(`循环导入: ${imp}`);
      }
      this.inProgress.add(target);
      const loaded = fs.statSync(target).isDirectory() ? this.loadDir(target) : this.loadFile(target);
      this.inProgress.delete(target);
      this.done.add(target);
      out.push(...loaded);
    }
    return out;
  }

  // stampFile 给全部 AST 节点盖上文件归属（diag file 字段用）
  private stampFile(stmts: Stmt[], file: string): void {
    for (const s of stmts) {
      const anyS = s as { span?: { file?: string } };
      if (anyS.span) anyS.span.file = file;
    }
  }
}

function importPath(s: Stmt): string | undefined {
  const anyS = s as { kind?: string; path?: string };
  if (anyS.kind === "import") return anyS.path;
  return undefined;
}

function collectImports(stmts: Stmt[]): string[] {
  const out: string[] = [];
  for (const s of stmts) {
    const p = importPath(s);
    if (p !== undefined) out.push(p);
  }
  return out;
}

export { ENTRY };
