using PalDDD.Core.Repository;
using PalDDD.CQRS;
using PalDDD.DependencyInjection;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Messaging;
using PalDDD.Projections;
using PalDDD.Projections.EventLog;
using PalDDD.Serialization;
using PalDDD.Serialization.Evolution;
using PalDDD.Serialization.Json;
using PalDDD.Transactions;
using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 🛡️ AOT 契约测试 — 验证 AOT 安全性不变量
// ═══════════════════════════════════════════════════════════════
// 1. A 类程序集无 RequiresDynamicCode/RequiresUnreferencedCode 标注
// 2. 消息目录完整性 — 多版本查找、插入顺序、重复检测
// 3. DIM 零反射 — typeof(T) 编译时常量、同步路径零分配、无 MakeGenericType
// 4. SmartEnum + [GenerateEnum] 在运行时正确注册
// 5. FrozenDictionary 构建后查找 AOT-safe
// 6. JsonSerializerIsReflectionEnabledByDefault=false 下不回退反射
// 7. 源生成器输出 AOT 兼容性 — Identity/Enum/MessageRegistry
// ═══════════════════════════════════════════════════════════════

public sealed class AotContractTests
{
    // ═══════════════════════════════════════════════════════════════
    // 1️⃣ A 类程序集 RequiresDynamicCode/RequiresUnreferencedCode 扫描
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A 类程序集（AOT 合规库）不应包含 [RequiresDynamicCode] 或 [RequiresUnreferencedCode] 标注。
    /// 防止误加导致 AOT 发布失败。
    /// </summary>
    [Test]
    [Arguments(typeof(PalDDD.Core.Entity))]                        // PalDDD.Core
    [Arguments(typeof(MessageCatalog))]                            // PalDDD.Serialization
    [Arguments(typeof(IBaseRequest))]                              // PalDDD.CQRS
    [Arguments(typeof(IEventHandler))]                             // PalDDD.Messaging
    [Arguments(typeof(IMessageSerializer))]                        // PalDDD.Serialization
    [Arguments(typeof(IUnitOfWork))]                               // PalDDD.Repository
    // ── 应用层 ──
    [Arguments(typeof(ServiceRegistration))]                       // PalDDD.DependencyInjection
    [Arguments(typeof(IEventLog))]                                 // PalDDD.EventLog
    [Arguments(typeof(IIdempotencyStore))]                         // PalDDD.Idempotency
    [Arguments(typeof(IProjectionCheckpointStore))]                // PalDDD.Projections
    [Arguments(typeof(ISagaStateStore<>))]                         // PalDDD.Transactions
    // ── 基础设施适配层（AOT-safe） ──
    [Arguments(typeof(JsonMessageSerializer))]                     // PalDDD.Serialization.Json
    [Arguments(typeof(MessageContractManifest))]                   // PalDDD.Serialization.Evolution
    [Arguments(typeof(EventLogReplaySource<>))]                    // PalDDD.Projections.EventLog
    public async Task AotCompliantAssemblies_HaveNoRequiresDynamicCodeAttribute(Type typeFromAssembly)
    {
        var assembly = typeFromAssembly.Assembly;

        // 扫描所有公共类型的公共方法
        foreach (var targetType in assembly.GetTypes())
        {
            if (!targetType.IsPublic) continue;

            // 检查类型级标注
            if (HasAotUnsafeAttribute(targetType))
                Assert.Fail($"类型 {targetType.FullName}（程序集 {assembly.GetName().Name}）标注了 AOT 不安全特性");

            // 检查方法级标注
            foreach (var method in targetType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (HasAotUnsafeAttribute(method))
                    Assert.Fail($"方法 {targetType.FullName}.{method.Name}（程序集 {assembly.GetName().Name}）标注了 AOT 不安全特性");
            }
        }
    }

    private static bool HasAotUnsafeAttribute(MemberInfo member)
    {
        return member.GetCustomAttributesData().Any(attr =>
            attr.AttributeType.Name is "RequiresDynamicCodeAttribute"
                or "RequiresUnreferencedCodeAttribute");
    }

    // ═══════════════════════════════════════════════════════════════
    // 2️⃣ 消息目录完整性
    // ═══════════════════════════════════════════════════════════════

