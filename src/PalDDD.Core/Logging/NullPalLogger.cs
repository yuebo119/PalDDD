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

    // R13 F20：结构化重载必须显式覆写——接口默认实现会先把命名槽位转成 string.Format
    // 数字槽位再格式化：参数少于槽位时抛 FormatException（NullPalLogger 反而成为故障源），
    // 参数形状匹配时也白付正则替换与 string.Format 分配。空实现是 null object 的契约。
    // 每个结构化重载紧邻其简单重载（S4136），便于对照门面契约。
    /// <summary>无操作（null object 模式）——不格式化、不分配、不抛。</summary>
    public void Debug(EventId eventId, string message, params object?[] args) { }

    /// <summary>无操作（null object 模式）。</summary>
    public void Information(string message) { }

    /// <summary>无操作（null object 模式）——不格式化、不分配、不抛。</summary>
    public void Information(EventId eventId, string message, params object?[] args) { }

    /// <summary>无操作（null object 模式）。</summary>
    public void Warning(string message) { }

    /// <summary>无操作（null object 模式）——不格式化、不分配、不抛。</summary>
    public void Warning(EventId eventId, string message, params object?[] args) { }

    /// <summary>无操作（null object 模式）——异常对象不外泄。</summary>
    public void Error(Exception ex, string message) { }

    /// <summary>无操作（null object 模式）——不格式化、不分配、不抛。</summary>
    public void Error(Exception ex, EventId eventId, string message, params object?[] args) { }

    /// <summary>恒 false——调用方据此短路消息格式化开销。</summary>
    public bool IsEnabled(LogLevel level) => false;
}
