// ─────────────────────────────────────────────────────────────
// ⚡ EventStreamConcurrencyException — 乐观并发冲突异常
// ─────────────────────────────────────────────────────────────
namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 事件流版本冲突异常
// ─────────────────────────────────────────────────────────────

/// <summary>当事件流期望版本检查失败时抛出。</summary>
public sealed class EventStreamConcurrencyException : InvalidOperationException
{
    /// <summary>创建并发异常。</summary>
    public EventStreamConcurrencyException()
        : this("Event stream expected version check failed.")
    {
    }

    /// <summary>创建并发异常。</summary>
    public EventStreamConcurrencyException(string message)
        : base(message)
    {
        StreamName = string.Empty;
        ExpectedVersion = ExpectedStreamVersion.Any;
        ActualVersion = -1;
    }

    /// <summary>创建并发异常。</summary>
    public EventStreamConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
        StreamName = string.Empty;
        ExpectedVersion = ExpectedStreamVersion.Any;
        ActualVersion = -1;
    }

    /// <summary>创建并发异常。</summary>
    public EventStreamConcurrencyException(string streamName, ExpectedStreamVersion expectedVersion, long actualVersion)
        : base($"Event stream '{streamName}' expected version check failed. Actual version is {actualVersion}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        StreamName = streamName;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>未通过并发检查的流名称。</summary>
    public string StreamName { get; }

    /// <summary>调用方提供的期望版本。</summary>
    public ExpectedStreamVersion ExpectedVersion { get; }

    /// <summary>追加时的当前实际流版本。</summary>
    public long ActualVersion { get; }
}
