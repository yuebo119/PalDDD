using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

/// <summary>
/// A1（2026-10-15 性能实施轮）：MaxDegreeOfParallelism &gt; 1 分区路径锁定。<br/>
/// 空租约下旧形态 <c>Enumerable.Chunk(empty, 0)</c> 抛 ArgumentOutOfRangeException
/// （ProcessBatchAsync 直路上抛无兜底，本测试于旧码判红）；新形态空批零分区正常返回。
/// 非空批分区正确性同文件锁定（满批全部 processed 且无重复）。
/// </summary>
public sealed class OutboxParallelPartitionTests
{
    [Test]
    public async Task ParallelPath_EmptyLease_CompletesWithoutChunkException()
    {
        // 旧形态：Chunk 首行 ThrowIfNegativeOrZero 在分区段上抛，本调用不可达
        var (_, hasBacklog) = await RunBatchAsync(messageCount: 0, degreeOfParallelism: 4);

        await Assert.That(hasBacklog).IsFalse();
    }

    [Test]
    public async Task ParallelPath_NonEmptyBatch_AllMessagesProcessed()
    {
        var (store, hasBacklog) = await RunBatchAsync(messageCount: 8, degreeOfParallelism: 4);

        await Assert.That(hasBacklog).IsTrue(); // 满批信号（8 ≥ BatchSize）
        await Assert.That(store.ProcessedOrder.Count).IsEqualTo(8);
        await Assert.That(store.ProcessedOrder.Distinct().Count()).IsEqualTo(8);
    }

    private static async Task<(PartitionRecordingStore Store, bool HasBacklog)> RunBatchAsync(
        int messageCount, int degreeOfParallelism)
    {
        var store = new PartitionRecordingStore(messageCount);
        var builder = new MessageCatalogBuilder();
        builder.Add<PipelinedMessage>(PipeliningJsonContext.Default.PipelinedMessage, "test.pipelined.v1");
        var catalog = builder.Build();
        var serializer = new NonNullSerializer();

        // dop > 1 要求 IServiceScopeFactory（per-worker scope 独立 store 实例）——
        // 经 DI 组装提供，与生产 AddPalOutbox 接线同构
        var services = new ServiceCollection();
        services.AddSingleton<IPalOutboxStore>(store);
        services.AddSingleton<IMessageBroker>(new NoopBroker(serializer, catalog));
        services.AddSingleton<IMessageSerializer>(serializer);
        services.AddSingleton<IMessageCatalog>(catalog);
        services.AddSingleton<IOptionsMonitor<OutboxOptions>>(
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
            {
                BatchSize = Math.Max(messageCount, 1),
                MaxDegreeOfParallelism = degreeOfParallelism,
                MaxRetryCount = 3,
            }));
        services.AddSingleton<IPalLogger<OutboxBatchProcessor>>(NullPalLogger<OutboxBatchProcessor>.Instance);
        services.AddScoped<OutboxBatchProcessor>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        var hasBacklog = await processor.ProcessBatchWithBacklogAsync(CancellationToken.None);
        return (store, hasBacklog);
    }

    // ─── stubs ─────────────────────────────────────────────────

    /// <summary>发布恒成功（分区路径的 broker 行为无关项）。</summary>
    private sealed class NoopBroker(IMessageSerializer serializer, IMessageCatalog catalog)
        : MessageBrokerBase(serializer, catalog)
    {
        public override ValueTask PublishAsync(
            object message, MessageDescriptor msgDescriptor, PalUlid messageId,
            MessagePublishContext context, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("分区测试不订阅");

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("分区测试不订阅");
    }

    /// <summary>反序列化恒返回非 null（全路径激活——目录查找/发布/标记链不走死信捷径）。</summary>
    private sealed class NonNullSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => default;

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => default;

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => new object();

        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    /// <summary>租约固定批 + 终态标记顺序记录（跨分区共享单例，锁内记录）。</summary>
    private sealed class PartitionRecordingStore(int messageCount) : IPalOutboxStore
    {
        public readonly List<PalUlid> ProcessedOrder = [];

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            var messages = new OutboxMessage[messageCount];
            for (var i = 0; i < messageCount; i++)
            {
                messages[i] = new OutboxMessage
                {
                    Type = "test.pipelined.v1",
                    SchemaVersion = 1,
                    Payload = "{}"u8.ToArray(),
                    ContentType = "application/json",
                    Status = OutboxStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
            }
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(messages);
        }

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default) => new(messages.Count);

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            lock (ProcessedOrder) ProcessedOrder.Add(message.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(messageCount);
    }
}
