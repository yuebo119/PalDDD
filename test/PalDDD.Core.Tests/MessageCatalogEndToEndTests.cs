using System.Text.Json.Serialization;
using PalDDD.Core;
using PalDDD.Generated;
using PalDDD.Serialization;

namespace PalDDD.Core.Tests;

// ─────────────────────────────────────────────────────────────
// ITM-626 · 生成物端到端测试
// ─────────────────────────────────────────────────────────────
// 背景：MessageRegistryGenerator emit 的 PalMessageCatalog.AddGeneratedMessages
// 此前全仓零调用（生成物是"孤儿 API"）——单测只用 Roslyn driver 断言生成源码文本，
// 没有任何测试证明生成物能被 MessageCatalogBuilder 消费。
//
// 本文件用**真实** [GenerateMessage] + [JsonSerializable]（测试项目以 Analyzer
// OutputItemType 引用生成器，故生成器会在测试程序集上自动 emit PalMessageCatalog），
// 在测试内直接调用生成物，证明"标注 → 生成 → 运行期目录"全链打通。
// ─────────────────────────────────────────────────────────────

/// <summary>端到端测试消息——真实携带 [GenerateMessage]，由生成器 emit 到 PalMessageCatalog。</summary>
[GenerateMessage(Name = "e2e-order-submitted.v1", SchemaVersion = 1)]
public sealed record E2eOrderSubmittedMessage(int OrderId);

/// <summary>端到端测试 JSON 上下文——为生成物的 jsonContext.GetTypeInfo 提供 JsonTypeInfo。</summary>
[JsonSerializable(typeof(E2eOrderSubmittedMessage))]
internal sealed partial class E2eMessageJsonContext : JsonSerializerContext;

public sealed class MessageCatalogEndToEndTests
{
    [Test]
    public async Task AddGeneratedMessages_RealGeneratedCatalog_RegistersMessageByNameAndType()
    {
        var builder = new MessageCatalogBuilder();

        PalMessageCatalog.AddGeneratedMessages(builder, E2eMessageJsonContext.Default);
        var catalog = builder.Build();

        // 生成物 emit 的键集经真实目录可查——按 wire name 与按 CLR 类型两个方向，
        // 且两条查询必须指向同一条描述符（行为断言：名称/CLR 类型/schema 版本一致）。
        var byName = catalog.Find("e2e-order-submitted.v1");
        var byType = catalog.Find(typeof(E2eOrderSubmittedMessage));
        var expected = typeof(E2eOrderSubmittedMessage);

        // 两个方向都必须命中且指向同一描述符（行为断言，非 IsNotNull）。
        await Assert.That(byName is not null ? byName.ClrType : null).IsEqualTo(expected);
        await Assert.That(byName?.Name).IsEqualTo("e2e-order-submitted.v1");
        await Assert.That(byName?.SchemaVersion).IsEqualTo(1);
        await Assert.That(byType).IsSameReferenceAs(byName);
    }

    [Test]
    public async Task AddGeneratedMessages_ResolvesJsonTypeInfo_CatalogContainsExpectedDescriptor()
    {
        // 生成物对每条消息执行 jsonContext.GetTypeInfo(typeof(T)) ?? throw InvalidOperationException
        // ——本测试证明 [JsonSerializable] 与 [GenerateMessage] 同源时该守卫不触发、目录含预期描述符
        // （若 JsonTypeInfo 缺失，生成物会在调用路径抛，这是孤儿 API 未接线时的典型失效形态）。
        var builder = new MessageCatalogBuilder();

        PalMessageCatalog.AddGeneratedMessages(builder, E2eMessageJsonContext.Default);
        var catalog = builder.Build();

        var descriptor = catalog.Find(typeof(E2eOrderSubmittedMessage));
        // JsonTypeInfo 必须与消息类型同源（生成物的 GetTypeInfo(typeof(T)) 已解析成功）。
        await Assert.That(descriptor?.JsonTypeInfo.Type).IsEqualTo(typeof(E2eOrderSubmittedMessage));
        await Assert.That(catalog.Descriptors.Count).IsEqualTo(1);
    }
}
