// ─────────────────────────────────────────────────────────────
// 🚚 Outbox 投递吞吐端到端量具（T0.2，2026-10-11 优化实施轮）
// ─────────────────────────────────────────────────────────────
// 口径：SQLite 真库（共享内存库：租约/标记为真实 SQL）+ 假 broker（发布确认延迟
// RTT 可配——Kafka ProduceAsync await / Rabbit confirm await 的串行化形态抽象）
// + 真序列化/catalog——完整跑 OutboxBatchProcessor 的
// 「租约批 → 逐条（目录查找→反序列化→发布→终态标记）」链。
//
// 基准体 = 排空 1000 条积压（10 个满批 100），读数单位 = 完整排空一次的墙钟；
// msg/s = 1000 / op（表内 Mean 为 ms/op）。行为守卫：排空后假 broker 发布计数
// 必须 == 1000（漏发即抛）。
//
// 两类：
//   OutboxDeliveryDrainBenchmarks —— RTT 矩阵（0/1/20ms）× T1.2 自适应排空形态；
//     用途：T2.1 批量标记（BatchMarkThreshold A/B）/ T3.1 确认流水线的同轮 A/B 基线。
//   OutboxDeliveryCadenceBenchmarks —— RTT=0 下的批间节奏对照：
//     Adaptive（T1.2 形态，批间 1ms）vs Fixed500（旧形态，批间 500ms 固定间隔）——
//     T1.2 收益的端到端量化（差异主要显形于批处理耗时 << 间隔的形态，故固定 RTT=0）。
//
// ⚠️ 无 job attribute：--delivery 入口模式级 ManualConfig（同 Program.cs 头注约定）。
//
// ⚠️ RTT=20 × InFlight=1 格边界（2026-10-15 实证）：本机 Task.Delay(20) ≈ 31ms
//（Windows 15.6ms 定时器粒度），单 op ~33s × 8 次逼近 BDN InProcess 单基准 5 分钟
// 硬上限——慢机上整轮可能在该格失败（本轮实测即如此，前 8 格数据有效）。A/B 取数
// 可先看 RTT=0/1 子集；该格科学价值低（发布延迟主导，标记段占比 ~0.04%）。

using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using PalDDD.Core.Logging;
using PalDDD.Dapper;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Testing;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Benchmarks;

/// <summary>RTT 矩阵 + 自适应排空形态的投递吞吐（T0.2 量具主体）。</summary>
[MemoryDiagnoser]
public class OutboxDeliveryDrainBenchmarks : PersistenceBenchBase
{
    private const int Backlog = 1000;

    [Params(0, 1, 20)]
    public int RttMs { get; set; }

    /// <summary>T3.1 发布确认流水线窗口 A/B：1 = 串行（现状基线），16 = FIFO 流水线。</summary>
    [Params(1, 16)]
    public int InFlight { get; set; }

    /// <summary>
    /// R4/P0-2（ADR-027）终态批量标记阈值 A/B：1 = 逐条路径（默认基线，逐字不变），
    /// 16 = 满阈值分组单语句标记（阈值语义见 <see cref="OutboxOptions.BatchMarkThreshold"/>：
    /// 标记时点延迟到满阈值/批末，at-least-once 契约内重复窗口随阈值增大；失败路径恒逐条）。
    /// </summary>
    [Params(1, 16)]
    public int BatchMark { get; set; }

