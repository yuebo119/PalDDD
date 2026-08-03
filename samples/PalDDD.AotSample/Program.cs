using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Core;
using PalDDD.CQRS;

#pragma warning disable CA1812 // DI 实例化的内部类（AddPalCommandHandler 泛型注册）
using PalDDD.DependencyInjection;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Transactions;
using System.Text.Json.Serialization;

// ═══════════════════════════════════════════════════════════════
// Pal.DDD AOT 示例 — 展示消息序列化 + Outbox + Inbox + Saga
// ═══════════════════════════════════════════════════════════════

// ── 1. 消息目录 ──
var builder = new MessageCatalogBuilder();
builder.Add(SampleJsonContext.Default.SampleMessage, name: "sample-message");
var catalog = builder.Build();

var serializer = new JsonMessageSerializer(catalog);
var descriptor = catalog.Find("sample-message")
    ?? throw new InvalidOperationException("sample-message was not registered.");

// ── 2. 序列化往返 ──
var payload = serializer.Serialize(new SampleMessage("aot", 10), descriptor);
var message = serializer.Deserialize(payload.Span, descriptor);
Console.WriteLine(message is SampleMessage { Name: "aot", Count: 10 } ? "OK: serialize round-trip" : "FAILED");

// ── 3. InMemory Outbox 发布 ──
var outboxStore = new InMemoryOutboxStore();
var outboxMsg = new OutboxMessage
{
    Type = descriptor.Name,
    Payload = payload.ToArray(),
    ContentType = descriptor.ContentType,
    SchemaVersion = descriptor.SchemaVersion
};
outboxStore.AddMessage(outboxMsg);
var pending = await outboxStore.LeasePendingMessagesAsync(10, "aot-sample", TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, CancellationToken.None).ConfigureAwait(false);
outboxStore.MarkProcessed(outboxMsg, DateTimeOffset.UtcNow);
Console.WriteLine(pending.Count == 1 ? "OK: outbox lease + process" : "FAILED");

// ── 4. InMemory Inbox 幂等消费 ──
var inboxStore = new InMemoryInboxStore();
var inboxResult = await inboxStore.TryStartProcessingAsync("aot-consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
Console.WriteLine(inboxResult is not null ? "OK: inbox first attempt" : "FAILED");
var inboxDup = await inboxStore.TryStartProcessingAsync("aot-consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
Console.WriteLine(inboxDup is null ? "OK: inbox deduplication" : "FAILED");

// ── 5. InMemory Saga 状态存储 ──
var sagaStore = new InMemorySagaStateStore<SampleSagaState>();
var sagaState = new SampleSagaState { CurrentState = "Started" };
sagaStore.Add(sagaState);
var active = await sagaStore.GetActiveSagasAsync(10, CancellationToken.None).ConfigureAwait(false);
Console.WriteLine(active.Count == 1 ? "OK: saga active scan" : "FAILED");

// ── 6. CQRS 管道（AOT 值类型管道验证）──
// 背景：Native AOT 下 DI 开放泛型解析 + 值类型响应（Unit/int）抛 AotCannotCreateGenericValueType。
// 解法：AddPalCommandHandler/AddPalQueryHandler 内部闭合注册管道行为——闭合类型走 TryCreateExact 不经值类型校验。
Console.WriteLine("\n── 6. CQRS 管道（AOT 值类型管道验证）──");

var pipelineServices = new ServiceCollection();
pipelineServices.AddPalLogging();
pipelineServices.AddPalDDD();
pipelineServices.AddPalCommandHandler<PipelineCreateCmd, Unit, PipelineCreateHandler>();
pipelineServices.AddPalQueryHandler<PipelineCountQry, int, PipelineCountHandler>();
await using var pipelineProvider = pipelineServices.BuildServiceProvider();

// 无 Host 场景下手动启动 HostedService（HandlerRegistrar 将 HandlerMarker 注册进 Dispatcher）
foreach (var hostedService in pipelineProvider.GetServices<IHostedService>())
    await hostedService.StartAsync(CancellationToken.None);

var dispatcher = pipelineProvider.GetRequiredService<Dispatcher>();
await dispatcher.SendAsync(new PipelineCreateCmd("aot-pipeline"));
await dispatcher.SendAsync(new PipelineCreateCmd("aot-pipeline-2"));
var count = await dispatcher.QueryAsync(new PipelineCountQry());
Console.WriteLine(count == 2 ? "OK: AOT pipeline (Unit command + int query)" : $"FAILED: count={count}");

Console.WriteLine("\n✅ Pal.DDD AOT sample — all checks passed.");

// ── 类型定义 ──
internal sealed record SampleMessage(string Name, int Count);

[JsonSerializable(typeof(SampleMessage))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

internal sealed class SampleSagaState : SagaState;

// ── CQRS 管道类型（AOT 值类型管道验证）──
internal sealed record PipelineCreateCmd(string Name) : ICommand;
internal sealed record PipelineCountQry : IQuery<int>;

/// <summary>管道验证共享存储（handler 间共享，模拟应用状态）</summary>
internal static class PipelineStore
{
    public static readonly List<string> Names = [];
}

internal sealed class PipelineCreateHandler : ICommandHandler<PipelineCreateCmd, Unit>
{
    public ValueTask<Unit> HandleAsync(PipelineCreateCmd cmd, CancellationToken ct)
    {
        PipelineStore.Names.Add(cmd.Name);
        return ValueTask.FromResult(new Unit());
    }
}

internal sealed class PipelineCountHandler : IQueryHandler<PipelineCountQry, int>
{
    public ValueTask<int> HandleAsync(PipelineCountQry qry, CancellationToken ct)
        => ValueTask.FromResult(PipelineStore.Names.Count);
}
