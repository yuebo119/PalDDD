using PalDDD.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using PalDDD.Core;
using System.Diagnostics;

if (args is ["--smoke"])
{
    return SmokeBenchmarks.Run();
}

// 2026-10-09 新维度轮①：--qps——绝对 QPS 并行量具（非 BDN，1/4/8 线程 × dispatch/chain
// 两场景，每格 3s 实测聚合 ops/sec；口径趋势级，同日同机可比）。
if (args is ["--qps"])
{
    return QpsHarness.Run();
}

// 2026-10-09 新维度轮②：--compare——横向对比（MediatR 12.5.0 同进程同 harness 四点对照；
// CAP 发布对比经 PALDDD_BENCH_CAP 环境变量 opt-in，见 CapComparisonBenchmarks 头注）。
if (args.Contains("--compare", StringComparer.OrdinalIgnoreCase))
{
    var medium = MediumInProcessConfig();
    BenchmarkRunner.Run<MediatRComparisonBenchmarks>(medium);
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PALDDD_BENCH_CAP_MYSQL"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PALDDD_BENCH_CAP_RABBIT")))
    {
        // 2026-10-10 前置检查门配套：BDN BenchmarkRunner 不因基准失败（如 GlobalSetup 抛出）
        // 置非零进程退出码——曾使重试风暴垃圾数字以 exit 0 静默通过。此处消费 Summary
        // 判定失败并返回 1（Report.Success=false 覆盖 setup 异常/执行异常两形态）
        var capSummary = BenchmarkRunner.Run<CapComparisonBenchmarks>(medium);
        if (capSummary.Reports.Any(r => !r.Success))
            return 1;
    }
    else
        Console.WriteLine("⏭ CAP 对比跳过（需 PALDDD_BENCH_CAP_MYSQL + PALDDD_BENCH_CAP_RABBIT 环境变量，见 CapComparisonBenchmarks 头注）");
    return 0;
}

// net11-rc runtime moniker 不被 BDN 0.15.8 的 DotNetSdkValidator 识别(GetRuntimeVersion
// throw NotRecognizedException;验证内嵌在 CsProjCoreToolchain.Validate,且 Switcher 在
// filter 前对全程序集 case 验证——任何 [ShortRunJob] 类都会触发崩溃)。
// 解法:--persist 显式跑三栈持久化基准类(逐类 BenchmarkRunner.Run<T> + [InProcess] attribute,
// InProcessValidator 不查 SDK moniker);其余类走 Switcher(等 BDN 上游补 net11 moniker 恢复)。
if (args.Contains("--verify-persist", StringComparer.OrdinalIgnoreCase))
{
    return await PersistenceVerifyRunner.RunAsync();
}

if (args.Contains("--persist", StringComparer.OrdinalIgnoreCase))
{
    // 片3-P2-1 修复（2026-09-19 第五十四轮）：--persist 也传显式 InProcess config——
    // 原依赖类 [InProcess] attribute（已随本修复移除），避免 attribute 与 ManualConfig
    // 叠加产生双 job（BDN 0.15.8 实测：config 合并对 jobs 是 union 不去重）
    var inProcessOnly = ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(Job.InProcess.WithToolchain(InProcessEmitToolchain.Instance))
        .AddDiagnoser(MemoryDiagnoser.Default);
    BenchmarkRunner.Run<DapperPersistenceBenchmarks>(inProcessOnly);
    BenchmarkRunner.Run<PalOrmPersistenceBenchmarks>(inProcessOnly);
    BenchmarkRunner.Run<EfCorePersistenceBenchmarks>(inProcessOnly);
    return 0;
}

