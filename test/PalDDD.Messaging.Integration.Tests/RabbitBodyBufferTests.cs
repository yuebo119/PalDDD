using PalDDD.Messaging.RabbitMQ;

namespace PalDDD.Messaging.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ A2（2026-10-15 性能实施轮）：发布 body 缓冲按发布租借化
// 旧形态 ThreadStatic 直传存在静默数据损坏风险：RabbitMQ.Client 7 的
// BasicPublishCoreAsync 在确认配额租约与流控两个可挂起 await 之后才经
// SerializeToFrames 同步拷贝 body（官方源码 Channel.BasicPublish.cs /
// SessionBase.cs 实证），同线程流水线发布的下一次入口 Clear 会抹掉
// 尚待拷贝的共享缓冲，帧照常确认，消息静默错内容。
// 本测试锁定池化 writer 的隔离契约（修复核心性质）与成长契约。
// 红证明为结构性红（同 B-5 先例）：旧码无 PooledBodyWriter 类型，
// 本测试不可达；隔离断言即旧形态违反的性质（第二次写入清空第一次）。
// ═══════════════════════════════════════════════════════════════

public sealed class RabbitBodyBufferTests
{
    [Test]
    public async Task PooledBodyWriter_ConcurrentInstances_DoNotShareState()
    {
        // 旧 TLS 形态违反的性质：第二次序列化的入口 Clear 会抹掉第一次尚待拷贝的内容
        using var first = new RabbitMqBroker.PooledBodyWriter(256);
        using var second = new RabbitMqBroker.PooledBodyWriter(256);

        Write(first, "first-payload"u8);
        Write(second, "second-payload"u8);

        await Assert.That(ToText(first.WrittenMemory)).IsEqualTo("first-payload");
        await Assert.That(ToText(second.WrittenMemory)).IsEqualTo("second-payload");
    }

    [Test]
    public async Task PooledBodyWriter_GrowthBeyondInitialCapacity_PreservesContentInOrder()
    {
        // 初始 256，写入 1000 字节触发倍增成长——内容完整且保序（旧数组已就地归还）
        using var writer = new RabbitMqBroker.PooledBodyWriter(256);
        var payload = new byte[1000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 26);

        var span = writer.GetSpan(payload.Length);
        payload.CopyTo(span);
        writer.Advance(payload.Length);

        await Assert.That(writer.WrittenMemory.Length).IsEqualTo(payload.Length);
        await Assert.That(writer.WrittenMemory.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task PooledBodyWriter_MultipleWrites_AccumulateWithoutOverwrite()
    {
        // 分段写入（Utf8JsonWriter 的 GetSpan/Advance 节奏）——后段追加不清空前段
        using var writer = new RabbitMqBroker.PooledBodyWriter(256);

        Write(writer, "aaa"u8);
        Write(writer, "bbb"u8);
        Write(writer, "ccc"u8);

        await Assert.That(ToText(writer.WrittenMemory)).IsEqualTo("aaabbbccc");
    }

    [Test]
    public async Task PooledBodyWriter_Dispose_ReturnsToPool_AndPoolStaysUsable()
    {
        // Dispose 归还后池仍可正常租借（归还契约不破坏池）
        var writer = new RabbitMqBroker.PooledBodyWriter(256);
        Write(writer, "pooled"u8);
        writer.Dispose();

        using var next = new RabbitMqBroker.PooledBodyWriter(256);
        Write(next, "again"u8);
        await Assert.That(ToText(next.WrittenMemory)).IsEqualTo("again");
    }

    private static void Write(RabbitMqBroker.PooledBodyWriter writer, ReadOnlySpan<byte> content)
    {
        var span = writer.GetSpan(content.Length);
        content.CopyTo(span);
        writer.Advance(content.Length);
    }

    private static string ToText(ReadOnlyMemory<byte> memory) => System.Text.Encoding.UTF8.GetString(memory.Span);
}
