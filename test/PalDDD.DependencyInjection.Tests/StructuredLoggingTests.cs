using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.DependencyInjection.Logging;

namespace PalDDD.DependencyInjection.Tests;

/// <summary>
/// T5（基础库优化清单）：结构化日志门面。适配器把模板与参数原样交给 MEL，
/// 字段可按 MessageId/ConsumerName 检索；旧实现者走默认实现，行为不变。
/// </summary>
public sealed class StructuredLoggingTests
{
    [Test]
    public async Task Information_EventIdAndTemplate_ForwardsStructuredFields()
    {
        var capture = new CapturingLogger<StructuredLoggingTests>();
        IPalLogger<StructuredLoggingTests> logger = new PalLogger<StructuredLoggingTests>(capture);

        logger.Information(
            PalLogEventIds.InboxMessageProcessed,
            "Inbox: message {MessageId} processed for {ConsumerName}",
            "m-1",
            "orders");

        await Assert.That(capture.EventId.Id).IsEqualTo(PalLogEventIds.InboxMessageProcessed);
        var fields = capture.Fields ?? throw new InvalidOperationException("结构化日志未携带字段集合。");
        await Assert.That(fields["MessageId"]).IsEqualTo("m-1");
        await Assert.That(fields["ConsumerName"]).IsEqualTo("orders");
    }

    [Test]
    public async Task Error_EventIdAndTemplate_ForwardsExceptionAndFields()
    {
        var capture = new CapturingLogger<StructuredLoggingTests>();
        IPalLogger<StructuredLoggingTests> logger = new PalLogger<StructuredLoggingTests>(capture);
        var failure = new InvalidOperationException("boom");

        logger.Error(
            failure,
            PalLogEventIds.OutboxMessageFailed,
            "Outbox: message {MessageId} failed at retry {RetryCount}",
            "m-2",
            3);

        await Assert.That(capture.EventId.Id).IsEqualTo(PalLogEventIds.OutboxMessageFailed);
        await Assert.That(capture.Exception).IsSameReferenceAs(failure);
        await Assert.That(capture.Fields!["MessageId"]).IsEqualTo("m-2");
        await Assert.That(capture.Fields["RetryCount"]).IsEqualTo(3);
    }

    [Test]
    public async Task LegacyImplementer_DefaultImplementation_FormatsTemplate()
    {
        IPalLogger<LegacyLogger> legacy = new LegacyLogger();

        legacy.Warning(new EventId(1), "Saga {SagaId} timed out at {State}", "s-1", "Payment");

        await Assert.That(((LegacyLogger)legacy).LastMessage).IsEqualTo("Saga s-1 timed out at Payment");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public EventId EventId { get; private set; }

        public Dictionary<string, object?>? Fields { get; private set; }

        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            EventId = eventId;
            Exception = exception;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                Fields = [];
                foreach (var pair in values)
                    Fields[pair.Key] = pair.Value;
            }
        }
    }

    private sealed class LegacyLogger : IPalLogger<LegacyLogger>
    {
        public string? LastMessage { get; private set; }

        public void Debug(string message) => LastMessage = message;

        public void Information(string message) => LastMessage = message;

        public void Warning(string message) => LastMessage = message;

        public void Error(Exception ex, string message) => LastMessage = message;

        public bool IsEnabled(LogLevel level) => true;
    }
}
