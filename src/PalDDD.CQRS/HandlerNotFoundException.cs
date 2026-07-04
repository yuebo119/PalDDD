// ─────────────────────────────────────────────────────────────
// ❓ HandlerNotFoundException — 未注册 Handler 时抛出（DTO 400）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 处理器未找到异常
// ─────────────────────────────────────────────────────────────

/// <summary>请求类型未找到已注册处理器时抛出</summary>
/// <remarks>
/// 被 <see cref="Dispatcher.ExecutePipelineAsync"/> 抛出，由异常处理中间件映射为 HTTP 404。<br/>
/// 替代 <see cref="InvalidOperationException"/> + 字符串匹配，提供精确的类型信息。
/// </remarks>
public sealed class HandlerNotFoundException : InvalidOperationException
{
    /// <summary>未找到处理器的请求类型</summary>
    public Type? RequestType { get; }

    public HandlerNotFoundException()
    { }

    public HandlerNotFoundException(string message) : base(message)
    {
    }

    public HandlerNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }

    public HandlerNotFoundException(Type requestType)
        : base(CreateMessage(requestType))
    {
        RequestType = requestType;
    }

    private static string CreateMessage(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        return $"未找到请求类型 '{requestType.Name}' 的处理器。请使用 AddPalCommandHandler / AddPalQueryHandler 显式注册处理器。";
    }
}
