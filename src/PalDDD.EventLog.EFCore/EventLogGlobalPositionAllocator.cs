// ─────────────────────────────────────────────────────────────
// 📐 EventLogGlobalPositionAllocator — 持久化的位置分配状态
// ─────────────────────────────────────────────────────────────
namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 持久化位置分配器状态行
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 全局有序事件日志位置的持久化分配器状态。
/// </summary>
/// <remarks>
/// <see cref="NextGlobalPosition"/> 是持久化的高水位线 —— 上一个已分配区块的上界（不含）。
/// <see cref="Revision"/> 是 EF Core 并发令牌，支持区块分配时的乐观 CAS，无需 Serializable 事务。
/// </remarks>
public sealed class EventLogGlobalPositionAllocator
{
    /// <summary>单例分配器行标识符。</summary>
    public const string SingletonId = "event-log";

    private EventLogGlobalPositionAllocator()
    {
        Id = SingletonId;
    }

    private EventLogGlobalPositionAllocator(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Id = id;
    }

    /// <summary>创建初始分配器状态。</summary>
    public static EventLogGlobalPositionAllocator Create() => new(SingletonId);

    /// <summary>分配器行标识符。</summary>
    public string Id { get; private set; }

    /// <summary>
    /// 持久化的高水位线 —— 上一个已分配区块的上界（不含）。
    /// 下一个区块从此值开始。
    /// </summary>
    public long NextGlobalPosition { get; private set; }

    /// <summary>
    /// 乐观并发令牌。每次区块分配时递增。
    /// 支持 CAS 重试，无需 Serializable 隔离。
    /// </summary>
    public uint Revision { get; private set; }

    /// <summary>
    /// 将高水位线推进 <paramref name="chunkSize"/> 个位置，
    /// 并返回新区块的起始位置。
    /// </summary>
    public long AllocateChunk(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var first = NextGlobalPosition;
        checked
        {
            NextGlobalPosition += chunkSize;
        }
        // ITM-117 修复：Revision 并发令牌 checked 自增——原 uint 溢出静默回绕
        //（0xFFFFFFFF 后归 0），EF concurrency token 的 WHERE Revision=orig 可能误匹配
        // 旧行（CAS 失效）；checked 抛 OverflowException 显式化。uint 需 42 亿次区块
        // 分配才可能触发，纯防御性修复，不影响字段类型与使用点（EF token 无碍）。
        checked { Revision++; }
        return first;
    }
}
