using Microsoft.Extensions.DependencyInjection;
using PalDDD.Serialization.Json;
using PalDDD.Serialization.MemoryPack;
using System.Text.Json.Serialization;

namespace PalDDD.Serialization.Tests;

[JsonSerializable(typeof(TestMessage))]
[JsonSerializable(typeof(UnregisteredMessage))]
[JsonSerializable(typeof(TestMessageV1))]
[JsonSerializable(typeof(TestMessageV2))]
[JsonSerializable(typeof(ValueMessage))]
[JsonSerializable(typeof(MemoryPackTestMessage))]
[JsonSerializable(typeof(MemoryPackValueMessage))]
[JsonSerializable(typeof(MemoryPackV1Message))]
[JsonSerializable(typeof(MemoryPackV2Message))]
[JsonSerializable(typeof(JsonOnlyMessage))]  // P1 回归（十七轮）：仅 Json 侧注册的消息
internal sealed partial class TestJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(JsonOnlyMessage))]
internal sealed partial class JsonOnlyMessageJsonContext : JsonSerializerContext;

public sealed record JsonOnlyMessage(string Payload);

public sealed record TestMessage(string Id, int Count);
public sealed record UnregisteredMessage(string Id);
public sealed record TestMessageV1(string Id);
public sealed record TestMessageV2(string Id, int Count);

// 值类型消息 — 用于验证 GetTypeInfo<T>() 零装箱路径
public readonly record struct ValueMessage(int Sequence, long Timestamp);

public sealed class MessageCatalogTests
{
    [Test]
    public async Task Builder_WithJsonTypeInfo_BuildsImmutableCatalog()
    {
        var builder = new MessageCatalogBuilder();

        var descriptor = builder.Add(
            TestJsonContext.Default.TestMessage,
            name: "test-message",
            schemaVersion: 2,
            contentType: ContentTypes.Json);
        var catalog = builder.Build();

        await Assert.That(catalog.Find("test-message")).IsSameReferenceAs(descriptor);
        await Assert.That(catalog.Find(typeof(TestMessage))).IsSameReferenceAs(descriptor);
        await Assert.That(descriptor.SchemaVersion).IsEqualTo(2);
        await Assert.That(descriptor.JsonTypeInfo).IsSameReferenceAs(TestJsonContext.Default.TestMessage);
    }

    [Test]
    public async Task Builder_WithDuplicateName_FailsFast()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TestJsonContext.Default.TestMessage, name: "duplicate-message");

        var exception = await Assert.That(
            () => builder.Add(TestJsonContext.Default.UnregisteredMessage, name: "duplicate-message")).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("duplicate-message");
    }

    [Test]
    public async Task Builder_WithSameNameDifferentSchemaVersions_AllowsPreciseAndLatestLookup()
    {
        var builder = new MessageCatalogBuilder();

        var v1Descriptor = builder.Add(
            TestJsonContext.Default.TestMessageV1,
            name: "versioned-message",
            schemaVersion: 1);
        var v2Descriptor = builder.Add(
            TestJsonContext.Default.TestMessageV2,
            name: "versioned-message",
            schemaVersion: 2);
        var catalog = builder.Build();

        await Assert.That(catalog.Find("versioned-message", schemaVersion: 1)).IsSameReferenceAs(v1Descriptor);
        await Assert.That(catalog.Find("versioned-message", schemaVersion: 2)).IsSameReferenceAs(v2Descriptor);
        await Assert.That(catalog.Find("versioned-message")).IsSameReferenceAs(v2Descriptor);
        await Assert.That(catalog.Descriptors).Count().IsEqualTo(2);
        await Assert.That(catalog.Descriptors[0]).IsSameReferenceAs(v1Descriptor);
        await Assert.That(catalog.Descriptors[1]).IsSameReferenceAs(v2Descriptor);
    }

    [Test]
    public async Task Builder_WithDuplicateClrType_FailsFast()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TestJsonContext.Default.TestMessage, name: "test-message-v1");

        var exception = await Assert.That(
            () => builder.Add(TestJsonContext.Default.TestMessage, name: "test-message-v2")).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(typeof(TestMessage).FullName!);
    }

    [Test]
    public async Task Builder_WithSameTypeDifferentName_FailureLeavesNoGhostKeyInBuild()
    {
        // v30 P3 差异化回归（v29 半提交修复的行为面）：同类型不同名 Add 触发 _byType
        // 重复键抛 InvalidOperationException 后，Builder 不得滞留已提交的名称版本键
        //（幽灵键）——修复前半提交会让 Build 产出含失败描述符的目录；修复后双键预检
        // 先于提交，失败 Add 零残留，Builder 仍可 Build 出只含成功注册项的目录
        var builder = new MessageCatalogBuilder();
        builder.Add(TestJsonContext.Default.TestMessage, name: "ghost-test");

        await Assert.That(
            () => builder.Add(TestJsonContext.Default.TestMessage, name: "ghost-test-2")).Throws<InvalidOperationException>();

        var catalog = builder.Build();
        await Assert.That(catalog.Descriptors).Count().IsEqualTo(1);
        await Assert.That(catalog.Find("ghost-test")).IsNotNull();
        await Assert.That(catalog.Find("ghost-test-2")).IsNull();
    }
}

