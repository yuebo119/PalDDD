// ─────────────────────────────────────────────────────────────
// 三栈持久化基准 — Dapper / PalORM / EFCore × SQLite(纯本地,无外部依赖)
// ─────────────────────────────────────────────────────────────
// X1 立项依据(orm-optimization-deep-analysis-2026-09-13):三栈优化清单所有量级标注
// 均为[推断]的根因是无基准——本文件建立数字裁判,后续每个优化裁决以实测为准。
//
// 口径(与 InfraBenchmarks ITM-246 同源):
//   ① 耗尽型操作(租约)每 invoke 含重灌成本;纯查询对照提供(GetPending 基线)
//   ② 重灌不变量守卫:不足额即抛(静默空转正是 ITM-246 修前的失效模式)
//   ③ 连接级 fixture:GlobalSetup 建表一次;IterationSetup 清表重灌
//   ④ SQLite :memory: 每连接独立库——多连接共享须命名共享内存库
//     (Data Source=<名>;Mode=Memory;Cache=Shared,keeper 连接保活库生命)
//   ⑤ EFCore 栈:表名是 EF CLR 默认名(OutboxMessages 等,与 docs/sql 小写 schema 不同),
//     EnsureCreated 是库级判断(库里已有任何表即整体跳过)——三个 context 必须三个独立库
//   ⑥ 运行方式:dotnet run --project bench/PalDDD.Benchmarks -c Release -- --persist
//     (Program.cs 显式 Run<T>——BDN 0.15.8 不识 net11-rc moniker,Switcher 全程序集
//     验证崩溃;--persist 绕行,InProcess attribute 免子进程 SDK 检查)
//
// 首批数字:docs/review/bench-baseline-2026-09-13.md

using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Benchmarks;

// ═══════════════════════════════════════════════════════════
// 共享工具
// ═══════════════════════════════════════════════════════════
public abstract class PersistenceBenchBase
{
    protected const int BatchSize = 100;

    /// <summary>公开常量(--verify-persist 等验证面复用口径)。</summary>
    public const int PublicBatchSize = BatchSize;

    /// <summary>命名共享内存库连接(cache=shared;多连接同库,首连接为 keeper)。</summary>
    public static SqliteConnection OpenSharedMemory(string dbName)
    {
        var conn = new SqliteConnection($"Data Source={dbName};Mode=Memory;Cache=Shared");
        conn.Open();
        return conn;
    }

