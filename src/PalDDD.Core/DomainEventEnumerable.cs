// ─────────────────────────────────────────────────────────────
// 🔗 DomainEventEnumerable — 单链表零分配 foreach（ref struct 枚举器）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// ref struct 枚举器 — 零分配遍历领域事件单链表
// 💡 保留理由：栈分配零 GC + ASP.NET Core LINQ 扩展点。
//    详见 docs/decisions/004-core-type-retention.md
// ─────────────────────────────────────────────────────────────
//
// 性能数据（Serena 引用图验证）：
//    - 被 Entity.DomainEvents() 返回
//    - 被 DomainEventCollector.Collect() 遍历
//    - 被 Benchmark EntityDomainEventBenchmarks.IterateEvents_RefStructEnumerator 验证

/// <summary>ref struct 枚举器，遍历领域事件单链表，零堆分配</summary>
public ref struct DomainEventEnumerable
{
    private readonly DomainEvent? _head;

    /// <summary>包裹事件链表头——null 表示空序列（无待发事件）。</summary>
    public DomainEventEnumerable(DomainEvent? head) => _head = head;

    /// <summary>foreach 模式入口——返回栈分配枚举器，全程零堆分配。</summary>
    public DomainEventEnumerator GetEnumerator() => new(_head);
}

/// <summary>ref struct 枚举器实现 — 配合 DomainEventEnumerable 实现零分配 foreach</summary>
public ref struct DomainEventEnumerator
{
    private DomainEvent? _current;
    private bool _first = true;

    internal DomainEventEnumerator(DomainEvent? head) => _current = head;

    public readonly DomainEvent Current => _current!;

    /// <summary>推进到下一事件——首次调用定位链表头，其后沿 Next 前进；耗尽返回 false。</summary>
    public bool MoveNext()
    {
        if (_first)
        {
            _first = false;
            return _current is not null;
        }

        if (_current is null) return false;
        _current = _current.Next;
        return _current is not null;
    }
}
