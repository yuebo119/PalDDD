// ─────────────────────────────────────────────────────────────
// 🏷️ DeferredAckRegistry — 批次结算移交标记（v4 批 A，2026-11-07）
// ─────────────────────────────────────────────────────────────
//
// 💡 批量订阅把 broker 的单次交付确认器交给 MessageBatchPump 组批后统一结算。
//   ｜ broker 的逐条兜底（成功 ACK / 失败 Nack）必须在「结算已移交批次」时跳过，
//   ｜ 否则会在批次 handler 成败之前提前结算（pre-ACK），或与批次收尾二次结算。
//   ｜ 标记以确认器实例为 key 存在 ConditionalWeakTable：条目随确认器回收，不泄漏；
//   ｜ 无需改动 IMessageAck 公共接口，也不给 Kafka 侧引入观察者。
//   ｜ RabbitMQ 经 PalDDD.Messaging.csproj 的 InternalsVisibleTo 只读本表；Kafka 的
//   ｜ HandleMessageAsync finally 判据已并入 IsDeferred 判定（v4 批 A 遗漏，2026-11-07 本轮补）。
// ─────────────────────────────────────────────────────────────
using System.Runtime.CompilerServices;

namespace PalDDD.Messaging;

/// <summary>批次结算移交标记（程序集内部，零公共 API 面）。</summary>
internal static class DeferredAckRegistry
{
    private static readonly ConditionalWeakTable<object, Mark> Marks = new();

    /// <summary>把确认器标记为「结算已移交批次泵」。</summary>
    /// <remarks>
    /// 顺序硬约束（v4 §2.1）：必须在 <c>channel.Writer.WriteAsync</c> 之前同步调用。
    /// broker 的 <c>await handler(...)</c> 返回后立刻判定兜底 ACK；标记若落在 WriteAsync
    /// 之后或 reader 侧，关停竞态会漏标，pre-ACK 与批次结算冲突（CwtTiming 探针实证）。
    /// </remarks>
    public static void MarkDeferred(IMessageAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        var mark = Marks.GetValue(ack, static _ => new Mark());
        Volatile.Write(ref mark.Deferred, 1);
    }

    /// <summary>确认器实例是否已被标记为「结算移交批次」。</summary>
    public static bool IsDeferred(IMessageAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return Marks.TryGetValue(ack, out var mark) && Volatile.Read(ref mark.Deferred) != 0;
    }

    private sealed class Mark
    {
        public int Deferred;
    }
}
