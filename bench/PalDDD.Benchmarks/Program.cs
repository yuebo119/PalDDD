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
    SmokeBenchmarks.Run();
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

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

return 0;

internal static class SmokeBenchmarks
{
    private const int Iterations = 1_000_000;

    public static void Run()
    {
        Console.WriteLine($"PalDDD benchmark smoke run: {Iterations:N0} iterations");
        _ = SmokeStatus.Pending;
        Measure("PalValidationResult.Success", static () => PalValidationResult.Success());
        Measure("PalValidationResult.Failed", static () => PalValidationResult.Failed("Prop", "Error message"));
        Measure("SmartEnum.FromValue", static () => SmokeStatus.FromValue("shipped"));
        MeasureAction("Entity.RaiseEvent", static () =>
        {
            var order = new SmokeOrder(Guid.NewGuid(), "Test");
            order.Complete();
        });
    }

    // 口径（ITM-171）：以下计时/分配均为 Iterations 次调用的总量；
    // 单次调用 = 总量 / Iterations（下方输出同时给出 ns/op 与 B/op）。
    private static void Measure<T>(string name, Func<T> action)
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
        Console.WriteLine(
            $"{name}: {totalMs:N2} ms / {Iterations:N0} = {totalMs / Iterations * 1_000_000:N2} ns/op, " +
            $"{allocatedBytes:N0} B / {Iterations:N0} = {allocatedBytes / (double)Iterations:F3} B/op");
    }

    private static void MeasureAction(string name, Action action)
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
        Console.WriteLine(
            $"{name}: {totalMs:N2} ms / {Iterations:N0} = {totalMs / Iterations * 1_000_000:N2} ns/op, " +
            $"{allocatedBytes:N0} B / {Iterations:N0} = {allocatedBytes / (double)Iterations:F3} B/op");
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
