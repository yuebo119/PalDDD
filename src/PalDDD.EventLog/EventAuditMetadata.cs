// 🕵️ EventAuditMetadata — OTel TraceContext + Actor 追踪
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.EventLog;

/// <summary>事件记录时捕获的不可变审计元数据。</summary>
public sealed record EventAuditMetadata
{
    /// <summary>无 actor 上下文的调用方使用的空审计元数据。</summary>
    public static EventAuditMetadata Empty { get; } = new(null, null, null, null, null, null);

    /// <summary>创建审计元数据。</summary>
    public EventAuditMetadata(
        string? actorId,
        string? reason,
        PalUlid? correlationId,
        PalUlid? causationId,
        string? traceParent,
        string? traceState)
    {
        ActorId = actorId;
        Reason = reason;
        CorrelationId = correlationId;
        CausationId = causationId;
        TraceParent = traceParent;
        TraceState = traceState;
    }

    /// <summary>导致事件发生的用户、服务或进程标识。</summary>
    public string? ActorId { get; }

    /// <summary>状态转换的人类可读原因。</summary>
    public string? Reason { get; }

    /// <summary>跨消息关联标识符。</summary>
    public PalUlid? CorrelationId { get; }

    /// <summary>导致本事件的命令或事件标识符。</summary>
    public PalUlid? CausationId { get; }

    /// <summary>
    /// 从 <see cref="Activity.Current"/> 捕获的 W3C traceparent。
    /// </summary>
    /// <remarks>
    /// ⚠️ v35 P3（EA4）格式前提声明：本值取自 <see cref="Activity.Current"/>.<see cref="Activity.Id"/>，
    /// 其格式跟随 <see cref="Activity.IdFormat"/>——<b>假定 W3C</b>（.NET 5+ 默认
    /// <see cref="Activity.DefaultIdFormat"/> 为 W3C）。宿主若全局改用
    /// <see cref="ActivityIdFormat.Hierarchical"/>（<c>Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical</c>），
    /// 写入值为 hierarchical 格式（<c>|...</c> 形态），非 W3C traceparent（<c>00-...</c> 四段）；
    /// 回放/查询侧按 W3C 解析将失真。跨格式环境需在宿主统一 IdFormat 或在消费侧按前缀判别。
    /// </remarks>
    public string? TraceParent { get; }

    /// <summary>从 <see cref="Activity.Current"/> 捕获的 W3C tracestate。</summary>
    public string? TraceState { get; }

    /// <summary>从当前执行上下文捕获审计元数据。</summary>
    public static EventAuditMetadata Capture(
        string? actorId,
        string? reason,
        PalUlid? correlationId = null,
        PalUlid? causationId = null)
        => new(
            actorId,
            reason,
            correlationId,
            causationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString);
}
