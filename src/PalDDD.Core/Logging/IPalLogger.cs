// ═══════════════════════════════════════════════════════════════
// 📝 IPalLogger<T> — 框架统一日志门面
// ═══════════════════════════════════════════════════════════════
// 💡 设计原则：
//   ｜ 框架代码只依赖 IPalLogger<T>，不直接依赖 ILogger<T> 或 ZLogger
//   ｜ 当前默认实现为 ZLogger（零分配 UTF8 结构化日志）
//   ｜ 未来替换只需修改 AddPalLogging() 中的注册，消费方零改动
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PalDDD.Core.Logging;

/// <summary>框架统一日志门面——当前实现为 ZLogger，未来可替换。</summary>
/// <remarks>
/// <b>message 模板语义</b>：message 按 .NET 日志模板解析，花括号 <c>{...}</c> 被下游
///（MEL/ZLogger）视为结构化槽位而非字面量，输出字面花括号需转义（<c>{{</c> / <c>}}</c>）。
/// 结构化参数使用带 <see cref="EventId"/> 的重载；旧实现者不实现这些重载时会走默认实现，
/// 把模板格式化成纯文本后转调原有字符串方法（二进制兼容）。
/// </remarks>
public interface IPalLogger<T>
{
    /// <summary>调试日志</summary>
    void Debug(string message);

    /// <summary>信息日志</summary>
    void Information(string message);

    /// <summary>警告日志</summary>
    void Warning(string message);

    /// <summary>错误日志 · 必须携带异常</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Error 是日志领域的标准命名，与 ILogger 的 LogError 扩展方法一致。")]
    void Error(Exception ex, string message);

    /// <summary>是否启用指定级别——用于避免昂贵的参数计算</summary>
    bool IsEnabled(LogLevel level);

    /// <summary>结构化调试日志（默认实现格式化后转调 <see cref="Debug(string)"/>）。</summary>
    void Debug(EventId eventId, string message, params object?[] args)
        => Debug(PalLogMessage.Format(message, args));

    /// <summary>结构化信息日志（默认实现格式化后转调 <see cref="Information(string)"/>）。</summary>
    void Information(EventId eventId, string message, params object?[] args)
        => Information(PalLogMessage.Format(message, args));

    /// <summary>结构化警告日志（默认实现格式化后转调 <see cref="Warning(string)"/>）。</summary>
    void Warning(EventId eventId, string message, params object?[] args)
        => Warning(PalLogMessage.Format(message, args));

    /// <summary>结构化错误日志（默认实现格式化后转调 <see cref="Error(Exception, string)"/>）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Error 是日志领域的标准命名，与 ILogger 的 LogError 扩展方法一致。")]
    void Error(Exception ex, EventId eventId, string message, params object?[] args)
        => Error(ex, PalLogMessage.Format(message, args));
}

/// <summary>日志模板格式化（门面默认实现用；适配器覆写后直接把模板与参数交给 MEL）。</summary>
internal static partial class PalLogMessage
{
    // MEL 使用命名槽位（{MessageId}），string.Format 只认数字槽位（{0}）。
    // 未实现结构化重载的旧日志器走本方法：先把命名槽位按出现顺序改成数字槽位，
    // 保留 :format 段，再交给 string.Format。{{ 转义不匹配。
    // 源生成正则：Core 声明 IsAotCompatible=true，RegexOptions.Compiled 会触发动态代码。
    [GeneratedRegex(@"\{(?!\{)(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<format>:[^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex NamedPlaceholder();

    internal static string Format(string message, object?[] args)
    {
        if (args.Length == 0)
            return message;

        var index = 0;
        var indexed = NamedPlaceholder().Replace(message, match =>
        {
            if (index >= args.Length)
                return match.Value;
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : string.Empty;
            return "{" + (index++).ToString(CultureInfo.InvariantCulture) + format + "}";
        });
        return string.Format(CultureInfo.InvariantCulture, indexed, args);
    }
}

/// <summary>框架内常用日志事件的稳定 ID（便于按 EventId 过滤，不随消息文案漂移）。</summary>
public static class PalLogEventIds
{
    public const int InboxMessageSkipped = 1001;
    public const int InboxMessageProcessed = 1002;
    public const int InboxMessageFailed = 1003;
    public const int InboxMessageProcessedMarkFailed = 1004;
    public const int InboxBatchProcessed = 1005;

    public const int OutboxRetryBackoffFailed = 1101;
    public const int OutboxMarkDeadFailed = 1102;
    public const int OutboxReleaseForRetryFailed = 1103;
    public const int OutboxMessageFailed = 1104;
    public const int OutboxBatchMarkFailed = 1105;
    public const int OutboxStatePersistFailed = 1106;

    public const int SagaTimedOut = 1201;
    public const int SagaCompensationFailed = 1202;
    public const int SagaLeaseBatchPartial = 1203;
    public const int SagaLeaseReleaseFailed = 1204;
    public const int SagaTimeoutCheckFailed = 1205;
    public const int SagaLeaseBatchReleaseFailed = 1206;
}
