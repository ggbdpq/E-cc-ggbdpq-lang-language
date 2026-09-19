package main

// CLI 实现：run/test/REPL
// 路径安全（v0.1）：不接收任何路径参数——lang-go run 固定运行当前目录的 main.lang-go，
// lang-go test 固定扫描当前目录。文件输入无 ../ 穿越面。

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"lang-go/internal/bind"
	"lang-go/internal/contract"
	"lang-go/internal/format"
	"lang-go/internal/gen"
	"lang-go/internal/genjs"
	"lang-go/internal/interp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
)

func hasFlag(args []string, flag string) bool {
	for _, a := range args {
		if a == flag {
			return true
		}
	}
	return false
}

// printJSON 统一输出契约 JSON
func printJSON(v any) {
	bs, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		fmt.Fprintln(os.Stderr, "JSON 编码失败:", err)
		return
	}
	fmt.Println(string(bs))
}

const version = contract.ToolVersion // 单一版本源（specs/agent-protocol.md）

// entryFile 固定入口文件名：与 Go「每目录一个 main」的习惯一致
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
  lang-go fmt        就地格式化当前目录 .lang-go
  lang-go build      语义检查后写 out/main.go（build js 写 out/main.mjs）
  lang-go bind       生成 extern "go" 声明草案（bind.d/）
  lang-go version    版本

文档: go/docs/spec.md（语言规范）  go/docs/decisions.md（设计决策）
`)
}

// runBind 生成 extern "go" 声明草案到 stdout（decisions #61）：
// 包目录固定为当前目录的 bind.d/（常量路径），草案需人工审核
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

// runFmt 就地格式化当前目录全部 .lang-go（不递归），打印改动的文件
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
// 诊断带稳定码 E1xxx/E2xxx/E3xxx/E4xxx；不再依赖 →JS 转译器，decisions #49 被 #66 取代）
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

// runCheckJSON check --json：契约负载
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
func runInspect(args []string) int {
	if len(args) == 0 {
		printJSON(contract.ErrorEnvelope("inspect", "缺少子命令：symbols|refs|type|dependencies|effects"))
		return 2
	}
	sub := args[0]
	kind := "inspect." + sub
	if !hasFlag(args, "--json") {
		fmt.Fprintln(os.Stderr, "inspect 仅支持 --json 输出（v0.4）")
		return 2
	}
	// type 子命令参数
	file, line, col := "", 0, 0
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
	switch sub {
	case "symbols":
		printJSON(contract.SymbolsEnvelope(prog, a.Res, a.Ty))
	case "refs":
		printJSON(contract.ReferencesEnvelope(prog, a.Res))
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
	case "dependencies":
		printJSON(contract.DependenciesEnvelope(files))
	case "effects":
		printJSON(contract.EffectsEnvelope(contract.Effects(a.Prog)))
	default:
		printJSON(contract.ErrorEnvelope(kind, "未知子命令 "+sub))
		return 2
	}
	return 0
}

// runDump dump ast|hir|ir --json
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

// runTests 装载目录即包（实现文件 + *_test.lang-go，import 由装载器展开）后依序执行 test* 函数
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
