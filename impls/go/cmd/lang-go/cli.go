package main

// CLI 实现：run/test/REPL
// 路径安全（v0.1）：不接收任何路径参数——lang-go run 固定运行当前目录的 main.lang-go，
// lang-go test 固定扫描当前目录。文件输入无 ../ 穿越面。

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"lang-go/internal/bind"
	"lang-go/internal/conformance"
	"lang-go/internal/contract"
	"lang-go/internal/edit"
	"lang-go/internal/fix"
	"lang-go/internal/format"
	"lang-go/internal/gen"
	"lang-go/internal/genjs"
	"lang-go/internal/graph"
	"lang-go/internal/interp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"lang-go/internal/refactor"
	"lang-go/internal/syntax"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

func hasFlag(args []string, flag string) bool {
	for _, a := range args {
		if a == flag {
			return true
		}
	}

	return false
}

// printJSON 统一输出契约 JSON.
func printJSON(v any) {
	bs, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		fmt.Fprintln(os.Stderr, "JSON 编码失败:", err)
		return
	}

	fmt.Println(string(bs))
}

const version = contract.ToolVersion // 单一版本源（specs/agent-protocol.md）

// entryFile 固定入口文件名：与 Go「每目录一个 main」的习惯一致.
const entryFile = "main.lang-go"

func cliMain() int {
	args := os.Args[1:]
	if len(args) == 0 {
		return repl()
	}

	switch args[0] {
	case "run":
		return runFile(entryFile)
	case "test":
		return runTests()
	case "fmt":
		return runFmt()
	case "check":
		rest := args[1:]
		if hasFlag(rest, "--json") {
			return runCheckJSON()
		}

		return runCheck()
	case "inspect":
		return runInspect(args[1:])
	case "dump":
		return runDump(args[1:])
	case "fix":
		return runFix(args[1:])
	case "refactor":
		return runRefactor(args[1:])
	case "verify":
		return runVerify(args[1:])
	case "bind":
		// 包目录固定为当前目录的 bind.d/（安全边界：任何路径输入都不进数据流）
		return runBind()
	case "build":
		target := "go"
		if len(args) >= 2 {
			target = args[1]
		}

		return runBuild(target)
	case "version", "--version", "-v":
		fmt.Println("lang-go", version)
		return 0
	case "help", "--help", "-h":
		usage()
		return 0
	default:
		fmt.Fprintf(os.Stderr, "未知命令 %q\n\n", args[0])
		usage()

		return 2
	}
}

func usage() {
	fmt.Print(`lang-go ` + version + ` — 三路执行个人语言（解释器 / →Go / →JS）

用法:
  lang-go            启动 REPL（:quit 退出，:help 帮助）
  lang-go run        运行当前目录的 main.lang-go
  lang-go test       运行当前目录 *_test.lang-go 里的 test* 函数
  lang-go check      语义检查（resolver + 类型检查，稳定诊断码）
  lang-go inspect    symbols|refs|type|dependencies|effects --json 语义查询
  lang-go dump       ast|hir|ir --json 输出编译器内部树
  lang-go fix --safe [--dry-run] [--json]
                     机械安全修复（重复/未使用 import、行尾空白、EOF 换行）+ 验证闭环
  lang-go refactor rename|organize-imports|safe-remove [--symbol N] [--to NAME]
                     [--apply] [--json]
                     语义级重构（SymbolId + 引用集，禁止 Regex）；默认只报编辑，--apply 落盘+验证闭环
  lang-go verify [--json]
                     全管线验证（parse→…→三路差分一致）+ conformance 套件同为 runner contract 入口
  lang-go fmt        就地格式化当前目录 .lang-go
  lang-go build      语义检查后写 out/main.go（build js 写 out/main.mjs）
  lang-go bind       生成 extern "go" 声明草案（bind.d/）
  lang-go version    版本

文档: go/docs/spec.md（语言规范）  go/docs/decisions.md（设计决策）
`)
}

