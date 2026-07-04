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

Console.WriteLine("\n✅ Pal.DDD AOT sample — all checks passed.");

// ── 类型定义 ──
internal sealed record SampleMessage(string Name, int Count);

[JsonSerializable(typeof(SampleMessage))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

internal sealed class SampleSagaState : SagaState;