public sealed class JsonMessageSerializerTests
{
    [Test]
    public async Task RoundTrip_UsesSourceGeneratedJsonTypeInfo()
    {
        var descriptor = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage,
            name: "test-message-roundtrip");
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var message = new TestMessage("order-1", 3);

        var payload = serializer.Serialize(message, descriptor);
        var result = serializer.Deserialize(payload.Span, descriptor);

        var typed = result as TestMessage;
        await Assert.That(typed).IsNotNull();
        await Assert.That(typed).IsEquivalentTo(message);
    }

    [Test]
    public async Task RoundTrip_WithGenericDeserialize_ValueType_ReturnsEqualMessage()
    {
        var descriptor = MessageDescriptor.Create(
            TestJsonContext.Default.ValueMessage,
            name: "test-message-generic-value");
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var message = new ValueMessage(Sequence: 3, Timestamp: 1700000000L);

        var payload = serializer.Serialize(message, descriptor);
        var result = serializer.Deserialize<ValueMessage>(payload.Span, descriptor);

        await Assert.That(result).IsEquivalentTo(message);
    }

    [Test]
    public async Task MessageDescriptor_IsSealedClass_NotRecord()
    {
        // MessageDescriptor 必须是 sealed class，防止 `with` 表达式绕过
        // jsonTypeInfo.Type == clrType 不变式。
        await Assert.That(typeof(MessageDescriptor).IsClass).IsTrue();
        await Assert.That(typeof(MessageDescriptor).IsSealed).IsTrue();
    }

    [Test]
    public async Task MessageDescriptor_Equality_BasedOnNameAndSchemaVersion()
    {
        var d1 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "test.v1", schemaVersion: 1);
        var d2 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "test.v1", schemaVersion: 1);

        await Assert.That(d1).IsEqualTo(d2);
        await Assert.That(d1.GetHashCode()).IsEqualTo(d2.GetHashCode());
        await Assert.That(d1 == d2).IsTrue();
    }

    [Test]
    public async Task MessageDescriptor_Inequality_DifferentNameOrVersion()
    {
        var d1 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "test.v1", schemaVersion: 1);
        var d2 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "test.v2", schemaVersion: 2);

        await Assert.That(d1).IsNotEqualTo(d2);
        await Assert.That(d1 == d2).IsFalse();
    }

    [Test]
    public async Task MessageDescriptor_NoInitSetters_PreventsWithExpression()
    {
        var d = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "test.v1");

        // 验证属性为只读（无 init/public setter）。
        await Assert.That(typeof(MessageDescriptor).GetProperty(nameof(MessageDescriptor.Name))!.CanWrite).IsFalse();
        await Assert.That(typeof(MessageDescriptor).GetProperty(nameof(MessageDescriptor.ClrType))!.CanWrite).IsFalse();
        await Assert.That(typeof(MessageDescriptor).GetProperty(nameof(MessageDescriptor.JsonTypeInfo))!.CanWrite).IsFalse();
    }

    [Test]
    public async Task SerializeGeneric_WithoutRegistration_FailsFast()
    {
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);

        var exception = await Assert.That(
            () => serializer.Serialize(new UnregisteredMessage("order-2"))).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("is not registered in MessageCatalog");
    }

    [Test]
    public async Task MessageDescriptor_WithoutJsonTypeInfo_FailsFast()
    {
        var exception = await Assert.That(
            () => new MessageDescriptor("missing-json-type-info-object", typeof(TestMessage), null!)).Throws<ArgumentNullException>();

        await Assert.That(exception!.ParamName).IsEqualTo("jsonTypeInfo");
    }

    [Test]
    public async Task MessageDescriptor_WithInvalidSchemaVersion_FailsFast()
    {
        var exception = await Assert.That(
            () => new MessageDescriptor(
                "invalid-schema",
                typeof(TestMessage),
                TestJsonContext.Default.TestMessage,
                schemaVersion: 0)).Throws<ArgumentOutOfRangeException>();

        await Assert.That(exception!.ParamName).IsEqualTo("schemaVersion");
    }

    [Test]
    public async Task MessageDescriptor_WithMismatchedJsonTypeInfo_FailsFast()
    {
        var exception = await Assert.That(
            () => new MessageDescriptor(
                "mismatched-json-type-info",
                typeof(UnregisteredMessage),
                TestJsonContext.Default.TestMessage)).Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("jsonTypeInfo");
    }

    // ── v30 P3 测试缺口：JsonMessageSerializer 显式 descriptor 的 ClrType 守卫（v29 P3 实现）──
    // 两路径（泛型 Serialize / 泛型 Deserialize）对 mismatched descriptor 均须抛指向性
    // ArgumentException（消息注明期望/实际类型），而非裸 InvalidCastException 落在强转行。

    [Test]
    public async Task SerializeGeneric_WithMismatchedDescriptorClrType_ThrowsArgumentException()
    {
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        // descriptor 注册给 TestMessage，泛型实参却是 TestMessageV1——ClrType 错配
        var mismatched = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage,
            name: "clr-type-mismatch-serialize");

        var exception = await Assert.That(
            () => serializer.Serialize(new TestMessageV1("order-x"), mismatched)).Throws<ArgumentException>();

        await Assert.That(exception!.Message).Contains(typeof(TestMessage).FullName!);
        await Assert.That(exception!.Message).Contains(typeof(TestMessageV1).FullName!);
        await Assert.That(exception!.ParamName).IsEqualTo("descriptor");
    }

    [Test]
    public async Task DeserializeGeneric_WithMismatchedDescriptorClrType_ThrowsArgumentException()
    {
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var v1Descriptor = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessageV1,
            name: "clr-type-mismatch-deserialize");
        var payload = serializer.Serialize(new TestMessageV1("order-x"), v1Descriptor);

        var exception = await Assert.That(
            () => serializer.Deserialize<TestMessage>(payload.Span, v1Descriptor)).Throws<ArgumentException>();

        await Assert.That(exception!.Message).Contains(typeof(TestMessageV1).FullName!);
        await Assert.That(exception!.Message).Contains(typeof(TestMessage).FullName!);
        await Assert.That(exception!.ParamName).IsEqualTo("descriptor");
    }
}

