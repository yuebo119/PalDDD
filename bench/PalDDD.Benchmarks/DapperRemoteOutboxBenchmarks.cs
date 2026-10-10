// ─────────────────────────────────────────────────────────────
// PG/MySQL 网络真库车道 — Dapper Outbox（2026-10-08 特性面缺口清单第⑦项，承接多实例租约残余）
// SQLite 单机结论的两个外推盲区：① 三栈差距只在 SQLite 成立；② 多实例租约
// （PG FOR UPDATE SKIP LOCKED / MySQL GET_LOCK 互斥发散 ADR-024）零性能数据。
// 容器形态复用 CI dialect-probe 经验（test/PalDDD.Integration.Tests/DialectProbeTests.cs
// 同款 Testcontainers 4.15 + DDL 真源 docs/sql/{dialect}/000_schema.sql + 镜像真源
// PalDDD.Testing/TestEnvironment，MySQL 显式 root 对齐探针实证）。
// 入口：--persist-pg / --persist-mysql（容器随模式起停）；Docker/WSL 不可达 →
// 报错退出码 2，不静默跳过（TOOL 模式人工运行语义，区别于测试侧 Skip.Test）。
// ─────────────────────────────────────────────────────────────
using System.Data.Common;
using BenchmarkDotNet.Attributes;
using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.Dapper.PostgreSql;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalUlid = ByteAether.Ulid.Ulid;
using PalDDD.Testing;
using PalDDD.Transactions;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace PalDDD.Benchmarks;

/// <summary>容器注入静态状态：模式启动时写入，基准类 GlobalSetup 读取（进程内单方言运行）。</summary>
internal static class RemoteDialectBenchState
{
    public static DbConnection? Connection { get; set; }
    public static string? ConnectionString { get; set; }
    public static DapperDbType DbType { get; set; }
}

/// <summary>一次性容器会话：起容器 → 开主连接 → 应用 DDL 真源；DisposeAsync 回收连接与容器。</summary>
internal sealed class RemoteDialectBenchSession : IAsyncDisposable
{
    private readonly IAsyncDisposable _container;

    public DbConnection Connection { get; }
    public string ConnectionString { get; }
    public DapperDbType DbType { get; }

    private RemoteDialectBenchSession(IAsyncDisposable container, DbConnection connection, string connectionString, DapperDbType dbType)
    {
        _container = container;
        Connection = connection;
        ConnectionString = connectionString;
        DbType = dbType;
    }

