namespace Lang.Core;

/// <summary>单点定位（specs/source-span.md）：offset 0 基码点；line/column 1 基码点计数。</summary>
public readonly record struct Position(uint Offset, uint Line, uint Column);

/// <summary>半开区间 [start, end)；空锚点允许 start == end。</summary>
public readonly record struct Span(SourceId Source, Position Start, Position End)
{
    public static Span Point(SourceId source, Position pos) => new(source, pos, pos);

    public bool IsEmpty => Start == End;
}