// decision-2026-09-17 §2.5 验收三段式（2026-09-19 增）：before 锚须 medium 口径
// 单次自比——历史两次 ShortRun 运行差 53%，拼接区间不作基线。显式 ManualConfig
// （Job.Medium + InProcess + MemoryDiagnoser + Lease filter），不依赖类 attribute
// 隐式默认，口径随 BDN artifacts 完整落盘。走 Run<T> 而非 Switcher（同 --persist
// 理由：Switcher 对全程序集验证 net11 moniker 必崩）。
if (args.Contains("--persist-medium", StringComparer.OrdinalIgnoreCase))
{
    // P2（perf-run-2026-09-20）：medium 报告写独立目录——原形态覆盖全量报告
    // github.md（裁决数字无处查）。归档纪律：medium 数字入 docs/performance.md
    // 时带日期，artifacts-medium 目录留存原始报告。
    var leaseOnly = ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(Job.MediumRun.WithToolchain(InProcessEmitToolchain.Instance))
        .AddDiagnoser(MemoryDiagnoser.Default)
        .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "artifacts-medium"))
        .AddFilter(new NameFilter(name =>
            name.Contains("Outbox_Lease_Batch100", StringComparison.Ordinal)));
    BenchmarkRunner.Run<DapperPersistenceBenchmarks>(leaseOnly);
    BenchmarkRunner.Run<PalOrmPersistenceBenchmarks>(leaseOnly);
    BenchmarkRunner.Run<EfCorePersistenceBenchmarks>(leaseOnly);
    return 0;
}

// P1（perf-run-2026-09-20）：Saga 车道基准锚——ProcessEventAsync 编排开销
// （Normal 基线 / FanOut items=4），供骨架优化与 v3.0 子项粒度改造做回归比对。
if (args.Contains("--saga", StringComparer.OrdinalIgnoreCase))
{
    BenchmarkRunner.Run<SagaLaneBenchmarks>();
    return 0;
}

// 2026-09-25 特性审计三轮：压缩路径基准（GZip/Deflate span 编码器改造的量具先行，
// InProcess 显式 config 同 --persist 理由）。
if (args.Contains("--compression", StringComparer.OrdinalIgnoreCase))
{
    var inProcessOnly = ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(Job.InProcess.WithToolchain(InProcessEmitToolchain.Instance))
        .AddDiagnoser(MemoryDiagnoser.Default);
    BenchmarkRunner.Run<CompressionBenchmarks>(inProcessOnly);
    return 0;
}

// 2026-10-08 性能测试系统优化：--framework / --infra——InProcess 旁路扩到 Framework 5 类
// 与 Infra 6 类。此前 11 类仅有 [ShortRunJob] attribute 形态，只能走默认 Switcher 入口，
// 而 Switcher 在 net11-rc 工具链必崩（见顶部 moniker 注释），正式报告实际不可得，
// testing.md §3.2「BDN 正式」列长期纸面 ✅。本两模式把该列落地。口径对齐
// --persist-medium（decision-2026-09-17 §2.5：medium 单次自比，ShortRun 历史 53%
// 跨轮差不入基线），artifacts 同落 artifacts-medium（归档纪律：数字入 performance.md 带日期）。
// 2026-10-10 Q2：--strict——严格档（引用级数字专用）：对环境敏感噪声类加密采样
// （迭代 15→40、预热 10→20、launch 2→3），压 Error 半置信区间；口径注：ns/op 语义不变
// 但与 medium 档数字分列（账本引用 strict 数字须带档位标签）。
// 覆盖类：OutboxThroughputBenchmarks（infra E13~18% 四轮一贯环境敏感带）+
// SmartEnumBenchmarks（ns 级固有抖动，历史 11~25% 带）。其余类 medium 档已 <5% 无需加密。
if (args.Contains("--strict", StringComparer.OrdinalIgnoreCase))
{
    var strict = ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(Job.MediumRun.WithToolchain(InProcessEmitToolchain.Instance)
            .WithIterationCount(40).WithWarmupCount(20).WithLaunchCount(3))
        .AddDiagnoser(MemoryDiagnoser.Default)
        .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "artifacts-strict"));
    BenchmarkRunner.Run<OutboxThroughputBenchmarks>(strict);
    BenchmarkRunner.Run<SmartEnumBenchmarks>(strict);
    return 0;
}

