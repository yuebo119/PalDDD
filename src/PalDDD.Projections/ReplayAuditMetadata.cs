// 🕵️ ReplayAuditMetadata — 回放审计元数据（与 EventAuditMetadata 类型对齐）
// ─────────────────────────────────────────────────────────────
// 设计决策：CorrelationId/CausationId 用 PalUlid?（ByteAether.Ulid.Ulid?），
// 与 PalDDD.EventLog.EventAuditMetadata 保持一致，保留 Ulid 的全序性与类型安全。
// 历史问题（P3-003）：曾用 Guid?，靠 Ulid→Guid 隐式转换导致全序性丢失。

using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Projections;

/// <summary>事件回放时携带的不可变审计元数据。</summary>
/// <remarks>
/// 字段类型与 EventLog 包的 EventAuditMetadata 一致（均为 PalUlid?），
/// 避免 Ulid→Guid 隐式转换导致的全序性丢失（P3-003）。
/// </remarks>
public readonly record struct ReplayAuditMetadata(
    string? ActorId,
    string? Reason,
    PalUlid? CorrelationId,
    PalUlid? CausationId,
    string? TraceParent,
    string? TraceState)
{
    public static ReplayAuditMetadata Empty { get; } = new(null, null, null, null, null, null);
}
