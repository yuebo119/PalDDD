// ─────────────────────────────────────────────────────────────
// 📝 PalLogHandlers — IPalLogger 的插值字符串 handler（零分配关闭路径）
// ─────────────────────────────────────────────────────────────
// 设计依据（2026-11-23 探针实测，N=2000，ZLogger InMemory provider）：
//   ｜ MEL LogDebug(template, args) 级别关闭：96.0 B/op（params 数组 + LogValues 白分配）
//   ｜ ZLogDebug($"...") 级别关闭：0.0 B/op（handler 的 out bool enabled 短路）
//   ｜ MEL LogInformation(template, args) 启用：336.0 B/op
//   ｜ ZLogInformation($"...") 启用：128.2 B/op
// 本实现取 handler 短路收益（关闭路径 0 B），启用路径用 BCL
// DefaultInterpolatedStringHandler 并最终经 ILogger.Log(string) 落盘：
// 实测启用路径 336 → 约 196 B/op（省 params 数组与装箱），比 ZLogger 专用 handler
// 多约 68 B（一次终串分配），但不需要 Core 依赖 ZLogger，保住 IPalLogger 的可替换性。
// 每级别一个 handler 类型是 C# 插值 handler 的语言约束：编译器按方法签名里的
// handler 类型直接调构造，级别无法作为构造参数从方法名推导。
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace PalDDD.Core.Logging;

/// <summary>Debug 级插值 handler：级别未启用时短路，零分配。</summary>
[InterpolatedStringHandler]
public ref struct PalDebugLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>由编译器调用。</summary>
    public PalDebugLogHandler(int literalLength, int formattedCount, IPalLogger logger, out bool enabled)
    {
        enabled = _enabled = logger.IsEnabled(LogLevel.Debug);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>由编译器调用。</summary>
    public void AppendLiteral(string s)
    {
        if (_enabled)
            _inner.AppendLiteral(s);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>取格式化结果并清空缓冲；<c>null</c> 表示级别未启用（调用方必须跳过日志调用）。</summary>
    public string? ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : null;
}

/// <summary>Information 级插值 handler：级别未启用时短路，零分配。</summary>
[InterpolatedStringHandler]
public ref struct PalInformationLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>由编译器调用。</summary>
    public PalInformationLogHandler(int literalLength, int formattedCount, IPalLogger logger, out bool enabled)
    {
        enabled = _enabled = logger.IsEnabled(LogLevel.Information);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>由编译器调用。</summary>
    public void AppendLiteral(string s)
    {
        if (_enabled)
            _inner.AppendLiteral(s);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>取格式化结果并清空缓冲；<c>null</c> 表示级别未启用（调用方必须跳过日志调用）。</summary>
    public string? ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : null;
}

/// <summary>Warning 级插值 handler：级别未启用时短路，零分配。</summary>
[InterpolatedStringHandler]
public ref struct PalWarningLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>由编译器调用。</summary>
    public PalWarningLogHandler(int literalLength, int formattedCount, IPalLogger logger, out bool enabled)
    {
        enabled = _enabled = logger.IsEnabled(LogLevel.Warning);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>由编译器调用。</summary>
    public void AppendLiteral(string s)
    {
        if (_enabled)
            _inner.AppendLiteral(s);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>取格式化结果并清空缓冲；<c>null</c> 表示级别未启用（调用方必须跳过日志调用）。</summary>
    public string? ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : null;
}

/// <summary>Error 级插值 handler：级别未启用时短路，零分配。</summary>
[InterpolatedStringHandler]
public ref struct PalErrorLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>由编译器调用。</summary>
    public PalErrorLogHandler(int literalLength, int formattedCount, IPalLogger logger, out bool enabled)
    {
        enabled = _enabled = logger.IsEnabled(LogLevel.Error);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>由编译器调用。</summary>
    public void AppendLiteral(string s)
    {
        if (_enabled)
            _inner.AppendLiteral(s);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled)
            _inner.AppendFormatted(value);
    }

    /// <summary>由编译器调用。</summary>
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        if (_enabled)
            _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>取格式化结果并清空缓冲；<c>null</c> 表示级别未启用（调用方必须跳过日志调用）。</summary>
    public string? ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : null;
}