// runBind 生成 extern "go" 声明草案到 stdout（decisions #61）：
// 包目录固定为当前目录的 bind.d/（常量路径），草案需人工审核.
func runBind() int {
	out, err := bind.Dir("bind.d")
	if err != nil {
		fmt.Fprintln(os.Stderr, "bind 失败:", err)
		return 1
	}

	fmt.Print(out)

	return 0
}

// runBuild 静态检查 + 产物写入 out/（Q21）：lang-go build → out/main.go，
// lang-go build js → out/main.mjs。写路径为常量，无用户输入参与。
func runBuild(target string) int {
	prog, _, err := load.Program(".", false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "装载失败:", err)
		return 1
	}

	if err := os.MkdirAll("out", 0o755); err != nil {
		fmt.Fprintln(os.Stderr, "建 out/ 失败:", err)
		return 1
	}

	p, perr := pipeline.CompileStmts(prog)
	if perr != nil {
		fmt.Fprintln(os.Stderr, "检查未通过:")
		fmt.Fprintln(os.Stderr, pipeline.FormatDiags(pipeline.Diagnostics(perr)))

		return 1
	}

	switch target {
	case "go":
		src, err := gen.Transpile(p)
		if err != nil {
			fmt.Fprintln(os.Stderr, "→Go 转译失败:", err)
			return 1
		}

		if err := os.WriteFile(filepath.Join("out", "main.go"), []byte(src), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "写产物失败:", err)
			return 1
		}

		fmt.Println("out/main.go（go build ./out 或 go run ./out/main.go 执行）")

		return 0
	case "js":
		src, err := genjs.TranspileJS(p)
		if err != nil {
			fmt.Fprintln(os.Stderr, "→JS 转译失败:", err)
			return 1
		}

		if err := os.WriteFile(filepath.Join("out", "main.mjs"), []byte(src), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "写产物失败:", err)
			return 1
		}

		fmt.Println("out/main.mjs（node out/main.mjs 执行）")

		return 0
	}

	fmt.Fprintln(os.Stderr, "未知目标:", target, "（可选 go / js）")

	return 2
}

// runFmt 就地格式化当前目录全部 .lang-go（不递归），打印改动的文件.
func runFmt() int {
	entries, err := os.ReadDir(".")
	if err != nil {
		fmt.Fprintln(os.Stderr, "读目录失败:", err)
		return 2
	}

	changed := 0

	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".lang-go") {
			continue
		}

		src, err := os.ReadFile(name)
		if err != nil {
			fmt.Fprintln(os.Stderr, "读文件失败:", err)
			return 1
		}

		out, err := format.Format(string(src))
		if err != nil {
			fmt.Fprintf(os.Stderr, "格式化 %s 失败: %v\n", name, err)
			return 1
		}

		if out != string(src) {
			if err := os.WriteFile(name, []byte(out), 0o644); err != nil {
				fmt.Fprintln(os.Stderr, "写文件失败:", err)
				return 1
			}

			fmt.Println(name)

			changed++
		}
	}

	fmt.Printf("%d 个文件已格式化\n", changed)

	return 0
}

// runCheck 语义检查（v0.3 起走独立前端：resolver + 类型检查器，
// 诊断带稳定码 E1xxx/E2xxx/E3xxx/E4xxx；不再依赖 →JS 转译器，decisions #49 被 #66 取代）.
func runCheck() int {
	prog, _, err := load.Program(".", false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "装载失败:", err)
		return 1
	}

	diags, _ := pipeline.CheckStmts(prog)
	if len(diags) > 0 {
		fmt.Fprintln(os.Stderr, "检查未通过:")
		fmt.Fprintln(os.Stderr, pipeline.FormatDiags(diags))

		return 1
	}

	fmt.Println("检查通过")

	return 0
}

// runCheckJSON check --json：契约负载.
func runCheckJSON() int {
	prog, _, err := load.Program(".", false)
	if err != nil {
		e := contract.ErrorEnvelope(contract.KindCheck, "装载失败: "+err.Error())
		printJSON(e)

		return 1
	}

	out := contract.CheckStmts(prog)
	printJSON(out)

	if !out.Success {
		return 1
	}

	return 0
}

