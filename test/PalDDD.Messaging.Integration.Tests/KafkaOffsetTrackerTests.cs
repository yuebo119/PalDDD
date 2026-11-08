using Confluent.Kafka;
using PalDDD.Messaging.Kafka;

namespace PalDDD.Messaging.Integration.Tests;

// ─────────────────────────────────────────────────────────────
// FIX-002（2026-11-08 综合审计 P1-2）锁定：OffsetTracker 的分区生命周期重置语义
// ─────────────────────────────────────────────────────────────
//
// 修前机制：OffsetTracker 不感知 rebalance，分区撤销后状态未重置；重新分配时新起点
// 大于旧 Next，Done.Remove(Next) 恒失败，位点永久卡住（重启全量重放 + Done 只增不减
// 致内存无界）。修后：分区生命周期回调（SetPartitionsAssigned/Revoked/Lost）调用 Reset。
//
// 两个用例：
//  · Reset_ReinitializesWatermarkAfterRebalance —— Reset 后新起点的 ack 能推进（修复后行为）。
//  · WithoutReset_WatermarkStallsOnForwardRebalance —— 对照：不 Reset 时前进型 rebalance
//    的 ack 无法推进（修前行为，证明该场景确实是缺陷而非设计）。

/// <summary>OffsetTracker 的 rebalance 重置语义（FIX-002 锁定）。</summary>
public sealed class KafkaOffsetTrackerTests
{
    /// <summary>Reset 后分区以新起点重新初始化，前进型 rebalance 的 ack 正常推进。</summary>
    [Test]
    public async Task Reset_ReinitializesWatermarkAfterRebalance()
    {
        var tracker = new KafkaBroker.OffsetTracker();
        var tp = new TopicPartition("fix002", 0);

        // 第一段：消费 offset 0 并确认，水印推进到 1
        tracker.Consumed(tp, 0);
        var first = tracker.Ack(new TopicPartitionOffset(tp, 0));
        await Assert.That(first?.Offset).IsEqualTo(1);

        // rebalance：分区撤销（Reset）→ 重新分配（新起点 5）
        tracker.Reset(tp);
        tracker.Consumed(tp, 5);
        var second = tracker.Ack(new TopicPartitionOffset(tp, 5));

        // Reset 后 tracker 以新起点 5 初始化，确认推进到 6
        await Assert.That(second?.Offset).IsEqualTo(6);
    }

    /// <summary>
    /// 对照：不 Reset 时，前进型 rebalance（新起点 5 &gt; 旧 Next 1）的 ack 无法推进——
    /// <c>Done.Remove(1)</c> 恒失败，位点永久卡在 1。证明该场景是缺陷而非设计。
    /// </summary>
    [Test]
    public async Task WithoutReset_WatermarkStallsOnForwardRebalance()
    {
        var tracker = new KafkaBroker.OffsetTracker();
        var tp = new TopicPartition("fix002", 0);

        tracker.Consumed(tp, 0);
        tracker.Ack(new TopicPartitionOffset(tp, 0)); // 水印推进到 1

        // 前进型 rebalance：新起点 5 的确认无法推进（Done 里只有 5，Remove(1) 失败）
        var stalled = tracker.Ack(new TopicPartitionOffset(tp, 5));
        await Assert.That(stalled).IsNull();
    }
}
