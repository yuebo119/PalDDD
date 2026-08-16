using BenchmarkDotNet.Running;
using PalDDD.Core;
using System.Diagnostics;

if (args is ["--smoke"])
{
    SmokeBenchmarks.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

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

    [AggregateName("SmokeOrder")]
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
