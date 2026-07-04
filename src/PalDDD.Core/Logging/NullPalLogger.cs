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

    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) { }
    public void Error(Exception ex, string message) { }
    public bool IsEnabled(LogLevel level) => false;
}
