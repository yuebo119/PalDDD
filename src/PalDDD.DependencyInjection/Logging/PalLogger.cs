// ─────────────────────────────────────────────────────────────
// 📝 PalLogger — IPalLogger 门面的 Microsoft.Extensions.Logging 适配
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.DependencyInjection.Logging;

[SuppressMessage("Design", "CA1848", Justification = "IPalLogger 门面不适用 LoggerMessage 源生成，消息由调用方传入。")]
[SuppressMessage("Usage", "CA2254", Justification = "IPalLogger 门面接收纯字符串消息，模板由调用方构造。")]

internal sealed class PalLogger<T> : IPalLogger<T>
{
    private readonly ILogger<T> _logger;

#pragma warning disable S6672 // 泛型包装器刻意使用消费者类型 T 的日志类别，而非包装器自身类型
    public PalLogger(ILogger<T> logger)
#pragma warning restore S6672
    {
        // ITM-284（R45）：对齐全仓构造守卫惯例
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Debug(string message) => _logger.LogDebug(message);
    public void Information(string message) => _logger.LogInformation(message);
    public void Warning(string message) => _logger.LogWarning(message);
    public void Error(Exception ex, string message) => _logger.LogError(ex, message);
    public bool IsEnabled(LogLevel level) => _logger.IsEnabled(level);
}
