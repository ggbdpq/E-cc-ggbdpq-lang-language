using System.Collections.Generic;

namespace Lang.Hir;

/// <summary>表相等/哈希助手：插入序逐元素比较（契约 §5：比较序确定，无哈希迭代序参与）。</summary>
internal static class Seq
{
    public static bool Eq<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>可空引用成员相等：双 null 视为相等，非空走值相等。</summary>
    public static bool OptEq<T>(T? a, T? b) where T : class
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Equals(b);
    }

    public static int Hash<T>(IReadOnlyList<T> items)
    {
        unchecked
        {
            int h = 17;
            for (int i = 0; i < items.Count; i++)
            {
                h = h * 31 + (items[i]?.GetHashCode() ?? 0);
            }
            return h;
        }
    }
}