    private SqliteConnection _conn = null!;
    private DapperOutboxStore _store = null!;
    private FakeDelayBroker _broker = null!;
    private OutboxBatchProcessor _processor = null!;
    private JsonMessageSerializer _serializer = null!;
    private MessageDescriptor _descriptor = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _conn = OpenSharedMemory("delivery-bench");
        CreateSharedSchemaAsync(_conn).GetAwaiter().GetResult();
        var builder = new MessageCatalogBuilder();
        _descriptor = builder.Add<SerializationBenchmarks.BenchOrderPlaced>(
            BenchJsonContext.Default.BenchOrderPlaced, "bench.order-placed.v1");
        var catalog = builder.Build();
        _serializer = new JsonMessageSerializer(catalog);
        _store = new DapperOutboxStore(_conn, DapperDbType.Sqlite);
        _broker = new FakeDelayBroker(TimeSpan.FromMilliseconds(RttMs), _serializer, catalog);
        _processor = new OutboxBatchProcessor(
            _store, _broker, _serializer, catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
            {
                BatchSize = 100,
                MaxInFlightPublishes = InFlight,
                BatchMarkThreshold = BatchMark,
            }),
            NullPalLogger<OutboxBatchProcessor>.Instance);
    }

    [IterationSetup]
    public async Task Setup()
    {
        await ClearSharedSchemaAsync(_conn);
        var message = new SerializationBenchmarks.BenchOrderPlaced(PalUlid.New(), "bench", 1);
        var payload = _serializer.Serialize(message).ToArray();
        for (var i = 0; i < Backlog; i += 100)
        {
            var batch = new OutboxMessage[100];
            for (var j = 0; j < 100; j++)
            {
                batch[j] = new OutboxMessage
                {
                    Type = _descriptor.Name,
                    SchemaVersion = _descriptor.SchemaVersion,
                    Payload = payload,
                    ContentType = "application/json",
                    Status = OutboxStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
            }
            await _store.AddMessagesAsync(batch);
        }
        _broker.Reset();
    }

    [Benchmark]
    public async ValueTask<int> Drain1000_Adaptive()
    {
        var published = await DrainAsync(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        if (published != Backlog)
            throw new InvalidOperationException($"排空守卫失败：发布 {published} != {Backlog}");
        return published;
    }

    private async ValueTask<int> DrainAsync(TimeSpan interBatchDelay)
    {
        while (await _processor.ProcessBatchWithBacklogAsync(default).ConfigureAwait(false))
            await Task.Delay(interBatchDelay).ConfigureAwait(false);
        return _broker.PublishCount;
    }
}

/// <summary>批间节奏对照（T1.2 端到端量化）：RTT=0 固定，Adaptive 1ms vs 旧 Fixed500。</summary>
[MemoryDiagnoser]
public class OutboxDeliveryCadenceBenchmarks : PersistenceBenchBase
{
    private const int Backlog = 1000;

    private SqliteConnection _conn = null!;
    private DapperOutboxStore _store = null!;
    private FakeDelayBroker _broker = null!;
    private OutboxBatchProcessor _processor = null!;
    private JsonMessageSerializer _serializer = null!;
    private MessageDescriptor _descriptor = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _conn = OpenSharedMemory("delivery-cadence-bench");
        CreateSharedSchemaAsync(_conn).GetAwaiter().GetResult();
        var builder = new MessageCatalogBuilder();
        _descriptor = builder.Add<SerializationBenchmarks.BenchOrderPlaced>(
            BenchJsonContext.Default.BenchOrderPlaced, "bench.order-placed.v1");
        var catalog = builder.Build();
        _serializer = new JsonMessageSerializer(catalog);
        _store = new DapperOutboxStore(_conn, DapperDbType.Sqlite);
        _broker = new FakeDelayBroker(TimeSpan.Zero, _serializer, catalog);
        _processor = new OutboxBatchProcessor(
            _store, _broker, _serializer, catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions { BatchSize = 100 }),
            NullPalLogger<OutboxBatchProcessor>.Instance);
    }

    [IterationSetup]
    public async Task Setup()
    {
        await ClearSharedSchemaAsync(_conn);
        var message = new SerializationBenchmarks.BenchOrderPlaced(PalUlid.New(), "bench", 1);
        var payload = _serializer.Serialize(message).ToArray();
        for (var i = 0; i < Backlog; i += 100)
        {
            var batch = new OutboxMessage[100];
            for (var j = 0; j < 100; j++)
            {
                batch[j] = new OutboxMessage
                {
                    Type = _descriptor.Name,
                    SchemaVersion = _descriptor.SchemaVersion,
                    Payload = payload,
                    ContentType = "application/json",
                    Status = OutboxStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
            }
            await _store.AddMessagesAsync(batch);
        }
        _broker.Reset();
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Drain1000_Adaptive1ms()
    {
        var published = await DrainAsync(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        if (published != Backlog)
            throw new InvalidOperationException($"排空守卫失败：发布 {published} != {Backlog}");
        return published;
    }

    [Benchmark]
    public async ValueTask<int> Drain1000_FixedInterval500ms()
    {
        var published = await DrainAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        if (published != Backlog)
            throw new InvalidOperationException($"排空守卫失败：发布 {published} != {Backlog}");
        return published;
    }

    private async ValueTask<int> DrainAsync(TimeSpan interBatchDelay)
    {
        while (await _processor.ProcessBatchWithBacklogAsync(default).ConfigureAwait(false))
            await Task.Delay(interBatchDelay).ConfigureAwait(false);
        return _broker.PublishCount;
    }
}

/// <summary>
/// 假 broker：发布确认延迟可配（RTT&gt;0 时逐条 await——对位 Kafka ProduceAsync await /
/// Rabbit publisher-confirms await 的串行确认形态，T3.1 流水线化的 A/B 基线即此）。
/// </summary>
internal sealed class FakeDelayBroker(
    TimeSpan publishLatency, IMessageSerializer serializer, IMessageCatalog catalog)
    : MessageBrokerBase(serializer, catalog)
{
    private int _publishCount;

    public int PublishCount => Volatile.Read(ref _publishCount);

    public void Reset() => Volatile.Write(ref _publishCount, 0);

    public override async ValueTask PublishAsync(
        object message, MessageDescriptor descriptor, PalUlid messageId,
        MessagePublishContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _publishCount);
        if (publishLatency > TimeSpan.Zero)
            await Task.Delay(publishLatency, ct).ConfigureAwait(false);
    }

    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        => throw new NotSupportedException("投递吞吐量具不订阅");

    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        => throw new NotSupportedException("投递吞吐量具不订阅");
}
