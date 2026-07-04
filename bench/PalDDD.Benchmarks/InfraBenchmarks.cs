// ─────────────────────────────────────────────────────────────
// 基础设施层基准测试 — Outbox/Inbox/Saga/EventLog 全流程
// 全部使用 InMemory 实现，无需数据库
// ─────────────────────────────────────────────────────────────
using BenchmarkDotNet.Attributes;
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
// Outbox 批处理吞吐量基准
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
[ShortRunJob]
public class OutboxThroughputBenchmarks
{
    private InMemoryOutboxStore _store = null!;
    private const int BatchSize = 100;

    [GlobalSetup]
    public void Setup()
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

    [Benchmark(Baseline = true)]
    public async ValueTask<int> LeasePending_Batch100()
    {
        var msgs = await _store.LeasePendingMessagesAsync(BatchSize, "bench", TimeSpan.FromSeconds(30), 10, default);
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask<int> GetPending_Batch100()
    {
        var msgs = await _store.GetPendingMessagesAsync(BatchSize, 10, default);
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask LeaseAndMarkAll_Batch100()
    {
        var msgs = await _store.LeasePendingMessagesAsync(BatchSize, "bench", TimeSpan.FromSeconds(30), 10, default);
        var now = DateTimeOffset.UtcNow;
        foreach (var m in msgs)
            _store.MarkProcessed(m, now);
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

    [GlobalSetup]
    public void Setup()
    {
        _log = new InMemoryEventLog();
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

    [Benchmark]
    public int Sharding_ModStrategy()
        => new ModShardingStrategy(8).GetShardId(Guid.NewGuid());

    [Benchmark]
    public int Sharding_ConsistentHash()
        => new ConsistentHashSharding(8).GetShardId(Guid.NewGuid());
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
