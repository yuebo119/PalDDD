// ─────────────────────────────────────────────────────────────
// Outbox 并发车道 — N worker 租约争用（2026-10-08 特性面缺口清单第 ① 项）
// 「多实例并发发布零丢失零重复」此前只有正确性测试，无性能数据。本车道把该主张
// 升级为可测量口径：每 invoke = 一个完整 wave（清场 + 种子 800 条 + N 个 worker
// 并发抢租至清空 + 零重复不变量守卫）。两层对照：
//   InMemory 层——store 内部 lock 争用（LeasePendingMessagesAsync 全程持锁，测框架簿记开销）
//   Dapper SQLite 层——真库租约 UPDATE 争用（单写者串行 + busy 等待，单机最坏形态；
//     PG/MySQL 网络真库车道仍缺，见 performance.md 未完成项）
// 口径注：wave 含种子与 Task.Run 调度成本（跨 N 恒定，N 间比值即争用信号），
// SeedOnly_800 提供趋势级减法锚。入口 --concurrency（MediumRun InProcess 同 --framework）。
// ─────────────────────────────────────────────────────────────
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using PalDDD.Dapper;
using PalDDD.Transactions;

namespace PalDDD.Benchmarks;

[MemoryDiagnoser]
// ⚠️ 无 job attribute：--concurrency 入口传显式 MediumRun InProcess ManualConfig，
// 类级 job attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker
// 上触发 DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
public class OutboxConcurrencyInMemoryBenchmarks
{
    protected const int TotalMessages = 800;
    protected const int BatchSize = 100;

    [Benchmark(Baseline = true)]
    public async Task<int> Drain_Sequential() => await DrainWaveAsync(1);

    [Benchmark]
    public async Task<int> Drain_2Workers() => await DrainWaveAsync(2);

    [Benchmark]
    public async Task<int> Drain_4Workers() => await DrainWaveAsync(4);

    [Benchmark]
    public async Task<int> Drain_8Workers() => await DrainWaveAsync(8);

    /// <summary>减法锚：种子与清场成本（无租约）。Drain_N − SeedOnly ≈ 租约耗时的趋势估计。</summary>
    [Benchmark]
    public async Task<int> SeedOnly_800()
    {
        var (store, inserted) = await SeedAsync();
        return inserted;
    }

    private static List<OutboxMessage> BuildSeed()
    {
        var messages = new List<OutboxMessage>(TotalMessages);
        for (var i = 0; i < TotalMessages; i++)
            messages.Add(new OutboxMessage
            {
                Type = "OrderCreated",
                Payload = "{}"u8.ToArray(),
                ContentType = "application/json"
            });
        return messages;
    }

    protected static async Task<(InMemoryOutboxStore Store, int Inserted)> SeedAsync()
    {
        var store = new InMemoryOutboxStore();
        var inserted = await store.AddMessagesAsync(BuildSeed());
        return (store, inserted);
    }

    protected static async Task<int> DrainWaveAsync(InMemoryOutboxStore store, int workers)
    {
        var tasks = new Task<int>[workers];
        for (var w = 0; w < workers; w++)
        {
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

        // 零重复不变量（多实例主张的直接守卫）：并发租约重叠时总和 > 种子数，
        // 漏租则 < 种子数——两种破坏形态均抛出，防基准在失真形态下静默出数
        if (leased != TotalMessages)
            throw new InvalidOperationException(
                $"并发租约不变量破坏：{workers} worker 租走 {leased} ≠ 种子 {TotalMessages}");
        return leased;
    }

    private static async Task<int> DrainWaveAsync(int workers)
    {
        var (store, _) = await SeedAsync();
        return await DrainWaveAsync(store, workers);
    }
}

[MemoryDiagnoser]
// ⚠️ 同上注（--concurrency 入口模式级 ManualConfig）
public class OutboxConcurrencyDapperSqliteBenchmarks : PersistenceBenchBase
{
    private const string DbName = "benchconcurrency";
    private const int TotalMessages = 800;
    private SqliteConnection _keeper = null!;
    private SqliteConnection[] _workerConns = null!;
    private DapperOutboxStore[] _workerStores = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _keeper = OpenSharedMemory(DbName);
        await CreateSharedSchemaAsync(_keeper);

        // 每 worker 独立连接 + 独立 store 实例（真实多实例形态：各自连接池抢租）
        // Microsoft.Data.Sqlite 默认 Default Timeout=30s 映射 busy_timeout——并发写互撞
        // 时等待重试而非 SQLITE_BUSY 抛出（共享内存库无 fsync，等待为纯锁竞争）
        _workerConns = new SqliteConnection[8];
        _workerStores = new DapperOutboxStore[8];
        for (var w = 0; w < 8; w++)
        {
            _workerConns[w] = OpenSharedMemory(DbName);
            _workerStores[w] = new DapperOutboxStore(_workerConns[w], DapperDbType.Sqlite);
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
        await ClearSharedSchemaAsync(_keeper);
        return await _workerStores[0].AddMessagesAsync(BuildSeed());
    }

    private static List<OutboxMessage> BuildSeed()
    {
        var messages = new List<OutboxMessage>(TotalMessages);
        for (var i = 0; i < TotalMessages; i++)
            messages.Add(NewOutboxMessage());
        return messages;
    }

    private async Task<int> DrainWaveAsync(int workers)
    {
        await ClearSharedSchemaAsync(_keeper);
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

        // 零重复不变量：同 InMemory 车道（真库层守卫 SQL 租约谓词在并发下的正确性）
        if (leased != TotalMessages)
            throw new InvalidOperationException(
                $"并发租约不变量破坏：{workers} worker 租走 {leased} ≠ 种子 {TotalMessages}");
        return leased;
    }
}
