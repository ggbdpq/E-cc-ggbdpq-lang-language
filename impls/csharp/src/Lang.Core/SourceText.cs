namespace Lang.Core;

/// <summary>
/// 源码所有权 + 三重索引（母本 §R1 / specs/source-span.md）。
/// C# string 是 UTF-16 code unit 序列：astral 码点占 2 个 char——这正是第五实现的对抗轴。
/// 本类是对抗闸门：对外契约一律码点；char index 只许 parser 内部使用，出边界前必须折算。
/// 换行只认 '\n'；'\r' 属于上一行内容。
/// </summary>
public sealed class SourceText
{
    private readonly string _path;
    private readonly string _text;
    /// <summary>每行首字符的码点偏移（行 1 从 0 起）。</summary>
    private readonly int[] _lineStartsCp;
    /// <summary>第 i 个码点的 char（UTF-16 unit）索引，长度 = 码点总数。</summary>
    private readonly int[] _cpToChar;

    public SourceText(string path, string text)
    {
        _path = path;
        _text = text;
        var lineStarts = new List<int> { 0 };
        var cpToChar = new List<int>(text.Length);
        for (int i = 0; i < text.Length;)
        {
            int charLen = IsSurrogatePairAt(text, i) ? 2 : 1;
            cpToChar.Add(i);
            if (text[i] == '\n')
                lineStarts.Add(CodePointCount(cpToChar));
            i += charLen;
        }
        _lineStartsCp = lineStarts.ToArray();
        _cpToChar = cpToChar.ToArray();
    }

    private static bool IsSurrogatePairAt(string s, int i) =>
        char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);

    private static int CodePointCount(List<int> cpToChar) => cpToChar.Count;

    public string Path => _path;
    public string Text => _text;

    /// <summary>码点总数（astral 码点计 1，不是 string.Length 的 char 数）。</summary>
    public int LengthCp => _cpToChar.Length;

    /// <summary>UTF-16 unit 总数（= Text.Length）。</summary>
    public int LengthChars => _text.Length;

    /// <summary>char index → 码点偏移。落在代理对中间时向下取整到该码点；文末（== LengthChars）返回 LengthCp。</summary>
    public int CharIndexToCp(int charIndex)
    {
        if (charIndex >= _text.Length) return _cpToChar.Length;
        // 第一个 charIndex 严格大于目标的条目之前，即包含目标的码点
        int n = _cpToChar.PartitionPoint(c => c <= charIndex);
        return n - 1;
    }

    /// <summary>码点偏移 → char index。越界回到文末。</summary>
    public int CpToCharIndex(int cp) =>
        cp < _cpToChar.Length ? _cpToChar[cp] : _text.Length;

    /// <summary>码点偏移 → (line, column)，均 1 基码点计数。</summary>
    public (int Line, int Column) LineCol(int cp)
    {
        int lineIdx = _lineStartsCp.PartitionPoint(s => s <= cp) - 1;
        return (lineIdx + 1, cp - _lineStartsCp[lineIdx] + 1);
    }

    /// <summary>行数（≥1）。</summary>
    public int LineCount => _lineStartsCp.Length;
}

internal static class Partition
{
    /// <summary>有序数组中第一个满足 predicate 为 false 的下标（等价 Rust partition_point）。</summary>
    public static int PartitionPoint(this int[] arr, Func<int, bool> predicate)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (predicate(arr[mid])) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
