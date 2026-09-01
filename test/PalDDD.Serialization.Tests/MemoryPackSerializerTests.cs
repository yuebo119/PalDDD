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
        // v39 P3：泛型 Deserialize 补 null descriptor 守卫后，round-trip 断言需传有效
        // descriptor（MemoryPack contentType）
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1,
            contentType: ContentTypes.MemoryPack);

        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<MemoryPackTestMessage>(bytes.Span, descriptor);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(original.Id);
        await Assert.That(result.Count).IsEqualTo(original.Count);
        await Assert.That(result.Amount).IsEqualTo(original.Amount);
    }

    [Test]
    public async Task Serialize_NonGeneric_RoundTrips()
    {
        var serializer = CreateSerializer();
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1,
            contentType: ContentTypes.MemoryPack);
        var original = new MemoryPackTestMessage("world", 7, 50.0m);

        var bytes = serializer.Serialize((object)original, descriptor);
        var result = serializer.Deserialize(bytes.Span, descriptor);

        await Assert.That(result).IsNotNull();
        var typed = result as MemoryPackTestMessage;
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
        // v39 P3：泛型 Deserialize 补 null descriptor 守卫后，需传有效 descriptor
        var v1Descriptor = new MessageDescriptor("test.v1.v1", typeof(MemoryPackV1Message),
            TestJsonContext.Default.MemoryPackV1Message, 1,
            contentType: ContentTypes.MemoryPack);
        var v2Descriptor = new MessageDescriptor("test.v2.v1", typeof(MemoryPackV2Message),
            TestJsonContext.Default.MemoryPackV2Message, 1,
            contentType: ContentTypes.MemoryPack);

        var v1Bytes = serializer.Serialize(v1);
        var v2Bytes = serializer.Serialize(v2);

        // v1 和 v2 的序列化结果不同（v2 多了 Version 字段）
        await Assert.That(v1Bytes.Length).IsNotEqualTo(v2Bytes.Length);

        var v1Result = serializer.Deserialize<MemoryPackV1Message>(v1Bytes.Span, v1Descriptor);
        await Assert.That(v1Result!.Name).IsEqualTo("Alice");

        var v2Result = serializer.Deserialize<MemoryPackV2Message>(v2Bytes.Span, v2Descriptor);
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
            TestJsonContext.Default.MemoryPackTestMessage, 1,
            contentType: ContentTypes.MemoryPack);

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
    public async Task Deserialize_EmptyPayload_ThrowsMemoryPackException()
    {
        var serializer = CreateSerializer();
        var emptyBytes = Array.Empty<byte>();
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1,
            contentType: ContentTypes.MemoryPack);

        // MemoryPack 对空 payload 应抛序列化异常（而非静默返回 default）。
        // 框架调用方应在反序列化前检查 payload 长度，但序列化器本身也应拒绝无效输入。
        await Assert.That(() => serializer.Deserialize(emptyBytes, descriptor)).Throws<MemoryPackSerializationException>();
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
                TestJsonContext.Default.MemoryPackTestMessage, 1,
                contentType: ContentTypes.MemoryPack));
        });

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IMessageSerializer>();
        var second = provider.GetRequiredService<IMessageSerializer>();

        await Assert.That(first).IsTypeOf<MemoryPackMessageSerializer>();
        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task AddPalMemoryPackSerialization_CatalogContainsRegisteredMessage()
    {
        var services = new ServiceCollection();
        services.AddPalMemoryPackSerialization(catalog =>
        {
            catalog.Add(new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
                TestJsonContext.Default.MemoryPackTestMessage, 1,
                contentType: ContentTypes.MemoryPack));
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
        // v39 P3：泛型 Deserialize 补 null descriptor 守卫后，需传有效 descriptor
        var descriptor = new MessageDescriptor("test.value.v1", typeof(MemoryPackValueMessage),
            TestJsonContext.Default.MemoryPackValueMessage, 1,
            contentType: ContentTypes.MemoryPack);

        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<MemoryPackValueMessage>(bytes.Span, descriptor);
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Sequence).IsEqualTo(original.Sequence);
        await Assert.That(result.Timestamp).IsEqualTo(original.Timestamp);
    }

    [Test]
    public async Task Serialize_ValueType_NonGeneric_RoundTrips()
    {
        var serializer = CreateSerializer();
        var descriptor = new MessageDescriptor("test.value.v1", typeof(MemoryPackValueMessage),
            TestJsonContext.Default.MemoryPackValueMessage, 1,
            contentType: ContentTypes.MemoryPack);
        var original = new MemoryPackValueMessage(42, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        var bytes = serializer.Serialize((object)original, descriptor);
        var result = serializer.Deserialize(bytes.Span, descriptor);

        await Assert.That(result).IsNotNull();
        var typed = result as MemoryPackValueMessage;
        await Assert.That(typed!.Sequence).IsEqualTo(original.Sequence);
        await Assert.That(typed.Timestamp).IsEqualTo(original.Timestamp);
    }

    // ═══════════════════════════════════════════════════════════════
    // 缺失注册 — 未在 MessageCatalog 中注册的消息类型
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Deserialize_GenericPath_WorksIndependentOfCatalog()
    {
        // MemoryPack 不依赖 MessageCatalog 的类型注册——它使用编译时 [MemoryPackable] 注解。
        // 此测试验证：泛型反序列化传入 descriptor（v39 P3 起 null 已被入口守卫拒绝）时，
        // 反序列化本体仍不查询 catalog，编译时类型路径独立于注册机制工作
        var serializer = CreateSerializer();
        var original = new MemoryPackTestMessage("unregistered-test", 99, 299.99m);
        var descriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1,
            contentType: ContentTypes.MemoryPack);

        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<MemoryPackTestMessage>(bytes.Span, descriptor);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(original.Id);
        await Assert.That(result.Count).IsEqualTo(original.Count);
        await Assert.That(result.Amount).IsEqualTo(original.Amount);
    }

    [Test]
    public async Task Deserialize_Generic_NullDescriptor_ThrowsArgumentNullException()
    {
        // v39 P3：泛型 Deserialize 补 null descriptor 守卫（对齐姊妹非泛型入口）——
        // 原 null 静默放行（ContentType 校验被跳过且反序列化照常成功），现入口快速失败
        var serializer = CreateSerializer();
        var bytes = serializer.Serialize(new MemoryPackTestMessage("x", 1, 2m));

        await Assert.That(() =>
            serializer.Deserialize<MemoryPackTestMessage>(bytes.Span, null!)).Throws<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════
    // ContentType 断链防护（八轮评审 P2）——错误 ContentType 的 descriptor 必须入口拒绝
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Serialize_NonGeneric_JsonContentTypeDescriptor_ThrowsInvalidOperationException()
    {
        var serializer = CreateSerializer();
        // 故意不传 contentType —— 沿用 Json 默认值，模拟注册时漏传的断链场景
        var jsonDescriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);
        var original = new MemoryPackTestMessage("x", 1, 2m);

        await Assert.That(() =>
            serializer.Serialize((object)original, jsonDescriptor)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Deserialize_NonGeneric_JsonContentTypeDescriptor_ThrowsInvalidOperationException()
    {
        var serializer = CreateSerializer();
        var jsonDescriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);
        var payload = new byte[] { 1, 2, 3 };

        await Assert.That(() =>
            serializer.Deserialize(payload.AsSpan(), jsonDescriptor)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Serialize_Generic_JsonContentTypeDescriptor_ThrowsInvalidOperationException()
    {
        var serializer = CreateSerializer();
        var jsonDescriptor = new MessageDescriptor("test.msg.v1", typeof(MemoryPackTestMessage),
            TestJsonContext.Default.MemoryPackTestMessage, 1);
        var original = new MemoryPackTestMessage("x", 1, 2m);

        // 泛型路径 descriptor 非 null 时同样校验
        await Assert.That(() =>
            serializer.Serialize(original, jsonDescriptor)).Throws<InvalidOperationException>();
    }
}
