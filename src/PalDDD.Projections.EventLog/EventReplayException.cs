namespace PalDDD.Projections.EventLog;

/// <summary>
/// Thrown when an event log replay operation fails.<br/>
/// 消费者不需要区分是"契约不匹配"还是"反序列化失败"——两种错误都意味着<br/>
/// 重放过不去，都需要人工介入修复。详细的错误原因在 <see cref="Exception.Message"/> 中提供。
/// </summary>
public sealed class EventReplayException : InvalidOperationException
{
    /// <summary>创建空的事件回放异常。</summary>
    public EventReplayException()
        : base("Event replay operation failed.") { }

    /// <summary>创建带消息的事件回放异常。</summary>
    public EventReplayException(string message)
        : base(message) { }

    /// <summary>创建带消息和内部异常的事件回放异常。</summary>
    public EventReplayException(string message, Exception? innerException)
        : base(message, innerException) { }
}
