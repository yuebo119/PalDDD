namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 收件箱存储抽象 + 消息实体 — AOT 安全，零反射
// ─────────────────────────────────────────────────────────────

/// <summary>收件箱消息状态</summary>
public enum InboxStatus
{
    Pending,
    Processing,
    Processed,
    Failed
}

/// <summary>收件箱存储抽象 — 解耦 InboxProcessor 与具体数据库实现</summary>
/// <remarks>
/// 生产实现必须提供 (ConsumerName, MessageId) 唯一约束，保证每个消费者的消息只处理一次。<br/>
/// EF Core 实现由 PalDDD.Transactions.EFCore 适配包提供。
/// </remarks>
public interface IInboxStore
{
    /// <summary>尝试获取消息处理权。返回 null 表示消息已处理或仍在其他消费者处理中。</summary>
    ValueTask<InboxMessage?> TryStartProcessingAsync(
        string consumerName,
        string messageId,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct);

    /// <summary>标记消息处理成功。</summary>
    ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct);

    /// <summary>标记消息处理失败。</summary>
    ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct);
}

/// <summary>收件箱消息实体</summary>
public sealed class InboxMessage
{
    /// <summary>自增主键</summary>
    public long Id { get; init; }

    /// <summary>消息唯一标识（带唯一约束）</summary>
    public string MessageId { get; init; } = "";

    /// <summary>消费者名称，同一消息可被不同消费者各自处理一次。</summary>
    public string ConsumerName { get; init; } = "default";

    /// <summary>当前状态</summary>
    public InboxStatus Status { get; set; } = InboxStatus.Pending;

    /// <summary>接收时间 — 由 store 在插入时显式赋值（InboxDbContext.TryStartProcessingAsync）</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>处理时间</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>处理开始时间 — 用于检测僵尸 Processing</summary>
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>处理尝试次数</summary>
    public int Attempts { get; set; }

    /// <summary>最近错误</summary>
    public string? LastError { get; set; }
}
