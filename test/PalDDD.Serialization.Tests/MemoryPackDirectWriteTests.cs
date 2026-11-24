using System.Buffers;
using PalDDD.Serialization.MemoryPack;

namespace PalDDD.Serialization.Tests;

/// <summary>
/// MemoryPack 直写重载（<c>Serialize(object, descriptor, IBufferWriter&lt;byte&gt;)</c>）的
/// 字节一致性与零分配断言（2026-11-23 兑现 ADR-029 延后段）。
/// <para>
/// 依据（探针实测 N=10000，47 B 载荷）：复用 <see cref="ArrayBufferWriter{T}"/> 直写
/// 0.0 B/op，非泛型内存重载 72.0 B/op，两者产出逐字节一致。原实现走接口 DIM 回退
///（内存形态 + 一次整载荷拷贝），本重载消除该拷贝与中间 byte[]。
/// </para>
/// <para>类级 NotInParallel 隔离线程级分配量具的测量窗口。</para>
/// </summary>
[NotInParallel("memorypack-alloc")]
public sealed class MemoryPackDirectWriteTests
{
    private const int Iterations = 5_000;

    private static MessageDescriptor Descriptor()
        => new("test.directwrite.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1,
            contentType: ContentTypes.MemoryPack);

    [Test]
    public async Task DirectWrite_ProducesSameBytesAsMemoryOverload()
    {
        var serializer = new MemoryPackMessageSerializer();
        var descriptor = Descriptor();
        var message = new MemoryPackTestMessage("order-7788", 42, 19.99m);

        var memory = serializer.Serialize((object)message, descriptor);
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize((object)message, descriptor, writer);

        await Assert.That(writer.WrittenCount).IsEqualTo(memory.Length);
        await Assert.That(writer.WrittenSpan.SequenceEqual(memory.Span)).IsTrue();
    }

    [Test]
    public async Task DirectWrite_ReusedBuffer_AllocatesNothing()
    {
        var serializer = new MemoryPackMessageSerializer();
        var descriptor = Descriptor();
        var message = new MemoryPackTestMessage("order-7788", 42, 19.99m);
        var buffer = new ArrayBufferWriter<byte>(256);

        for (var i = 0; i < 64; i++)
        {
            buffer.ResetWrittenCount();
            serializer.Serialize((object)message, descriptor, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
        {
            buffer.ResetWrittenCount();
            serializer.Serialize((object)message, descriptor, buffer);
        }
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;

        await Assert.That(perOp).IsLessThan(1.0);
        await Assert.That(buffer.WrittenCount).IsGreaterThan(0);
    }

    [Test]
    public async Task DirectWrite_MismatchedContentType_Throws()
    {
        var serializer = new MemoryPackMessageSerializer();
        var descriptor = new MessageDescriptor("test.directwrite.json", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);
        var message = new MemoryPackTestMessage("x", 1, 1m);
        var writer = new ArrayBufferWriter<byte>();

        await Assert.That(() => serializer.Serialize((object)message, descriptor, writer))
            .Throws<InvalidOperationException>();
    }
}