static IConfig MediumInProcessConfig()
    => ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(Job.MediumRun.WithToolchain(InProcessEmitToolchain.Instance))
        .AddDiagnoser(MemoryDiagnoser.Default)
        .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "artifacts-medium"));

if (args.Contains("--framework", StringComparer.OrdinalIgnoreCase))
{
    var medium = MediumInProcessConfig();
    BenchmarkRunner.Run<EntityDomainEventBenchmarks>(medium);
    BenchmarkRunner.Run<ValueObjectBenchmarks>(medium);
    BenchmarkRunner.Run<SmartEnumBenchmarks>(medium);
    BenchmarkRunner.Run<ValidationBenchmarks>(medium);
    BenchmarkRunner.Run<EntityCreationBenchmarks>(medium);
    return 0;
}

if (args.Contains("--infra", StringComparer.OrdinalIgnoreCase))
{
    var medium = MediumInProcessConfig();
    BenchmarkRunner.Run<OutboxThroughputBenchmarks>(medium);
    BenchmarkRunner.Run<InboxThroughputBenchmarks>(medium);
    BenchmarkRunner.Run<EventLogBenchmarks>(medium);
    BenchmarkRunner.Run<SagaStateBenchmarks>(medium);
    BenchmarkRunner.Run<SqlGenBenchmarks>(medium);
    BenchmarkRunner.Run<ConfigurationBenchmarks>(medium);
    BenchmarkRunner.Run<CompressionBenchmarks>(medium);
    return 0;
}

// 2026-10-08 缺口补齐轮：--app——CQRS 分发链（零行为基线 vs 单直通行为，PipelineStateMachine
// 编排开销的增量可见）+ 消息序列化（源生成 JSON 序列化/反序列化/Catalog 查找）。评估确认
// 两车道是消息发布路径缺位面：分发此前仅有 <500B 分配契约，序列化零数据。
if (args.Contains("--app", StringComparer.OrdinalIgnoreCase))
{
    var medium = MediumInProcessConfig();
    BenchmarkRunner.Run<DispatcherBenchmarks>(medium);
    BenchmarkRunner.Run<SerializationBenchmarks>(medium);
    return 0;
}

// 2026-10-08 特性面缺口清单第 ① 项：--concurrency——Outbox N worker 租约争用
// （InMemory 簿记层 + Dapper SQLite 真库层，每 wave 带零重复不变量守卫）。
// 「多实例并发发布零丢失零重复」从正确性主张升级为性能数据。
if (args.Contains("--concurrency", StringComparer.OrdinalIgnoreCase))
{
    var medium = MediumInProcessConfig();
    BenchmarkRunner.Run<OutboxConcurrencyInMemoryBenchmarks>(medium);
    BenchmarkRunner.Run<OutboxConcurrencyDapperSqliteBenchmarks>(medium);
    return 0;
}

// 2026-10-08 特性面缺口清单第⑦项：persist-pg / persist-mysql——PG/MySQL 网络真库车道
//（Testcontainers 一次性容器 + DDL 真源 docs/sql/{dialect}/000_schema.sql，镜像真源
// PalDDD.Testing/TestEnvironment；承接多实例租约残余：并发 wave 在 PG FOR UPDATE
// SKIP LOCKED / MySQL GET_LOCK 互斥发散 ADR-024 上的真实形态）。Docker/WSL 不可达 →
// 报错退出码 2，不静默跳过（TOOL 人工运行语义，区别于测试侧 Skip.Test 环境守卫）。
if (args.Contains("--persist-pg", StringComparer.OrdinalIgnoreCase))
    return await RunRemoteDialectAsync(isPostgreSql: true);

if (args.Contains("--persist-mysql", StringComparer.OrdinalIgnoreCase))
    return await RunRemoteDialectAsync(isPostgreSql: false);

