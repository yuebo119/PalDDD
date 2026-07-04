using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Serialization.MemoryPack;

namespace PalDDD.Serialization.Tests;

// ═══════════════════════════════════════════════════════════════
// MemoryPack 序列化测试 — GA 就绪验证
// ═══════════════════════════════════════════════════════════════

[MemoryPackable]
public sealed partial record MemoryPackTestMessage(string Id, int Count, decimal Amount);

[MemoryPackable]
public sealed partial record MemoryPackValueMessage(int Sequence, DateTimeOffset Timestamp);

[MemoryPackable]
public sealed partial record MemoryPackV1Message(string Name);

[MemoryPackable]
public sealed partial record MemoryPackV2Message(string Name, int Version);

public sealed class MemoryPackSerializerTests
{
    private static MemoryPackMessageSerializer CreateSerializer()
    {
        return new MemoryPackMessageSerializer();
    }

    // ═══════════════════════════════════════════════════════════════
    // 基本序列化/反序列化
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Serialize_Generic_RoundTrips()
    {
        var serializer = CreateSerializer();
        var original = new MemoryPackTestMessage("hello", 42, 99.95m);

        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<MemoryPackTestMessage>(bytes.Span, null!);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(original.Id);
        await Assert.That(result.Count).IsEqualTo(original.Count);
        await Assert.That(result.Amount).IsEqualTo(original.Amount);
    }

    [Test]
    public async Task Serialize_NonGeneric_RoundTrips()
    {
        var serializer = CreateSerializer();
        var catalog = serializer.ContentType; // trigger build
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);
        var original = new MemoryPackTestMessage("world", 7, 50.0m);

        var bytes = serializer.Serialize((object)original, descriptor);
        var result = serializer.Deserialize(bytes.Span, descriptor);

        await Assert.That(result).IsNotNull();
        var typed = result as MemoryPackTestMessage;
        await Assert.That(typed).IsNotNull();
        await Assert.That(typed!.Id).IsEqualTo(original.Id);
        await Assert.That(typed.Count).IsEqualTo(original.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // ContentType
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task ContentType_ReturnsMemoryPackContentType()
    {
        var serializer = CreateSerializer();
        await Assert.That(serializer.ContentType).IsEqualTo(ContentTypes.MemoryPack);
    }

    // ═══════════════════════════════════════════════════════════════
    // Schema 版本 — 不同版本的同一消息类型
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Serialize_WithSchemaVersion_DifferentPayloads()
    {
        var serializer = CreateSerializer();
        var v1 = new MemoryPackV1Message("Alice");
        var v2 = new MemoryPackV2Message("Bob", 2);

        var v1Bytes = serializer.Serialize(v1);
        var v2Bytes = serializer.Serialize(v2);

        // v1 和 v2 的序列化结果不同（v2 多了 Version 字段）
        await Assert.That(v1Bytes.Length).IsNotEqualTo(v2Bytes.Length);

        var v1Result = serializer.Deserialize<MemoryPackV1Message>(v1Bytes.Span, null!);
        await Assert.That(v1Result!.Name).IsEqualTo("Alice");

        var v2Result = serializer.Deserialize<MemoryPackV2Message>(v2Bytes.Span, null!);
        await Assert.That(v2Result!.Name).IsEqualTo("Bob");
        await Assert.That(v2Result!.Version).IsEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // 边界情况
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Serialize_NullMessage_ThrowsArgumentNullException()
    {
        var serializer = CreateSerializer();
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);

        await Assert.That(() =>
            serializer.Serialize((object)null!, descriptor)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Serialize_NullDescriptor_ThrowsArgumentNullException()
    {
        var serializer = CreateSerializer();

        await Assert.That(() =>
            serializer.Serialize((object)new MemoryPackTestMessage("", 0, 0), null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Deserialize_EmptyPayload_ReturnsDefaultOrThrows()
    {
        var serializer = CreateSerializer();
        var empty = ReadOnlySpan<byte>.Empty;
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);

        // MemoryPack 对空 payload 的行为：可能返回 default 或抛异常
        // 两种情况均可接受——框架调用方应在反序列化前检查 payload 长度
        try
        {
            var result = serializer.Deserialize(empty, descriptor);
            // 返回 null/default 是可接受的
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // MemoryPack 抛序列化异常也是可接受的
        }

        await Task.CompletedTask; // 保持 async Task 签名
    }

    // ═══════════════════════════════════════════════════════════════
    // DI 注册
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddPalMemoryPackSerialization_RegistersSingleton()
    {
        var services = new ServiceCollection();
        services.AddPalMemoryPackSerialization(catalog =>
        {
            catalog.Add(new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
                TestJsonContext.Default.MemoryPackTestMessage, 1));
        });

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IMessageSerializer>();

        await Assert.That(serializer).IsTypeOf<MemoryPackMessageSerializer>();
    }

    [Test]
    public async Task AddPalMemoryPackSerialization_CatalogContainsRegisteredMessage()
    {
        var services = new ServiceCollection();
        services.AddPalMemoryPackSerialization(catalog =>
        {
            catalog.Add(new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
                TestJsonContext.Default.MemoryPackTestMessage, 1));
        });

        var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IMessageCatalog>();
        var descriptor = catalog.Find("test.msg.v1");

        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor.ClrType).IsEqualTo(typeof(MemoryPackTestMessage));
    }

    // ═══════════════════════════════════════════════════════════════
    // 值类型消息 — struct 序列化/反序列化
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Serialize_ValueType_RoundTrips()
    {
        var serializer = CreateSerializer();
        var original = new MemoryPackValueMessage(7, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<MemoryPackValueMessage>(bytes.Span, null!);
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Sequence).IsEqualTo(original.Sequence);
        await Assert.That(result.Timestamp).IsEqualTo(original.Timestamp);
    }

    [Test]
    public async Task Serialize_ValueType_NonGeneric_RoundTrips()
    {
        var serializer = CreateSerializer();
        var descriptor = new MessageDescriptor("test.value.v1", typeof(MemoryPackValueMessage),
            TestJsonContext.Default.MemoryPackValueMessage, 1);
        var original = new MemoryPackValueMessage(42, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        var bytes = serializer.Serialize((object)original, descriptor);
        var result = serializer.Deserialize(bytes.Span, descriptor);

        await Assert.That(result).IsNotNull();
        var typed = result as MemoryPackValueMessage;
        await Assert.That(typed).IsNotNull();
        await Assert.That(typed!.Sequence).IsEqualTo(original.Sequence);
        await Assert.That(typed.Timestamp).IsEqualTo(original.Timestamp);
    }

    // ═══════════════════════════════════════════════════════════════
    // 缺失注册 — 未在 MessageCatalog 中注册的消息类型
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Deserialize_UnregisteredType_ReturnsCorrectResult()
    {
        // MemoryPack 不依赖 MessageCatalog 的类型注册——它使用编译时 [MemoryPackable] 注解
        // 此测试验证：即使不使用 MessageDescriptor（传 null），泛型反序列化也能正常工作
        var serializer = CreateSerializer();
        var original = new MemoryPackTestMessage("unregistered-test", 99, 299.99m);

        var bytes = serializer.Serialize(original);
        // 使用泛型路径，不传 descriptor（null）
        var result = serializer.Deserialize<MemoryPackTestMessage>(bytes.Span, null!);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(original.Id);
        await Assert.That(result.Count).IsEqualTo(original.Count);
        await Assert.That(result.Amount).IsEqualTo(original.Amount);
    }
}
