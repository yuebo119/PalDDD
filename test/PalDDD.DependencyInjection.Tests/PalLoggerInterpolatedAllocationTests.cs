using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;

namespace PalDDD.DependencyInjection.Tests;

/// <summary>
/// 插值日志重载的分配与行为断言（2026-11-23 零分配兑现）。
/// <para>
/// 依据（探针实测 N=2000，ZLogger InMemory provider）：MEL 参数化重载
/// <c>LogDebug(template, args)</c> 在级别关闭时仍分配 96.0 B/op（params 数组 + LogValues），
/// 插值 handler 形态为 0.0 B/op（构造期 <c>out bool enabled</c> 短路，Append 全部跳过）。
/// 本测试锁定该短路：关闭态零分配且不产生任何日志调用；启用态消息文本与异常原样透传。
/// </para>
/// <para>类级 NotInParallel 隔离测量窗口（对齐 <c>AnalyzerAllocationTests</c> 的实证教训：
/// 并行执行时同线程其他测试的分配计入 delta，会造成偶发误红）。</para>
/// </summary>
[NotInParallel("pal-logger-alloc")]
public sealed class PalLoggerInterpolatedAllocationTests
{
    private const int Iterations = 2_000;

    [Test]
    public async Task DisabledLevel_InterpolatedMessage_AllocatesNothing()
    {
        var capture = new CountingLogger<PalLoggerInterpolatedAllocationTests>(enabled: false);
        IPalLogger<PalLoggerInterpolatedAllocationTests> logger = new PalLogger<PalLoggerInterpolatedAllocationTests>(capture);
        var id = 42;
        var name = "order-7788";
        var amount = 19.99;

        for (var i = 0; i < 64; i++)
            logger.Debug($"id={id} name={name} amount={amount}");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            logger.Debug($"id={id} name={name} amount={amount}");
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;

        await Assert.That(perOp).IsLessThan(1.0);
        await Assert.That(capture.LogCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisabledLevel_ParameterizedOverload_StillAllocates_BaselineForContrast()
    {
        // 对照组：同一关闭级别下 MEL 参数化重载仍有分配（证明上面的零分配不是测量空转）。
        // 关键测量细节：参数必须在循环体内传入，由编译器每次创建 params 数组 + 装箱；
        // 若复用循环外预建的数组，关闭态下 MEL 的 IsEnabled 前置检查会让它也是 0 分配，
        // 对照就失去意义（本测试首版即因此误红）。
        var capture = new CountingLogger<PalLoggerInterpolatedAllocationTests>(enabled: false);
        IPalLogger<PalLoggerInterpolatedAllocationTests> logger = new PalLogger<PalLoggerInterpolatedAllocationTests>(capture);
        var id = 42;
        var name = "order-7788";
        var amount = 19.99;

        for (var i = 0; i < 64; i++)
            logger.Debug(PalLogEventIds.InboxMessageProcessed, "id={Id} name={Name} amount={Amount}", id, name, amount);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            logger.Debug(PalLogEventIds.InboxMessageProcessed, "id={Id} name={Name} amount={Amount}", id, name, amount);
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;

        await Assert.That(perOp).IsGreaterThan(1.0);
        await Assert.That(capture.LogCount).IsEqualTo(0);
    }

    [Test]
    public async Task EnabledLevel_InterpolatedMessage_ForwardsFormattedText()
    {
        var capture = new CountingLogger<PalLoggerInterpolatedAllocationTests>(enabled: true);
        IPalLogger<PalLoggerInterpolatedAllocationTests> logger = new PalLogger<PalLoggerInterpolatedAllocationTests>(capture);

        logger.Information($"id={42} name={"order-7788"}");

        await Assert.That(capture.LastMessage).IsEqualTo("id=42 name=order-7788");
        await Assert.That(capture.LogCount).IsEqualTo(1);
    }

    [Test]
    public async Task Error_InterpolatedMessage_ForwardsExceptionAndText()
    {
        var capture = new CountingLogger<PalLoggerInterpolatedAllocationTests>(enabled: true);
        IPalLogger<PalLoggerInterpolatedAllocationTests> logger = new PalLogger<PalLoggerInterpolatedAllocationTests>(capture);
        var failure = new InvalidOperationException("boom");

        logger.Error(failure, $"outbox {7} failed");

        await Assert.That(capture.LastMessage).IsEqualTo("outbox 7 failed");
        await Assert.That(capture.Exception).IsSameReferenceAs(failure);
        await Assert.That(capture.LogCount).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyImplementer_InterpolatedOverload_UsesDefaultImplementation()
    {
        // DIM 默认实现路径：只实现字符串方法的旧实现者拿到格式化后的文本（二进制兼容）。
        IPalLogger<InterpolationOnlyLogger> legacy = new InterpolationOnlyLogger();

        legacy.Warning($"saga {"s-1"} timed out");

        await Assert.That(((InterpolationOnlyLogger)legacy).LastMessage).IsEqualTo("saga s-1 timed out");
    }

    private sealed class CountingLogger<T>(bool enabled) : ILogger<T>
    {
        public int LogCount { get; private set; }

        public string? LastMessage { get; private set; }

        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => enabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LogCount++;
            LastMessage = formatter(state, exception);
            Exception = exception;
        }
    }

    private sealed class InterpolationOnlyLogger : IPalLogger<InterpolationOnlyLogger>
    {
        public string? LastMessage { get; private set; }

        public void Debug(string message) => LastMessage = message;

        public void Information(string message) => LastMessage = message;

        public void Warning(string message) => LastMessage = message;

        public void Error(Exception ex, string message) => LastMessage = message;

        public bool IsEnabled(LogLevel level) => true;
    }
}
