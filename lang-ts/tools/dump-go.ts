// 调试工具：打印 →Go 产物片段（DBG_SRC 传源码；DBG_FROM/DBG_TO 传行号区间）
import { parseSource, analyzeStmts } from "../dist/pipeline/pipeline.js";
import { transpile } from "../dist/backends/gen.js";

const src = process.env.DBG_SRC ?? "";
const prog = parseSource(src, "main.lang-go");
const ir = analyzeStmts(prog).prog;
const out = transpile(ir).split("\n");
const from = Number(process.env.DBG_FROM ?? 1);
const to = Number(process.env.DBG_TO ?? 0) || out.length;
console.log(out.slice(from - 1, to).map((l, i) => `${from + i}: ${l}`).join("\n"));
