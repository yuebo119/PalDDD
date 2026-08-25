namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 收件箱存储抽象 — AOT 安全，零反射
// （InboxStatus/InboxMessage 已拆分至同目录独立文件，对齐 OutboxStore 单类型组织，
//  精炼重组 2026-08-26）
// ─────────────────────────────────────────────────────────────

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