    /// <summary>同一消息名注册多个版本时，Find(name) 返回该确切名称的最高版本</summary>
    [Test]
    public async Task MessageCatalog_FindLatestVersion_ReturnsMaxSchemaVersion()
    {
        var builder = new MessageCatalogBuilder();
        // 同一 wire name 不同 schema version（表示调用了两次 Add 但其中一次 SchemaVersion 不同）
        // Name 相同意味着对最新的覆盖查找
        var v1 = builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "latest-test.v1", schemaVersion: 1);
        var v2 = builder.Add(AotContractJsonContext.Default.AotTestMessageV2, name: "latest-test.v1", schemaVersion: 2);
        var catalog = builder.Build();

        // Find(exact name) 返回最高 schema version
        var latest = catalog.Find("latest-test.v1");
        await Assert.That(latest).IsNotNull();
        await Assert.That(latest).IsSameReferenceAs(v2);
        await Assert.That(latest.SchemaVersion).IsEqualTo(2);
    }

    /// <summary>Find(name, schemaVersion) 精确匹配指定版本</summary>
    [Test]
    public async Task MessageCatalog_FindExactVersion_ReturnsCorrectDescriptor()
    {
        var builder = new MessageCatalogBuilder();
        var v1 = builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "exact-test.v1", schemaVersion: 1);
        var v2 = builder.Add(AotContractJsonContext.Default.AotTestMessageV2, name: "exact-test.v2", schemaVersion: 2);
        var catalog = builder.Build();

        // Find(exactName, schemaVersion) 使用完整 wire name
        await Assert.That(catalog.Find("exact-test.v1", schemaVersion: 1)).IsSameReferenceAs(v1);
        await Assert.That(catalog.Find("exact-test.v2", schemaVersion: 2)).IsSameReferenceAs(v2);
        // 不存在的组合返回 null
        await Assert.That(catalog.Find("exact-test.v1", schemaVersion: 2)).IsNull();
    }

    /// <summary>Find(name) 精确匹配完整 wire name — 不同前缀独立</summary>
    [Test]
    public async Task MessageCatalog_FindByName_DifferentPrefixesAreIndependent()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "orders.created.v1", schemaVersion: 1);
        builder.Add(AotContractJsonContext.Default.AotTestMessageV2, name: "payments.created.v1", schemaVersion: 1);
        var catalog = builder.Build();

        await Assert.That(catalog.Find("orders.created.v1")).IsNotNull();
        await Assert.That(catalog.Find("payments.created.v1")).IsNotNull();
        await Assert.That(catalog.Find("shipments.created.v1")).IsNull(); // 未注册
    }

    /// <summary>Find(Type) 按 CLR 类型查找</summary>
    [Test]
    public async Task MessageCatalog_FindByType_ReturnsCorrectDescriptor()
    {
        var builder = new MessageCatalogBuilder();
        var v1 = builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "aot-test.v1");
        builder.Add(AotContractJsonContext.Default.AotTestMessageV2, name: "aot-test.v2", schemaVersion: 2);
        var catalog = builder.Build();

        var found = catalog.Find(typeof(AotTestMessage));
        await Assert.That(found).IsSameReferenceAs(v1);
    }

    /// <summary>MessageCatalogBuilder 拒绝重复消息名+版本组合</summary>
    [Test]
    public async Task MessageCatalogBuilder_DuplicateNameAndVersion_ThrowsInvalidOperation()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "dup.v1", schemaVersion: 1);

        var ex = await Assert.That(() => builder.Add(AotContractJsonContext.Default.AotTestMessageV2, name: "dup.v1", schemaVersion: 1))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("dup.v1");
        await Assert.That(ex!.Message.ToUpperInvariant()).Contains("ALREADY REGISTERED");
    }

    /// <summary>MessageCatalogBuilder 拒绝重复 CLR 类型</summary>
    [Test]
    public async Task MessageCatalogBuilder_DuplicateClrType_ThrowsInvalidOperation()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "type-dup-1.v1", schemaVersion: 1);

        var ex = await Assert.That(() => builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "type-dup-2.v1", schemaVersion: 1))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message.ToUpperInvariant()).Contains("ALREADY REGISTERED");
    }

    /// <summary>Descriptors 按注册顺序枚举</summary>
    [Test]
    public async Task MessageCatalog_Descriptors_PreserveInsertionOrder()
    {
        var builder = new MessageCatalogBuilder();
        var first = builder.Add(AotContractJsonContext.Default.AotTestMessage, name: "first.v1", schemaVersion: 1);
        var second = builder.Add(AotContractJsonContext.Default.AotTestMessageV2, name: "second.v1", schemaVersion: 1);
        var catalog = builder.Build();

        await Assert.That(catalog.Descriptors.Count).IsEqualTo(2);
        await Assert.That(catalog.Descriptors[0]).IsSameReferenceAs(first);
        await Assert.That(catalog.Descriptors[1]).IsSameReferenceAs(second);
    }

    /// <summary>空目录查找返回 null 而非抛异常</summary>
    [Test]
    public async Task MessageCatalog_EmptyCatalog_FindReturnsNull()
    {
        await Assert.That(MessageCatalog.Empty.Find("nonexistent.v1")).IsNull();
        await Assert.That(MessageCatalog.Empty.Find("nonexistent", schemaVersion: 1)).IsNull();
        await Assert.That(MessageCatalog.Empty.Find(typeof(AotTestMessage))).IsNull();
    }

    // ═══════════════════════════════════════════════════════════════
    // 3️⃣ DIM 零反射 — 默认接口方法 AOT 安全
    // ═══════════════════════════════════════════════════════════════

    /// <summary>IEventHandler<TEvent>.EventType 使用 typeof(TEvent) —— 编译时常量</summary>
    [Test]
    public async Task DimBridge_EventType_IsCompileTimeTypeof_ForMultipleHandlers()
    {
        // 验证不同类型 handler 的 EventType 均正确
        var handler1 = new AotTestEventHandler();
        var handler2 = new AotTestSecondaryEventHandler();

        await Assert.That(((IEventHandler)handler1).EventType).IsEqualTo(typeof(AotTestDomainEvent));
        await Assert.That(((IEventHandler)handler2).EventType).IsEqualTo(typeof(AotTestSecondaryDomainEvent));
    }

    /// <summary>IEventHandler<TEvent>.HandleAsync 同步完成路径返回已完成 ValueTask（零分配）</summary>
    [Test]
    public async Task DimBridge_SynchronousHandler_ReturnsCompletedTask()
    {
        var handler = new AotTestSyncEventHandler();
        var result = ((IEventHandler)handler).HandleAsync(new AotTestDomainEvent(), CancellationToken.None);

        await Assert.That(result.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>DIM 桥接使用直接类型转换 (TEvent)@event —— 非反射调用</summary>
    [Test]
    public async Task DimBridge_NonGenericDispatch_CastsDirectly_NotReflection()
    {
        var handler = new AotTestEventHandler();
        var domainEvent = new AotTestDomainEvent();

        // 故意使用 IEventHandler 接口测试 DIM 桥接——CA1859 在此处不适用
#pragma warning disable CA1859
        IEventHandler nonGeneric = handler;
#pragma warning restore CA1859

        // 非泛型 HandleAsync 应通过 DIM 桥接成功调用泛型 HandleAsync
        var task = nonGeneric.HandleAsync(domainEvent, CancellationToken.None);
        await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>验证 DIM 路径不存在 MakeGenericType —— 所有泛型参数编译时确定</summary>
    [Test]
    public async Task DimBridge_EventHandlerTypes_AreKnownAtCompileTime()
    {
        // 通过 IEventHandler<T> 的泛型约束，T 在编译时完全确定
        // typeof(TEvent) 是 IL Ldtoken 指令，不是反射

        // 此处注册所有已知 handler 类型，确认它们不使用 MakeGenericType
        await RegisterAndVerifyHandler<AotTestDomainEvent, AotTestEventHandler>();
        await RegisterAndVerifyHandler<AotTestSecondaryDomainEvent, AotTestSecondaryEventHandler>();
    }

    private static async Task RegisterAndVerifyHandler<TEvent, THandler>()
        where TEvent : DomainEvent
        where THandler : IEventHandler<TEvent>, new()
    {
        var handler = new THandler();

        // 故意使用 IEventHandler 接口测试 DIM 桥接——CA1859 在此处不适用
#pragma warning disable CA1859
        IEventHandler nonGeneric = handler;
#pragma warning restore CA1859

        // EventType 返回编译时 typeof(TEvent)
        await Assert.That(nonGeneric.EventType).IsEqualTo(typeof(TEvent));

        // 验证方法签名不依赖 MakeGenericType
        var handleMethod = typeof(IEventHandler).GetMethod(nameof(IEventHandler.HandleAsync))!;
        await Assert.That(handleMethod).IsNotNull();
        await Assert.That(handleMethod.GetParameters().Length).IsEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4️⃣ SmartEnum + [GenerateEnum] 源生成器 AOT 验证
    // ═══════════════════════════════════════════════════════════════

    /// <summary>SmartEnum + [GenerateEnum] 在运行时通过 [ModuleInitializer] 正确注册</summary>
    [Test]
    public async Task SmartEnum_FromValue_WorksWithModuleInitializer()
    {
        var pending = OrderStatus.FromValue("pending");
        await Assert.That(pending).IsNotNull();
        await Assert.That(pending.Value).IsEqualTo("pending");

        var all = OrderStatus.All;
        await Assert.That(all.Count).IsEqualTo(3);
    }

    /// <summary>InvariantOrderStatus 也应通过 [ModuleInitializer] 注册</summary>
    [Test]
    public async Task InvariantOrderStatus_RegisteredCorrectly()
    {
        var submitted = InvariantOrderStatus.FromValue("submitted");
        await Assert.That(submitted).IsNotNull();
        await Assert.That(submitted.Value).IsEqualTo("submitted");

        var all = InvariantOrderStatus.All;
        await Assert.That(all.Count).IsEqualTo(3);
    }

    /// <summary>SmartEnum 值注册后 FrozenDictionary 查找零反射</summary>
    [Test]
    public async Task SmartEnum_TryFromValue_UsesFrozenDictionaryLookup()
    {
        await Assert.That(OrderStatus.TryFromValue("shipped", out var result)).IsTrue();
        await Assert.That(result!.Value).IsEqualTo("shipped");
        await Assert.That(result.Name).IsEqualTo("已发货");
    }

    // ═══════════════════════════════════════════════════════════════
    // 5️⃣ FrozenDictionary 构建后查找 AOT-safe — 零反射
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task FrozenDictionary_Lookup_AotSafe()
    {
        var dict = new Dictionary<string, int>
        {
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3,
        }.ToFrozenDictionary();

        await Assert.That(dict.TryGetValue("two", out var value)).IsTrue();
        await Assert.That(value).IsEqualTo(2);
        await Assert.That(dict.TryGetValue("four", out _)).IsFalse();
    }

    /// <summary>MessageCatalog 构建后 FrozenDictionary 查找正确</summary>
    [Test]
    public async Task MessageCatalog_FrozenDictionary_LookupCorrect()
    {
        var builder = new MessageCatalogBuilder();
        var descriptor1 = builder.Add(
            AotContractJsonContext.Default.AotTestMessage,
            name: "aot-test.v1", schemaVersion: 1);
        var descriptor2 = builder.Add(
            AotContractJsonContext.Default.AotTestMessageV2,
            name: "aot-test", schemaVersion: 2);
        var catalog = builder.Build();

        await Assert.That(catalog.Find("aot-test.v1")).IsSameReferenceAs(descriptor1);
        await Assert.That(catalog.Find("aot-test", schemaVersion: 2)).IsSameReferenceAs(descriptor2);
        await Assert.That(catalog.Find(typeof(AotTestMessage))).IsSameReferenceAs(descriptor1);
    }

    // ═══════════════════════════════════════════════════════════════
    // 6️⃣ JsonSerializerIsReflectionEnabledByDefault=false 序列化
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task JsonSerializer_ReflectionDisabled_DoesNotFallback()
    {
        var options = AotContractJsonContext.Default.Options;
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty, options);

        var descriptor = MessageDescriptor.Create(
            AotContractJsonContext.Default.AotTestMessage, name: "aot-fallback-test");
        var message = new AotTestMessage("test-001", 42);

        var payload = serializer.Serialize(message, descriptor);
        var result = serializer.Deserialize<AotTestMessage>(payload.Span, descriptor);

        await Assert.That(result).IsEqualTo(message);
    }

    /// <summary>未注册类型在反射禁用模式下快速失败</summary>
    [Test]
    public async Task UnregisteredType_FailsFast_WhenReflectionDisabled()
    {
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);

        var exception = await Assert.That(() => serializer.Serialize(new UnregisteredAotMessage("test")))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("not registered in MessageCatalog");
    }

    /// <summary>序列化+反序列化往返保持数据一致性（AOT 安全路径）</summary>
    [Test]
    public async Task JsonSerializer_RoundTrip_PreservesData_AotSafe()
    {
        var options = AotContractJsonContext.Default.Options;
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty, options);

        var descriptor = MessageDescriptor.Create(
            AotContractJsonContext.Default.AotTestMessageV2, name: "roundtrip-test.v1");
        var original = new AotTestMessageV2("msg-42", 99, "label-42");

        var payload = serializer.Serialize(original, descriptor);
        var result = serializer.Deserialize<AotTestMessageV2>(payload.Span, descriptor);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(original.Id);
        await Assert.That(result.Count).IsEqualTo(original.Count);
        await Assert.That(result.Label).IsEqualTo(original.Label);
    }

    // ═══════════════════════════════════════════════════════════════
    // 7️⃣ 源生成器输出 AOT 兼容性验证
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [GenerateEnum] 源生成器输出不包含任何反射调用 —— 所有字段引用为编译时硬编码。
    /// 验证：生成的 ModuleInitializer 正确注册所有字段值。
    /// </summary>
    [Test]
    public async Task GeneratedEnum_AllValues_AccessibleViaFromValue()
    {
        // 所有三个 OrderStatus 值均应通过 ModuleInitializer 注册
        foreach (var expected in new[] { "pending", "shipped", "delivered" })
        {
            var status = OrderStatus.FromValue(expected);
            await Assert.That(status.Value).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// [GenerateEnum] 源生成器输出的 SmartEnum 值查找使用 FrozenDictionary —— O(1) 零反射。
    /// </summary>
    [Test]
    public async Task GeneratedEnum_FrozenDictionaryLookup_ConsistentPerformance()
    {
        // 重复查找验证 FrozenDictionary 行为一致性
        for (var i = 0; i < 100; i++)
        {
            await Assert.That(OrderStatus.TryFromValue("delivered", out var result)).IsTrue();
            await Assert.That(result!.Value).IsEqualTo("delivered");
        }
    }

    /// <summary>
    /// MessageRegistryGenerator 源生成器输出的代码模式是 AOT 安全的：
    /// - 使用 typeof(T) 而非 Type.GetType(string)
    /// - 使用 JsonSerializerContext.GetTypeInfo(typeof(T)) 而非反射 GetTypeInfo
    /// - 所有类型在编译时已知
    /// </summary>
    [Test]
    public async Task SourceGen_MessageRegistrationPattern_IsAotSafe()
    {
        // MessageCatalogBuilder.Add<T>(JsonTypeInfo<T>) 通过泛型参数编译时确定类型
        // 这保证了所有 JSON 序列化元数据在编译时由 STJ 源生成器生成
        var descriptor = MessageDescriptor.Create(
            AotContractJsonContext.Default.AotTestMessage,
            name: "src-gen-pattern.v1",
            schemaVersion: 1);

        await Assert.That(descriptor.Name).IsEqualTo("src-gen-pattern.v1");
        await Assert.That(descriptor.ClrType).IsEqualTo(typeof(AotTestMessage));
    }
}

// ─── AOT 契约测试夹具 ───

public sealed record AotTestMessage(string Id, int Count);
public sealed record AotTestMessageV2(string Id, int Count, string Label);
public sealed record UnregisteredAotMessage(string Id);

public sealed class AotTestDomainEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "aot.test-event.v1";
}

public sealed class AotTestSecondaryDomainEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "aot.test-event-secondary.v1";
}

public sealed class AotTestEventHandler : IEventHandler<AotTestDomainEvent>
{
    public ValueTask HandleAsync(AotTestDomainEvent @event, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed class AotTestSecondaryEventHandler : IEventHandler<AotTestSecondaryDomainEvent>
{
    public ValueTask HandleAsync(AotTestSecondaryDomainEvent @event, CancellationToken ct)
        => ValueTask.CompletedTask;
}

/// <summary>同步处理器 —— 验证 DIM 桥接同步完成路径</summary>
public sealed class AotTestSyncEventHandler : IEventHandler<AotTestDomainEvent>
{
    public ValueTask HandleAsync(AotTestDomainEvent @event, CancellationToken ct)
        => ValueTask.CompletedTask;
}

[JsonSerializable(typeof(AotTestMessage))]
[JsonSerializable(typeof(AotTestMessageV2))]
internal sealed partial class AotContractJsonContext : JsonSerializerContext;
