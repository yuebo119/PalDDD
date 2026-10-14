namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 事件回放源接口
// ─────────────────────────────────────────────────────────────

/// <summary>事件回放源 — 按源名读取回放事件流（Rebuilder 消费的抽象）。</summary>
/// <remarks>
/// <b>契约</b>：实现必须保证返回的每个 <see cref="ReplayEvent{TMessage}"/> 的 SourceName
/// 与传入 <c>sourceName</c> 一致——检查点按 (ProjectionName, SourceName,
/// Position) 轴写入，错位实现（返回事件的 SourceName 与请求源不符）会使检查点写入异流；
/// Rebuilder 的名称守卫只护 projectionName 轴（handler.ProjectionName 与注册名一致），
/// sourceName 轴由本契约约束实现方。
/// </remarks>
public interface IEventReplaySource<TMessage>
{
    /// <summary>按源名读取回放事件流。</summary>
    /// <param name="sourceName">回放源名称——返回事件的 SourceName 必须与其一致（见接口契约）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回放事件流。</returns>
    IAsyncEnumerable<ReplayEvent<TMessage>> ReadAsync(
        string sourceName,
        CancellationToken ct = default);
}

/// <summary>
/// 可续读回放源能力（R6/P0-3，ADR-028，2026-10-14 性能实施轮）——支持按起始位置下推
/// 读取的回放源。与 <see cref="IResumableProjectionCheckpointStore"/> 配对：Rebuilder 取
/// 检查点续读位置后经本接口从增量位置读取，免除对已完成前缀的全量重读。
/// 位置语义与 <c>ReplayEvent{TMessage}.Position</c> 同轴（EventLog 流的 StreamVersion）。
/// 自定义回放源不实现本接口时 Rebuilder 自动保持全量读取路径（非破坏）。
/// </summary>
public interface IResumableEventReplaySource<TMessage> : IEventReplaySource<TMessage>
{
    /// <summary>按源名从指定起始位置（含）读取回放事件流。</summary>
    /// <param name="sourceName">回放源名称——返回事件的 SourceName 必须与其一致（见基接口契约）。</param>
    /// <param name="fromPosition">起始位置（含）——应等于 <see cref="IResumableProjectionCheckpointStore.GetResumePositionAsync"/> 返回值。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回放事件流。</returns>
    IAsyncEnumerable<ReplayEvent<TMessage>> ReadAsync(
        string sourceName,
        long fromPosition,
        CancellationToken ct = default);
}