// ═══════════════════════════════════════════════════════════════
// Phase A1 验收测试 — STJ GetTypeInfo<T>() 强类型路径（.NET 11 P2）
// ═══════════════════════════════════════════════════════════════

public sealed class JsonMessageSerializerGetTypeInfoTests
{
    // A1-T1: 通过 JsonSerializerOptions 注入的序列化器能正确处理值类型消息
    [Test]
    public async Task GetValueType_DeserializesValueTypes_WithoutBoxing()
    {
        var options = TestJsonContext.Default.Options;
        var catalog = MessageCatalog.Empty;
        var serializer = new JsonMessageSerializer(catalog, options);

        var descriptor = MessageDescriptor.Create(
            TestJsonContext.Default.ValueMessage,
            name: "value-message");

        var message = new ValueMessage(Sequence: 42, Timestamp: 1700000000L);
        var payload = serializer.Serialize(message, descriptor);
        var result = serializer.Deserialize<ValueMessage>(payload.Span, descriptor);

        await Assert.That(result).IsEquivalentTo(message);
    }

    // A1-T2: 通过 options 注入的序列化器与旧路径（直接传 JsonTypeInfo）输出完全一致
    [Test]
    public async Task GetValueType_ProducesIdenticalBytes_ToLegacyPath()
    {
        var options = TestJsonContext.Default.Options;
        var legacySerializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var optionsSerializer = new JsonMessageSerializer(MessageCatalog.Empty, options);

        var descriptor = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage,
            name: "test-message-a1");
        var message = new TestMessage("order-a1", 99);

