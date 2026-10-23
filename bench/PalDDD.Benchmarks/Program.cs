using PalDDD.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using PalDDD.Core;
using Perfolizer.Horology;
using System.Diagnostics;
using System.Text.RegularExpressions;

if (args is ["--smoke"])
{
    return SmokeBenchmarks.Run();
}

// 2026-10-09 新维度轮①：--qps——绝对 QPS 并行量具（非 BDN，1/4/8 线程 × dispatch/chain
// 两场景，每格 3s 实测聚合 ops/sec；口径趋势级，同日同机可比）。不依赖注册表，保持秒级可用。
if (args is ["--qps"])
{
    return QpsHarness.Run();
}

var registry = BenchRegistry.Current;

if (args.Contains("--list-lanes", StringComparer.OrdinalIgnoreCase))
{
    PrintRegistryLanes(registry);
    return 0;
}

var runners = new Dictionary<string, Action<IConfig>>(StringComparer.Ordinal)
{
    ["EntityDomainEventBenchmarks"] = c => BenchmarkRunner.Run<EntityDomainEventBenchmarks>(c),
    ["ValueObjectBenchmarks"] = c => BenchmarkRunner.Run<ValueObjectBenchmarks>(c),
    ["SmartEnumBenchmarks"] = c => BenchmarkRunner.Run<SmartEnumBenchmarks>(c),
    ["ValidationBenchmarks"] = c => BenchmarkRunner.Run<ValidationBenchmarks>(c),
    ["EntityCreationBenchmarks"] = c => BenchmarkRunner.Run<EntityCreationBenchmarks>(c),
    ["DispatcherBenchmarks"] = c => BenchmarkRunner.Run<DispatcherBenchmarks>(c),
    ["SerializationBenchmarks"] = c => BenchmarkRunner.Run<SerializationBenchmarks>(c),
    ["OutboxThroughputBenchmarks"] = c => BenchmarkRunner.Run<OutboxThroughputBenchmarks>(c),
    ["InboxThroughputBenchmarks"] = c => BenchmarkRunner.Run<InboxThroughputBenchmarks>(c),
    ["EventLogBenchmarks"] = c => BenchmarkRunner.Run<EventLogBenchmarks>(c),
    ["SagaStateBenchmarks"] = c => BenchmarkRunner.Run<SagaStateBenchmarks>(c),
    ["ConfigurationBenchmarks"] = c => BenchmarkRunner.Run<ConfigurationBenchmarks>(c),
    ["CompressionBenchmarks"] = c => BenchmarkRunner.Run<CompressionBenchmarks>(c),
    ["SqlGenBenchmarks"] = c => BenchmarkRunner.Run<SqlGenBenchmarks>(c),
    ["ProjectionBenchmarks"] = c => BenchmarkRunner.Run<ProjectionBenchmarks>(c),
    ["MessageEvolutionBenchmarks"] = c => BenchmarkRunner.Run<MessageEvolutionBenchmarks>(c),
    ["SpecificationBenchmarks"] = c => BenchmarkRunner.Run<SpecificationBenchmarks>(c),
    ["EndToEndChainBenchmarks"] = c => BenchmarkRunner.Run<EndToEndChainBenchmarks>(c),
    ["HostingBenchmarks"] = c => BenchmarkRunner.Run<HostingBenchmarks>(c),
    ["OutboxConcurrencyInMemoryBenchmarks"] = c => BenchmarkRunner.Run<OutboxConcurrencyInMemoryBenchmarks>(c),
    ["OutboxConcurrencyDapperSqliteBenchmarks"] = c => BenchmarkRunner.Run<OutboxConcurrencyDapperSqliteBenchmarks>(c),
    ["SagaLaneBenchmarks"] = c => BenchmarkRunner.Run<SagaLaneBenchmarks>(c),
    ["StreamReadBenchmarks"] = c => BenchmarkRunner.Run<StreamReadBenchmarks>(c),
    ["DapperPersistenceBenchmarks"] = c => BenchmarkRunner.Run<DapperPersistenceBenchmarks>(c),
    ["PalOrmPersistenceBenchmarks"] = c => BenchmarkRunner.Run<PalOrmPersistenceBenchmarks>(c),
    ["EfCorePersistenceBenchmarks"] = c => BenchmarkRunner.Run<EfCorePersistenceBenchmarks>(c),
    ["OutboxMarkPathBenchmarks"] = c => BenchmarkRunner.Run<OutboxMarkPathBenchmarks>(c),
    ["OutboxDeliveryDrainBenchmarks"] = c => BenchmarkRunner.Run<OutboxDeliveryDrainBenchmarks>(c),
    ["OutboxDeliveryCadenceBenchmarks"] = c => BenchmarkRunner.Run<OutboxDeliveryCadenceBenchmarks>(c),
    ["OutboxDeliveryRemoteDrainBenchmarks"] = c => BenchmarkRunner.Run<OutboxDeliveryRemoteDrainBenchmarks>(c),
    ["OutboxDeliveryRemoteAutoDrainBenchmarks"] = c => BenchmarkRunner.Run<OutboxDeliveryRemoteAutoDrainBenchmarks>(c),
    ["MediatRComparisonBenchmarks"] = c => BenchmarkRunner.Run<MediatRComparisonBenchmarks>(c),
    ["CapComparisonBenchmarks"] = c => BenchmarkRunner.Run<CapComparisonBenchmarks>(c),
    ["DapperRemoteOutboxThroughputBenchmarks"] = c => BenchmarkRunner.Run<DapperRemoteOutboxThroughputBenchmarks>(c),
    ["DapperRemoteOutboxConcurrencyBenchmarks"] = c => BenchmarkRunner.Run<DapperRemoteOutboxConcurrencyBenchmarks>(c),
    ["PalOrmRemoteOutboxBenchmarks"] = c => BenchmarkRunner.Run<PalOrmRemoteOutboxBenchmarks>(c),
    ["PgDialectFeaturesBenchmarks"] = c => BenchmarkRunner.Run<PgDialectFeaturesBenchmarks>(c),
    ["MySqlSkipLockedShapeBenchmarks"] = c => BenchmarkRunner.Run<MySqlSkipLockedShapeBenchmarks>(c),
};

