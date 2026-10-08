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