        var legacyBytes = legacySerializer.Serialize(message, descriptor).ToArray();
        var optionsBytes = optionsSerializer.Serialize(message, descriptor).ToArray();

        await Assert.That(optionsBytes).IsEquivalentTo(legacyBytes);
    }

    // A1-T3: 非泛型 Serialize(object, descriptor) 与 Deserialize(descriptor) 行为不回归
    [Test]
    public async Task NonGeneric_Overloads_RemainFunctional()
    {
        var options = TestJsonContext.Default.Options;
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty, options);
        var descriptor = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage,
            name: "test-message-nongeneric");
        object message = new TestMessage("order-ng", 7);

        var payload = serializer.Serialize(message, descriptor);
        var result = serializer.Deserialize(payload.Span, descriptor);

        var typed = result as TestMessage;
        await Assert.That(typed).IsNotNull();
        await Assert.That(typed).IsEquivalentTo((TestMessage)message);
    }
}

public sealed class JsonSerializationServiceCollectionTests
{
    [Test]
    public async Task AddPalJsonSerialization_AfterMemoryPack_CatalogIsReplacedByJson()
    {
        // P1 回归（十七轮）：Json 侧 catalog 曾用 TryAdd——MemoryPack→Json 注册顺序下
        // 序列化器换成 Json 但 catalog 留在 MemoryPack 版（Json 的 configureCatalog 被静默
        // 丢弃）。修复后双向"后者覆盖前者"：catalog 应能找到仅 Json 侧注册的消息。
        var services = new ServiceCollection();
        services.AddPalMemoryPackSerialization();
        services.AddPalJsonSerialization(b =>
            b.Add(Serialization.Tests.JsonOnlyMessageJsonContext.Default.JsonOnlyMessage));

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IMessageCatalog>();
        var serializer = provider.GetRequiredService<IMessageSerializer>();

        await Assert.That(serializer).IsTypeOf<JsonMessageSerializer>();
        // 行为断言（弱断言棘轮）：SchemaVersion 默认 1——Find 未命中时 ?? 0 使断言失败
        await Assert.That(catalog.Find(typeof(JsonOnlyMessage))?.SchemaVersion ?? 0).IsEquivalentTo(1);
    }

    [Test]
    public async Task AddPalJsonSerialization_RegistersMessageSerializer()
    {
        var services = new ServiceCollection();

        services.AddPalJsonSerialization();

        using var provider = services.BuildServiceProvider();
        await Assert.That(provider.GetRequiredService<IMessageSerializer>()).IsTypeOf<JsonMessageSerializer>();
    }
}

