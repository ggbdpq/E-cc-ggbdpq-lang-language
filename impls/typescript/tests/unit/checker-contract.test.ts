// Checker 契约点测试：ADR-0005 赋值顶层 ? 放行 + NAME-SCOPE-001 块级作用域

import { describe, expect, it } from "vitest";
import { parseSource, checkSource } from "../../src/pipeline/pipeline.js";
import { check, type CheckResult } from "../../src/types/checker.js";
import { resolve } from "../../src/semantic/resolver.js";
import type { LetStmt, Stmt } from "../../src/syntax/ast.js";

/** 解析一次 → resolve + check（类型断言需要稳定的 AST 对象身份） */
function checkOf(src: string): { res: CheckResult; prog: readonly Stmt[] } {
  const prog = parseSource(src);
  return { res: check(prog, resolve(prog)), prog };
}

function findLet(stmts: readonly Stmt[], name: string): LetStmt | undefined {
  for (const s of stmts) {
    if (s.kind === "let_stmt") {
      if (s.name === name) return s;
    } else if (s.kind === "fn_decl") {
      const r = findLet(s.body, name);
      if (r !== undefined) return r;
    } else if (s.kind === "if") {
      const r = findLet(s.then, name) ?? (s.els !== undefined ? findLet(s.els, name) : undefined);
      if (r !== undefined) return r;
    } else if (s.kind === "while") {
      const r = findLet(s.body, name);
      if (r !== undefined) return r;
    } else if (s.kind === "for_in") {
      const r = findLet(s.body, name);
      if (r !== undefined) return r;
    }
  }
  return undefined;
}

/** let 绑定初值的推断类型 kind */
function initTypeOf(res: CheckResult, prog: readonly Stmt[], name: string): string {
  const d = findLet(prog, name);
  if (d === undefined) throw new Error(`未找到 let ${name}`);
  const t = res.exprTy.get(d.init);
  if (t === undefined) throw new Error(`let ${name} 的初值无推断类型`);
  return t.kind;
}

describe("checker 契约点", () => {
  it("ADR-0005：赋值右值位置的顶层 ? 合法", () => {
    // 所在函数注解 Result → 零诊断
    const ok1 =
      "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nvar x = 0\nx = f()?\nreturn Ok(x)\n}\nfn main() { }";
    expect(checkSource(ok1).map((d) => d.code)).toEqual([]);
    // 复合赋值右值同规则
    const ok2 =
      "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nvar x = 0\nx += f()?\nreturn Ok(x)\n}\nfn main() { }";
    expect(checkSource(ok2).map((d) => d.code)).toEqual([]);
    // 所在函数未注解 Result 仍按 E3005 拒绝
    const bad =
      "fn f(): Result[int, error] { return Ok(1) }\nfn g() {\nvar x = 0\nx = f()?\n}\nfn main() { }";
    expect(checkSource(bad).map((d) => d.code)).toEqual(["E3005"]);
  });

  it("NAME-SCOPE-001：块内 let 不写函数层", () => {
    const src = 'fn main() {\nlet a = 1\nif true {\nlet a = "s"\n}\nlet c = a\nprintln(c)\n}';
    expect(checkSource(src).map((d) => d.code)).toEqual([]);
    const { res, prog } = checkOf(src);
    expect(initTypeOf(res, prog, "c")).toBe("int");
  });

  it("NAME-SCOPE-001：循环变量写循环块层，不覆盖函数层", () => {
    const src =
      'fn main() {\nlet x = "s"\nfor x in [1, 2] {\nlet y = x\nprintln(y)\n}\nlet z = x\nprintln(z)\n}';
    expect(checkSource(src).map((d) => d.code)).toEqual([]);
    const { res, prog } = checkOf(src);
    expect(initTypeOf(res, prog, "z")).toBe("string");
  });

  it("NAME-SCOPE-001：match 臂绑定写臂层，不覆盖函数层", () => {
    const src = `
enum E { A(int)
    B
}
fn main() {
    let v = "s"
    var e = A(1)
    match e {
        A(v) => println(v)
        B => println(0)
    }
    let z = v
    println(z)
}`;
    expect(checkSource(src).map((d) => d.code)).toEqual([]);
    const { res, prog } = checkOf(src);
    expect(initTypeOf(res, prog, "z")).toBe("string");
  });
});
