using System.Text;
using Lang.Cli;

namespace Lang.Cli.Tests;

/// <summary>
/// inspect / dump / refactor 命令 E2E（临时目录夹具 + 真 JSON/文本断言 +
/// dump 确定性）。Console 重定向与 <c>[Collection]</c> 串行。
/// </summary>
[Collection("cli-console")]
public class QueryCmdTests
{
    private static (int Code, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        TextWriter oldOut = Console.Out;
        TextWriter oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int code = CliApp.CliMain(args.ToList());
            // 归一化换行（测试重定向下 WriteLine 产 \r\n；断言按 \n 口径）
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static string WriteModule(string tag, params (string Name, string Source)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lang_cs_cli_{Environment.ProcessId}_{tag}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        foreach (var (name, source) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), source);
        }

        return dir;
    }

    /// <summary>调用链夹具：a→b→c，main→a。</summary>
    private static readonly string CallChain =
        "fn a() {\n    b()\n}\n\nfn b() {\n    c()\n}\n\nfn c() {\n    return 0\n}\n\n"
        + "fn main() {\n    a()\n}\n";

    // ———— inspect ————

    [Fact]
    public void Inspect_symbols_lists_non_builtin_symbols_in_canonical_order()
    {
        string dir = WriteModule(
            "syms",
            ("main.lang-go", "struct P {\n    x: int\n}\n\nfn helper(v: int): int {\n    return v\n}\n\nfn main() {\n    println(helper(1))\n}\n"));
        try
        {
            var (code, stdout, stderr) = RunCli("inspect", "symbols", dir);
            Assert.Equal(0, code);
            Assert.Equal("", stderr);
            // 文本形：id\tkind\tname\ttype + 计数行
            Assert.Contains("\tfunction\thelper\tfn(int): int", stdout);
            Assert.Contains("\tstruct\tP", stdout);
            Assert.Contains("\tparameter\tv\tint", stdout);
            Assert.Matches(@"\n\d+ 个符号\n$", stdout);
            var (code2, json, _) = RunCli("inspect", "symbols", dir, "--json");
            Assert.Equal(0, code2);
            Assert.Contains("\"kind\": \"inspect.symbols\"", json);
            Assert.Contains("\"symbolId\": ", json);
            Assert.Contains("\"name\": \"helper\"", json);
            Assert.Contains("\"kind\": \"function\"", json);
            Assert.Contains("\"arity\": 1", json);
            Assert.Contains("\"module\": \"main.lang-go\"", json);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inspect_refs_classifies_read_write_call_type()
    {
        string dir = WriteModule(
            "refs",
            ("main.lang-go",
                "struct P {\n    x: int\n}\n\nfn helper() {\n    return 0\n}\n\nfn main() {\n"
                + "    let p = P { x: 1 }\n    var y = p.x\n    y = 2\n    println(y)\n    helper()\n}\n"));
        try
        {
            // y：写（y = 2）+ 读（println(y)）
            var (code, stdout, _) = RunCli("inspect", "refs", "y", dir);
            Assert.Equal(0, code);
            Assert.Contains("write", stdout);
            Assert.Contains("read", stdout);
            Assert.Contains("2 处引用", stdout);

            // helper：调用点
            var (code2, stdout2, _) = RunCli("inspect", "refs", "helper", dir);
            Assert.Equal(0, code2);
            Assert.Contains("call", stdout2);
            Assert.Contains("1 处引用", stdout2);

            // P：类型位置（StructLit）
            var (code3, stdout3, _) = RunCli("inspect", "refs", "P", dir);
            Assert.Equal(0, code3);
            Assert.Contains("type", stdout3);
            Assert.Contains("1 处引用", stdout3);

            // 未知名
            var (code4, _, stderr4) = RunCli("inspect", "refs", "nope", dir);
            Assert.Equal(1, code4);
            Assert.Contains("未知符号 nope", stderr4);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inspect_type_locates_innermost_expression()
    {
        string dir = WriteModule("typeq", ("main.lang-go", "fn main() {\n    let x = 1 + 2\n    println(x)\n}\n"));
        try
        {
            // 第 2 行 `    let x = 1 + 2`：col 13 = 字面量 1
            var (code, stdout, _) = RunCli("inspect", "type", dir, "--file", "main.lang-go", "--line", "2", "--column", "13");
            Assert.Equal(0, code);
            Assert.Contains("int_lit", stdout);
            Assert.Contains("\tint", stdout);

            // JSON 形
            var (code2, json, _) = RunCli("inspect", "type", dir, "--file", "main.lang-go", "--line", "2", "--column", "13", "--json");
            Assert.Equal(0, code2);
            Assert.Contains("\"kind\": \"inspect.type\"", json);
            Assert.Contains("\"kind\": \"int_lit\"", json);

            // 缺参数 → 2
            var (code3, _, stderr3) = RunCli("inspect", "type", dir);
            Assert.Equal(2, code3);
            Assert.Contains("缺少 --file/--line/--column 参数", stderr3);

            // 未知文件 → 1
            var (code4, _, stderr4) = RunCli("inspect", "type", dir, "--file", "nope.lang-go", "--line", "1", "--column", "1");
            Assert.Equal(1, code4);
            Assert.Contains("未知文件 nope.lang-go", stderr4);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inspect_effects_solves_direct_effects()
    {
        string dir = WriteModule(
            "eff",
            ("main.lang-go", "fn pure() {\n    return 1\n}\n\nfn main() {\n    println(pure())\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("inspect", "effects", dir);
            Assert.Equal(0, code);
            Assert.Contains("main\tIO", stdout);
            Assert.Contains("pure\tPURE", stdout);
            Assert.Contains("2 个函数", stdout);

            var (code2, json, _) = RunCli("inspect", "effects", dir, "--json");
            Assert.Equal(0, code2);
            Assert.Contains("\"kind\": \"inspect.effects\"", json);
            Assert.Contains("\"effects\": [\n        \"IO\"\n      ]", json);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inspect_callers_callees_impact_walk_call_edges()
    {
        string dir = WriteModule("chain", ("main.lang-go", CallChain));
        try
        {
            var (code, stdout, _) = RunCli("inspect", "callers", "c", dir);
            Assert.Equal(0, code);
            Assert.Contains("a\n", stdout);
            Assert.Contains("b\n", stdout);
            Assert.Contains("3 个调用方", stdout);

            var (code2, stdout2, _) = RunCli("inspect", "callees", "a", dir);
            Assert.Equal(0, code2);
            Assert.Contains("2 个被调方", stdout2);

            // 限深：depth 1 只到直接被调方
            var (code3, stdout3, _) = RunCli("inspect", "callees", "a", dir, "--depth", "1");
            Assert.Equal(0, code3);
            Assert.Contains("1 个被调方", stdout3);

            var (code4, stdout4, _) = RunCli("inspect", "impact", "c", dir);
            Assert.Equal(0, code4);
            Assert.Contains("direct: [\"b\"]", stdout4);
            Assert.Contains("transitive: [\"a\", \"b\", \"main\"]", stdout4);

            var (code5, json, _) = RunCli("inspect", "callers", "c", dir, "--json");
            Assert.Equal(0, code5);
            Assert.Contains("\"kind\": \"inspect.callers\"", json);
            Assert.Contains("\"callers\": [", json);
            Assert.Contains("\"name\": \"a\"", json);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inspect_subcommand_errors_and_dirty_module()
    {
        string dir = WriteModule("insperr", ("main.lang-go", "fn main() {}\n"));
        try
        {
            var (code, json, _) = RunCli("inspect");
            Assert.Equal(2, code);
            Assert.Contains("\"kind\": \"inspect\"", json);
            Assert.Contains("缺少子命令", json);

            var (code2, json2, _) = RunCli("inspect", "bogus", dir);
            Assert.Equal(2, code2);
            Assert.Contains("未知子命令 bogus", json2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        string dirty = WriteModule("inspdirty", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, _, stderr) = RunCli("inspect", "symbols", dirty);
            Assert.Equal(1, code);
            Assert.Contains("条诊断，语义面不可查询", stderr);
        }
        finally
        {
            Directory.Delete(dirty, true);
        }
    }

    // ———— dump ————

    [Fact]
    public void Dump_ast_and_ir_are_deterministic()
    {
        string dir = WriteModule("dumpdet", ("main.lang-go", "fn main() {\n    let x = 1 + 2\n    println(x)\n}\n"));
        try
        {
            var (codeA1, a1, _) = RunCli("dump", "ast", dir);
            var (_, a2, _) = RunCli("dump", "ast", dir);
            Assert.Equal(0, codeA1);
            Assert.Equal(a1, a2);
            Assert.Contains("fn main", a1);
            Assert.Contains("(Add int 1 int 2)", a1);

            var (codeI1, i1, _) = RunCli("dump", "ir", dir);
            var (_, i2, _) = RunCli("dump", "ir", dir);
            Assert.Equal(0, codeI1);
            Assert.Equal(i1, i2);
            Assert.StartsWith("entry init=", i1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Dump_ir_requires_clean_program()
    {
        string dir = WriteModule("dumpdirty", ("main.lang-go", "fn main() {\n    let a = b\n}\n"));
        try
        {
            var (code, _, stderr) = RunCli("dump", "ir", dir);
            Assert.Equal(1, code);
            Assert.Contains("条诊断，ir dump 需要干净程序（先 check）", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Dump_subcommand_errors_exit_two()
    {
        string dir = WriteModule("dumperr", ("main.lang-go", "fn main() {}\n"));
        try
        {
            var (code, json, _) = RunCli("dump");
            Assert.Equal(2, code);
            Assert.Contains("缺少子命令：ast|ir", json);

            var (code2, json2, _) = RunCli("dump", "hir", dir);
            Assert.Equal(2, code2);
            Assert.Contains("未知子命令 hir", json2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ———— refactor ————

    [Fact]
    public void Refactor_rename_dry_run_reports_plan_and_leaves_files()
    {
        string dir = WriteModule(
            "rndry",
            ("main.lang-go", "import \"./util\"\n\nfn main() {\n    println(hello())\n}\n"),
            ("util.lang-go", "fn hello() {\n    return \"hi\"\n}\n"));
        try
        {
            var before = File.ReadAllText(Path.Combine(dir, "util.lang-go"));
            var (code, json, _) = RunCli("refactor", "rename", "--symbol", "hello", "--to", "greet", dir, "--json");
            Assert.Equal(0, code);
            Assert.Contains("\"kind\": \"refactor.rename\"", json);
            Assert.Contains("\"applied\": false", json);
            Assert.Contains("\"editCount\": 2", json);
            Assert.Contains("\"name\": \"hello\"", json);
            Assert.Contains("\"file\": \"util.lang-go\"", json);
            Assert.Contains("\"file\": \"main.lang-go\"", json);
            Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "util.lang-go")));

            // 文本形 dry-run
            var (code2, stdout2, _) = RunCli("refactor", "rename", "--symbol", "hello", "--to", "greet", dir);
            Assert.Equal(0, code2);
            Assert.Contains("重构计划（dry-run，--apply 落盘）：2 处编辑", stdout2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Refactor_rename_apply_rewrites_files_and_still_runs()
    {
        string dir = WriteModule(
            "rnapply",
            ("main.lang-go", "import \"./util\"\n\nfn main() {\n    println(hello())\n}\n"),
            ("util.lang-go", "fn hello() {\n    return \"hi\"\n}\n"));
        try
        {
            var (code, json, _) = RunCli("refactor", "rename", "--symbol", "hello", "--to", "greet", "--apply", dir, "--json");
            Assert.Equal(0, code);
            Assert.Contains("\"applied\": true", json);
            Assert.Contains("\"writtenFiles\": 2", json);
            Assert.Contains("greet", File.ReadAllText(Path.Combine(dir, "util.lang-go")));
            Assert.Contains("greet", File.ReadAllText(Path.Combine(dir, "main.lang-go")));

            // 闭环后程序仍可执行
            var (runCode, runStdout, _) = RunCli("run", dir);
            Assert.Equal(0, runCode);
            Assert.Equal("hi\n", runStdout);

            // 缺 --to → 1
            var (code2, _, stderr2) = RunCli("refactor", "rename", "--symbol", "greet", dir);
            Assert.Equal(1, code2);
            Assert.Contains("缺少 --to 参数", stderr2);

            // 未知符号 → 1
            var (code3, _, stderr3) = RunCli("refactor", "rename", "--symbol", "nope", "--to", "x", dir);
            Assert.Equal(1, code3);
            Assert.Contains("未知符号 nope", stderr3);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Refactor_organize_imports_sorts_and_dedupes()
    {
        string dir = WriteModule(
            "orgimp",
            ("main.lang-go", "import \"./zeta\"\nimport \"./alpha\"\nimport \"./zeta\"\n\nfn main() {\n    println(a() + z())\n}\n"),
            ("alpha.lang-go", "fn a() {\n    return \"A\"\n}\n"),
            ("zeta.lang-go", "fn z() {\n    return \"Z\"\n}\n"));
        try
        {
            var (code, stdout, _) = RunCli("refactor", "organize-imports", dir);
            Assert.Equal(0, code);
            Assert.Contains("1 处编辑", stdout);

            Assert.Equal(0, RunCli("refactor", "organize-imports", "--apply", dir).Code);
            var text = File.ReadAllText(Path.Combine(dir, "main.lang-go"));
            var alphaIdx = text.IndexOf("import \"./alpha\"", StringComparison.Ordinal);
            var zetaIdx = text.IndexOf("import \"./zeta\"", StringComparison.Ordinal);
            Assert.True(alphaIdx >= 0 && zetaIdx > alphaIdx, $"import 未排序:\n{text}");
            // 去重保首现：zeta 只剩一条
            Assert.Equal(1, CountOccurrences(text, "import \"./zeta\""));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    [Fact]
    public void Refactor_safe_remove_reports_evidence_for_used_symbols()
    {
        string dir = WriteModule(
            "rmused",
            ("main.lang-go", "import \"./util\"\n\nfn main() {\n    println(hello())\n}\n"),
            ("util.lang-go", "fn hello() {\n    return \"hi\"\n}\n"));
        try
        {
            var (code, json, _) = RunCli("refactor", "safe-remove", "--symbol", "hello", dir, "--json");
            Assert.Equal(1, code);
            Assert.Contains("\"removable\": false", json);
            Assert.Contains("\"reason\": \"1 处引用\"", json);
            Assert.Contains("main.lang-go", json);

            var (code2, _, stderr2) = RunCli("refactor", "safe-remove", "--symbol", "hello", dir);
            Assert.Equal(1, code2);
            Assert.Contains("不可删：", stderr2);
            Assert.Contains("引用点 main.lang-go:", stderr2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Refactor_safe_remove_unused_function_applies_cleanly()
    {
        string dir = WriteModule(
            "rmunused",
            ("util.lang-go", "fn unusedFn() {\n    return 1\n}\n\nfn kept() {\n    return 2\n}\n"),
            ("main.lang-go", "import \"./util\"\n\nfn main() {\n    println(kept())\n}\n"));
        try
        {
            var (code, json, _) = RunCli("refactor", "safe-remove", "--symbol", "unusedFn", dir, "--json");
            Assert.Equal(0, code);
            Assert.Contains("\"applied\": false", json);
            Assert.Contains("\"editCount\": 1", json);

            Assert.Equal(0, RunCli("refactor", "safe-remove", "--symbol", "unusedFn", "--apply", dir).Code);
            var text = File.ReadAllText(Path.Combine(dir, "util.lang-go"));
            Assert.DoesNotContain("unusedFn", text);
            Assert.Contains("fn kept", text);

            var (runCode, runStdout, _) = RunCli("run", dir);
            Assert.Equal(0, runCode);
            Assert.Equal("2\n", runStdout);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Refactor_subcommand_errors_exit_two()
    {
        string dir = WriteModule("referr", ("main.lang-go", "fn main() {}\n"));
        try
        {
            var (code, json, _) = RunCli("refactor");
            Assert.Equal(2, code);
            Assert.Contains("缺少子命令：rename|organize-imports|safe-remove", json);

            var (code2, json2, _) = RunCli("refactor", "bogus", dir);
            Assert.Equal(2, code2);
            Assert.Contains("未知子命令 bogus", json2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