    /// <summary>docs/sql/sqlite 小写 schema(Dapper/PalORM 段共用)。</summary>
    public static async Task CreateSharedSchemaAsync(SqliteConnection conn)
    {
        await global::Dapper.SqlMapper.ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS outbox_messages (
                id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL,
                content_type TEXT NOT NULL DEFAULT 'application/json',
                schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0,
                processed_at TEXT, next_attempt_at TEXT, retry_count INTEGER NOT NULL DEFAULT 0,
                error TEXT, locked_by TEXT, locked_until TEXT,
                created_at TEXT NOT NULL, correlation_id TEXT, causation_id TEXT,
                trace_parent TEXT, trace_state TEXT);
            CREATE TABLE IF NOT EXISTS events (
                global_position INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL, event_name TEXT NOT NULL, stream_name TEXT NOT NULL,
                stream_version INTEGER NOT NULL,
                schema_version INTEGER NOT NULL DEFAULT 1,
                content_type TEXT NOT NULL DEFAULT 'application/json',
                payload BLOB NOT NULL, metadata BLOB, recorded_at TEXT NOT NULL,
                actor_id TEXT, reason TEXT, correlation_id TEXT, causation_id TEXT,
                trace_parent TEXT, trace_state TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_events_stream ON events(stream_name, stream_version);
            CREATE TABLE IF NOT EXISTS idempotency_records (
                operation_name TEXT NOT NULL, idempotency_key TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0, locked_until TEXT NOT NULL,
                expires_at TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                response_payload BLOB, error TEXT, revision INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (operation_name, idempotency_key));
            """);
    }

    public static Task ClearSharedSchemaAsync(SqliteConnection conn)
        => global::Dapper.SqlMapper.ExecuteAsync(conn,
            "DELETE FROM outbox_messages; DELETE FROM events; DELETE FROM idempotency_records;");

    protected static OutboxMessage NewOutboxMessage()
        => new()
        {
            Type = "OrderCreated",
            Payload = "{}"u8.ToArray(),
            ContentType = "application/json",
            Status = OutboxStatus.Pending,
            CreatedAt = TimeProvider.System.GetUtcNow(),
        };

    protected static EventData NewEventData()
        => new(PalUlid.New(), "bench.event.v1", 1, "application/json",
            "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty);

    /// <summary>重灌不变量守卫(镜像 InfraBenchmarks.EnsureFullLease)。</summary>
    protected static void EnsureFullLease(IReadOnlyList<OutboxMessage> msgs)
        => EnsureFullLeasePublic(msgs);

    /// <summary>守卫公开入口(--verify-persist 验证面直接断言守卫语义)。</summary>
    public static void EnsureFullLeasePublic(IReadOnlyList<OutboxMessage> msgs)
    {
        if (msgs.Count != BatchSize)
            throw new InvalidOperationException($"应满额租走 {BatchSize} 条,实际 {msgs.Count} 条——重灌不变量被破坏,基准数字失真");
    }
}

// ═══════════════════════════════════════════════════════════
// Dapper 栈(拦截器路径;连接注入式构造)
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
public class DapperPersistenceBenchmarks : PersistenceBenchBase
{
    private SqliteConnection _conn = null!;
    private DapperOutboxStore _outbox = null!;
    private DapperEventLog _eventLog = null!;
    private DapperIdempotencyStore _idempotency = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _conn = OpenSharedMemory("benchdapper");
        await CreateSharedSchemaAsync(_conn);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _conn.Dispose();

    [IterationSetup]
    public async Task Setup()
    {
        await ClearSharedSchemaAsync(_conn);
        _outbox = new DapperOutboxStore(_conn, DapperDbType.Sqlite);
        _eventLog = new DapperEventLog(_conn);
        _idempotency = new DapperIdempotencyStore(_conn, DapperDbType.Sqlite);
        for (int i = 0; i < BatchSize; i++)
            _outbox.AddMessage(NewOutboxMessage());
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Outbox_GetPending_Batch100()
    {
        var msgs = await _outbox.GetPendingMessagesAsync(BatchSize, 10, default);
        if (msgs.Count != BatchSize) throw new InvalidOperationException($"GetPending {msgs.Count} != {BatchSize}");
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask<int> Outbox_Lease_Batch100()
    {
        var msgs = await _outbox.LeasePendingMessagesAsync(BatchSize, "bench",
            TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        for (int i = 0; i < BatchSize; i++)
            _outbox.AddMessage(NewOutboxMessage());
        return msgs.Count;
    }

    [Benchmark]
    public void Outbox_AddSingle()
        => _outbox.AddMessage(NewOutboxMessage());

    [Benchmark]
    public async ValueTask EventLog_AppendSingle()
        => await _eventLog.AppendAsync("bench-stream", ExpectedStreamVersion.Any,
            [NewEventData()], default);

    [Benchmark]
    public async ValueTask Idempotency_TryStart_Completed()
    {
        var now = DateTimeOffset.UtcNow;
        var key = PalUlid.New().ToString();
        var record = await _idempotency.TryStartAsync("bench-op", key, now, IdempotencyPolicy.Default, default);
        if (record is null) throw new InvalidOperationException("TryStart 返回 null(不应冲突)");
        await _idempotency.MarkCompletedAsync(record, "resp"u8.ToArray(), now.AddSeconds(1));
    }
}

// ═══════════════════════════════════════════════════════════
// PalORM 栈(Sqlite 方言固化中间类;DataSession 自建连接——共享内存库同库)
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
public class PalOrmPersistenceBenchmarks : PersistenceBenchBase
{
    private SqliteConnection _keeper = null!;
    private DataSession<SqliteProvider> _session = null!;
    private SqliteOutboxStore _outbox = null!;
    private SqliteEventLog _eventLog = null!;
    private SqliteIdempotencyStore _idempotency = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        // keeper 连接保活共享内存库(DataSession 用同连接串自建连接,cache=shared 同库)
        _keeper = OpenSharedMemory("benchpalorm");
        await CreateSharedSchemaAsync(_keeper);
        var opts = DbOptions.Development("Data Source=benchpalorm;Mode=Memory;Cache=Shared");
        _session = await DataSession<SqliteProvider>.CreateAsync(opts, default);
        _outbox = new SqliteOutboxStore(_session);
        _eventLog = new SqliteEventLog(_session);
        _idempotency = new SqliteIdempotencyStore(_session);
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _session.DisposeAsync();
        _keeper.Dispose();
    }

    [IterationSetup]
    public async Task Setup()
    {
        await ClearSharedSchemaAsync(_keeper);
        for (int i = 0; i < BatchSize; i++)
            await _outbox.AddMessagesAsync([NewOutboxMessage()]);
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Outbox_GetPending_Batch100()
    {
        var msgs = await _outbox.GetPendingMessagesAsync(BatchSize, 10, default);
        if (msgs.Count != BatchSize) throw new InvalidOperationException($"GetPending {msgs.Count} != {BatchSize}");
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask<int> Outbox_Lease_Batch100()
    {
        var msgs = await _outbox.LeasePendingMessagesAsync(BatchSize, "bench",
            TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        for (int i = 0; i < BatchSize; i++)
            await _outbox.AddMessagesAsync([NewOutboxMessage()]);
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask Outbox_AddSingle()
        => await _outbox.AddMessagesAsync([NewOutboxMessage()]);

    [Benchmark]
    public async ValueTask EventLog_AppendSingle()
        => await _eventLog.AppendAsync("bench-stream", ExpectedStreamVersion.Any,
            [NewEventData()], default);

    [Benchmark]
    public async ValueTask Idempotency_TryStart_Completed()
    {
        var now = DateTimeOffset.UtcNow;
        var key = PalUlid.New().ToString();
        var record = await _idempotency.TryStartAsync("bench-op", key, now, IdempotencyPolicy.Default, default);
        if (record is null) throw new InvalidOperationException("TryStart 返回 null(不应冲突)");
        await _idempotency.MarkCompletedAsync(record, "resp"u8.ToArray(), now.AddSeconds(1));
    }
}

// ═══════════════════════════════════════════════════════════
// EFCore 栈(基准专用派生 DbContext——生产禁 new 抽象基类,架构红线同源)
// 表名 EF CLR 默认名;EnsureCreated 库级判断 → 三 context 三独立库
// ═══════════════════════════════════════════════════════════
public sealed class BenchOutboxDbContext(DbContextOptions<BenchOutboxDbContext> options)
    : SqliteOutboxDbContext(options);
public sealed class BenchEventLogDbContext(DbContextOptions<BenchEventLogDbContext> options)
    : EventLogDbContext(options);
public sealed class BenchIdempotencyDbContext(DbContextOptions<BenchIdempotencyDbContext> options)
    : IdempotencyDbContext(options);

[MemoryDiagnoser]
public class EfCorePersistenceBenchmarks : PersistenceBenchBase
{
    private SqliteConnection _outboxConn = null!;
    private SqliteConnection _eventLogConn = null!;
    private SqliteConnection _idempotencyConn = null!;
    private BenchOutboxDbContext _outbox = null!;
    private BenchEventLogDbContext _eventLog = null!;
    private BenchIdempotencyDbContext _idempotency = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        // EnsureCreated 库级判断(库里有任何表即整体跳过)——三 context 三独立库,
        // 各自首连 EnsureCreated 建自己的 CLR 名表
        _outboxConn = await NewPreparedDbAsync("benchefoutbox",
            c => new BenchOutboxDbContext(new DbContextOptionsBuilder<BenchOutboxDbContext>().UseSqlite(c).Options));
        _eventLogConn = await NewPreparedDbAsync("benchefeventlog",
            c => new BenchEventLogDbContext(new DbContextOptionsBuilder<BenchEventLogDbContext>().UseSqlite(c).Options));
        _idempotencyConn = await NewPreparedDbAsync("benchefidempotency",
            c => new BenchIdempotencyDbContext(new DbContextOptionsBuilder<BenchIdempotencyDbContext>().UseSqlite(c).Options));
    }

    private static async Task<SqliteConnection> NewPreparedDbAsync(string dbName, Func<SqliteConnection, DbContext> contextFactory)
    {
        var conn = OpenSharedMemory(dbName);
        await using (var db = contextFactory(conn))
            await db.Database.EnsureCreatedAsync();
        return conn;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _outboxConn.Dispose();
        _eventLogConn.Dispose();
        _idempotencyConn.Dispose();
    }

    [IterationSetup]
    public async Task Setup()
    {
        // 各库清自己的表;EnsureCreated 幂等兜底(InProcess 分区上下文里 GlobalSetup 的
        // 建表可能不跨分区可见——三轮实证,分区里库空则此处补建,库在则 False 跳过)
        await using (var db = new BenchOutboxDbContext(new DbContextOptionsBuilder<BenchOutboxDbContext>().UseSqlite(_outboxConn).Options))
            await db.Database.EnsureCreatedAsync();
        await using (var db = new BenchEventLogDbContext(new DbContextOptionsBuilder<BenchEventLogDbContext>().UseSqlite(_eventLogConn).Options))
            await db.Database.EnsureCreatedAsync();
        await using (var db = new BenchIdempotencyDbContext(new DbContextOptionsBuilder<BenchIdempotencyDbContext>().UseSqlite(_idempotencyConn).Options))
            await db.Database.EnsureCreatedAsync();
        await global::Dapper.SqlMapper.ExecuteAsync(_outboxConn, "DELETE FROM OutboxMessages;");
        await global::Dapper.SqlMapper.ExecuteAsync(_eventLogConn, "DELETE FROM Events; DELETE FROM GlobalPositionAllocators;");
        await global::Dapper.SqlMapper.ExecuteAsync(_idempotencyConn, "DELETE FROM IdempotencyRecords;");
        _outbox = new BenchOutboxDbContext(new DbContextOptionsBuilder<BenchOutboxDbContext>().UseSqlite(_outboxConn).Options);
        _eventLog = new BenchEventLogDbContext(new DbContextOptionsBuilder<BenchEventLogDbContext>().UseSqlite(_eventLogConn).Options);
        _idempotency = new BenchIdempotencyDbContext(new DbContextOptionsBuilder<BenchIdempotencyDbContext>().UseSqlite(_idempotencyConn).Options);
        for (int i = 0; i < BatchSize; i++)
            _outbox.OutboxMessages.Add(NewOutboxMessage());
        await _outbox.SaveChangesAsync();
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Outbox_GetPending_Batch100()
    {
        var msgs = await _outbox.GetPendingMessagesAsync(BatchSize, 10, default);
        if (msgs.Count != BatchSize) throw new InvalidOperationException($"GetPending {msgs.Count} != {BatchSize}");
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask<int> Outbox_Lease_Batch100()
    {
        var msgs = await _outbox.LeasePendingMessagesAsync(BatchSize, "bench",
            TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        for (int i = 0; i < BatchSize; i++)
            _outbox.OutboxMessages.Add(NewOutboxMessage());
        await _outbox.SaveChangesAsync();
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask Outbox_AddSingle()
    {
        _outbox.OutboxMessages.Add(NewOutboxMessage());
        await _outbox.SaveChangesAsync();
    }

    [Benchmark]
    public async ValueTask EventLog_AppendSingle()
        => await _eventLog.AppendAsync("bench-stream", ExpectedStreamVersion.Any,
            [NewEventData()], default);

    [Benchmark]
    public async ValueTask Idempotency_TryStart_Completed()
    {
        var now = DateTimeOffset.UtcNow;
        var key = PalUlid.New().ToString();
        var record = await _idempotency.TryStartAsync("bench-op", key, now, IdempotencyPolicy.Default, default);
        if (record is null) throw new InvalidOperationException("TryStart 返回 null(不应冲突)");
        await _idempotency.MarkCompletedAsync(record, "resp"u8.ToArray(), now.AddSeconds(1));
    }
}
