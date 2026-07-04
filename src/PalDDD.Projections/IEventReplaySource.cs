namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 事件回放源接口
// ─────────────────────────────────────────────────────────────

public interface IEventReplaySource<TMessage>
{
    IAsyncEnumerable<ReplayEvent<TMessage>> ReadAsync(
        string sourceName,
        CancellationToken ct = default);
}