// runInspect inspect symbols|refs|type|dependencies|effects --json
// runInspect inspect symbols|refs|type|dependencies|effects|callers|callees|impact|references.
func runInspect(args []string) int {
	if len(args) == 0 {
		printJSON(contract.ErrorEnvelope("inspect", "缺少子命令：symbols|refs|type|dependencies|effects|callers|callees|impact|references"))
		return 2
	}

	sub := args[0]
	kind := "inspect." + sub

	if !hasFlag(args, "--json") {
		fmt.Fprintln(os.Stderr, "inspect 仅支持 --json 输出（v0.4+）")
		return 2
	}
	// 参数解析
	file, line, col := "", 0, 0
	depth := -1
	target := ""

	for i, a := range args {
		switch a {
		case "--file":
			if i+1 < len(args) {
				file = args[i+1]
			}
		case "--line":
			if i+1 < len(args) {
				line, _ = strconv.Atoi(args[i+1])
			}
		case "--column":
			if i+1 < len(args) {
				col, _ = strconv.Atoi(args[i+1])
			}
		case "--depth":
			if i+1 < len(args) {
				depth, _ = strconv.Atoi(args[i+1])
			}
		case "--symbol":
			if i+1 < len(args) {
				target = args[i+1]
			}
		default:
			if !strings.HasPrefix(a, "-") && a != sub && target == "" && sub != "symbols" && sub != "dependencies" && sub != "effects" {
				target = a
			}
		}
	}

	prog, _, files, err := load.ProgramEx(".", false)
	if err != nil {
		printJSON(contract.ErrorEnvelope(kind, "装载失败: "+err.Error()))
		return 1
	}

	a, aerr := pipeline.AnalyzeStmts(prog)
	if aerr != nil {
		printJSON(contract.ErrorEnvelope(kind, pipeline.FormatDiags(pipeline.Diagnostics(aerr))))
		return 1
	}

	g := graph.Build(prog, a.Res, a.Ty, a.HIR, a.Prog, files)

	switch sub {
	case "symbols":
		printJSON(contract.SymbolsEnvelope(prog, a.Res, a.Ty))
		return 0
	case "refs", "references":
		symID := -1
		if target != "" {
			symID = resolveSymbolArg(a, target, nil)
			if symID < 0 {
				printJSON(contract.ErrorEnvelope(kind, "未知符号 "+target))
				return 1
			}
		}

		printJSON(contract.ReferencesEnvelope(prog, a.Res, symID))

		return 0
	case "callers", "callees", "impact":
		g := graph.Build(prog, a.Res, a.Ty, a.HIR, a.Prog, files)

		id := resolveSymbolArg(a, target, g)
		if id <= 0 {
			printJSON(contract.ErrorEnvelope(kind, "未知符号 "+target))
			return 1
		}

		switch sub {
		case "callers":
			printJSON(contract.CallersEnvelope(g, g.Callers(id, depth)))
		case "callees":
			printJSON(contract.CalleesEnvelope(g, g.Callees(id, depth)))
		default:
			printJSON(contract.ImpactEnvelope(g.Impact(id, depth)))
		}

		return 0
	case "type":
		if line == 0 {
			printJSON(contract.ErrorEnvelope(kind, "缺少 --line/--column 参数"))
			return 2
		}

		env, ok := contract.TypeEnvelope(prog, a.Res, a.Ty, file, line, col)
		printJSON(env)

		if !ok {
			return 1
		}

		return 0
	case "dependencies":
		printJSON(contract.DependenciesEnvelope(files))
		return 0
	case "dependency":
		target := ""

		for i, a := range args {
			if a == "--file" && i+1 < len(args) {
				target = args[i+1]
			}
		}

		if target == "" {
			target = "main.lang-go"
		}

		dep, ok := g.FileDep(target)
		if !ok {
			printJSON(contract.ErrorEnvelope(kind, "未知文件 "+target))
			return 1
		}

		printJSON(contract.GraphDepJSON(dep))

		return 0
	case "effects":
		printJSON(contract.EffectsEnvelope(contract.Effects(a.Prog)))
		return 0
	}

	printJSON(contract.ErrorEnvelope(kind, "未知子命令 "+sub))

	return 2
}