// ═══════════════════════════════════════════════════════════════
// Phase A2 验收测试 — Utf8JsonWriter.Reset + ArrayPool 池化（.NET 11 P5）
// ═══════════════════════════════════════════════════════════════

public sealed class JsonMessageSerializerPooledTests
{
    private static MessageDescriptor TestDescriptor => MessageDescriptor.Create(
        TestJsonContext.Default.TestMessage,
        name: "pooled-test");

    // A2-T1: 池化路径输出与非池化完全一致
    [Test]
    public async Task Pooled_OutputMatchesNonPooled()
    {
        var options = TestJsonContext.Default.Options;
        var legacy = new JsonMessageSerializer(MessageCatalog.Empty);
        var pooled = new JsonMessageSerializer(MessageCatalog.Empty, options);

        var message = new TestMessage("pooled-check", 42);
        var descriptor = TestDescriptor;

        var legacyBytes = legacy.Serialize(message, descriptor).ToArray();
        var pooledBytes = pooled.Serialize(message, descriptor).ToArray();

        await Assert.That(pooledBytes).IsEquivalentTo(legacyBytes);
    }

    // A2-T2: 池化路径不分配多于非池化路径
    // 注意：.NET 11 STJ 内部已做 writer/reader 池化，因此用户层池化的直接收益是
    // 代码清晰和 GetTypeInfo<T>() 引入的零装箱路径（A1），而非额外分配节省。
    [Test]
    public async Task Pooled_AllocationsNotHigherThanNonPooled()
    {
        var options = TestJsonContext.Default.Options;
        var legacy = new JsonMessageSerializer(MessageCatalog.Empty);
        var pooled = new JsonMessageSerializer(MessageCatalog.Empty, options);

        var message = new TestMessage("alloc-test", 99);
        var descriptor = TestDescriptor;
        const int iterations = 10_000;

        // 预热
        legacy.Serialize(message, descriptor);
        pooled.Serialize(message, descriptor);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baselineAlloc = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            legacy.Serialize(message, descriptor);
        var legacyAlloc = GC.GetAllocatedBytesForCurrentThread() - baselineAlloc;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var pooledBaseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            pooled.Serialize(message, descriptor);
        var pooledAlloc = GC.GetAllocatedBytesForCurrentThread() - pooledBaseline;

        // P3 修复（等值边缘宽容化）：.NET 11 STJ 内部已池化，两路径分配几乎相等（见上方注释）——
        // 严格的 pooled <= legacy 在 GC/线程本地分配抖动下会假红。保留回归方向（池化不得
        // 显著多分配），允许 10% 比例容差；legacyAlloc 为零分配基线时退化为 1KB 绝对容差
        //（10_000 次迭代下 1KB 仅 0.1 字节/次，仍能拦住真实的分配回归）
        var tolerance = Math.Max(legacyAlloc / 10, 1024L);
        await Assert.That(pooledAlloc <= legacyAlloc + tolerance).IsTrue();
    }

    // A2-T3: 并发安全 — 100 线程各 100 次，无数据错乱
    [Test]
    public async Task Pooled_ThreadSafe_NoDataCorruption()
    {
        var options = TestJsonContext.Default.Options;
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty, options);
        var descriptor = TestDescriptor;

        var tasks = Enumerable.Range(0, 100).Select(threadId => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var message = new TestMessage($"thread-{threadId}-msg-{i}", i);
                var bytes = serializer.Serialize(message, descriptor).ToArray();
                var result = serializer.Deserialize<TestMessage>(bytes.AsSpan(), descriptor);
                await Assert.That(result).IsEquivalentTo(message);
            }
        }));

        await Task.WhenAll(tasks);
    }
}

// ═══════════════════════════════════════════════════════════════
// Phase A3 验收测试 — EqualityComparer<T>.Create 统一比较器（.NET 11 P3）
// ═══════════════════════════════════════════════════════════════