// 2026-10-08 缺口清单第③④⑤⑥⑧项：--coverage——Projection 吞吐 / 消息演化管线 /
// Specification 内存路径 / Hosting 每请求开销 / 端到端单命令链路（全部零外部依赖，
// 端到端与 Hosting 为框架开销层口径：InMemory store / DefaultHttpContext，无网络与 DB）。
if (args.Contains("--coverage", StringComparer.OrdinalIgnoreCase))
{
    var medium = MediumInProcessConfig();
    BenchmarkRunner.Run<ProjectionBenchmarks>(medium);
    BenchmarkRunner.Run<MessageEvolutionBenchmarks>(medium);
    BenchmarkRunner.Run<SpecificationBenchmarks>(medium);
    BenchmarkRunner.Run<EndToEndChainBenchmarks>(medium);
    BenchmarkRunner.Run<HostingBenchmarks>(medium);
    return 0;
}

static async Task<int> RunRemoteDialectAsync(bool isPostgreSql)
{
    RemoteDialectBenchSession session;
    try
    {
        session = await RemoteDialectBenchSession.CreateAsync(isPostgreSql);
    }
#pragma warning disable CA1031 // 意图性宽捕获：容器不可达是环境态而非代码缺陷，明确报错退出
    catch (Exception ex)
#pragma warning restore CA1031
    {
        Console.Error.WriteLine(
            $"⛔ {(isPostgreSql ? "PostgreSQL" : "MySQL")} 容器不可达（需 Docker/WSL 环境，参考 CI dialect-probe）：{ex.Message}\n" +
            $"   替代路径：环境变量 {(isPostgreSql ? "PALDDD_BENCH_PG_CONN" : "PALDDD_BENCH_MYSQL_CONN")} 指向外部实例连接串" +
            $"（库名须 palddd_bench 前缀；wslc 容器环境用此路径，容器生命周期外部管理）");
        return 2;
    }

    await using (session)
    {
        RemoteDialectBenchState.Connection = session.Connection;
        RemoteDialectBenchState.ConnectionString = session.ConnectionString;
        RemoteDialectBenchState.DbType = session.DbType;
        var medium = MediumInProcessConfig();
        BenchmarkRunner.Run<DapperRemoteOutboxThroughputBenchmarks>(medium);
        BenchmarkRunner.Run<DapperRemoteOutboxConcurrencyBenchmarks>(medium);
        BenchmarkRunner.Run<PalOrmRemoteOutboxBenchmarks>(medium);
        // 方言专属车道：PG 跑方言特性（Pipeline/Router/LISTEN-NOTIFY）；MySQL 跑 EF 栈
        // 租约 SQL 形态（SKIP LOCKED 派生表互斥验证——EF 栈级车道待上游 Provider 出
        // EF 11 rc 配套版，2026-10-09 实证见类头注）
        if (isPostgreSql)
            BenchmarkRunner.Run<PgDialectFeaturesBenchmarks>(medium);
        else
            BenchmarkRunner.Run<MySqlSkipLockedShapeBenchmarks>(medium);
        RemoteDialectBenchState.Connection = null;
        RemoteDialectBenchState.ConnectionString = null;
    }
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

return 0;

internal static class SmokeBenchmarks
{
    private const int Iterations = 1_000_000;

    // 分配预算（B/op，2026-10-08 rc.1 校准；基线实测：Success 0.000088 / Failed 40.000 /
    // FromValue 0.00004 / RaiseEvent 128.001）。预算语义对齐 AllocationContractTests：
    // 上界含安全余量，只拦截大幅回归（误引入容器扩容/装箱量级），精确预算由正式基准承载。
    // 超限退出码 1——使 smoke 具备人工 TOOL 门禁资格（conventions §10.6 口径，未接 CI）。
    public static int Run()
    {
        Console.WriteLine($"PalDDD benchmark smoke run: {Iterations:N0} iterations");
        _ = SmokeStatus.Pending;
        var failures = new List<string>();

        Measure("PalValidationResult.Success", budgetBytesPerOp: 1.0,
            static () => PalValidationResult.Success(), failures);
        Measure("PalValidationResult.Failed", budgetBytesPerOp: 50.0,
            static () => PalValidationResult.Failed("Prop", "Error message"), failures);
        Measure("SmartEnum.FromValue", budgetBytesPerOp: 1.0,
            static () => SmokeStatus.FromValue("shipped"), failures);
        MeasureAction("Entity.RaiseEvent", budgetBytesPerOp: 140.0, static () =>
        {
            var order = new SmokeOrder(Guid.NewGuid(), "Test");
            order.Complete();
        }, failures);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"⛔ smoke 分配预算超标 {failures.Count} 项：{string.Join("；", failures)}");
            return 1;
        }

        Console.WriteLine("✅ smoke 分配预算 4/4 通过（预算语义：上界含余量，只拦截大幅回归）");
        return 0;
    }

    // 口径（ITM-171）：以下计时/分配均为 Iterations 次调用的总量；
    // 单次调用 = 总量 / Iterations（下方输出同时给出 ns/op 与 B/op）。
    // 2026-10-08：增预算参数——计时仍为趋势观察（单轮无统计效力），分配为判定项。
    private static void Measure<T>(string name, double budgetBytesPerOp, Func<T> action, List<string> failures)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++)
            _ = action();
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        var totalMs = stopwatch.Elapsed.TotalMilliseconds;
        var bytesPerOp = allocatedBytes / (double)Iterations;
        Console.WriteLine(
            $"{name}: {totalMs:N2} ms / {Iterations:N0} = {totalMs / Iterations * 1_000_000:N2} ns/op, " +
            $"{allocatedBytes:N0} B / {Iterations:N0} = {bytesPerOp:F3} B/op — " +
            $"{(bytesPerOp <= budgetBytesPerOp ? "✅ 预算内" : "⛔ 超预算")} {budgetBytesPerOp} B/op");
        if (bytesPerOp > budgetBytesPerOp)
            failures.Add($"{name}: {bytesPerOp:F3} B/op > {budgetBytesPerOp} B/op");
    }

    private static void MeasureAction(string name, double budgetBytesPerOp, Action action, List<string> failures)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++)
            action();
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        var totalMs = stopwatch.Elapsed.TotalMilliseconds;
        var bytesPerOp = allocatedBytes / (double)Iterations;
        Console.WriteLine(
            $"{name}: {totalMs:N2} ms / {Iterations:N0} = {totalMs / Iterations * 1_000_000:N2} ns/op, " +
            $"{allocatedBytes:N0} B / {Iterations:N0} = {bytesPerOp:F3} B/op — " +
            $"{(bytesPerOp <= budgetBytesPerOp ? "✅ 预算内" : "⛔ 超预算")} {budgetBytesPerOp} B/op");
        if (bytesPerOp > budgetBytesPerOp)
            failures.Add($"{name}: {bytesPerOp:F3} B/op > {budgetBytesPerOp} B/op");
    }

    private sealed class SmokeStatus : SmartEnum<SmokeStatus, string>
    {
        public static readonly SmokeStatus Pending = new("pending");
        public static readonly SmokeStatus Shipped = new("shipped");

        static SmokeStatus()
        {
            RegisterValues([Pending, Shipped]);
        }

        private SmokeStatus(string value) : base(value)
        {
        }
    }

    private sealed class SmokeOrder : AggregateRoot<Guid>
    {
        public SmokeOrder(Guid id, string name) : base(id) => CustomerName = name;

        public string CustomerName { get; }

        public void Complete() => RaiseEvent(new SmokeOrderCompleted(Id));
    }

    private sealed class SmokeOrderCompleted(Guid orderId) : DomainEvent
    {
        public Guid OrderId { get; } = orderId;
    }
}
