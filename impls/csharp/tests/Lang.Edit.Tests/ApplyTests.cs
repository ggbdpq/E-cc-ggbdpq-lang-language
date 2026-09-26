using System.Text;
using Lang.Edit;
using Xunit;

namespace Lang.Edit.Tests;

/// <summary>apply 事务语义（行为证据：impls/rust/crates/lang-edit/src/{hash,apply}.rs，对齐其测试全集）。</summary>
public class ApplyTests
{
    private const string Src = "fn add(a: int, b: int): int {\n    return a + b\n}\n";

    private static Precondition Pre(string file, string text) => new(file, EditHash.Sha256Hex(text));

    private static TextEdit Edit(string file, int start, int end, string replacement) =>
        new(file, start, end, replacement);

    private static IReadOnlyDictionary<string, string> Map(params (string Path, string Text)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in pairs)
        {
            dict[path] = text;
        }
        return dict;
    }

    // —— SHA-256（NIST FIPS 180-4 标准向量） ——

    [Fact]
    public void Sha256_nist_vectors()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            EditHash.Sha256Hex(""));
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            EditHash.Sha256Hex("abc"));
        // 双块（448 bit 消息，跨块边界）
        Assert.Equal(
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
            EditHash.Sha256Hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"));
        // UTF-8 字节度量：甲 = E7 94 B2
        Assert.Equal(
            "33e3580c093111e1c4cac922872c40371f7896bb3fa4e23be5840bd1834fc622",
            EditHash.Sha256Hex("甲"));
    }

    [Fact]
    public void Single_edit_replaces_by_codepoints()
    {
        // "fn add" 的 "add"：码点 3..6
        var ws = new WorkspaceEdit([Edit("m.lang-go", 3, 6, "sum")], [Pre("m.lang-go", Src)]);
        var (applied, error) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws);
        Assert.Null(error);
        Assert.Equal("fn sum(a: int, b: int): int {\n    return a + b\n}\n", applied!["m.lang-go"]);
    }

    [Fact]
    public void Cjk_offsets_are_codepoint_metric()
    {
        var src = "let 甲 = 乙 + 1\n";
        // 「乙」在码点 8..9（l0 e1 t2 ␣3 甲4 ␣5 =6 ␣7 乙8）
        var ws = new WorkspaceEdit([Edit("c.lang-go", 8, 9, "丙")], [Pre("c.lang-go", src)]);
        var (applied, error) = EditTransaction.Apply(Map(("c.lang-go", src)), ws);
        Assert.Null(error);
        Assert.Equal("let 甲 = 丙 + 1\n", applied!["c.lang-go"]);
    }

    /// <summary>UTF-16 对抗轴：astral 码点计 1——char index 4 ≠ 码点 offset 2。</summary>
    [Fact]
    public void Astral_offsets_are_codepoint_metric()
    {
        var src = "🦀🦀x";
        // 'x' 的码点 offset = 2（char index 4）：区间 [2, 3) 必须命中 'x'
        var ws = new WorkspaceEdit([Edit("a.lang-go", 2, 3, "y")], [Pre("a.lang-go", src)]);
        var (applied, error) = EditTransaction.Apply(Map(("a.lang-go", src)), ws);
        Assert.Null(error);
        Assert.Equal("🦀🦀y", applied!["a.lang-go"]);
    }

    [Fact]
    public void Hash_mismatch_rejects()
    {
        var ws = new WorkspaceEdit(
            [Edit("m.lang-go", 0, 2, "xx")], [Pre("m.lang-go", "另一个版本")]);
        var (_, error) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws);
        Assert.Equal(ApplyErrorKind.PreconditionFailed, error!.Kind);
    }

    [Fact]
    public void Missing_precondition_rejects()
    {
        var ws = new WorkspaceEdit([Edit("m.lang-go", 0, 2, "xx")], [Pre("other.lang-go", Src)]);
        var (_, error) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws);
        Assert.Equal(ApplyErrorKind.MissingPrecondition, error!.Kind);
    }

    [Fact]
    public void Missing_file_is_io_error()
    {
        var ws = new WorkspaceEdit([Edit("ghost.lang-go", 0, 2, "xx")], [Pre("ghost.lang-go", Src)]);
        var (_, error) = EditTransaction.Apply(Map(), ws);
        Assert.Equal(ApplyErrorKind.Io, error!.Kind);
    }

    [Fact]
    public void Overlapping_edits_reject()
    {
        var ws = new WorkspaceEdit(
            [Edit("m.lang-go", 3, 9, "a"), Edit("m.lang-go", 6, 12, "b")],
            [Pre("m.lang-go", Src)]);
        var (_, error) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws);
        Assert.Equal(ApplyErrorKind.Overlap, error!.Kind);
    }

    [Fact]
    public void Out_of_range_and_reversed_reject()
    {
        var len = Src.EnumerateRunes().Count();
        var ws1 = new WorkspaceEdit([Edit("m.lang-go", 0, len + 1, "x")], [Pre("m.lang-go", Src)]);
        var (_, e1) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws1);
        Assert.Equal(ApplyErrorKind.OutOfRange, e1!.Kind);

        var ws2 = new WorkspaceEdit([Edit("m.lang-go", 5, 3, "x")], [Pre("m.lang-go", Src)]);
        var (_, e2) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws2);
        Assert.Equal(ApplyErrorKind.OutOfRange, e2!.Kind);
    }

    [Fact]
    public void Multiple_edits_apply_descending_without_interference()
    {
        // 两个编辑都在同一行：3..6（add→sum）与 15..16（b→b2）
        var ws = new WorkspaceEdit(
            [Edit("m.lang-go", 3, 6, "sum"), Edit("m.lang-go", 15, 16, "b2")],
            [Pre("m.lang-go", Src)]);
        var (applied, error) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws);
        Assert.Null(error);
        Assert.Equal("fn sum(a: int, b2: int): int {\n    return a + b\n}\n", applied!["m.lang-go"]);
    }

    [Fact]
    public void Same_point_insertions_deterministic()
    {
        // 同点插入 (5,5,"A") 与 (5,5,"B")：稳定降序后按表序倒序落位 → "…BA…"
        var ws = new WorkspaceEdit(
            [Edit("s", 5, 5, "A"), Edit("s", 5, 5, "B")], [Pre("s", "hello world")]);
        var (applied, error) = EditTransaction.Apply(Map(("s", "hello world")), ws);
        Assert.Null(error);
        Assert.Equal("helloBA world", applied!["s"]);
    }

    [Fact]
    public void Empty_edit_set_is_noop_and_unedited_files_untouched()
    {
        var ws = new WorkspaceEdit([], [Pre("m.lang-go", "过期哈希")]);
        var (applied, error) = EditTransaction.Apply(Map(("m.lang-go", Src)), ws);
        Assert.Null(error);
        Assert.Empty(applied!);
    }

    [Fact]
    public void Multi_file_transaction_all_or_nothing()
    {
        // 第二个文件哈希不匹配 → 整体失败，不返回任何部分结果
        var ws = new WorkspaceEdit(
            [Edit("a.lang-go", 3, 6, "sum"), Edit("b.lang-go", 4, 5, "2")],
            [Pre("a.lang-go", Src), Pre("b.lang-go", "过期")]);
        var (_, error) = EditTransaction.Apply(
            Map(("a.lang-go", Src), ("b.lang-go", "let q = 1\n")), ws);
        Assert.Equal(ApplyErrorKind.PreconditionFailed, error!.Kind);
    }

    /// <summary>冻结 IEditApplier 签名的适配实现（out 形态）。</summary>
    [Fact]
    public void Frozen_applier_interface_contract()
    {
        var applier = new EditApplier();
        var ws = new WorkspaceEdit([Edit("m.lang-go", 3, 6, "sum")], [Pre("m.lang-go", Src)]);
        var ok = applier.ApplyInMemory(Map(("m.lang-go", Src)), ws, out var applied, out var err);
        Assert.True(ok);
        Assert.Equal(ApplyErrorKind.MissingPrecondition, err); // default 值 = 枚举首项（无错误时）
        Assert.Equal("fn sum(a: int, b: int): int {\n    return a + b\n}\n", applied!["m.lang-go"]);

        var bad = new WorkspaceEdit([Edit("m.lang-go", 3, 6, "sum")], [Pre("m.lang-go", "旧")]);
        var failed = applier.ApplyInMemory(Map(("m.lang-go", Src)), bad, out var none, out var err2);
        Assert.False(failed);
        Assert.Equal(ApplyErrorKind.PreconditionFailed, err2);
        Assert.Null(none);
    }

    /// <summary>文件系统事务：全过落盘；哈希不匹配一个字节都不动。</summary>
    [Fact]
    public void Fs_apply_atomicity()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"lang-edit-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var pathA = Path.Combine(dir, "a.lang-go");
            var pathB = Path.Combine(dir, "b.lang-go");
            File.WriteAllText(pathA, Src);
            File.WriteAllText(pathB, "let q = 1\n");

            // 全绿事务：两文件都改
            var ws = new WorkspaceEdit(
                [Edit("a.lang-go", 3, 6, "sum"), Edit("b.lang-go", 8, 9, "2")],
                [Pre("a.lang-go", Src), Pre("b.lang-go", "let q = 1\n")]);
            Assert.Null(EditTransaction.ApplyToDirectory(ws, dir));
            Assert.Equal("fn sum(a: int, b: int): int {\n    return a + b\n}\n", File.ReadAllText(pathA));
            Assert.Equal("let q = 2\n", File.ReadAllText(pathB));

            // 哈希不匹配：b 被外部改过 → a 也不得被写
            File.WriteAllText(pathB, "let q = 999\n");
            var sumSrc = "fn sum(a: int, b: int): int {\n    return a + b\n}\n";
            var wsBad = new WorkspaceEdit(
                [Edit("a.lang-go", 3, 6, "plus"), Edit("b.lang-go", 8, 11, "42")],
                [Pre("a.lang-go", sumSrc), Pre("b.lang-go", "let q = 1\n")]);
            var err = EditTransaction.ApplyToDirectory(wsBad, dir);
            Assert.Equal(ApplyErrorKind.PreconditionFailed, err!.Kind);
            Assert.Equal(sumSrc, File.ReadAllText(pathA)); // a 原样（未半写）
            Assert.Equal("let q = 999\n", File.ReadAllText(pathB)); // b 原样

            // 临时文件零残留
            Assert.Empty(Directory.GetFiles(dir, "*.lang-edit-tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
