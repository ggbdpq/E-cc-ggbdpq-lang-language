using System.Runtime.CompilerServices;

namespace Lang.Semantic;

/// <summary>
/// 节点键映射：同一分析内 AST 稳定存活，对象引用即身份（与 lang-rs 的指针键同构）。
/// 查询必须传入同一次分析所消费的那棵 AST 的节点，跨树查询返回 null。
/// </summary>
internal sealed class NodeMap<T> where T : struct
{
    private readonly Dictionary<object, T> _map = new(RefComparer.Instance);

    public T? Get(object key) => _map.TryGetValue(key, out var v) ? v : null;

    public void Set(object key, T value) => _map[key] = value;
}

/// <summary>引用相等比较器（记录类型自带结构相等，AST 键必须按身份区分）。</summary>
internal sealed class RefComparer : IEqualityComparer<object>
{
    public static readonly RefComparer Instance = new();

    private RefComparer() { }

    bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

    int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