if (TryValue(args, "--profile", out var profile))
    return await RunProfileAsync(profile, args, registry, runners);

if (TryValue(args, "--lane", out var laneName))
    return await RunLaneByNameAsync(laneName, args, registry, runners);

// 2026-10-09 新维度轮②：--compare——横向对比（MediatR 12.5.0 同进程同 harness；
// CAP 发布对比经 PALDDD_BENCH_CAP 环境变量 opt-in）。--lane compare 与旧旗标同路。
if (args.Contains("--compare", StringComparer.OrdinalIgnoreCase))
    return RunCompare(args);

if (args.Contains("--verify-persist", StringComparer.OrdinalIgnoreCase))
    return await PersistenceVerifyRunner.RunAsync();

// ─── 车道别名（保留旧旗标，内部统一走注册表采样档）───
if (args.Contains("--persist-medium", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("persist-medium", args, registry, runners);

if (args.Contains("--persist-mark", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("persist-mark", args, registry, runners);

if (args.Contains("--persist", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("persist", args, registry, runners);

if (args.Contains("--framework", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("framework", args, registry, runners);

if (args.Contains("--app", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("app", args, registry, runners);

if (args.Contains("--infra", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("infra", args, registry, runners);

if (args.Contains("--coverage", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("coverage", args, registry, runners);

if (args.Contains("--concurrency", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("concurrency", args, registry, runners);

if (args.Contains("--saga", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("saga", args, registry, runners);

if (args.Contains("--compression", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("compression", args, registry, runners);

if (args.Contains("--stream", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("stream", args, registry, runners);

if (args.Contains("--delivery", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("delivery", args, registry, runners);

if (args.Contains("--sqlgen", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("sqlgen", args, registry, runners);

// ─── 迁移/量具探针（保持原入口，不参与注册表采样）───
// 迁移演练探针（2026-10-19）：真库上执行 M1/M4 expand + 回填 + 读切换校验。
// M2/M3 量具（2026-10-19）：M2 内存 100k 回放检查点调用计数；M3 真 broker 订阅拓扑压测。
if (args.Contains("--m2-probe", StringComparer.OrdinalIgnoreCase))
    return await ProjectionProbe.RunM2Async();

// 出站唤醒信号量具（2026-10-22 性能轮）：写入到标记发布的延迟（InMemory 全链路，
// 5 分钟轮询使信号成为唯一唤醒源）+ 突发写入的 tick 合并校验。
if (args.Contains("--wakeup-probe", StringComparer.OrdinalIgnoreCase))
    return await OutboxWakeUpProbe.RunAsync();

if (args.Contains("--m3-probe", StringComparer.OrdinalIgnoreCase))
    return await RabbitChannelProbe.RunM3Async();

if (args.Contains("--m1-probe-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM1Async(postgreSql: true);

if (args.Contains("--m1-probe-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM1Async(postgreSql: false);

// 产品性能轮（2026-10-24）：#45 批量 NextCheckAt 真库语句数/EXPLAIN；#46 10k 活跃轮转覆盖。
if (args.Contains("--saga-advance-probe-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunSagaAdvanceProbeAsync(postgreSql: true);

if (args.Contains("--saga-advance-probe-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunSagaAdvanceProbeAsync(postgreSql: false);

if (args.Contains("--saga-rotate-probe-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunSagaRotateProbeAsync(postgreSql: true);

if (args.Contains("--saga-rotate-probe-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunSagaRotateProbeAsync(postgreSql: false);

// 生产窗口规模演练（2026-10-20）：M1 10k 活跃 Saga 尾部延迟；M4 10 万行回填与索引体积。
if (args.Contains("--m1-load-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM1LoadAsync(postgreSql: true);

if (args.Contains("--m1-load-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM1LoadAsync(postgreSql: false);

if (args.Contains("--m4-scale-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM4ScaleAsync(postgreSql: true);

if (args.Contains("--m4-scale-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM4ScaleAsync(postgreSql: false);

// PalORM PG 单事件追加快路径行为探针（2026-10-20 性能轮）。
if (args.Contains("--palorm-append-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunPalOrmAppendPgAsync();

// Outbox 批量写入探针（2026-10-21）：PG 强类型 COPY / MySQL IDataReader BulkCopy，
// 1000 行写入 + 抽样回读。
if (args.Contains("--outbox-bulk-probe-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunOutboxBulkProbeAsync(postgreSql: true);

if (args.Contains("--outbox-bulk-probe-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunOutboxBulkProbeAsync(postgreSql: false);

// Inbox 批量认领探针（2026-10-21）：100 条一次认领/完成/重复拒绝。
if (args.Contains("--inbox-batch-probe-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunInboxBatchProbeAsync(postgreSql: true);

if (args.Contains("--inbox-batch-probe-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunInboxBatchProbeAsync(postgreSql: false);

if (args.Contains("--m4-probe-pg", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM4Async(postgreSql: true);

if (args.Contains("--m4-probe-mysql", StringComparer.OrdinalIgnoreCase))
    return await MigrationProbes.RunM4Async(postgreSql: false);

// ADR-033 Auto 组合 A/B（2026-10-19）：只跑新车道，避开旧 BatchMark 矩阵的 5 分钟上限。
if (args.Contains("--delivery-auto-pg", StringComparer.OrdinalIgnoreCase))
    return await RunRemoteDeliveryAsync(isPostgreSql: true, autoOnly: true);

if (args.Contains("--delivery-auto-mysql", StringComparer.OrdinalIgnoreCase))
    return await RunRemoteDeliveryAsync(isPostgreSql: false, autoOnly: true);

// 远程真库车道别名（采样档由注册表给出；--strict 叠加时走严格档）。
if (args.Contains("--persist-pg", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("persist-pg", args, registry, runners);

if (args.Contains("--persist-mysql", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("persist-mysql", args, registry, runners);

if (args.Contains("--delivery-pg", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("delivery-pg", args, registry, runners);

if (args.Contains("--delivery-mysql", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("delivery-mysql", args, registry, runners);

if (args.Contains("--remote-smoke-pg", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("remote-smoke-pg", args, registry, runners);

if (args.Contains("--remote-smoke-mysql", StringComparer.OrdinalIgnoreCase))
    return await RunLaneByNameAsync("remote-smoke-mysql", args, registry, runners);

// 严格参考档（无其他模式参数时默认跑两类既有严格档）。
if (args.Contains("--strict", StringComparer.OrdinalIgnoreCase)
    && args.Count(a => a.StartsWith("--", StringComparison.Ordinal)) == 1)
    return await RunLaneByNameAsync("strict-core", args, registry, runners);

// 历史注记（2026-09-19 五十四轮）：net11-rc runtime moniker 不被 BDN 0.15.8 的
// DotNetSdkValidator 识别（GetRuntimeVersion throw NotRecognizedException；验证内嵌在
// CsProjCoreToolchain.Validate，且 Switcher 在 filter 前对全程序集 case 验证——任何
// [ShortRunJob] 类都会触发崩溃），故旧入口走显式 InProcess config 路径绕过。
// 2026-10-17 升位 BDN 0.16.0-preview.2 后默认 Switcher 入口已恢复（--list 实测 exit 0
// 全量列出，moniker 识别修复）；显式 InProcess 路径保留（注册表模式）。
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

return 0;

// ══════════════ 注册表驱动路由 ══════════════

static bool TryValue(string[] arguments, string name, out string value)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
        {
            value = arguments[i + 1];
            return true;
        }
    }
    value = "";
    return false;
}

static async Task<int> RunProfileAsync(
    string profile,
    string[] args,
    BenchRegistry registry,
    IReadOnlyDictionary<string, Action<IConfig>> runners)
{
    var lanes = registry.Profile(profile).ToList();
    if (lanes.Count == 0)
    {
        Console.Error.WriteLine($"ERROR: profile「{profile}」没有车道；可用：{string.Join(", ", registry.Lanes.SelectMany(l => l.Profiles).Distinct())}");
        return 2;
    }
    foreach (var lane in lanes)
    {
        var exitCode = await RunLaneAsync(lane, args, registry, runners).ConfigureAwait(false);
        if (exitCode != 0) return exitCode;
    }
    return 0;
}

static async Task<int> RunLaneByNameAsync(
    string name,
    string[] args,
    BenchRegistry registry,
    IReadOnlyDictionary<string, Action<IConfig>> runners)
{
    LaneDef lane;
    try
    {
        lane = registry.Lane(name);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}；--list-lanes 查看清单");
        return 2;
    }
    return await RunLaneAsync(lane, args, registry, runners).ConfigureAwait(false);
}

static async Task<int> RunLaneAsync(
    LaneDef lane,
    string[] args,
    BenchRegistry registry,
    IReadOnlyDictionary<string, Action<IConfig>> runners)
{
    var strict = args.Contains("--strict", StringComparer.OrdinalIgnoreCase);
    return lane.Name switch
    {
        "smoke" => SmokeBenchmarks.Run(),
        "verify-persist" => await PersistenceVerifyRunner.RunAsync().ConfigureAwait(false),
        "qps" => QpsHarness.Run(),
        "compare" => RunCompare(args),
        "persist-pg" => await RunRemoteDialectAsync(isPostgreSql: true, lane, strict).ConfigureAwait(false),
        "persist-mysql" => await RunRemoteDialectAsync(isPostgreSql: false, lane, strict).ConfigureAwait(false),
        "delivery-pg" => await RunRemoteDeliveryAsync(isPostgreSql: true).ConfigureAwait(false),
        "delivery-mysql" => await RunRemoteDeliveryAsync(isPostgreSql: false).ConfigureAwait(false),
        "remote-smoke-pg" => await RunRemoteSmokeAsync(isPostgreSql: true, lane, runners).ConfigureAwait(false),
        "remote-smoke-mysql" => await RunRemoteSmokeAsync(isPostgreSql: false, lane, runners).ConfigureAwait(false),
        _ => RunClasses(lane, strict, runners),
    };
}

static int RunClasses(
    LaneDef lane,
    bool strict,
    IReadOnlyDictionary<string, Action<IConfig>> runners)
{
    foreach (var className in lane.Classes)
    {
        if (!runners.TryGetValue(className, out var run))
        {
            Console.Error.WriteLine($"ERROR: 车道 {lane.Name} 引用了未登记 runner 的类 {className}");
            return 2;
        }
        var modeName = strict && lane.Name != "coverage"
            ? "strict"
            : lane.Overrides.TryGetValue(className, out var overrideMode) ? overrideMode : lane.Mode;
        var config = ModeConfig(modeName);
        if (lane.Filter.Count > 0)
        {
            var filter = new HashSet<string>(lane.Filter, StringComparer.Ordinal);
            config.AddFilter(new NameFilter(filter.Contains));
        }
        run(config);
    }
    return 0;
}

/// <summary>
/// 采样档到 BDN ManualConfig 的单点映射。alloc 的计时不作引用；auto/fixed/fixed-short/
/// medium-lite/tiny 是趋势档；medium 与 strict 是参考档（口径见 docs/performance.md）。
/// </summary>
static ManualConfig ModeConfig(string modeName)
{
    if (!BenchRegistry.Current.Modes.TryGetValue(modeName, out var mode))
        throw new InvalidOperationException($"未知采样档「{modeName}」");

    // 以 Job.MediumRun 预设为基座（与既有 _persist/--delivery 车道同形态）：Job.InProcess
    // 是 mutator，会与 DefaultConfig 的默认 job union 出双 job，实测把 saga 从 4 秒拉到 47 秒。
    // MediumRun 预设 + 覆盖计数保持单 job。
    var job = Job.MediumRun.WithToolchain(InProcessEmitToolchain.Default)
        .WithWarmupCount(mode.Warmup)
        .WithLaunchCount(mode.Launch)
        .WithIterationCount(mode.Iterations ?? 5);
    if (mode.Invocation is int invocation)
        job = job.WithInvocationCount(invocation).WithUnrollFactor(1);
    if (mode.IterationTimeMs is int iterationTimeMs)
    {
        job = job
            .WithIterationTime(TimeInterval.FromMilliseconds(iterationTimeMs))
            .WithMinIterationCount(mode.MinIterations ?? 3)
            .WithMaxIterationCount(mode.MaxIterations ?? 5);
    }

    var artifactsName = modeName is "medium" ? "artifacts-medium"
        : modeName is "strict" ? "artifacts-strict"
        : "artifacts-" + modeName;
    return ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(job)
        .AddDiagnoser(MemoryDiagnoser.Default)
        .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, artifactsName));
}

static int RunCompare(string[] args)
{
    var strict = args.Contains("--strict", StringComparer.OrdinalIgnoreCase);
    BenchmarkRunner.Run<MediatRComparisonBenchmarks>(ModeConfig(strict ? "strict" : "auto"));
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PALDDD_BENCH_CAP_MYSQL"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PALDDD_BENCH_CAP_RABBIT")))
    {
        // 2026-10-10 前置检查门配套：BDN BenchmarkRunner 不因基准失败（如 GlobalSetup 抛出）
        // 置非零进程退出码——曾使重试风暴垃圾数字以 exit 0 静默通过。此处消费 Summary
        // 判定失败并返回 1（Report.Success=false 覆盖 setup 异常/执行异常两形态）。
        var capSummary = BenchmarkRunner.Run<CapComparisonBenchmarks>(ModeConfig("medium"));
        if (capSummary.Reports.Any(r => !r.Success))
            return 1;
    }
    else
    {
        Console.WriteLine("⏭ CAP 对比跳过（需 PALDDD_BENCH_CAP_MYSQL + PALDDD_BENCH_CAP_RABBIT 环境变量，见 CapComparisonBenchmarks 头注）");
    }
    return 0;
}

static void PrintRegistryLanes(BenchRegistry registry)
{
    Console.WriteLine("EPSL 车道（bench/bench-registry.json）：");
    foreach (var lane in registry.Lanes)
    {
        var profiles = lane.Profiles.Count == 0 ? "tool" : string.Join("/", lane.Profiles);
        var remote = lane.Remote ? " [remote]" : "";
        Console.WriteLine($"  {lane.Name,-18} --{lane.Flag,-18} {profiles,-24} mode={lane.Mode,-12} {lane.TimeoutMinutes,2}m {lane.ExpectedSeconds,5}s{remote}");
    }
    Console.WriteLine();
    Console.WriteLine("--profile gate|standard|full|remote-smoke|remote-full|strict");
    Console.WriteLine("--lane <name> 单车道；旧旗标（--framework 等）保留为别名");
}

// ══════════════ 远程车道 ══════════════

static async Task<int> RunRemoteDialectAsync(bool isPostgreSql, LaneDef lane, bool strict)
{
    RemoteDialectBenchSession session;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        session = await RemoteDialectBenchSession.CreateAsync(isPostgreSql, cts.Token);
    }
#pragma warning disable CA1031 // 意图性宽捕获：容器不可达是环境态而非代码缺陷，明确报错退出
    catch (Exception ex)
#pragma warning restore CA1031
    {
        ReportRemoteUnreachable(isPostgreSql, ex);
        return 2;
    }

    IConfig For(string className) => ModeConfig(
        strict ? "strict" : lane.Overrides.GetValueOrDefault(className) ?? lane.Mode);

    await using (session)
    {
        RemoteDialectBenchState.Connection = session.Connection;
        RemoteDialectBenchState.ConnectionString = session.ConnectionString;
        RemoteDialectBenchState.DbType = session.DbType;
        BenchmarkRunner.Run<DapperRemoteOutboxThroughputBenchmarks>(For("DapperRemoteOutboxThroughputBenchmarks"));
        BenchmarkRunner.Run<DapperRemoteOutboxConcurrencyBenchmarks>(For("DapperRemoteOutboxConcurrencyBenchmarks"));
        BenchmarkRunner.Run<PalOrmRemoteOutboxBenchmarks>(For("PalOrmRemoteOutboxBenchmarks"));
        // 方言专属车道：PG 跑方言特性（Pipeline/Router/LISTEN-NOTIFY）；MySQL 跑 EF 栈
        // 租约 SQL 形态（SKIP LOCKED 派生表互斥验证——EF 栈级车道待上游 Provider 出
        // EF 11 rc 配套版，2026-10-09 实证见类头注）
        if (isPostgreSql)
            BenchmarkRunner.Run<PgDialectFeaturesBenchmarks>(For("PgDialectFeaturesBenchmarks"));
        else
            BenchmarkRunner.Run<MySqlSkipLockedShapeBenchmarks>(For("MySqlSkipLockedShapeBenchmarks"));
        RemoteDialectBenchState.Connection = null;
        RemoteDialectBenchState.ConnectionString = null;
    }
    return 0;
}

static async Task<int> RunRemoteDeliveryAsync(bool isPostgreSql, bool autoOnly = false)
{
    RemoteDialectBenchSession session;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        session = await RemoteDialectBenchSession.CreateAsync(isPostgreSql, cts.Token);
    }
#pragma warning disable CA1031 // 同 RunRemoteDialectAsync：容器不可达是环境态
    catch (Exception ex)
#pragma warning restore CA1031
    {
        ReportRemoteUnreachable(isPostgreSql, ex);
        return 2;
    }

    await using (session)
    {
        RemoteDialectBenchState.Connection = session.Connection;
        RemoteDialectBenchState.ConnectionString = session.ConnectionString;
        RemoteDialectBenchState.DbType = session.DbType;
        var deliveryRemote = ModeConfig("medium-lite");
        // ADR-033：Serial（窗口 1/逐条标记/对象发布）vs Auto（窗口 16/批量标记 16/raw）组合 A/B。
        // RttMs=20 的 Serial 基线约 33s/op；3 迭代 + 2 预热 = 5 op ≈ 165s，稳在 InProcess 5 分钟上限内。
        var deliveryAuto = ModeConfig("tiny");
        if (!autoOnly)
            BenchmarkRunner.Run<OutboxDeliveryRemoteDrainBenchmarks>(deliveryRemote);
        BenchmarkRunner.Run<OutboxDeliveryRemoteAutoDrainBenchmarks>(deliveryAuto);
        RemoteDialectBenchState.Connection = null;
        RemoteDialectBenchState.ConnectionString = null;
    }
    return 0;
}

static async Task<int> RunRemoteSmokeAsync(
    bool isPostgreSql,
    LaneDef lane,
    IReadOnlyDictionary<string, Action<IConfig>> runners)
{
    RemoteDialectBenchSession session;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        session = await RemoteDialectBenchSession.CreateAsync(isPostgreSql, cts.Token);
    }
#pragma warning disable CA1031 // 环境态：快速失败并给出可行动原因
    catch (Exception ex)
#pragma warning restore CA1031
    {
        ReportRemoteUnreachable(isPostgreSql, ex);
        return 2;
    }

    await using (session)
    {
        RemoteDialectBenchState.Connection = session.Connection;
        RemoteDialectBenchState.ConnectionString = session.ConnectionString;
        RemoteDialectBenchState.DbType = session.DbType;
        var config = ModeConfig(lane.Mode);
        if (lane.Filter.Count > 0)
        {
            var filter = new HashSet<string>(lane.Filter, StringComparer.Ordinal);
            config.AddFilter(new NameFilter(filter.Contains));
        }
        foreach (var className in lane.Classes)
        {
            if (!runners.TryGetValue(className, out var run))
            {
                Console.Error.WriteLine($"ERROR: 远程冒烟车道引用了未登记 runner 的类 {className}");
                return 2;
            }
            run(config);
        }
        RemoteDialectBenchState.Connection = null;
        RemoteDialectBenchState.ConnectionString = null;
    }
    return 0;
}

static void ReportRemoteUnreachable(bool isPostgreSql, Exception ex)
    => Console.Error.WriteLine(
        $"⛔ {(isPostgreSql ? "PostgreSQL" : "MySQL")} 远程预检失败（10 秒上限；需 Docker/WSL 环境或外部实例）：{SanitizeRemoteMessage(ex.Message)}\n" +
        $"   替代路径：环境变量 {(isPostgreSql ? "PALDDD_BENCH_PG_CONN" : "PALDDD_BENCH_MYSQL_CONN")} 指向外部实例连接串" +
        $"（库名须 palddd_bench 前缀；wslc 容器环境用此路径，容器生命周期外部管理）");

static string SanitizeRemoteMessage(string message)
    => Regex.Replace(message, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***", RegexOptions.None, TimeSpan.FromSeconds(1));

internal static class SmokeBenchmarks
{
    private const int Iterations = 1_000_000;

    // 分配预算（B/op，2026-10-08 rc.1 校准；基线实测：Success 0.000088 / Failed 40.000 /
    // FromValue 0.00004 / RaiseEvent 128.001）。预算语义对齐 AllocationContractTests：
    // 上界含安全余量，只拦截大幅回归（误引入容器扩容/装箱量级），精确预算由正式基准承载。
    // 超限退出码 1——使 smoke 具备门禁资格（conventions §10.6；2026-10-18 接入 CI perf-smoke）。
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

        // R1（P1-4，2026-10-14 性能实施轮）：遥测关闭（bench 进程无 ActivityListener）时
        // Start* 入口零分配——旧码 int tag 实参装箱 ~24B/op（v8 勘正），守卫前置后 0；
        // 预算 1（含噪声余量，对齐 Success/FromValue 口径）。锁定测试载体说明：测试宿主
        //（TUnit/MTP）订阅全局 ActivitySource 使 HasListeners 恒真，零分配断言只能在
        // 无宿主进程成立（EPSL「测试宿主 ActivityListener」坑），故落本进程而非 xUnit 侧。
        Measure("PalActivitySource.StartOutboxProcess(no listener)", budgetBytesPerOp: 1.0,
            static () => PalDDD.Core.Diagnostics.PalActivitySource.StartOutboxProcess(100), failures);
        Measure("PalActivitySource.StartEventLogAppend(no listener)", budgetBytesPerOp: 1.0,
            static () => PalDDD.Core.Diagnostics.PalActivitySource.StartEventLogAppend("stream-1", 3), failures);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"⛔ smoke 分配预算超标 {failures.Count} 项：{string.Join("；", failures)}");
            return 1;
        }

        Console.WriteLine("✅ smoke 分配预算 6/6 通过（预算语义：上界含余量，只拦截大幅回归）");
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
