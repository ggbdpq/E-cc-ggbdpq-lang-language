package main

// CLI 实现：run/test/REPL
// 路径安全（v0.1）：不接收任何路径参数——ling run 固定运行当前目录的 main.ling，
// ling test 固定扫描当前目录。文件输入无 ../ 穿越面。

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"ling/internal/format"
	"ling/internal/gen"
	"ling/internal/genjs"
	"ling/internal/interp"
	"ling/internal/load"
	"ling/internal/syntax"
)

const version = "0.1.0"

// entryFile 固定入口文件名：与 Go「每目录一个 main」的习惯一致
const entryFile = "main.ling"

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
		return runCheck()
	case "build":
		target := "go"
		if len(args) >= 2 {
			target = args[1]
		}
		return runBuild(target)
	case "version", "--version", "-v":
		fmt.Println("ling", version)
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
	fmt.Print(`ling ` + version + ` — 个人三域语言（v0 解释器）

用法:
  ling          启动 REPL（:quit 退出，:help 帮助）
  ling run      运行当前目录的 main.ling
  ling test     运行当前目录 *_test.ling 里的 test* 函数
  ling version  版本

文档: docs/spec.md（语言规范）  docs/decisions.md（设计决策）
`)
}

// runBuild 静态检查 + 产物写入 out/（Q21）：ling build → out/main.go，
// ling build js → out/main.mjs。写路径为常量，无用户输入参与。
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
	switch target {
	case "go":
		src, err := gen.Transpile(prog)
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
		src, err := genjs.TranspileJS(prog)
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

// runFmt 就地格式化当前目录全部 .ling（不递归），打印改动的文件
func runFmt() int {
	entries, err := os.ReadDir(".")
	if err != nil {
		fmt.Fprintln(os.Stderr, "读目录失败:", err)
		return 2
	}
	changed := 0
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".ling") {
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

// runCheck 静态检查（decisions #49）：复用 →JS 转译器的前置检查
// （match 穷尽、? 约束、参数注解、未定义名），产物丢弃
func runCheck() int {
	prog, _, err := load.Program(".", false)
	if err != nil {
		fmt.Fprintln(os.Stderr, "装载失败:", err)
		return 1
	}
	if _, err := genjs.TranspileJS(prog); err != nil {
		fmt.Fprintln(os.Stderr, "检查未通过:", err)
		return 1
	}
	fmt.Println("检查通过")
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

// runTests 装载目录即包（实现文件 + *_test.ling，import 由装载器展开）后依序执行 test* 函数
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
		fmt.Println("未发现 *_test.ling")
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
	fmt.Printf("ling v%s — :help 帮助, :quit 退出\n", version)
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 64*1024), 1024*1024)
	var buf strings.Builder
	prompt := "ling> "
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
				fmt.Println("输入 ling 源码回车执行；未闭合的块会进入续行模式；:quit 退出")
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
			prompt = "ling> "
			continue
		}
		val, has, err := it.EvalChunk(prog)
		if err != nil {
			fmt.Println("错误:", err)
		} else if has {
			fmt.Println("=>", interp.Fmt(val))
		}
		buf.Reset()
		prompt = "ling> "
	}
}