// resolveSymbolArg 解析符号参数：数字 → SymbolID；名字 → 首个同名用户符号；未找到 → -1.
func resolveSymbolArg(a *pipeline.Analyzed, arg string, g *graph.Graph) int {
	if arg == "" {
		return -1
	}

	if id, err := strconv.Atoi(arg); err == nil {
		if _, ok := g.Nodes[id]; ok {
			return id
		}

		return -1
	}

	for _, sym := range a.Res.Symbols {
		if sym.BuiltinMark {
			continue
		}

		if sym.Name == arg {
			return int(sym.ID)
		}
	}

	return -1
}

// runDump dump ast|hir|ir --json.
func runDump(args []string) int {
	if len(args) == 0 {
		printJSON(contract.ErrorEnvelope("dump", "缺少子命令：ast|hir|ir"))
		return 2
	}

	sub := args[0]
	if !hasFlag(args, "--json") {
		fmt.Fprintln(os.Stderr, "dump 仅支持 --json 输出（v0.4）")
		return 2
	}

	kind := "dump." + sub

	prog, _, _, err := load.ProgramEx(".", false)
	if err != nil {
		printJSON(contract.ErrorEnvelope(kind, "装载失败: "+err.Error()))
		return 1
	}

	switch sub {
	case "ast":
		a, aerr := pipeline.AnalyzeStmts(prog)
		if aerr != nil {
			printJSON(contract.ErrorEnvelope(kind, pipeline.FormatDiags(pipeline.Diagnostics(aerr))))
			return 1
		}

		printJSON(contract.DumpEnvelope(kind, contract.ASTJSON(prog, a.Res, a.Ty)))
	case "hir":
		a, aerr := pipeline.AnalyzeStmts(prog)
		if aerr != nil {
			printJSON(contract.ErrorEnvelope(kind, pipeline.FormatDiags(pipeline.Diagnostics(aerr))))
			return 1
		}

		printJSON(contract.DumpEnvelope(kind, contract.HIRJSON(a.HIR)))
	case "ir":
		p, aerr := pipeline.CompileStmts(prog)
		if aerr != nil {
			printJSON(contract.ErrorEnvelope(kind, pipeline.FormatDiags(pipeline.Diagnostics(aerr))))
			return 1
		}

		printJSON(contract.DumpEnvelope(kind, contract.IRJSON(p)))
	default:
		printJSON(contract.ErrorEnvelope(kind, "未知子命令 "+sub))
		return 2
	}

	return 0
}

func runFile(path string) int {
	prog, _, err := load.Program(".", false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "装载失败:", err)
		return 1
	}

	_ = path // v0.1：入口固定为当前目录（目录即包）

	it := interp.New(os.Stdout)
	if err := it.Run(prog); err != nil {
		fmt.Fprintln(os.Stderr, "错误:", err)
		return 1
	}

	return 0
}

// runTests 装载目录即包（实现文件 + *_test.lang-go，import 由装载器展开）后依序执行 test* 函数.
func runTests() int {
	it := interp.New(os.Stdout)

	prog, tests, err := load.Program(".", true)
	if err != nil {
		fmt.Fprintln(os.Stderr, "装载失败:", err)
		return 1
	}

	if _, _, err := it.EvalChunk(prog); err != nil {
		fmt.Fprintln(os.Stderr, "装载失败:", err)
		return 1
	}

	if len(tests) == 0 {
		fmt.Println("未发现 *_test.lang-go")
		return 0
	}

	pass, fail := 0, 0

	for _, name := range tests {
		if err := it.CallByName(name); err != nil {
			fail++

			fmt.Printf("FAIL %s: %v\n", name, err)
		} else {
			pass++

			fmt.Printf("PASS %s\n", name)
		}
	}

	fmt.Printf("\n%d 通过, %d 失败\n", pass, fail)

	if fail > 0 {
		return 1
	}

	return 0
}

