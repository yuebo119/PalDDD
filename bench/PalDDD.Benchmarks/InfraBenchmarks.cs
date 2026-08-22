// ─────────────────────────────────────────────────────────────
// 基础设施层基准测试 — Outbox/Inbox/Saga/EventLog 全流程
// 全部使用 InMemory 实现，无需数据库
// ─────────────────────────────────────────────────────────────
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Compression;
using PalDDD.EventLog;
using PalDDD.Transactions;
using PalDDD.Dapper;
using ConsistentHashSharding = PalDDD.Dapper.PostgreSql.ConsistentHashSharding;
using ModShardingStrategy = PalDDD.Dapper.PostgreSql.ModShardingStrategy;
using PostgreSqlAuditor = PalDDD.Dapper.PostgreSql.PostgreSqlAuditor;
using PostgreSqlJsonb = PalDDD.Dapper.PostgreSql.PostgreSqlJsonb;
using PostgreSqlSoftDelete = PalDDD.Dapper.PostgreSql.PostgreSqlSoftDelete;
using SqliteFts = PalDDD.Dapper.Sqlite.SqliteFts;
using SqliteJson = PalDDD.Dapper.Sqlite.SqliteJson;

namespace PalDDD.Benchmarks;

// ═══════════════════════════════════════════════════════════
// Outbox 批处理基准 — GetPending 为纯查询；
// LeasePending/LeaseAndMarkAll 为耗尽型操作，每 invoke 含重灌成本（见方法注释）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class OutboxThroughputBenchmarks
{
    private InMemoryOutboxStore _store = null!;
    private const int BatchSize = 100;

    // ITM-152 修复：IterationSetup 重建 store（跨迭代隔离）。
    // ITM-246 修复：IterationSetup 只覆盖每个迭代的第一笔 invoke——BDN 单迭代内方法被
    // 调用数万次，租约型基准首个 invoke 即满额租走 100 条，后续 invoke 恒为空扫描，
    // ns/op 被稀释失真。现改为方法体内满额租走后立即重灌（见 ReseedStore 与各方法注释）。
    [IterationSetup]
    public void Setup() => ReseedStore();

    [IterationCleanup]
    public void Cleanup()
    {
        // 每次迭代结束丢弃 store，避免跨迭代状态泄漏（双保险，IterationSetup 亦会重建）
        _store = null!;
    }

    /// <summary>
    /// 重灌 = 新建 store + 100 条待处理消息。不向旧 store 追加：已租约/已处理消息
    /// 永不移除，追加会使 _messages 线性增长，QueryPending 扫描成本随 invoke 次数漂移；
    /// 新建保证每次租约面对恒定 100 条 backlog，扫描口径稳定。
    /// </summary>
    private void ReseedStore()
    {
        _store = new InMemoryOutboxStore();
        for (int i = 0; i < BatchSize; i++)
        {
            _store.AddMessage(new OutboxMessage
            {
                Type = "OrderCreated",
                Payload = "{}"u8.ToArray(),
                ContentType = "application/json"
            });
        }
    }

    // 守卫：不足额租约说明重灌不变量被破坏（store 未满灌或被外部消费）——
    // 抛出而非静默空转，静默空扫描正是 ITM-246 修前的失效模式。
    private static void EnsureFullLease(IReadOnlyList<OutboxMessage> msgs)
    {
        if (msgs.Count != BatchSize)
            throw new InvalidOperationException(
                $"应满额租走 {BatchSize} 条，实际 {msgs.Count} 条——重灌不变量被破坏，基准数字失真");
    }

    // 测量语义（ITM-246）：每个 invoke = 1 次满额租约（100 条）+ 1 次重灌（新建 store +
    // 100 次 AddMessage）。ns/op 含重灌成本，不是纯租约开销；纯查询对照见 GetPending_Batch100。
    [Benchmark(Baseline = true)]
    public async ValueTask<int> LeasePending_Batch100()
    {
        var msgs = await _store.LeasePendingMessagesAsync(BatchSize, "bench", TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        ReseedStore();
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask<int> GetPending_Batch100()
    {
        var msgs = await _store.GetPendingMessagesAsync(BatchSize, 10, default);
        return msgs.Count;
    }

    // 同 LeasePending_Batch100：每 invoke = 满额租约 + MarkProcessed×100 + 重灌，ns/op 含重灌成本。
    [Benchmark]
    public async ValueTask LeaseAndMarkAll_Batch100()
    {
        var msgs = await _store.LeasePendingMessagesAsync(BatchSize, "bench", TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        var now = DateTimeOffset.UtcNow;
        foreach (var m in msgs)
            _store.MarkProcessed(m, now);
        ReseedStore();
    }

    [Benchmark]
    public void AddSingleMessage()
        => _store.AddMessage(new OutboxMessage
        {
            Type = "OrderCreated",
            Payload = "{}"u8.ToArray()
        });
}

// ═══════════════════════════════════════════════════════════
// EventLog 事件追加 + 流式读取基准
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class EventLogBenchmarks
{
    private InMemoryEventLog _log = null!;
    private static readonly EventAuditMetadata _audit = EventAuditMetadata.Empty;
    private const string Stream = "order-123";
    private const int SeedCount = 100;

    [GlobalSetup]
    public void Setup()
    {
        _log = new InMemoryEventLog();

        // ITM-153 修复：种子 100 条事件——原 Setup 空流使 ReadStream_Forward 空枚举，
        // 基准测的是"空流迭代器开销"而非真实读取路径。
        for (var i = 0; i < SeedCount; i++)
        {
            _log.AppendAsync(
                Stream,
                ExpectedStreamVersion.Any,
                [new EventData(Guid.NewGuid(), "OrderCreated", 1, "application/json",
                    "{}"u8.ToArray(), ReadOnlyMemory<byte>.Empty, _audit)],
                default).AsTask().GetAwaiter().GetResult();
        }
    }

    [Benchmark]
    public async ValueTask<AppendEventsResult> Append_SingleEvent()
    {
        var events = new[]
        {
            new EventData(Guid.NewGuid(), "OrderCreated", 1, "application/json",
                "{}"u8.ToArray(), ReadOnlyMemory<byte>.Empty, _audit)
        };
        return await _log.AppendAsync(Stream, ExpectedStreamVersion.Any, events, default);
    }

    [Benchmark]
    public async ValueTask<int> ReadStream_Forward()
    {
        int count = 0;
        await foreach (var _ in _log.ReadStreamAsync(Stream, 0, int.MaxValue, default))
            count++;
        return count;
    }
}

// ═══════════════════════════════════════════════════════════
// Saga 状态持久化基准
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class SagaStateBenchmarks
{
    private InMemorySagaStateStore<BenchSagaState> _store = null!;
    private readonly Guid _sagaId = Guid.NewGuid();
    private const int ActiveSagaCount = 50;

    public sealed class BenchSagaState : SagaState
    { }

    [GlobalSetup]
    public void Setup()
    {
        _store = new InMemorySagaStateStore<BenchSagaState>();
        var state = new BenchSagaState
        {
            SagaId = _sagaId,
            CurrentState = "Active",
            Status = SagaStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _store.Add(state);

        // P3-SMP-106 修复：补足至 50 条——原 GlobalSetup 仅 1 条，GetActiveSagas_Batch50
        // 名义批量 50 实际只测单条返回路径。GetActiveSagas 为只读查询不耗尽状态，
        // GlobalSetup 一次性种子即可，无需 IterationSetup。
        // SagaId/CreatedAt 留默认（构造时自动生成），保证 50 条键唯一。
        for (int i = 1; i < ActiveSagaCount; i++)
        {
            _store.Add(new BenchSagaState
            {
                CurrentState = "Active",
                Status = SagaStatus.Active
            });
        }
    }

    [Benchmark]
    public async ValueTask<BenchSagaState?> GetById()
        => await _store.GetByIdAsync(_sagaId, default);

    [Benchmark]
    public async ValueTask<int> GetActiveSagas_Batch50()
    {
        var list = await _store.GetActiveSagasAsync(50, default);
        return list.Count;
    }
}

// ═══════════════════════════════════════════════════════════
// SQL 生成工具性能基准（静态方法，无 IO）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class SqlGenBenchmarks
{
    [Benchmark(Baseline = true)]
    public string PostgreSql_JsonbInclude()
        => PostgreSqlJsonb.Include("payload", "Type", "OrderCreated");

    [Benchmark]
    public string Sqlite_JsonExtract()
        => SqliteJson.Extract("payload", "Type");

    [Benchmark]
    public string PostgreSql_SoftDelete()
        => PostgreSqlSoftDelete.Delete("outbox_messages", "id=1");

    [Benchmark]
    public string PostgreSql_AuditLog()
        => PostgreSqlAuditor.AppendAuditLog("outbox_messages", "42", "UPDATE", newDataJson: "{}");

    [Benchmark]
    public string Sqlite_FtsCreateIndex()
        => SqliteFts.CreateOutboxIndex("outbox_messages");

    // 三十七轮 P2-6 修复：构造外置——ConsistentHash 构造含 2048 vnode + Array.Sort，
    // 原基准把构造成本计入 GetShardId 测量（对齐 ITM-152/153 构造外置先例）
    private ConsistentHashSharding? _consistentHash;
    private ModShardingStrategy? _modStrategy;

    [IterationSetup]
    public void SetupSharding()
    {
        _consistentHash ??= new ConsistentHashSharding(8);
        _modStrategy ??= new ModShardingStrategy(8);
    }

    [Benchmark]
    public int Sharding_ModStrategy()
        => (_modStrategy ?? new ModShardingStrategy(8)).GetShardId(Guid.NewGuid());

    [Benchmark]
    public int Sharding_ConsistentHash()
        => (_consistentHash ?? new ConsistentHashSharding(8)).GetShardId(Guid.NewGuid());
}

// ═══════════════════════════════════════════════════════════
// DI/Dapper 配置基准
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class ConfigurationBenchmarks
{
    [Benchmark]
    public string DapperConfig_CreatePg()
    {
        var conn = DapperConfiguration.Create(DapperDbType.PostgreSql, "Host=localhost");
        return conn.GetType().Name;
    }

    [Benchmark]
    public string DapperConfig_CreateSqlite()
    {
        var conn = DapperConfiguration.Create(DapperDbType.Sqlite, "Data Source=:memory:");
        return conn.GetType().Name;
    }
}

// ═══════════════════════════════════════════════════════════
// 压缩基准 — Brotli span 直压 / GZip Stream 对比（16KB 典型消息负载）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class CompressionBenchmarks
{
    private ICompressor _brotli = null!;
    private ICompressor _gzip = null!;
    private byte[] _payload = null!;
    private ReadOnlyMemory<byte> _brotliCompressed;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddPalCompression();
        var provider = services.BuildServiceProvider().GetRequiredService<ICompressionProvider>();
        _brotli = provider.GetCompressor(CompressionAlgorithm.Brotli);
        _gzip = provider.GetCompressor(CompressionAlgorithm.GZip);

        _payload = new byte[16 * 1024];
        for (int i = 0; i < _payload.Length; i++)
            _payload[i] = (byte)(i * 7 % 256);

        _brotliCompressed = _brotli.Compress(_payload);
    }

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> Brotli_Compress_16KB() => _brotli.Compress(_payload);

    [Benchmark]
    public byte[] Brotli_Decompress_16KB() => _brotli.Decompress(_brotliCompressed.Span);

    [Benchmark]
    public ReadOnlyMemory<byte> GZip_Compress_16KB() => _gzip.Compress(_payload);
}