    public static async Task<RemoteDialectBenchSession> CreateAsync(bool isPostgreSql, CancellationToken ct = default)
    {
        // 显式 opt-in 外部实例（WSLC/手工容器环境——Testcontainers 走 Docker Remote API，
        // 无法直连 wslc 运行时；对齐 samples/PalDDD.DapperAotProbe 读环境变量先例）：
        // 连接串来自 PALDDD_BENCH_PG_CONN / PALDDD_BENCH_MYSQL_CONN，容器生命周期由外部
        // 管理（例：wslc run -d --name palddd-bench-pg -p 127.0.0.1:15432:5432 …）。
        // 库名白名单守卫 palddd_bench 前缀：基准 wave 会 DELETE 全表，拒连非专用库。
        var external = isPostgreSql
            ? Environment.GetEnvironmentVariable("PALDDD_BENCH_PG_CONN")
            : Environment.GetEnvironmentVariable("PALDDD_BENCH_MYSQL_CONN");
        if (!string.IsNullOrWhiteSpace(external))
            return await CreateExternalAsync(external, isPostgreSql, ct);

        // 库名带 palddd_bench_ 前缀 + 随机后缀（容器即沙箱，无探针侧 ownership marker 仪式）
        if (isPostgreSql)
        {
            var container = new PostgreSqlBuilder(TestEnvironment.PostgreSqlImage)
                .WithDatabase($"palddd_bench_{Guid.NewGuid():N}")
                .Build();
            await container.StartAsync(ct);
            try
            {
                var cs = container.GetConnectionString();
                var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync(ct);
                await ApplyDdlAsync(conn, "docs/sql/postgresql/000_schema.sql", ct);
                return new RemoteDialectBenchSession(container, conn, cs, DapperDbType.PostgreSql);
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }
        else
        {
            // MySQL 显式 root（dialect-probe CI 实跑实证：MYSQL_USER 只对默认库授权，
            // CREATE DATABASE 后连接 probe 库即 Access denied；root 全库权限）
            var container = new MySqlBuilder(TestEnvironment.MySqlImage)
                .WithUsername("root")
                .WithPassword("palddd-bench-root")
                .WithDatabase($"palddd_bench_{Guid.NewGuid():N}")
                .Build();
            await container.StartAsync(ct);
            try
            {
                var cs = container.GetConnectionString();
                var conn = new MySqlConnection(cs);
                await conn.OpenAsync(ct);
                await ApplyDdlAsync(conn, "docs/sql/mysql/000_schema.sql", ct);
                return new RemoteDialectBenchSession(container, conn, cs, DapperDbType.MySql);
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }
    }

    private static async Task<RemoteDialectBenchSession> CreateExternalAsync(string connectionString, bool isPostgreSql, CancellationToken ct)
    {
        EnsureBenchDatabase(connectionString);

        DbConnection conn = isPostgreSql
            ? new NpgsqlConnection(connectionString)
            : new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        try
        {
            // DDL 非幂等（PG/MySQL 均无 IF NOT EXISTS）：表已存在即跳过建表——专用 bench 库
            // 上语义为「已建过」；schema 漂移由后续 store 操作 fail-fast 暴露
            if (!await OutboxTableExistsAsync(conn, isPostgreSql))
                await ApplyDdlAsync(conn, isPostgreSql ? "docs/sql/postgresql/000_schema.sql" : "docs/sql/mysql/000_schema.sql", ct);
            return new RemoteDialectBenchSession(
                NoopDisposable.Instance, conn, connectionString,
                isPostgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static void EnsureBenchDatabase(string connectionString)
    {
        var database = (string?)new DbConnectionStringBuilder { ConnectionString = connectionString }["Database"];
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("外部连接串缺少 Database= 段（库名守卫需要）");
        if (!database.StartsWith("palddd_bench", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"外部实例库名守卫失败：{database} 不以 palddd_bench 开头——基准 wave 会清空表，拒连非专用库");
    }

    private static async Task<bool> OutboxTableExistsAsync(DbConnection conn, bool isPostgreSql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = isPostgreSql
            ? "SELECT 1 FROM information_schema.tables WHERE table_name = 'outbox_messages'"
            : "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'outbox_messages'";
        return await cmd.ExecuteScalarAsync(default) is not null;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>按方言开新连接（并发车道 worker 各持独立连接 = 真实多实例形态）。</summary>
    public DbConnection OpenWorkerConnection()
    {
        DbConnection conn = DbType == DapperDbType.PostgreSql
            ? new NpgsqlConnection(ConnectionString)
            : new MySqlConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private static async Task ApplyDdlAsync(DbConnection conn, string schemaPath, CancellationToken ct)
    {
        var ddl = await File.ReadAllTextAsync(FindSchemaFile(schemaPath), ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 从输出目录向上走找仓库根（镜像 DialectProbeSession 的 DDL 真源定位）
    private static string FindSchemaFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent!;
        if (dir is null)
            throw new InvalidOperationException($"未找到 {relative}——应从仓库内运行 bench");
        return Path.Combine(dir.FullName, relative);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[MemoryDiagnoser]
// ⚠️ 无 job attribute：persist-pg/persist-mysql 入口传显式 MediumRun InProcess
// ManualConfig，类级 job attribute 会与之 union 叠加出双 job（同 FrameworkBenchmarks.cs 头注）
public class DapperRemoteOutboxThroughputBenchmarks : PersistenceBenchBase
{
    private DapperOutboxStore _store = null!;
    private DapperEventLog _eventLog = null!;
    private DapperIdempotencyStore _idempotency = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // 静态注入由模式入口写入——直接经默认入口运行本类会在此 fail-fast
        if (RemoteDialectBenchState.Connection is null)
            throw new InvalidOperationException("远程方言基准须经 persist-pg/persist-mysql 入口运行（缺容器注入）");
        _store = new DapperOutboxStore(RemoteDialectBenchState.Connection, RemoteDialectBenchState.DbType);
        // EventLog/Idempotency 口袋补齐（2026-10-09）：与 Outbox 共享主连接（单线程吞吐类无并发争用）。
        // ⚠️ DapperEventLog 的 dbType 是第三参且默认 Sqlite——首版漏传致 PG/MySQL 走 SQLite 模板
        //（PG 42804 timestamptz/text、MySQL last_insert_rowid 不存在，两库同根因），修后复跑
        _eventLog = new DapperEventLog(RemoteDialectBenchState.Connection, dbType: RemoteDialectBenchState.DbType);
        _idempotency = new DapperIdempotencyStore(RemoteDialectBenchState.Connection, RemoteDialectBenchState.DbType);
    }

    [IterationSetup]
    public async Task Setup()
    {
        await ClearAllAsync();
        for (var i = 0; i < BatchSize; i++)
            await _store.AddMessageAsync(NewOutboxMessage());
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Outbox_GetPending_Batch100()
    {
        var msgs = await _store.GetPendingMessagesAsync(BatchSize, 10, default);
        if (msgs.Count != BatchSize)
            throw new InvalidOperationException($"GetPending {msgs.Count} != {BatchSize}");
        return msgs.Count;
    }

    // 每 invoke = 满额租约 + 重灌 100（口径同 DapperPersistenceBenchmarks.Outbox_Lease_Batch100）
    [Benchmark]
    public async ValueTask<int> Outbox_Lease_Batch100()
    {
        var msgs = await _store.LeasePendingMessagesAsync(BatchSize, "bench",
            TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        for (var i = 0; i < BatchSize; i++)
            await _store.AddMessageAsync(NewOutboxMessage());
        return msgs.Count;
    }

    [Benchmark]
    public async Task Outbox_AddSingle()
        => await _store.AddMessageAsync(NewOutboxMessage());

    // C2'（2026-10-09 优化轮）：k=5 批量写入（多事件保存典型形态）——PG 单 COPY 往返 /
    // MySQL VALUES 单往返（≤16 行路由，2026-10-09 起）；与 AddSingle 对照即批化收益。
    // 行为校验：返回行数必须等于 5（VALUES/COPY 路径行数守卫的端到端实证）
    [Benchmark]
    public async ValueTask<int> Outbox_AddBatch5()
    {
        var messages = new OutboxMessage[5];
        for (var i = 0; i < 5; i++)
            messages[i] = NewOutboxMessage();
        var count = await _store.AddMessagesAsync(messages);
        if (count != 5) throw new InvalidOperationException($"AddMessagesAsync {count} != 5——批量路径行为校验失败");
        return count;
    }

    // EventLog 追加单事件（乐观并发 Any 形态；口径同 DapperPersistenceBenchmarks）
    [Benchmark]
    public async ValueTask EventLog_AppendSingle()
        => await _eventLog.AppendAsync("bench-stream", ExpectedStreamVersion.Any,
            [NewEventData()], default);

    // 幂等全周期：TryStart（唯一键）+ MarkCompleted（口径同 SQLite 三栈车道）
    [Benchmark]
    public async ValueTask Idempotency_TryStart_Completed()
    {
        var now = DateTimeOffset.UtcNow;
        var key = PalUlid.New().ToString();
        var record = await _idempotency.TryStartAsync("bench-op", key, now, IdempotencyPolicy.Default, default);
        if (record is null)
            throw new InvalidOperationException("TryStart 返回 null（唯一键不应冲突）");
        await _idempotency.MarkCompletedAsync(record, "resp"u8.ToArray(), now.AddSeconds(1));
    }

    // 三表全清（EventLog/Idempotency 口袋补齐起 outbox/events/idempotency_records）
    // Error 专项治理轮三（2026-10-10）：DELETE → TRUNCATE——Drain 波段多峰的根因假设是
    // 基准方法学内的死元组累积：每波 800 插入 + 800×2 更新（租约+标记）+ 800 删除 ≈ 3200
    // 死元组 × 40 迭代 × 3 launch，autovacuum 间歇触发制造慢模式（同日 strict 实证 Drain
    // StdDev 76ms/多峰警告四处）。TRUNCATE 即时回收空间使每迭代起始状态恒定。
    // ⚠️ 方法学变更：Drain/SeedOnly 数字与 DELETE 时代不可直接比（旧值含累积死元组开销
    // 与 autovacuum 干扰——那是基准伪影非产品行为）；A/B 判据为 Error 是否实质收窄。
    // 多表逗号 TRUNCATE 是 PG 专有语法，MySQL 仅接受单表（2026-10-10 MySQL strict 首跑
    // 实证：GlobalSetup 即抛 SQL 语法错 near ', events, idempotency_records'，吞吐/PalORM
    // 两类全 NA）——逐表执行，两方言通用。本文件仅远程方言车道可达（SQLite 本地车道另文件）。
    private static async Task ClearAllAsync()
    {
        string[] tables = ["outbox_messages", "events", "idempotency_records"];
        foreach (var table in tables)
        {
            await using var cmd = RemoteDialectBenchState.Connection!.CreateCommand();
            cmd.CommandText = $"TRUNCATE {table}";
            await cmd.ExecuteNonQueryAsync(default);
        }
    }
}

[MemoryDiagnoser]
// ⚠️ 同上注（模式级 ManualConfig）
public class DapperRemoteOutboxConcurrencyBenchmarks : PersistenceBenchBase
{
    private const int TotalMessages = 800;

    private DbConnection _keeper = null!;
    private DbConnection[] _workerConns = null!;
    private DapperOutboxStore[] _workerStores = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (RemoteDialectBenchState.ConnectionString is null)
            throw new InvalidOperationException("远程方言基准须经 persist-pg/persist-mysql 入口运行（缺容器注入）");

        _keeper = OpenKeeper();
        _workerConns = new DbConnection[8];
        _workerStores = new DapperOutboxStore[8];
        for (var w = 0; w < 8; w++)
        {
            _workerConns[w] = OpenKeeper();
            _workerStores[w] = new DapperOutboxStore(_workerConns[w], RemoteDialectBenchState.DbType);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var conn in _workerConns) conn.Dispose();
        _keeper.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Drain_Sequential() => await DrainWaveAsync(1);

    [Benchmark]
    public async Task<int> Drain_2Workers() => await DrainWaveAsync(2);

    [Benchmark]
    public async Task<int> Drain_4Workers() => await DrainWaveAsync(4);

    [Benchmark]
    public async Task<int> Drain_8Workers() => await DrainWaveAsync(8);

    /// <summary>减法锚：清场 + 批量种子（无租约）。口径与 Drain_N 的清场/种子段完全一致。</summary>
    [Benchmark]
    public async Task<int> SeedOnly_800()
    {
        await ClearOutboxAsync();
        return await _workerStores[0].AddMessagesAsync(BuildSeed());
    }

    private static DbConnection OpenKeeper()
    {
        DbConnection conn = RemoteDialectBenchState.DbType == DapperDbType.PostgreSql
            ? new NpgsqlConnection(RemoteDialectBenchState.ConnectionString!)
            : new MySqlConnection(RemoteDialectBenchState.ConnectionString!);
        conn.Open();
        return conn;
    }

    private static List<OutboxMessage> BuildSeed()
    {
        var messages = new List<OutboxMessage>(TotalMessages);
        for (var i = 0; i < TotalMessages; i++)
            messages.Add(NewOutboxMessage());
        return messages;
    }

    private async Task ClearOutboxAsync()
    {
        await using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "TRUNCATE outbox_messages";
        await cmd.ExecuteNonQueryAsync(default);
    }

    private async Task<int> DrainWaveAsync(int workers)
    {
        await ClearOutboxAsync();
        await _workerStores[0].AddMessagesAsync(BuildSeed());

        var tasks = new Task<int>[workers];
        for (var w = 0; w < workers; w++)
        {
            var store = _workerStores[w];
            var owner = $"worker-{w}";
            tasks[w] = Task.Run(async () =>
            {
                var count = 0;
                while (true)
                {
                    var batch = await store.LeasePendingMessagesAsync(
                        BatchSize, owner, TimeSpan.FromSeconds(30), 10, default);
                    if (batch.Count == 0) break;
                    count += batch.Count;
                }
                return count;
            });
        }

        var results = await Task.WhenAll(tasks);
        var leased = 0;
        foreach (var c in results) leased += c;

        // 零重复不变量：同 OutboxConcurrencyBenchmarks——多实例租约主张的直接守卫，
        // 在 PG（FOR UPDATE SKIP LOCKED）/MySQL（ADR-024 互斥发散）上验证
        if (leased != TotalMessages)
            throw new InvalidOperationException(
                $"并发租约不变量破坏：{workers} worker 租走 {leased} ≠ 种子 {TotalMessages}");
        return leased;
    }
}

// ═══════════════════════════════════════════════════════════
// PalORM 栈远程车道（2026-10-08 ⑦ 残余：PG/MySQL 栈扩展）
// 口径对齐 Dapper 远程车道。⚠️ 实测形态约束（首跑实录）：DataSession 单活跃操作
// 限制（非线程安全）——共享单会话多 worker 并发抛「already has an active database
// operation」；多实例形态 = 每 worker 独立 DataSession + store（本车道实现形态，
// 亦是 PalORM 多实例部署的实测结论）。并发 wave 2W 点裁剪（1/4/8 跨形态）。
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 无 job attribute：persist-pg/persist-mysql 入口模式级 ManualConfig（同文件头注）
public class PalOrmRemoteOutboxBenchmarks : PersistenceBenchBase
{
    private const int TotalMessages = 800;
    private DbConnection _keeper = null!;
    private IPalOutboxStore[] _workerStores = null!;
    private List<IAsyncDisposable> _sessions = null!;
    private bool _isPostgreSql;
    // EventLog/Idempotency 口袋补齐（2026-10-09）：专用会话（吞吐单线程），接口类型分派
    private IEventLog? _eventLog;
    private IIdempotencyStore? _idempotency;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var cs = RemoteDialectBenchState.ConnectionString
            ?? throw new InvalidOperationException("远程方言基准须经 persist-pg/persist-mysql 入口运行（缺容器注入）");
        _isPostgreSql = RemoteDialectBenchState.DbType == DapperDbType.PostgreSql;
        _keeper = _isPostgreSql ? new NpgsqlConnection(cs) : new MySqlConnection(cs);
        _keeper.Open();

        // 每 worker 独立 DataSession（单会话非线程安全——类头注实测实录）
        _workerStores = new IPalOutboxStore[8];
        _sessions = new List<IAsyncDisposable>(8);
        for (var w = 0; w < 8; w++)
        {
            if (_isPostgreSql)
            {
                var session = await DataSession<PostgreSqlProvider>.CreateAsync(DbOptions.Development(cs), default);
                _sessions.Add(session);
                _workerStores[w] = new PostgreSqlOutboxStore(session);
            }
            else
            {
                var session = await DataSession<MySqlProvider>.CreateAsync(DbOptions.Development(cs), default);
                _sessions.Add(session);
                _workerStores[w] = new MySqlOutboxStore(session);
            }
        }

        // EventLog/Idempotency 专用会话（复用 worker 0 的会话族；接口按方言动态分派）
        if (_isPostgreSql)
        {
            var session = await DataSession<PostgreSqlProvider>.CreateAsync(DbOptions.Development(cs), default);
            _sessions.Add(session);
            _eventLog = new PostgreSqlEventLog(session);
            _idempotency = new PostgreSqlIdempotencyStore(session);
        }
        else
        {
            var session = await DataSession<MySqlProvider>.CreateAsync(DbOptions.Development(cs), default);
            _sessions.Add(session);
            _eventLog = new MySqlEventLog(session);
            _idempotency = new MySqlIdempotencyStore(session);
        }
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        foreach (var session in _sessions)
            await session.DisposeAsync();
        _keeper.Dispose();
    }

    [IterationSetup]
    public async Task Setup()
    {
        await ClearOutboxAsync();
        await ClearAuxTablesAsync();
        for (var i = 0; i < BatchSize; i++)
            await _workerStores[0].AddMessagesAsync([NewOutboxMessage()]);
    }

    // EventLog 追加单事件（乐观并发 Any 形态；口径同 SQLite 三栈车道）
    [Benchmark]
    public async ValueTask EventLog_AppendSingle()
        => await _eventLog!.AppendAsync("bench-stream", ExpectedStreamVersion.Any,
            [NewEventData()], default);

    // 幂等全周期：TryStart（唯一键）+ MarkCompleted（口径同 SQLite 三栈车道）
    [Benchmark]
    public async ValueTask Idempotency_TryStart_Completed()
    {
        var now = DateTimeOffset.UtcNow;
        var key = PalUlid.New().ToString();
        var record = await _idempotency!.TryStartAsync("bench-op", key, now, IdempotencyPolicy.Default, default);
        if (record is null)
            throw new InvalidOperationException("TryStart 返回 null（唯一键不应冲突）");
        await _idempotency.MarkCompletedAsync(record, "resp"u8.ToArray(), now.AddSeconds(1));
    }

    // events/idempotency_records 清场（EventLog/Idempotency 口袋补齐；outbox 由 ClearOutboxAsync
    // 负责）。逐表 TRUNCATE：MySQL 不支持多表逗号语法（见 ClearAllAsync 注）
    private async Task ClearAuxTablesAsync()
    {
        string[] tables = ["events", "idempotency_records"];
        foreach (var table in tables)
        {
            await using var cmd = _keeper.CreateCommand();
            cmd.CommandText = $"TRUNCATE {table}";
            await cmd.ExecuteNonQueryAsync(default);
        }
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<int> Outbox_GetPending_Batch100()
    {
        var msgs = await _workerStores[0].GetPendingMessagesAsync(BatchSize, 10, default);
        if (msgs.Count != BatchSize)
            throw new InvalidOperationException($"GetPending {msgs.Count} != {BatchSize}");
        return msgs.Count;
    }

    [Benchmark]
    public async ValueTask<int> Outbox_Lease_Batch100()
    {
        var msgs = await _workerStores[0].LeasePendingMessagesAsync(BatchSize, "bench",
            TimeSpan.FromSeconds(30), 10, default);
        EnsureFullLease(msgs);
        for (var i = 0; i < BatchSize; i++)
            await _workerStores[0].AddMessagesAsync([NewOutboxMessage()]);
        return msgs.Count;
    }

    [Benchmark]
    public async Task<int> Drain_Sequential() => await DrainWaveAsync(1);

    [Benchmark]
    public async Task<int> Drain_4Workers() => await DrainWaveAsync(4);

    [Benchmark]
    public async Task<int> Drain_8Workers() => await DrainWaveAsync(8);

    /// <summary>减法锚：清场 + 批量种子（无租约）。口径与 Drain_N 的清场/种子段一致。</summary>
    [Benchmark]
    public async Task<int> SeedOnly_800()
    {
        await ClearOutboxAsync();
        return await _workerStores[0].AddMessagesAsync(BuildSeed());
    }

    private static List<OutboxMessage> BuildSeed()
    {
        var messages = new List<OutboxMessage>(TotalMessages);
        for (var i = 0; i < TotalMessages; i++)
            messages.Add(NewOutboxMessage());
        return messages;
    }

    private async Task ClearOutboxAsync()
    {
        await using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "TRUNCATE outbox_messages";
        await cmd.ExecuteNonQueryAsync(default);
    }

    private async Task<int> DrainWaveAsync(int workers)
    {
        await ClearOutboxAsync();
        await _workerStores[0].AddMessagesAsync(BuildSeed());

        // 每 worker 独立会话 + store（PalORM 多实例形态）
        var tasks = new Task<int>[workers];
        for (var w = 0; w < workers; w++)
        {
            var store = _workerStores[w];
            var owner = $"worker-{w}";
            tasks[w] = Task.Run(async () =>
            {
                var count = 0;
                while (true)
                {
                    var batch = await store.LeasePendingMessagesAsync(
                        BatchSize, owner, TimeSpan.FromSeconds(30), 10, default);
                    if (batch.Count == 0) break;
                    count += batch.Count;
                }
                return count;
            });
        }

        var results = await Task.WhenAll(tasks);
        var leased = 0;
        foreach (var c in results) leased += c;

        // 零重复不变量：同 Dapper 远程车道（ADR-024 PalORM 侧 MySQL 无锁子句的并发实证点）
        if (leased != TotalMessages)
            throw new InvalidOperationException(
                $"并发租约不变量破坏：{workers} worker 租走 {leased} ≠ 种子 {TotalMessages}");
        return leased;
    }
}

// ═══════════════════════════════════════════════════════════
// EF 栈租约 SQL 形态车道（MySQL，2026-10-09 缺口清单⑦收尾）
// EF 栈级车道当前不可行（2026-10-09 实证：上游无 EF 11 rc.1 配套 Provider——
// Npgsql.EF 11 线仅 preview 且钉死 EF preview 精确版本与仓 rc.1 冲突 NU1107、
// Pomelo 无 11 线 NU1102，即当初死条目清理注释「待出 RC 后跟进」的真因）。
// ADR-024 指引「需要强互斥的 MySQL 场景选 EF 栈（SKIP LOCKED）」的验证对象是
// SQL 形态（MySqlOutboxDbContext 步骤 1：JOIN 派生表 FOR UPDATE SKIP LOCKED）——
// 本车道逐字保留该锁语义直跑（标识符适配 snake_case 基线表），以 UPDATE 受影响
// 行数之和证明互斥：SUM == 种子数 ⇔ 零重复零丢失。Provider 出 RC 后可升级为栈级车道。
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 无 job attribute：persist-mysql 入口模式级 ManualConfig（同文件头注）
public class MySqlSkipLockedShapeBenchmarks : PersistenceBenchBase
{
    private const int TotalMessages = 800;
    private DbConnection _keeper = null!;
    private DbConnection[] _workerConns = null!;
    private DapperOutboxStore _seedStore = null!;

    // MySqlOutboxDbContext.LeasePendingMessagesAsync 步骤 1 逐字迁移（{N} 占位符 →
    // @named 参数；表/列名适配 snake_case 基线表；FOR UPDATE SKIP LOCKED 锁语义不变）
    private const string LeaseSql = """
        UPDATE outbox_messages t
        JOIN (
            SELECT id FROM outbox_messages
            WHERE status = 0 AND retry_count < @maxRetry
              AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
              AND (locked_until IS NULL OR locked_until <= @now)
            ORDER BY created_at
            LIMIT @n FOR UPDATE SKIP LOCKED
        ) AS sub ON t.id = sub.id
        SET t.locked_by = @owner, t.locked_until = @until
        """;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var cs = RemoteDialectBenchState.ConnectionString
            ?? throw new InvalidOperationException("远程方言基准须经 persist-mysql 入口运行（缺容器注入）");
        if (RemoteDialectBenchState.DbType != DapperDbType.MySql)
            throw new InvalidOperationException("SKIP LOCKED 形态车道仅限 --persist-mysql");

        _keeper = new MySqlConnection(cs);
        _keeper.Open();
        _seedStore = new DapperOutboxStore(_keeper, DapperDbType.MySql);

        _workerConns = new DbConnection[8];
        for (var w = 0; w < 8; w++)
        {
            _workerConns[w] = new MySqlConnection(cs);
            _workerConns[w].Open();
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var conn in _workerConns) conn.Dispose();
        _keeper.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Drain_Sequential() => await DrainWaveAsync(1);

    [Benchmark]
    public async Task<int> Drain_4Workers() => await DrainWaveAsync(4);

    [Benchmark]
    public async Task<int> Drain_8Workers() => await DrainWaveAsync(8);

    /// <summary>减法锚：清场 + 批量种子（无租约）。口径与 Drain_N 的清场/种子段一致。</summary>
    [Benchmark]
    public async Task<int> SeedOnly_800()
    {
        await ClearOutboxAsync();
        return await _seedStore.AddMessagesAsync(BuildSeed());
    }

    private static List<OutboxMessage> BuildSeed()
    {
        var messages = new List<OutboxMessage>(TotalMessages);
        for (var i = 0; i < TotalMessages; i++)
            messages.Add(NewOutboxMessage());
        return messages;
    }

    private async Task ClearOutboxAsync()
    {
        await using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "TRUNCATE outbox_messages";
        await cmd.ExecuteNonQueryAsync(default);
    }

    private async Task<int> DrainWaveAsync(int workers)
    {
        await ClearOutboxAsync();
        await _seedStore.AddMessagesAsync(BuildSeed());

        var tasks = new Task<int>[workers];
        for (var w = 0; w < workers; w++)
        {
            var conn = _workerConns[w];
            var owner = $"worker-{w}";
            tasks[w] = Task.Run(async () =>
            {
                var leased = 0;
                while (true)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = LeaseSql;
                    AddParameters(cmd, owner);
                    var affected = await cmd.ExecuteNonQueryAsync(default);
                    if (affected == 0) break;
                    leased += affected;
                }
                return leased;
            });
        }

        var results = await Task.WhenAll(tasks);
        var total = 0;
        foreach (var c in results) total += c;

        // 互斥不变量：受影响行数之和 == 种子数 ⇔ SKIP LOCKED 派生表互斥成立
        //（重叠租约则 > 种子数，漏租则 < 种子数；死锁则异常先于此守卫——与 Dapper 栈对照点）
        if (total != TotalMessages)
            throw new InvalidOperationException(
                $"SKIP LOCKED 互斥不变量破坏：{workers} worker 合计 {total} ≠ 种子 {TotalMessages}");
        return total;
    }

    private static void AddParameters(DbCommand cmd, string owner)
    {
        var now = DateTimeOffset.UtcNow;
        var p = cmd.CreateParameter();
        p.ParameterName = "@maxRetry"; p.Value = 10; cmd.Parameters.Add(p);
        p = cmd.CreateParameter();
        p.ParameterName = "@now"; p.Value = now; cmd.Parameters.Add(p);
        p = cmd.CreateParameter();
        p.ParameterName = "@n"; p.Value = BatchSize; cmd.Parameters.Add(p);
        p = cmd.CreateParameter();
        p.ParameterName = "@owner"; p.Value = owner; cmd.Parameters.Add(p);
        p = cmd.CreateParameter();
        p.ParameterName = "@until"; p.Value = now.Add(TimeSpan.FromSeconds(30)); cmd.Parameters.Add(p);
    }
}

// ═══════════════════════════════════════════════════════════
// PG 方言特性车道（2026-10-09 缺口清单⑦收尾；仅 --persist-pg 运行）
// Pipeline 批处理（单往返 vs 逐条对照）/ 读写分离路由决策 / LISTEN-NOTIFY 往返。
// NOTIFIER 说明：PostgreSqlOutboxNotifier 是 BackgroundService 包装，推送延迟属
// 异步服务量纲——本车道直接测其底层能力（原生 LISTEN/NOTIFY 往返），包装开销由
// 集成测试承载。
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 无 job attribute：persist-pg 入口模式级 ManualConfig（同文件头注）
public class PgDialectFeaturesBenchmarks : PersistenceBenchBase
{
    private NpgsqlConnection _keeper = null!;
    private NpgsqlConnection _listener = null!;
    private NpgsqlConnection _notifySender = null!;
    private PostgreSqlReadWriteRouter _router = null!;
    private volatile TaskCompletionSource<bool>? _notifySignal;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var cs = RemoteDialectBenchState.ConnectionString
            ?? throw new InvalidOperationException("远程方言基准须经 persist-pg 入口运行（缺容器注入）");
        if (RemoteDialectBenchState.DbType != DapperDbType.PostgreSql)
            throw new InvalidOperationException("PG 方言特性车道仅限 --persist-pg");

        _keeper = new NpgsqlConnection(cs);
        await _keeper.OpenAsync();

        // Pipeline 批处理连接
        _notifySender = new NpgsqlConnection(cs);
        await _notifySender.OpenAsync();

        // LISTEN 通道：监听连接常开 + 事件转发到逐 op 的 TCS
        _listener = new NpgsqlConnection(cs);
        await _listener.OpenAsync();
        _listener.Notification += (_, _) => _notifySignal?.TrySetResult(true);
        await using (var cmd = _listener.CreateCommand())
        {
            cmd.CommandText = "LISTEN bench_notify_ch";
            await cmd.ExecuteNonQueryAsync(default);
        }

        // 读写分离路由：双数据源（无真实副本时同库——测路由决策与池获取成本）
        _router = new PostgreSqlReadWriteRouter(
            NpgsqlDataSource.Create(cs), NpgsqlDataSource.Create(cs));
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _router.DisposeAsync();
        _listener.Dispose();
        _notifySender.Dispose();
        _keeper.Dispose();
    }

    private async Task ClearOutboxAsync()
    {
        await using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "TRUNCATE outbox_messages";
        await cmd.ExecuteNonQueryAsync(default);
    }

    // Pipeline 批处理：100 条 INSERT 一次往返（NpgsqlPipeline.Add ×100 → ExecuteAsync）
    [Benchmark(Baseline = true)]
    public async Task<int> Pipeline_BatchInsert100()
    {
        await ClearOutboxAsync();
        await using var pipeline = new PostgreSqlPipeline(_notifySender);
        for (var i = 0; i < BatchSize; i++)
        {
            var m = NewOutboxMessage();
            pipeline.Add(
                "INSERT INTO outbox_messages (id, type, payload, content_type, schema_version, status, created_at) "
                + "VALUES (@id, @type, @payload, @ct, @sv, @st, @ca)",
                new NpgsqlParameter("id", m.Id.ToString()),
                new NpgsqlParameter("type", m.Type),
                new NpgsqlParameter("payload", m.Payload),
                new NpgsqlParameter("ct", m.ContentType),
                new NpgsqlParameter("sv", m.SchemaVersion),
                new NpgsqlParameter("st", (int)m.Status),
                new NpgsqlParameter("ca", m.CreatedAt));
        }
        return await pipeline.ExecuteAsync(default);
    }

    // 对照：同 100 条逐条往返（差值即单往返批处理的净收益；含清场）
    [Benchmark]
    public async Task<int> Sequential_Insert100_Control()
    {
        await ClearOutboxAsync();
        for (var i = 0; i < BatchSize; i++)
        {
            var m = NewOutboxMessage();
            await using var cmd = _notifySender.CreateCommand();
            cmd.CommandText =
                "INSERT INTO outbox_messages (id, type, payload, content_type, schema_version, status, created_at) "
                + "VALUES (@id, @type, @payload, @ct, @sv, @st, @ca)";
            cmd.Parameters.AddWithValue("id", m.Id.ToString());
            cmd.Parameters.AddWithValue("type", m.Type);
            cmd.Parameters.AddWithValue("payload", m.Payload);
            cmd.Parameters.AddWithValue("ct", m.ContentType);
            cmd.Parameters.AddWithValue("sv", m.SchemaVersion);
            cmd.Parameters.AddWithValue("st", (int)m.Status);
            cmd.Parameters.AddWithValue("ca", m.CreatedAt);
            await cmd.ExecuteNonQueryAsync(default);
        }
        return BatchSize;
    }

    // 读写分离路由决策 + 连接池获取（获取即还——真实形态由中间件按请求借还）
    [Benchmark]
    public int Router_GetWriter()
    {
        var conn = _router.GetWriter();
        conn.Dispose();
        return 0;
    }

    [Benchmark]
    public int Router_GetReader()
    {
        var conn = _router.GetReader();
        conn.Dispose();
        return 0;
    }

    // LISTEN/NOTIFY 往返（PostgreSqlOutboxNotifier 底层能力）：PG 协议语义——通知随
    // 监听连接的下一次命令结果冲刷投递（空闲连接不主动推送），NOTIFY 后跟一次冲刷读
    // 触发 Notification 事件；口径 = 发送 + 冲刷读 + 事件到达
    [Benchmark]
    public async Task<bool> ListenNotify_Roundtrip()
    {
        _notifySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var cmd = _notifySender.CreateCommand())
        {
            cmd.CommandText = "NOTIFY bench_notify_ch, 'bench'";
            await cmd.ExecuteNonQueryAsync(default);
        }
        await using (var flush = _listener.CreateCommand())
        {
            flush.CommandText = "SELECT 1";
            await flush.ExecuteNonQueryAsync(default);
        }
        var received = await _notifySignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return received;
    }
}
