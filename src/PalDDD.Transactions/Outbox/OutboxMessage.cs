using System.Diagnostics.CodeAnalysis;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 发件箱消息实体
// ─────────────────────────────────────────────────────────────

/// <summary>发件箱消息状态</summary>
public enum OutboxStatus
{
    Pending,
    Processed,
    Dead
}

/// <summary>发件箱消息实体</summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// 框架级时间提供者 —— 与 <c>DomainEvent.TimeProvider</c> 同模式（P2 定案：时间控制双轨统一）。
    /// <para>AsyncLocal 隔离：并行测试互不干扰；测试通过 InternalsVisibleTo 注入 FakeTimeProvider
    /// 实现 CreatedAt 的确定性时间；默认 System 真实 UTC。</para>
    /// </summary>
    internal static TimeProvider TimeProvider
    {
        get => s_timeProvider.Value ?? System.TimeProvider.System;
        set => s_timeProvider.Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static readonly AsyncLocal<TimeProvider?> s_timeProvider = new();

    public PalUlid Id { get; init; } = PalUlid.New();
    public string Type { get; init; } = "";

    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Outbox payload is an EF Core binary column and immutable message storage boundary.")]
    public byte[] Payload { get; init; } = [];

    public string ContentType { get; init; } = Serialization.ContentTypes.Json;
    public int SchemaVersion { get; init; } = 1;
    public PalUlid? CorrelationId { get; init; }
    public PalUlid? CausationId { get; init; }
    public string? TraceParent { get; init; }
    public string? TraceState { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.GetUtcNow();
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
}
