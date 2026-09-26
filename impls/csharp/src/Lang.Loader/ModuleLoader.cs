using System.Text;

namespace Lang.Loader;

/// <summary>一条 import 声明：源码原文路径 + 所在行号（1 基，错误消息用）。</summary>
public sealed record ImportRef(string Path, int Line);

/// <summary>合并程序中的一个文件：模块根相对路径（<c>/</c> 分隔）+ 源码。</summary>
public sealed record LoadedFile(string RelPath, string Source);

/// <summary>文件装载信息（依赖查询用）：装载序记录，Imports 为源码原文依出现序。
/// 命名避开 System.IO.FileInfo。</summary>
public sealed record LoadedFileInfo(string Path, IReadOnlyList<string> Imports, int Order);

/// <summary>装载产物：合并序文件列表 + 文件级信息 + 测试文件列表。</summary>
public sealed record LoadedProgram(
    IReadOnlyList<LoadedFile> Files,
    IReadOnlyList<LoadedFileInfo> Infos,
    IReadOnlyList<string> TestFiles);

/// <summary>装载错误（消息文本已含位置前缀；输出到 stderr 并以非零码终止——
/// 装载错误不是 Diagnostic）。</summary>
public sealed record LoadError
{
    public LoadError(string message)
    {
        Message = message;
    }

    public string Message { get; }

    public override string ToString() => Message;
}

/// <summary>import 提取委托：入参（根相对路径, 源码）→ import 列表。
/// 本层不做语法分析；解析器接线后由语法层提供实现（测试用注入桩）。</summary>
public delegate IReadOnlyList<ImportRef> ImportsOf(string relPath, string source);

/// <summary>
/// 模块装载器：目录即包 + import 展开（MODULE-ROOT-001 .. MODULE-TEST-007）。
/// 行为证据（只读）：<c>impls/go/internal/load/load.go</c> 与
/// <c>impls/typescript/src/loader/loader.ts</c>。错误消息文本与参考实现对齐。
///
/// 合并语义（MODULE-MERGE-003）：同目录文件按文件名序；目录装载先完成全部
/// 文件的读取与 done 标记，再按序展开各自 import——被导入内容拼接在导入文件
/// 之前（同目录 import 目标因预读已 done 而跳过，保持文件名序，与参考实现
/// 一致）。文件级输出以「导入先于导入文件」排列；语句级 splice（import 位于
/// 文件中部时）在解析器接线后于语句流上执行。
/// </summary>
public static class ModuleLoader
{
    /// <summary>装载 <paramref name="dir"/> 为一个合并程序（受控错误，不抛异常）。</summary>
    /// <param name="dir">模块根目录（目录即包）。</param>
    /// <param name="includeTests">MODULE-TEST-007：为 true 时根目录 <c>*_test.lang-go</c>
    /// 追加在主程序之后（文件名序），其 import 同样展开；普通 run 传 false。</param>
    /// <param name="importsOf">import 提取委托。</param>
    public static bool TryLoadProgram(string dir, bool includeTests, ImportsOf importsOf, out LoadedProgram? program, out LoadError? error)
    {
        program = null;
        string root;
        try
        {
            root = Path.GetFullPath(dir);
        }
        catch (Exception ex)
        {
            error = new LoadError($"{dir}: {ex.Message}");
            return false;
        }

        var loader = new LoaderState(root, importsOf);
        LoadError? err = loader.LoadDir(root);
        if (err is null && includeTests)
        {
            err = loader.LoadTestFiles(root);
        }

        if (err is not null)
        {
            error = err;
            return false;
        }

        program = new LoadedProgram(loader.Files, loader.Infos, loader.TestFiles);
        error = null;
        return true;
    }

    private sealed class LoaderState
    {
        private readonly string _root;
        private readonly ImportsOf _importsOf;
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);

        public LoaderState(string root, ImportsOf importsOf)
        {
            _root = root;
            _importsOf = importsOf;
        }

        public List<LoadedFile> Files { get; } = new();

        public List<LoadedFileInfo> Infos { get; } = new();

        public List<string> TestFiles { get; } = new();

        /// <summary>装载目录包（MODULE-ROOT-001：目录即包）。两阶段：先读完目录内
        /// 全部非测试 .lang-go（文件名序）并标记 done，再按序展开各文件 import、
        /// 随后落该文件自身。</summary>
        public LoadError? LoadDir(string dir)
        {
            LoadError? listErr = ListLangGo(dir, tests: false, out List<string> names);
            if (listErr is not null)
            {
                return listErr;
            }

            var raws = new List<RawFile>(names.Count);
            foreach (string name in names)
            {
                string ap = Path.Combine(dir, name);
                (RawFile? raw, LoadError? err) = LoadFile(ap);
                if (err is not null)
                {
                    return err;
                }

                // import 解析键不含扩展名（"./x" → <dir>/x）：
                // 同目录文件既被根装载又被 import 引用时不得双载
                _done.Add(ap);
                _done.Add(WithoutLangGoExt(ap));
                raws.Add(raw!);
            }

            foreach (RawFile raw in raws)
            {
                LoadError? err = Expand(raw.Imports, dir);
                if (err is not null)
                {
                    return err;
                }

                Files.Add(new LoadedFile(raw.Rel, raw.Source));
            }

            return null;
        }

        /// <summary>includeTests：根目录 *_test.lang-go 追加在主程序后（文件名序），
        /// 其 import 同样展开（MODULE-TEST-007）。</summary>
        public LoadError? LoadTestFiles(string root)
        {
            LoadError? listErr = ListLangGo(root, tests: true, out List<string> names);
            if (listErr is not null)
            {
                return listErr;
            }

            foreach (string name in names)
            {
                string ap = Path.Combine(root, name);
                (RawFile? raw, LoadError? err) = LoadFile(ap);
                if (err is not null)
                {
                    return err;
                }

                err = Expand(raw!.Imports, root);
                if (err is not null)
                {
                    return err;
                }

                TestFiles.Add(raw.Rel);
                Files.Add(new LoadedFile(raw.Rel, raw.Source));
            }

            return null;
        }

