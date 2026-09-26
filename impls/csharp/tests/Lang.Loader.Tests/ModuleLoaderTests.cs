using Lang.Loader;

namespace Lang.Loader.Tests;

/// <summary>
/// 模块装载器（MODULE-ROOT-001..TEST-007）：目录即包、./x 解析、菱形去重、
/// 循环检测、根包含、文件名序/导入先序。期望全部手推自 specs/modules.md +
/// 参考实现语义（impls/go/internal/load/load.go）。import 提取走注入桩（本层
/// 不做语法分析）。
/// </summary>
public class ModuleLoaderTests
{
    /// <summary>测试用 import 提取桩：识别 import "./x" 形式的行（不做真语法分析）。</summary>
    private static IReadOnlyList<ImportRef> ScanImports(string rel, string src)
    {
        string[] lines = src.Split('\n');
        var list = new List<ImportRef>();
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (!t.StartsWith("import ", StringComparison.Ordinal))
            {
                continue;
            }

            string p = t["import ".Length..].Trim().Trim('"');
            list.Add(new ImportRef(p, i + 1));
        }

        return list;
    }

    private static bool Load(string dir, bool tests, out LoadedProgram? prog, out LoadError? err) =>
        ModuleLoader.TryLoadProgram(dir, tests, ScanImports, out prog, out err);

    private static List<string> Rels(LoadedProgram prog) => prog.Files.Select(f => f.RelPath).ToList();

    // —— 目录即包 + 文件名序 ——

    [Fact]
    public void Single_dir_files_sorted_by_name()
    {
        using var td = TempDir.Create("flat");
        td.Write("b.lang-go", "println(2)");
        td.Write("a.lang-go", "println(1)");
        td.Write("c.lang-go", "println(3)");
        td.Write("notes.txt", "非 .lang-go 不装载");
        Assert.True(Load(td.Path, false, out var prog, out var err));
        Assert.Null(err);
        Assert.Equal(["a.lang-go", "b.lang-go", "c.lang-go"], Rels(prog!));
        Assert.Equal("println(2)", prog!.Files[1].Source);
    }

    [Fact]
    public void Dir_package_imported_before_importer()
    {
        using var td = TempDir.Create("dirpkg");
        td.Write("main.lang-go", "import \"./lib\"\nprintln(1)");
        td.Write("lib/y.lang-go", "println(2)");
        td.Write("lib/x.lang-go", "println(3)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        // 被导入目录包先于导入者；目录内按文件名序
        Assert.Equal(["lib/x.lang-go", "lib/y.lang-go", "main.lang-go"], Rels(prog!));
        // FileInfo 依装载序：main 先读，lib 后读
        Assert.Equal("main.lang-go", prog!.Infos[0].Path);
        Assert.Equal(["./lib"], prog.Infos[0].Imports);
        Assert.Equal("lib/x.lang-go", prog.Infos[1].Path);
        Assert.Equal("lib/y.lang-go", prog.Infos[2].Path);
    }

    [Fact]
    public void Single_file_import_spliced_before_importer()
    {
        // import 目标文件不在已预读目录里（子目录直引文件；import 原文不带扩展名，
        // 解析时补 .lang-go——MODULE-IMPORT-002）
        using var td = TempDir.Create("fileimp");
        td.Write("main.lang-go", "import \"./sub/u\"\nprintln(1)");
        td.Write("sub/u.lang-go", "println(2)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal(["sub/u.lang-go", "main.lang-go"], Rels(prog!));
    }

    [Fact]
    public void Transitive_chain_deep_dependency_first()
    {
        using var td = TempDir.Create("chain");
        td.Write("main.lang-go", "import \"./a\"\nprintln(0)");
        td.Write("a/a.lang-go", "import \"./../b\"\nprintln(1)");
        td.Write("b/b.lang-go", "println(2)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal(["b/b.lang-go", "a/a.lang-go", "main.lang-go"], Rels(prog!));
    }

    // —— 菱形去重（MODULE-CYCLE-004）——

    [Fact]
    public void Diamond_import_deduplicated()
    {
        // main→a→c、main→b→c：c 只载入一次
        using var td = TempDir.Create("diamond");
        td.Write("main.lang-go", "import \"./a\"\nimport \"./b\"\nprintln(0)");
        td.Write("a.lang-go", "import \"./c\"\nprintln(1)");
        td.Write("b.lang-go", "import \"./c\"\nprintln(2)");
        td.Write("c.lang-go", "println(3)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        var all = Rels(prog!);
        Assert.Equal(1, all.Count(p => p == "c.lang-go"));
        Assert.Equal(["a.lang-go", "b.lang-go", "c.lang-go", "main.lang-go"], all);
        Assert.Equal(4, prog!.Infos.Count); // c 只读一次
    }

    [Fact]
    public void Same_dir_file_import_keeps_filename_order()
    {
        // 同目录文件被根目录预读（文件名序在前），import 目标 done → 跳过，
        // 合并序保持文件名序（MODULE-MERGE-003）
        using var td = TempDir.Create("samedir");
        td.Write("main.lang-go", "import \"./util\"\nprintln(1)");
        td.Write("util.lang-go", "println(2)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal(["main.lang-go", "util.lang-go"], Rels(prog!));
    }

    // —— 循环检测（MODULE-CYCLE-004）——

    [Fact]
    public void Dir_cycle_detected()
    {
        // lib → inner → 回指 lib（lib 尚在展开链上）；消息不带引号（参考实现 %s）
        using var td = TempDir.Create("cycle");
        td.Write("main.lang-go", "import \"./lib\"");
        td.Write("lib/s.lang-go", "import \"./inner\"");
        td.Write("lib/inner/i.lang-go", "import \"./..\"");
        Assert.False(Load(td.Path, false, out _, out var err));
        Assert.Equal("第1行: 循环 import ./..", err!.Message);
    }

    [Fact]
    public void File_cycle_detected()
    {
        // 经 import 直引文件链 a → b → a（目标目录未预读，真环）
        using var td = TempDir.Create("filecycle");
        td.Write("main.lang-go", "import \"./sub/a\"");
        td.Write("sub/a.lang-go", "import \"./b\"");
        td.Write("sub/b.lang-go", "import \"./a\"");
        Assert.False(Load(td.Path, false, out _, out var err));
        Assert.Equal("第1行: 循环 import ./a", err!.Message);
    }

    // —— 根包含与非相对 import（MODULE-IMPORT-002）——

    [Fact]
    public void Escape_root_rejected()
    {
        using var td = TempDir.Create("escape");
        string outside = Path.Combine(td.Path, "..", "outside.lang-go");
        File.WriteAllText(outside, "println(9)");
        td.Write("main.lang-go", "import \"./../outside\"");
        Assert.False(Load(td.Path, false, out _, out var err));
        Assert.Equal("第1行: import ./../outside 逃出模块根目录", err!.Message);
        File.Delete(outside);
    }

    [Fact]
    public void Non_relative_import_rejected()
    {
        using var td = TempDir.Create("nonrel");
        td.Write("main.lang-go", "import \"lib\"");
        Assert.False(Load(td.Path, false, out _, out var err));
        Assert.Equal("第1行: import 仅支持 \"./相对路径\"（得到 \"lib\"）", err!.Message);
    }

    [Fact]
    public void Missing_import_target_reports_file()
    {
        using var td = TempDir.Create("missing");
        td.Write("main.lang-go", "import \"./nope\"");
        Assert.False(Load(td.Path, false, out _, out var err));
        Assert.Contains("nope.lang-go", err!.Message);
    }

    [Fact]
    public void Relative_normalization_stays_inside_root()
    {
        // "./sub/../util" 规范化后仍在根内：util 已被根预读（done）→ 跳过不报错；
        // sub/keep 从未被 import，不装载
        using var td = TempDir.Create("normalize");
        td.Write("main.lang-go", "import \"./sub/../util\"\nprintln(1)");
        td.Write("sub/keep.lang-go", "println(2)");
        td.Write("util.lang-go", "println(3)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal(["main.lang-go", "util.lang-go"], Rels(prog!));
    }

    [Fact]
    public void Same_stem_file_preload_shadows_dir_target()
    {
        // 参考实现的去重键语义：根预读 x.lang-go 时登记去扩展名键 <root>/x，
        // 与目录包目标 <root>/x 撞键 → import "./x" 被当作已装载而跳过，
        // x/ 目录包不装载（x.lang-go 仅按文件名序出现在合并序里）
        using var td = TempDir.Create("dirwins");
        td.Write("main.lang-go", "import \"./x\"");
        td.Write("x/inner.lang-go", "println(1)");
        td.Write("x.lang-go", "println(2)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal(["main.lang-go", "x.lang-go"], Rels(prog!));
        Assert.Equal(2, prog!.Infos.Count);
    }

    // —— 测试文件装载（MODULE-TEST-007）——

    [Fact]
    public void Test_files_only_loaded_when_requested()
    {
        using var td = TempDir.Create("tests");
        td.Write("main.lang-go", "println(1)");
        td.Write("z_test.lang-go", "fn testZ() {}");
        td.Write("a_test.lang-go", "import \"./lib\"\nfn testA() {}");
        td.Write("lib/l.lang-go", "println(2)");

        Assert.True(Load(td.Path, false, out var run, out _));
        Assert.Equal(["main.lang-go"], Rels(run!));
        Assert.Empty(run!.TestFiles);

        Assert.True(Load(td.Path, true, out var t, out _));
        // 测试文件追加在主程序后（文件名序），其 import 同样展开
        Assert.Equal(
            ["main.lang-go", "lib/l.lang-go", "a_test.lang-go", "z_test.lang-go"],
            Rels(t!));
        Assert.Equal(["a_test.lang-go", "z_test.lang-go"], t!.TestFiles);
    }

    // —— imports 委托与路径口径 ——

    [Fact]
    public void Imports_delegate_receives_rel_path_and_source()
    {
        using var td = TempDir.Create("closure");
        td.Write("main.lang-go", "println(1)");
        var got = new List<(string Rel, string Src)>();
        Assert.True(ModuleLoader.TryLoadProgram(
            td.Path, false,
            (rel, src) =>
            {
                got.Add((rel, src));
                return [];
            },
            out var prog, out _));
        _ = prog;
        Assert.Equal([("main.lang-go", "println(1)")], got);
    }

    [Fact]
    public void Rel_paths_use_forward_slashes()
    {
        // Windows 上内部路径是反斜杠；对外 RelPath 必须是 / 分隔
        using var td = TempDir.Create("slashes");
        td.Write("main.lang-go", "import \"./p/q\"");
        td.Write("p/q.lang-go", "println(1)");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal(["p/q.lang-go", "main.lang-go"], Rels(prog!));
        Assert.DoesNotContain('\\', prog!.Files[0].RelPath);
    }

    [Fact]
    public void Astral_source_content_round_trips()
    {
        // astral 码点源码原样装载（字符串按 UTF-8 读回，不拆代理对）
        using var td = TempDir.Create("astral");
        td.Write("main.lang-go", "println(\"𝕏🦀\")");
        Assert.True(Load(td.Path, false, out var prog, out _));
        Assert.Equal("println(\"𝕏🦀\")", prog!.Files[0].Source);
    }

    /// <summary>临时目录夹具（进程内唯一编号，Dispose 时清理）。</summary>
    private sealed class TempDir : IDisposable
    {
        private static int _next;
        private const string Root = "lang-cs-loader-tests";

        private TempDir(string path)
        {
            PathDir = path;
        }

        public string PathDir { get; }

        public string Path => PathDir;

        public static TempDir Create(string tag)
        {
            string p = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{Root}-{tag}-{Environment.ProcessId}-{Interlocked.Increment(ref _next)}");
            if (Directory.Exists(p))
            {
                Directory.Delete(p, recursive: true);
            }

            Directory.CreateDirectory(p);
            return new TempDir(p);
        }

        public void Write(string rel, string content)
        {
            string ap = System.IO.Path.Combine(PathDir, rel);
            string? parent = System.IO.Path.GetDirectoryName(ap);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(ap, content);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(PathDir))
                {
                    Directory.Delete(PathDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 尽力清理；Windows 句柄延迟不作为测试失败
            }
        }
    }
}
