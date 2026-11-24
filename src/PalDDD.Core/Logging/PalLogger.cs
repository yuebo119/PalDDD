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
    /// <remarks>
    /// 2026-11-23：补 IsEnabled 门控。MEL 的 <c>LogDebug(EventId, string, params object?[])</c>
    /// 重载<b>不做</b>级别检查（直接构造 FormattedLogValues 再交给 provider 过滤），关闭态下
    /// params 数组、装箱与 LogValues 全部照常分配并进入 provider。门控后关闭态实现零分配。
    /// </remarks>
    public void Debug(EventId eventId, string message, params object?[] args)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;
        _logger.LogDebug(eventId, message, args);
    }

    /// <summary>零分配重载：handler 在构造期已按级别短路，返回 null 表示未启用。</summary>
    public void Debug(ref PalDebugLogHandler handler)
    {
        if (handler.ToStringAndClear() is { } message)
            _logger.LogDebug(message);
    }

    public void Information(string message) => _logger.LogInformation(message);

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    /// <remarks>2026-11-23 IsEnabled 门控，理由同 <see cref="Debug(EventId, string, object?[])"/>。</remarks>
    public void Information(EventId eventId, string message, params object?[] args)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;
        _logger.LogInformation(eventId, message, args);
    }

    /// <summary>零分配重载：handler 在构造期已按级别短路，返回 null 表示未启用。</summary>
    public void Information(ref PalInformationLogHandler handler)
    {
        if (handler.ToStringAndClear() is { } message)
            _logger.LogInformation(message);
    }

    public void Warning(string message) => _logger.LogWarning(message);

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    /// <remarks>2026-11-23 IsEnabled 门控，理由同 <see cref="Debug(EventId, string, object?[])"/>。</remarks>
    public void Warning(EventId eventId, string message, params object?[] args)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
            return;
        _logger.LogWarning(eventId, message, args);
    }

    /// <summary>零分配重载：handler 在构造期已按级别短路，返回 null 表示未启用。</summary>
    public void Warning(ref PalWarningLogHandler handler)
    {
        if (handler.ToStringAndClear() is { } message)
            _logger.LogWarning(message);
    }

    public void Error(Exception ex, string message) => _logger.LogError(ex, message);

    /// <summary>结构化重载：直接把模板与参数交给 MEL，保留字段而非拼成字符串。</summary>
    /// <remarks>2026-11-23 IsEnabled 门控，理由同 <see cref="Debug(EventId, string, object?[])"/>。</remarks>
    public void Error(Exception ex, EventId eventId, string message, params object?[] args)
    {
        if (!_logger.IsEnabled(LogLevel.Error))
            return;
        _logger.LogError(eventId, ex, message, args);
    }

    /// <summary>零分配重载：handler 在构造期已按级别短路，返回 null 表示未启用。</summary>
    public void Error(Exception ex, ref PalErrorLogHandler handler)
    {
        if (handler.ToStringAndClear() is { } message)
            _logger.LogError(ex, message);
    }

    public bool IsEnabled(LogLevel level) => _logger.IsEnabled(level);
}
