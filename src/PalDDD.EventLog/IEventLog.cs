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

    /// <summary>
    /// 只读查询流的最后版本（ADR-039 / 审计 A1）。
    /// </summary>
    /// <remarks>
    /// 跨进程/跨会话向已有流追加并要求 <see cref="ExpectedStreamVersion.Exact"/> 时，
    /// 调用方此前只能 <see cref="ReadAllAsync"/> 全量物化才能算出最后版本——10k 事件的流
    /// 单次追加要付 10k 条物化。本方法走 <c>(StreamName, StreamVersion)</c> 唯一索引的
    /// 单行聚合，零事件物化。<br/>
    /// 返回<b>流不存在时为 -1</b>（与 <see cref="ExpectedStreamVersion.NoStream"/> 的
    /// 语义对齐：<c>Matches(-1)</c> 为真）。流存在且首个事件版本为 0 时返回 0，之后随版本递增。
    /// </remarks>
    ValueTask<long> GetStreamVersionAsync(
        string streamName,
        CancellationToken cancellationToken = default);
}
