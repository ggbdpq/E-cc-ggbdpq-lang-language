package main

// CLI 实现：run/test/REPL
// 路径安全（v0.1）：不接收任何路径参数——ling run 固定运行当前目录的 main.ling，
// ling test 固定扫描当前目录。文件输入无 ../ 穿越面。

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"strings"

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
