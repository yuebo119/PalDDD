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

        Console.WriteLine($"{name}: {stopwatch.Elapsed.TotalMilliseconds:N2} ms, {allocatedBytes:N0} B allocated");
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

        Console.WriteLine($"{name}: {stopwatch.Elapsed.TotalMilliseconds:N2} ms, {allocatedBytes:N0} B allocated");
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
