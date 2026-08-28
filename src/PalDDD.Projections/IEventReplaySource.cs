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
