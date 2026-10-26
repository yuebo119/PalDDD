// ─────────────────────────────────────────────────────────────
// 📝 PalLogger — IPalLogger 门面的 Microsoft.Extensions.Logging 适配
// ─────────────────────────────────────────────────────────────
// 2026-10-21 契约盘点 R7：由 PalDDD.DependencyInjection 迁入 Core，使 Transactions 的
// 组件级注册（AddPalOutbox/AddPalInbox/AddPalSaga）也能登记门面——该门面原先只在
// AddPalCoreStack/AddPalPipelineBehaviors 中注册，单独使用组件注册时宿主启动即抛
// "Unable to resolve service for type IPalLogger<T>"。Core 已依赖 Logging.Abstractions
// （.NET 11 共享框架内置），故迁移不新增包依赖。

using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Core.Logging;

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

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    public void Debug(EventId eventId, string message, params object?[] args)
        => _logger.LogDebug(eventId, message, args);

    public void Information(string message) => _logger.LogInformation(message);

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    public void Information(EventId eventId, string message, params object?[] args)
        => _logger.LogInformation(eventId, message, args);

    public void Warning(string message) => _logger.LogWarning(message);

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    public void Warning(EventId eventId, string message, params object?[] args)
        => _logger.LogWarning(eventId, message, args);

    public void Error(Exception ex, string message) => _logger.LogError(ex, message);

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    public void Error(Exception ex, EventId eventId, string message, params object?[] args)
        => _logger.LogError(eventId, ex, message, args);

    public bool IsEnabled(LogLevel level) => _logger.IsEnabled(level);
}
