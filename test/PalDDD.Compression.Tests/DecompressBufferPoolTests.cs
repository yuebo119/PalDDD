namespace PalDDD.Compression.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ EPSL 地毯审计 2026-10-16 B 批 #8：DecompressBufferPool 驻留上限弃槽
// R9/P2-1 给四个同族 TLS 池（JsonMessageSerializer / JsonLinesEventWriter /
// KafkaBroker / TlsStreamPool）加了 256KB 阈值弃槽，解压池只改了首轮 GetSpan
// 提示上限、Rent 无容量检查——历史峰值无限驻留（线程池线程长驻不退 = 常驻）。
// 本测试锁定弃槽契约（超阈值重建）与复用契约（阈值内同实例）两面。
// ═══════════════════════════════════════════════════════════════

public sealed class DecompressBufferPoolTests
{
    private const int Threshold = 256 * 1024;

    [Test]
    public async Task Rent_AfterOversizedGrowth_DiscardsAndRebuilds()
    {
        // 第一次 Rent 并增长到超阈值（模拟一次大输出解压后的线程槽峰值驻留）
        var first = DecompressBufferPool.Rent();
        first.GetSpan(Threshold + 1024);
        first.Advance(Threshold + 1024);
        await Assert.That(first.Capacity).IsGreaterThan(Threshold);

        // 下次入口应弃槽重建：新实例 + 容量回落（不再按历史峰值驻留）
        var second = DecompressBufferPool.Rent();
        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(second.Capacity).IsLessThanOrEqualTo(Threshold);
    }

    [Test]
    public async Task Rent_UnderThreshold_ReusesSameInstance()
    {
        // 阈值内复用契约不受影响：小载荷常态（框架消息级）零额外成本
        var first = DecompressBufferPool.Rent();
        first.GetSpan(1024);
        first.Advance(1024);

        var second = DecompressBufferPool.Rent();
        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(second.Capacity).IsGreaterThanOrEqualTo(1024);
        // 复用前 Clear 契约：已写内容被清空（长度归零），容量保留
        await Assert.That(second.WrittenCount).IsEqualTo(0);
    }
}