func repl() int {
	it := interp.New(os.Stdout)

	fmt.Printf("lang-go v%s — :help 帮助, :quit 退出\n", version)

	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 64*1024), 1024*1024)

	var buf strings.Builder

	prompt := "lang-go> "
	for {
		fmt.Print(prompt)

		if !scanner.Scan() {
			fmt.Println()
			return 0
		}

		line := scanner.Text()
		if buf.Len() == 0 {
			switch strings.TrimSpace(line) {
			case "":
				continue
			case ":quit", ":q", "exit":
				return 0
			case ":help":
				fmt.Println("输入 lang-go 源码回车执行；未闭合的块会进入续行模式；:quit 退出")
				continue
			}
		}

		buf.WriteString(line)
		buf.WriteString("\n")

		prog, err := syntax.Parse(buf.String())
		if err != nil {
			var pe *syntax.Error
			if errors.As(err, &pe) && pe.Incomplete {
				prompt = "  ... "
				continue
			}

			fmt.Println("错误:", err)
			buf.Reset()

			prompt = "lang-go> "

			continue
		}

		val, has, err := it.EvalChunk(prog)
		if err != nil {
			fmt.Println("错误:", err)
		} else if has {
			fmt.Println("=>", interp.Fmt(val))
		}

		buf.Reset()

		prompt = "lang-go> "
	}
}

// runFix fix --safe [--dry-run] [--json]：机械安全修复 + 编辑事务闭环。
// 事务：analyze → plan → precondition → apply → parse → resolve → typecheck；
// 任一步失败即 STOP 并输出结构化原因。
func runFix(args []string) int {
	kind := "fix"
	if !hasFlag(args, "--safe") {
		printJSON(contract.ErrorEnvelope(kind, "v0.5 仅支持 --safe（机械可证明安全修复）"))
		return 2
	}

	dry := hasFlag(args, "--dry-run")
	asJSON := hasFlag(args, "--json")

	ws, err := fix.PlanDir(".")
	if err != nil {
		printJSON(contract.ErrorEnvelope(kind, err.Error()))
		return 1
	}

	if asJSON || dry {
		printJSON(struct {
			SchemaVersion string             `json:"schemaVersion"`
			ToolVersion   string             `json:"toolVersion"`
			Kind          string             `json:"kind"`
			DryRun        bool               `json:"dryRun"`
			Applied       bool               `json:"applied"`
			WorkspaceEdit edit.WorkspaceEdit `json:"workspaceEdit"`
		}{contract.SchemaVersion, contract.ToolVersion, kind, dry, false, ws})

		if dry {
			return 0
		}
	}

	if err := edit.Apply(ws, "."); err != nil {
		msg := "apply: " + err.Error()
		printJSON(fixStop(contract.ToolVersion, kind, "apply", msg))
		fmt.Fprintln(os.Stderr, "STOP——应用编辑失败:", err)

		return 1
	}

	// 验证闭环：parse → resolve → typecheck
	prog, _, _, derr := load.ProgramEx(".", false)
	if derr != nil {
		msg := "parse/resolve: " + derr.Error()
		printJSON(fixStop(contract.ToolVersion, kind, "verify", msg))
		fmt.Fprintln(os.Stderr, "STOP——编辑后验证失败:", msg)

		return 1
	}

	diags, _ := pipeline.CheckStmts(prog)
	if len(diags) > 0 {
		msg := "typecheck: " + pipeline.FormatDiags(diags)
		printJSON(fixStop(contract.ToolVersion, kind, "verify", msg))
		fmt.Fprintln(os.Stderr, "STOP——编辑后验证失败:", msg)

		return 1
	}

	if asJSON {
		printJSON(struct {
			SchemaVersion string `json:"schemaVersion"`
			ToolVersion   string `json:"toolVersion"`
			Kind          string `json:"kind"`
			Success       bool   `json:"success"`
			Applied       bool   `json:"applied"`
			EditCount     int    `json:"editCount"`
		}{contract.SchemaVersion, contract.ToolVersion, kind, true, true, len(ws.Edits)})
	} else {
		fmt.Printf("safe fix 完成（%d 处编辑），验证通过\n", len(ws.Edits))
	}

	return 0
}

