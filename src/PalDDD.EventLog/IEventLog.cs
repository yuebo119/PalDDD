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

/// <summary>
/// 可选能力：只读查询流的最后版本（ADR-039 / 审计 A1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是独立能力接口而不是 <see cref="IEventLog"/> 的新成员</b>：给主接口加成员会破坏
/// 所有既有自定义实现者（源码 + 二进制）；而用默认接口实现（DIM）返回 -1 会让未更新的实现
/// 静默给出「流不存在」的<b>假结果</b>——与本项目「宁可缺能力，不可给假 API」的既有裁决
/// （2026-11-04 C 批 `GetByStatusAsync` 拒绝 DIM）一致。能力探测形态对齐
/// `TProvider.SupportsReturningClause` 先例：调用方显式 <c>is IEventLogVersionReader</c>，
/// 不支持时自己决定降级路径，不会被静默误导。
/// </para>
/// <para>
/// 四个内置栈（InMemory / EF Core / Dapper / PalORM）均实现本接口，语义一致：
/// 流不存在或为空返回 <b>-1</b>（与 <see cref="ExpectedStreamVersion.NoStream"/> 的
/// <c>Matches(-1) == true</c> 对齐）；流存在时返回末位 <c>StreamVersion</c>。
/// </para>
/// </remarks>
public interface IEventLogVersionReader
{
    /// <summary>只读查询流的最后版本；流不存在或为空返回 -1。</summary>
    ValueTask<long> GetStreamVersionAsync(
        string streamName,
        CancellationToken cancellationToken = default);
}
