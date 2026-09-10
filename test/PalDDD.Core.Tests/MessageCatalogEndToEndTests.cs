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

        // 生成物 emit 的键集经真实目录可查——按 wire name 与按 CLR 类型两个方向。
        await Assert.That(catalog.Find("e2e-order-submitted.v1")).IsNotNull();
        await Assert.That(catalog.Find(typeof(E2eOrderSubmittedMessage))).IsNotNull();
    }

    [Test]
    public async Task AddGeneratedMessages_ResolvesJsonTypeInfo_NoMissingTypeInfoThrow()
    {
        // 生成物对每条消息执行 jsonContext.GetTypeInfo(typeof(T)) ?? throw InvalidOperationException
        // ——本测试证明 [JsonSerializable] 与 [GenerateMessage] 同源时该守卫不触发；
        // 若 JsonTypeInfo 缺失，Build() 路径会抛（生成物是孤儿 API 时的典型失效形态）。
        var builder = new MessageCatalogBuilder();

        await Assert.That(() =>
        {
            PalMessageCatalog.AddGeneratedMessages(builder, E2eMessageJsonContext.Default);
            return builder.Build();
        }).ThrowsNothing();
    }
}
