// ─────────────────────────────────────────────────────────────
// 📽️ IProjectionHandler — 投影处理器接口
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影处理器接口
// ─────────────────────────────────────────────────────────────

/// <summary>投影处理器 — 将消息应用到投影模型。</summary>
public interface IProjectionHandler<in TMessage>
{
    /// <summary>投影显示名称 — checkpoint 租约键的组成部分。</summary>
    string ProjectionName { get; }

    /// <summary>将消息应用到投影模型。</summary>
    /// <remarks>
    /// ⚠️ 投递语义声明（八轮评审）：at-least-once —— <see cref="ProjectionProcessor{TMessage}"/>
    /// 在 MarkCompletedAsync 持久化失败（租约被抢占、进程崩溃）后会重放同一位置的消息，
    /// <see cref="ProjectAsync"/> 可能对同一消息重复调用；投影实现必须幂等
    /// （upsert 而非 append，或按位置去重），否则重复投递会产生重复副作用。
    /// </remarks>
    ValueTask ProjectAsync(TMessage message, ProjectionContext context, CancellationToken ct = default);
}

/// <summary>
/// 可选批量投影能力（2026-10-21）：实现后 <see cref="ProjectionProcessor{TMessage}.ProcessBatchAsync"/>
/// 把整组一次性交给 <see cref="ProjectBatchAsync"/>（可做批量 upsert，把 N 次写降为 1 次），
/// 不再逐条调用 <see cref="IProjectionHandler{TMessage}.ProjectAsync"/>；未实现时保持逐条路径。
/// </summary>
/// <remarks>
/// 检查点语义与逐条路径一致：只在组末位置 TryStart/MarkCompleted，失败时组末与组首均标
/// Failed 并重放整组（at-least-once）。批内部分成功不可回滚——实现必须整批幂等，
/// 重放不得产生额外副作用；批内顺序即回放顺序。
/// </remarks>
public interface IProjectionBatchHandler<TMessage> : IProjectionHandler<TMessage>
{
    /// <summary>把一组消息应用到投影模型（按 <paramref name="batch"/> 顺序）。</summary>
    ValueTask ProjectBatchAsync(
        IReadOnlyList<(TMessage Message, ProjectionContext Context)> batch,
        CancellationToken ct = default);
}
