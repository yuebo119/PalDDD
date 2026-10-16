using PalUlid = ByteAether.Ulid.Ulid;
using System.Text;

namespace PalDDD.EventLog.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ EPSL 地毯审计 2026-10-16 A 批 #15：InMemory 追加路径单次载荷拷贝
// 旧码每事件双拷——EventData 构造的防御拷（payload.ToArray）之后，RecordedEvent 的
// EventData 构造又 ToArray 一次；EFCore 栈 StoredEvent 走 PayloadArray 零拷贝别名
// （v65 P3 声明在案），本处是跨栈不对称残余。修复 = RecordedEvent 别名 EventData 的
// 构造期副本（PayloadArray/MetadataArray），每事件省一次全载荷分配+拷贝。
// 公开 API 契约不变（Payload 仍为 ReadOnlyMemory 只读视图）。
// ═══════════════════════════════════════════════════════════════

public sealed class EventLogAllocationTests
{
    [Test]
    public async Task AppendAsync_SinglePayloadCopy_NoRedundantRecordedEventCopy(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        // 1KB 载荷：让全载荷拷贝在分配计数里显著（旧码双拷 = +1KB/事件）
        var payload = new byte[1024];
        var streamName = $"alloc-{Guid.NewGuid():N}";
        const string eventName = "orders.order-submitted.v1";

        EventData BuildEvent() => new(
            PalUlid.New(),
            eventName,
            schemaVersion: 1,
            "application/json",
            payload,
            metadata: ReadOnlyMemory<byte>.Empty,
            EventAuditMetadata.Empty);

        // 预热（JIT 成型 + Activity/指标路径稳定）
        for (var i = 0; i < 50; i++)
            _ = await log.AppendAsync(streamName, ExpectedStreamVersion.Any, [BuildEvent()], cancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 1_000;
        for (var i = 0; i < iterations; i++)
            _ = await log.AppendAsync(streamName, ExpectedStreamVersion.Any, [BuildEvent()], cancellationToken);

        var alloc = GC.GetAllocatedBytesForCurrentThread() - baseline;

        // 预算构成（新码实测标定，2026-10-16）：新码 1974B/事件 = EventData 构造期防御拷
        // ~1KB + 测试宿主 ActivityListener 下的 StartEventLogAppend ~440B + 对象与集合
        // 固定成本 ~500B。旧码（+ RecordedEvent 第二次全载荷拷）实测 2998B/事件，在本
        // 预算下红（红证明在案）——锁定的回归敏感点即追加路径冗余全载荷拷贝的重引入。
        // 预算余量 <1KB（载荷尺寸），Debug/Release 的 async 状态机形态差异（~140B）不越界。
        await Assert.That(alloc).IsLessThanOrEqualTo(2100L * iterations);
    }
}
