// ─────────────────────────────────────────────────────────────
// 🚫 NullPalLogger — 空操作日志器
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;

namespace PalDDD.Core.Logging;

/// <summary>无操作日志器 — 用于测试和不需要日志的场景。</summary>
public sealed class NullPalLogger<T> : IPalLogger<T>
{
    public static readonly NullPalLogger<T> Instance = new();

    private NullPalLogger() { }

    /// <summary>无操作（null object 模式）——调用即吞，零开销。</summary>
    public void Debug(string message) { }
    /// <summary>无操作（null object 模式）。</summary>
    public void Information(string message) { }
    /// <summary>无操作（null object 模式）。</summary>
    public void Warning(string message) { }
    /// <summary>无操作（null object 模式）——异常对象不外泄。</summary>
    public void Error(Exception ex, string message) { }
    /// <summary>恒 false——调用方据此短路消息格式化开销。</summary>
    public bool IsEnabled(LogLevel level) => false;
}
