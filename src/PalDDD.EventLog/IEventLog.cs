// ─────────────────────────────────────────────────────────────
// 📜 IEventLog — 事件日志抽象（Append/Read/Concurrency）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 追加写事件日志抽象接口
// ─────────────────────────────────────────────────────────────

/// <summary>追加写事件日志抽象。</summary>
public interface IEventLog
{
    /// <summary>在检查期望版本后，将事件追加到命名流。</summary>
    ValueTask<AppendEventsResult> AppendAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default);

    /// <summary>按流版本顺序从流中读取事件。</summary>
    IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName,
        long fromVersion = 0,
        int maxCount = int.MaxValue,
        CancellationToken cancellationToken = default);

    /// <summary>按全局追加顺序读取所有事件。</summary>
    IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0,
        int maxCount = int.MaxValue,
        CancellationToken cancellationToken = default);
}