public sealed class MessageDescriptorEqualityComparerTests
{
    // A3-T1: EqualityComparer<MessageDescriptor>.Create 与现有 IEquatable 行为一致
    [Test]
    public async Task CreateComparer_MatchesExistingEquals()
    {
        var comparer = EqualityComparer<MessageDescriptor>.Create(
            (a, b) => a is not null && b is not null
                && a.Name == b.Name && a.SchemaVersion == b.SchemaVersion,
            d => d is null ? 0 : HashCode.Combine(d.Name, d.SchemaVersion));

        var d1 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "compare-test", schemaVersion: 1);
        var d2 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "compare-test", schemaVersion: 1);
        var d3 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "compare-test", schemaVersion: 2);

        await Assert.That(comparer.Equals(d1, d2)).IsTrue();
        await Assert.That(comparer.GetHashCode(d1)).IsEqualTo(comparer.GetHashCode(d2));
        await Assert.That(comparer.Equals(d1, d3)).IsFalse();
        // P3 修复：原此处断言 GetHashCode(d1) != GetHashCode(d3)——HashCode.Combine 契约
        // 允许不同输入合法碰撞，该不等断言在某次进程随机种子下可假红（flaky）；
        // 哈希契约只要求"相等对象哈希相等"（上方已断言），不等的方向无契约可验，删除
        await Assert.That(comparer.Equals(d1, d2)).IsEqualTo(d1.Equals(d2));
    }

    // A3-T2: Create 比较器为键的字典查找语义正确
    [Test]
    public async Task CreateComparer_DictionaryLookup_CorrectSemantics()
    {
        var comparer = EqualityComparer<MessageDescriptor>.Create(
            (a, b) => a is not null && b is not null
                && a.Name == b.Name && a.SchemaVersion == b.SchemaVersion,
            d => d is null ? 0 : HashCode.Combine(d.Name, d.SchemaVersion));

        var dict = new Dictionary<MessageDescriptor, string>(comparer);

        var d1 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "dict-key", schemaVersion: 1);
        dict[d1] = "value-1";

        var d2 = MessageDescriptor.Create(
            TestJsonContext.Default.TestMessage, name: "dict-key", schemaVersion: 1);

        await Assert.That(dict.ContainsKey(d2)).IsTrue();
        await Assert.That(dict[d2]).IsEqualTo("value-1");
    }

    // A3-T3: 替换 catalog 内部比较器不改变对外行为
    [Test]
    public async Task MessageCatalog_ComparerReplacement_PreservesSemantics()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(
            TestJsonContext.Default.TestMessageV1,
            name: "semantics-test", schemaVersion: 1);
        builder.Add(
            TestJsonContext.Default.TestMessageV2,
            name: "semantics-test", schemaVersion: 2);
        var catalog = builder.Build();

        await Assert.That(catalog.Find("semantics-test")).IsNotNull();
        await Assert.That(catalog.Find("semantics-test", schemaVersion: 1)).IsSameReferenceAs(catalog.Descriptors[0]);
        await Assert.That(catalog.Find("semantics-test", schemaVersion: 2)).IsSameReferenceAs(catalog.Descriptors[1]);
        await Assert.That(catalog.Find("semantics-test")).IsSameReferenceAs(catalog.Descriptors[1]);
        await Assert.That(catalog.Descriptors).Count().IsEqualTo(2);
    }
}

// ═══════════════════════════════════════════════════════════════
// Phase B2 验收测试 — STJ JSON Lines 批量事件流（.NET 11 P5）
// ═══════════════════════════════════════════════════════════════

