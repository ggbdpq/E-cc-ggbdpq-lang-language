import { readFileSync, writeFileSync } from "node:fs";
import { parseSource, analyzeStmts } from "../dist/pipeline/pipeline.js";
import { transpile } from "../dist/backends/gen.js";
import { transpileJS } from "../dist/backends/genjs.js";
const src = readFileSync(process.argv[2], "utf8");
const prog = parseSource(src, "main.lang-go");
const ir = analyzeStmts(prog).prog;
writeFileSync(process.argv[3], transpile(ir));
writeFileSync(process.argv[4], transpileJS(ir));
