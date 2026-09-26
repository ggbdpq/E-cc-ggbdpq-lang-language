using Lang.Core;

namespace Lang.Syntax;

/// <summary>trivia 类别。</summary>
public enum TriviaKind
{
    /// <summary>`//` 行注释（无块注释，LEX-002）。</summary>
    Comment,
    /// <summary>空白串（空格 / 制表 / 回车）。</summary>
    Whitespace,
    /// <summary>单个换行 `\n`。</summary>
    Newline,
}

/// <summary>一段 trivia：类别 + 码点 span + 原文。</summary>
public sealed record Trivia(TriviaKind Kind, Span Span, string Text);

/// <summary>
/// 无损 trivia 层（对照 rust lang-syntax trivia.rs / TS comments.ts）。
/// 词法态扫描：跳过字符串字面量（含转义），把源码切成 注释 / 空白 / 换行 三类 trivia，
/// 按出现序输出，span 与原文齐备。Formatter 的 100% 注释保留与安全编辑（A6）据此工作。
/// 注释 text 含 `//` 前缀，不含换行与行尾 `\r`（CRLF 源）；span 终点仍抵 `\n` 之前。
/// </summary>
public static class TriviaScanner
{
    public static IReadOnlyList<Trivia> Scan(SourceText text, SourceId source)
    {
        string s = text.Text;
        Position Pos(int charIndex)
        {
            // 文末边界修正：charIndex == LengthChars 时取 LengthCp（Lang.Core 边界缺陷，status 已登记）
            int cp = charIndex >= s.Length ? text.LengthCp : text.CharIndexToCp(charIndex);
            var (line, column) = text.LineCol(cp);
            return new Position((uint)cp, (uint)line, (uint)column);
        }

        var outList = new List<Trivia>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            switch (c)
            {
                case '\n':
                    {
                        int start = i;
                        i++;
                        outList.Add(new Trivia(TriviaKind.Newline,
                            new Span(source, Pos(start), Pos(i)), "\n"));
                        break;
                    }
                case ' ' or '\t' or '\r':
                    {
                        int start = i;
                        while (i < s.Length && s[i] is not '\n' && s[i] is ' ' or '\t' or '\r') i++;
                        outList.Add(new Trivia(TriviaKind.Whitespace,
                            new Span(source, Pos(start), Pos(i)), s[start..i]));
                        break;
                    }
                case '"':
                    // 跳过字符串字面量（含转义）；未闭合串到 `\n`/文末为止（与 comments.ts 同构）
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }
                        if (s[i] == '"' || s[i] == '\n')
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    break;
                case '/' when i + 1 < s.Length && s[i + 1] == '/':
                    {
                        int start = i;
                        while (i < s.Length && s[i] != '\n') i++;
                        outList.Add(new Trivia(TriviaKind.Comment,
                            new Span(source, Pos(start), Pos(i)), s[start..i].TrimEnd('\r')));
                        break;
                    }
                default:
                    i++;
                    break;
            }
        }
        return outList;
    }
}