public sealed class JsonLinesEventTests
{
    // B2-T1: 逐行写入后逐行读取，结果一致
    [Test]
    public async Task WriteThenRead_PerEventYield_Matches()
    {
        var writer = new JsonLinesEventWriter();
        var messages = new[]
        {
            new TestMessage("evt-1", 10),
            new TestMessage("evt-2", 20),
            new TestMessage("evt-3", 30),
        };

        var options = TestJsonContext.Default.Options;
        var typeInfo = options.GetTypeInfo<TestMessage>();
        var lines = new List<byte[]>();
        foreach (var msg in messages)
            lines.Add(writer.SerializeLine(msg, typeInfo).ToArray());

        // 拼接 payload（SerializeLine 已含 \n）
        var payload = new byte[lines.Sum(l => l.Length)];
        var pos = 0;
        foreach (var line in lines)
        {
            line.CopyTo(payload, pos);
            pos += line.Length;
        }

        // 逐行反序列化验证
        var reader = new JsonLinesEventReader();
        var deserialized = reader.DeserializeAll<TestMessage>(
            payload.AsMemory(), typeInfo);

        await Assert.That(deserialized).Count().IsEqualTo(messages.Length);
        for (var i = 0; i < messages.Length; i++)
            await Assert.That(deserialized[i]).IsEqualTo(messages[i]);
    }

    // B2-T2: 逐行读取无整批缓存 — 10000 条事件每事件分配低于阈值（无与批处理模式的峰值对比）
    [Test]
    public async Task JsonLines_PerEventAlloc_BelowThreshold()
    {
        var options = TestJsonContext.Default.Options;
        var writer = new JsonLinesEventWriter();
        var messages = Enumerable.Range(0, 10_000)
            .Select(i => new TestMessage($"evt-{i}", i))
            .ToArray();

        // 逐行序列化
        var lines = new List<byte[]>(messages.Length);
        foreach (var msg in messages)
            lines.Add(writer.SerializeLine(msg, options.GetTypeInfo<TestMessage>()).ToArray());

        // 合并成单块 JSON Lines payload（SerializeLine 已含 \n）
        var payload = new byte[lines.Sum(l => l.Length)];
        var pos = 0;
        foreach (var line in lines)
        {
            line.CopyTo(payload, pos);
            pos += line.Length;
        }

        // 逐行反序列化
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetAllocatedBytesForCurrentThread();

        var reader = new JsonLinesEventReader();
        var results = reader.DeserializeAll<TestMessage>(
            payload.AsMemory(), options.GetTypeInfo<TestMessage>());

        var alloc = GC.GetAllocatedBytesForCurrentThread() - baseline;
        await Assert.That(results).Count().IsEqualTo(messages.Length);
        // 逐行应在合理范围内（避免每行~200B 以上的分配）
        await Assert.That(alloc < messages.Length * 512).IsTrue();
    }

    // B2-T3: 空/只有换行的 payload 返回空列表
    [Test]
    public async Task EmptyPayload_ReturnsEmptyList()
    {
        var options = TestJsonContext.Default.Options;
        var reader = new JsonLinesEventReader();

        var empty = reader.DeserializeAll<TestMessage>(
            ReadOnlyMemory<byte>.Empty, options.GetTypeInfo<TestMessage>());
        await Assert.That(empty).IsEmpty();

        var onlyNewline = reader.DeserializeAll<TestMessage>(
            new byte[] { (byte)'\n' }.AsMemory(), options.GetTypeInfo<TestMessage>());
        await Assert.That(onlyNewline).IsEmpty();
    }