// fixStop fix 失败的结构化负载.
func fixStop(toolVersion, kind, stage, msg string) map[string]any {
	return map[string]any{
		"schemaVersion": contract.SchemaVersion,
		"toolVersion":   toolVersion,
		"kind":          kind,
		"success":       false,
		"stage":         stage,
		"error":         msg,
	}
}

// runRefactor 语义级重构（v0.7 P0）：rename / organize-imports / safe-remove。
// 一律输出契约 JSON（编辑集本身是结构化产物）；默认 dry-run 只报编辑，
// --apply 落盘（sha256 前置校验）并走验证闭环 parse → resolve → typecheck。
func runRefactor(args []string) int {
	if len(args) == 0 {
		printJSON(contract.ErrorEnvelope("refactor", "缺少子命令：rename|organize-imports|safe-remove"))
		return 2
	}

	sub := args[0]

	kind := "refactor." + sub
	switch sub {
	case "rename", "organize-imports", "safe-remove":
	default:
		printJSON(contract.ErrorEnvelope(kind, "未知子命令 "+sub))
		return 2
	}

	symbol, to := "", ""

	apply := hasFlag(args, "--apply")
	for i, a := range args {
		switch a {
		case "--symbol":
			if i+1 < len(args) {
				symbol = args[i+1]
			}
		case "--to":
			if i+1 < len(args) {
				to = args[i+1]
			}
		}
	}

	prog, _, files, err := load.ProgramEx(".", false)
	if err != nil {
		printJSON(contract.ErrorEnvelope(kind, "装载失败: "+err.Error()))
		return 1
	}
	// 每文件源码（token 扫描按声明所在文件取）+ 哈希前置
	byFile := map[string]string{}
	def := ""

	for _, fi := range files {
		bs, rerr := os.ReadFile(filepath.Join(".", filepath.FromSlash(fi.Path)))
		if rerr != nil {
			printJSON(contract.ErrorEnvelope(kind, "读 "+fi.Path+": "+rerr.Error()))
			return 1
		}

		byFile[fi.Path] = string(bs)
		if fi.Path == entryFile || def == "" {
			def = string(bs)
		}
	}

	srcs := refactor.Sources{Default: def, ByFile: byFile}

	a, aerr := pipeline.AnalyzeStmts(prog)
	if aerr != nil {
		printJSON(contract.ErrorEnvelope(kind, pipeline.FormatDiags(pipeline.Diagnostics(aerr))))
		return 1
	}

	var edits []edit.TextEdit

	symJSON := map[string]any{}

	switch sub {
	case "rename", "safe-remove":
		target, terr := refactor.ResolveTarget(a.Res, symbol)
		if terr != nil {
			printJSON(contract.ErrorEnvelope(kind, terr.Error()))
			return 1
		}

		symJSON = map[string]any{"id": int(target.ID), "name": target.Name, "kind": target.Kind}

		if sub == "rename" {
			if to == "" {
				printJSON(contract.ErrorEnvelope(kind, "缺少 --to 参数"))
				return 2
			}

			edits, err = refactor.Rename(prog, srcs, a.Res, a.HIR, target, to)
		} else {
			chk, cerr := refactor.SafeRemoveCheck(prog, a.Res, target)
			if cerr != nil {
				printJSON(contract.ErrorEnvelope(kind, cerr.Error()))
				return 1
			}

			if !chk.Removable {
				printJSON(map[string]any{
					"schemaVersion": contract.SchemaVersion,
					"toolVersion":   contract.ToolVersion,
					"kind":          kind,
					"removable":     false,
					"reason":        chk.Reason,
					"refSites":      chk.RefSites,
					"symbol":        symJSON,
				})

				return 1
			}

			edits, err = refactor.SafeRemoveEdits(prog, srcs, a.Res, target)
		}
	case "organize-imports":
		// 每文件独立整理；变化文件以整文件替换编辑表达
		var paths []string
		for p := range byFile {
			paths = append(paths, p)
		}

		sort.Strings(paths)

		for _, p := range paths {
			out, changed, oerr := refactor.OrganizeImports(byFile[p])
			if oerr != nil {
				printJSON(contract.ErrorEnvelope(kind, p+": "+oerr.Error()))
				return 1
			}

			if changed {
				edits = append(edits, edit.TextEdit{
					File:        p,
					StartOffset: 0,
					EndOffset:   len([]rune(byFile[p])),
					Replacement: out,
				})
			}
		}
	}

	if err != nil {
		printJSON(contract.ErrorEnvelope(kind, err.Error()))
		return 1
	}

	// 前置哈希：编辑涉及的全部文件
	pre := map[string]bool{}

	var preconds []edit.Precondition

	for _, e := range edits {
		if pre[e.File] {
			continue
		}

		pre[e.File] = true
		preconds = append(preconds, edit.Precondition{
			File:     e.File,
			FileHash: edit.HashBytes([]byte(srcs.Source(e.File))),
		})
	}

	ws := edit.WorkspaceEdit{Edits: edits, Preconditions: preconds}

	report := func(applied bool) int {
		printJSON(map[string]any{
			"schemaVersion": contract.SchemaVersion,
			"toolVersion":   contract.ToolVersion,
			"kind":          kind,
			"applied":       applied,
			"editCount":     len(edits),
			"symbol":        symJSON,
			"workspaceEdit": ws,
		})

		return 0
	}
	if !apply {
		return report(false)
	}

	if err := edit.Apply(ws, "."); err != nil {
		printJSON(fixStop(contract.ToolVersion, kind, "apply", err.Error()))
		fmt.Fprintln(os.Stderr, "STOP——应用编辑失败:", err)

		return 1
	}
	// 验证闭环：parse → resolve → typecheck
	vprog, _, _, derr := load.ProgramEx(".", false)
	if derr != nil {
		printJSON(fixStop(contract.ToolVersion, kind, "verify", derr.Error()))
		fmt.Fprintln(os.Stderr, "STOP——重构后验证失败:", derr)

		return 1
	}

	if diags, _ := pipeline.CheckStmts(vprog); len(diags) > 0 {
		msg := pipeline.FormatDiags(diags)
		printJSON(fixStop(contract.ToolVersion, kind, "verify", msg))
		fmt.Fprintln(os.Stderr, "STOP——重构后验证失败:", msg)

		return 1
	}

	return report(true)
}

