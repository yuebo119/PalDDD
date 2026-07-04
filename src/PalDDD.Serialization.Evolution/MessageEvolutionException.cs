namespace PalDDD.Serialization.Evolution;

/// <summary>
/// Thrown when a message evolution pipeline execution encounters an error.<br/>
/// 消费者不需要区分"步骤缺失"还是"步骤越界"——两种都意味着演化流水线无法继续，<br/>
/// 都需要人工检查演化路径定义。详细的错误原因在 <see cref="Exception.Message"/> 中提供。
/// </summary>
public sealed class MessageEvolutionException : InvalidOperationException
{
    /// <summary>创建空的消息演化异常。</summary>
    public MessageEvolutionException()
        : base("Message evolution pipeline execution failed.") { }

    /// <summary>创建带消息的消息演化异常。</summary>
    public MessageEvolutionException(string message)
        : base(message) { }

    /// <summary>创建带消息和内部异常的消息演化异常。</summary>
    public MessageEvolutionException(string message, Exception? innerException)
        : base(message, innerException) { }
}