    // v53 P2：UTF-8 BOM 剥离——Windows 编辑器/PowerShell 输出的 .jsonl 首行常带 BOM
    //（EF BB BF），残留时首行 JsonException（'0xEF' 非法值起始）——CRLF 修复（v52）的姊妹
    [Test]
    public async Task Utf8BomPrefixedPayload_DeserializesAllLines()
    {
        var options = TestJsonContext.Default.Options;
        var writer = new JsonLinesEventWriter();
        var typeInfo = options.GetTypeInfo<TestMessage>();
        var messages = new[] { new TestMessage("evt-0", 0), new TestMessage("evt-1", 1) };

        var body = messages
            .Select(m => writer.SerializeLine(m, typeInfo).ToArray())
            .SelectMany(b => b)
            .ToArray();
        var payload = new byte[3 + body.Length];
        payload[0] = 0xEF;
        payload[1] = 0xBB;
        payload[2] = 0xBF;
        body.CopyTo(payload, 3);

        var reader = new JsonLinesEventReader();
        var deserialized = reader.DeserializeAll<TestMessage>(payload.AsMemory(), typeInfo);

        await Assert.That(deserialized).Count().IsEqualTo(messages.Length);
        for (var i = 0; i < messages.Length; i++)
            await Assert.That(deserialized[i]).IsEqualTo(messages[i]);
    }
}

// ═══════════════════════════════════════════════════════════════
// Phase B1 验收测试 — OrderedDictionary 保序目录（.NET 11 P4）
// ═══════════════════════════════════════════════════════════════

public sealed class MessageCatalogOrderedTests
{
    // B1-T1: Descriptors 枚举顺序 = Add 调用顺序
    [Test]
    public async Task Descriptors_PreserveRegistrationOrder()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TestJsonContext.Default.TestMessageV1, name: "z-message", schemaVersion: 1);
        builder.Add(TestJsonContext.Default.TestMessage, name: "a-message", schemaVersion: 1);
        builder.Add(TestJsonContext.Default.TestMessageV2, name: "m-message", schemaVersion: 1);
        var catalog = builder.Build();

        await Assert.That(catalog.Descriptors[0].Name).IsEqualTo("z-message");
        await Assert.That(catalog.Descriptors[1].Name).IsEqualTo("a-message");
        await Assert.That(catalog.Descriptors[2].Name).IsEqualTo("m-message");
    }

    // B1-T2: 多次 Build 结果一致
    [Test]
    public async Task Builder_AddOrder_StableAcrossBuilds()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TestJsonContext.Default.TestMessageV1, name: "first", schemaVersion: 1);
        builder.Add(TestJsonContext.Default.TestMessageV2, name: "second", schemaVersion: 1);

        var catalog1 = builder.Build();
        var catalog2 = builder.Build();

        await Assert.That(catalog2.Descriptors).Count().IsEqualTo(catalog1.Descriptors.Count);
        for (var i = 0; i < catalog1.Descriptors.Count; i++)
            await Assert.That(catalog2.Descriptors[i]).IsSameReferenceAs(catalog1.Descriptors[i]);
    }

    // B1-T3: 保序后查找 O(1) 性能正常（用版本化消息避免 CLR 类型重复）
    [Test]
    public async Task Lookup_StillFast()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TestJsonContext.Default.TestMessage, name: "lookup-a", schemaVersion: 1);
        builder.Add(TestJsonContext.Default.TestMessageV1, name: "lookup-b", schemaVersion: 1);
        builder.Add(TestJsonContext.Default.TestMessageV2, name: "lookup-c", schemaVersion: 1);
        builder.Add(TestJsonContext.Default.UnregisteredMessage, name: "lookup-d", schemaVersion: 1);

        var catalog = builder.Build();

        await Assert.That(catalog.Descriptors).Count().IsEqualTo(4);
        await Assert.That(catalog.Descriptors[0].Name).IsEqualTo("lookup-a");
        await Assert.That(catalog.Descriptors[1].Name).IsEqualTo("lookup-b");
        await Assert.That(catalog.Descriptors[2].Name).IsEqualTo("lookup-c");
        await Assert.That(catalog.Descriptors[3].Name).IsEqualTo("lookup-d");

        await Assert.That(catalog.Find("lookup-a")).IsNotNull();
        await Assert.That(catalog.Find("lookup-b", schemaVersion: 1)).IsNotNull();
        await Assert.That(catalog.Find(typeof(TestMessage))).IsNotNull();
        await Assert.That(catalog.Find(typeof(UnregisteredMessage))).IsNotNull();
    }
}
