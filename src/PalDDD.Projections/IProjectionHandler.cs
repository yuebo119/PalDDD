// ─────────────────────────────────────────────────────────────
// 📽️ IProjectionHandler — 投影处理器接口
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影处理器接口
// ─────────────────────────────────────────────────────────────

public interface IProjectionHandler<in TMessage>
{
    string ProjectionName { get; }

    ValueTask ProjectAsync(TMessage message, ProjectionContext context, CancellationToken ct = default);
}
