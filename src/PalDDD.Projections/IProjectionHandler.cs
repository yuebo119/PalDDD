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