        /// <summary>读单个文件并记录 LoadedFileInfo。</summary>
        private (RawFile? File, LoadError? Error) LoadFile(string ap)
        {
            string source;
            try
            {
                source = File.ReadAllText(ap, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return (null, new LoadError($"{FileNameOf(ap)}: {ex.Message}"));
            }

            string rel = RelToRoot(_root, ap);
            IReadOnlyList<ImportRef> imports = _importsOf(rel, source);
            int order = Infos.Count;
            var paths = new List<string>(imports.Count);
            foreach (ImportRef imp in imports)
            {
                paths.Add(imp.Path);
            }

            Infos.Add(new LoadedFileInfo(rel, paths, order));
            return (new RawFile(rel, source, imports), null);
        }

        /// <summary>展开 import 序列（MODULE-IMPORT-002/MERGE-003/CYCLE-004）：
        /// 相对 fromDir 解析 "./x" → x/ 目录或 x.lang-go 文件，越根报错，
        /// done 跳过（菱形），inProgress 报循环。</summary>
        private LoadError? Expand(IReadOnlyList<ImportRef> imports, string fromDir)
        {
            foreach (ImportRef imp in imports)
            {
                if (!imp.Path.StartsWith("./", StringComparison.Ordinal))
                {
                    return new LoadError(
                        $"第{imp.Line}行: import 仅支持 \"./相对路径\"（得到 \"{imp.Path}\"）");
                }

                string target;
                try
                {
                    // 词法规范化（Go filepath.Clean(Join(fromDir, rel)) 等价，
                    // 不做符号链接解析）；GetFullPath 对绝对入参是纯词法折叠
                    target = Path.GetFullPath(Path.Combine(fromDir, imp.Path));
                }
                catch (Exception ex)
                {
                    return new LoadError($"{imp.Path}: {ex.Message}");
                }

                string rel = RelToRoot(_root, target);
                if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal))
                {
                    return new LoadError($"第{imp.Line}行: import {imp.Path} 逃出模块根目录");
                }

                if (_done.Contains(target))
                {
                    continue; // 菱形导入（含同目录预读）：已装载
                }

                if (_inProgress.Contains(target))
                {
                    return new LoadError($"第{imp.Line}行: 循环 import {imp.Path}");
                }

                _inProgress.Add(target);
                LoadError? err;
                if (Directory.Exists(target))
                {
                    err = LoadDir(target);
                }
                else
                {
                    string fileAp = target + ".lang-go";
                    (RawFile? raw, LoadError? loadErr) = LoadFile(fileAp);
                    if (loadErr is not null)
                    {
                        return loadErr;
                    }

                    err = Expand(raw!.Imports, Path.GetDirectoryName(target) ?? target);
                    if (err is null)
                    {
                        Files.Add(new LoadedFile(raw.Rel, raw.Source));
                    }
                }

                if (err is not null)
                {
                    return err;
                }

                _done.Add(target);
                _inProgress.Remove(target);
            }

            return null;
        }
    }

    private sealed record RawFile(string Rel, string Source, IReadOnlyList<ImportRef> Imports);

    /// <summary>目录内 .lang-go 文件名（tests=true 取 *_test.lang-go，否则取其余），
    /// 文件名序（Ordinal）——不依赖 readdir 的磁盘序。</summary>
    private static LoadError? ListLangGo(string dir, bool tests, out List<string> names)
    {
        names = new List<string>();
        try
        {
            var info = new DirectoryInfo(dir);
            foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo)
                {
                    continue;
                }

                string name = entry.Name;
                if (!name.EndsWith(".lang-go", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.EndsWith("_test.lang-go", StringComparison.Ordinal) == tests)
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            return new LoadError($"{dir}: {ex.Message}");
        }

        names.Sort(StringComparer.Ordinal);
        return null;
    }

    /// <summary>去掉 .lang-go 尾扩展（Go strings.TrimSuffix 等价，从尾后缀截断）。</summary>
    private static string WithoutLangGoExt(string p) =>
        p.EndsWith(".lang-go", StringComparison.Ordinal) ? p[..^".lang-go".Length] : p;

    /// <summary>相对 root 的路径文本（<c>/</c> 分隔；root 自身为 <c>.</c>；
    /// 越出 root 以 <c>../</c> 开头）。双方均为绝对规范化路径。</summary>
    private static string RelToRoot(string root, string p)
    {
        string[] rc = SplitComponents(root);
        string[] pc = SplitComponents(p);
        int i = 0;
        while (i < rc.Length && i < pc.Length && string.Equals(rc[i], pc[i], StringComparison.Ordinal))
        {
            i++;
        }

        var parts = new List<string>();
        for (int k = i; k < rc.Length; k++)
        {
            parts.Add("..");
        }

        for (int k = i; k < pc.Length; k++)
        {
            parts.Add(pc[k]);
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    /// <summary>拆路径组件：Windows 盘根/UNC 根单独一段，其余按 \ / 分。</summary>
    private static string[] SplitComponents(string path)
    {
        string rootPart = Path.GetPathRoot(path) ?? "";
        string rest = path[rootPart.Length..];
        string[] segs = rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var all = new string[segs.Length + 1];
        all[0] = rootPart;
        segs.CopyTo(all, 1);
        return all;
    }

    private static string FileNameOf(string p) => Path.GetFileName(p);
}
