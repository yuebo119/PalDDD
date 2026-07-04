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

    public PalLogger(ILogger<T> logger) => _logger = logger;

    public void Debug(string msg) => _logger.LogDebug(msg);
    public void Information(string msg) => _logger.LogInformation(msg);
    public void Warning(string msg) => _logger.LogWarning(msg);
    public void Error(Exception ex, string msg) => _logger.LogError(ex, msg);
    public bool IsEnabled(LogLevel level) => _logger.IsEnabled(level);
}
