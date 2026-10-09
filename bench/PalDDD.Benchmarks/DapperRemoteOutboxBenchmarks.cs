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
using PalDDD.Testing;
using PalDDD.Transactions;
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

    [GlobalSetup]
    public void GlobalSetup()
    {
        // 静态注入由模式入口写入——直接经默认入口运行本类会在此 fail-fast
        if (RemoteDialectBenchState.Connection is null)
            throw new InvalidOperationException("远程方言基准须经 persist-pg/persist-mysql 入口运行（缺容器注入）");
        _store = new DapperOutboxStore(RemoteDialectBenchState.Connection, RemoteDialectBenchState.DbType);
    }

    [IterationSetup]
    public async Task Setup()
    {
        await ClearOutboxAsync();
        for (var i = 0; i < BatchSize; i++)
            _store.AddMessage(NewOutboxMessage());
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
            _store.AddMessage(NewOutboxMessage());
        return msgs.Count;
    }

    [Benchmark]
    public void Outbox_AddSingle()
        => _store.AddMessage(NewOutboxMessage());

    private static async Task ClearOutboxAsync()
    {
        await using var cmd = RemoteDialectBenchState.Connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM outbox_messages";
        await cmd.ExecuteNonQueryAsync(default);
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
        cmd.CommandText = "DELETE FROM outbox_messages";
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