// runVerify 全管线验证（v0.8）：parse → resolve → typecheck → hir → ir → effect →
// compile → execute → differential。任一阶段失败即停；--json 输出结构化阶段明细。
func runVerify(args []string) int {
	kind := "verify"
	asJSON := hasFlag(args, "--json")

	r := conformance.VerifyDir(".")
	if asJSON {
		printJSON(struct {
			SchemaVersion string              `json:"schemaVersion"`
			ToolVersion   string              `json:"toolVersion"`
			Kind          string              `json:"kind"`
			Success       bool                `json:"success"`
			Stages        []conformance.Stage `json:"stages"`
			StdoutBytes   int                 `json:"stdoutBytes,omitempty"`
			ExitCode      int                 `json:"exitCode,omitempty"`
		}{contract.SchemaVersion, contract.ToolVersion, kind, r.Success, r.Stages, r.StdoutBytes, r.ExitCode})
	} else {
		for _, st := range r.Stages {
			mark := "ok  "
			if !st.OK {
				mark = "FAIL"
			} else if st.Skipped {
				mark = "SKIP"
			}

			line := fmt.Sprintf("[%s] %s", mark, st.Name)
			if st.Note != "" {
				line += " — " + st.Note
			}

			fmt.Println(line)
		}

		if r.Success {
			fmt.Println("验证通过：四路执行一致（AST Oracle / IR 解释器 / →Go / →JS）")
		}
	}

	if !r.Success {
		return 1
	}

	return 0
}
