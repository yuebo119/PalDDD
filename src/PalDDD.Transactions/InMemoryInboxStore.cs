namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 内存收件箱存储 — 测试和开发用
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么不直接用数据库？
//   ｜ 单元测试和原型开发时需要无依赖的快速存储。
//   ｜ 这个 InMemory 实现和 EF Core 适配器实现了同一个 IInboxStore 接口，
//   ｜ 测试中用它替代真实数据库，确保幂等逻辑的行为一致。
//   ｜
// 💡 幂等原理：
//   ｜ 基于 (ConsumerName, MessageId) 唯一约束。同一个消费者+消息只要处理过一次，
//   ｜ 后续尝试返回 null（跳过）。如果之前的处理处于 Processing 状态且超时，
//   ｜ 允许重入（重新获取处理权）。
// ─────────────────────────────────────────────────────────────

/// <summary>内存收件箱存储 — 用于测试和单进程原型。</summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string ConsumerName, string MessageId), InboxMessage> _records = [];

    /// <inheritdoc/>
    public ValueTask<InboxMessage?> TryStartProcessingAsync(
        string consumerName,
        string messageId,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_lock)
        {
            var key = (consumerName, messageId);
            if (_records.TryGetValue(key, out var existing))
            {
                if (existing.Status == InboxStatus.Processed)
                    return ValueTask.FromResult<InboxMessage?>(null);

                if (existing.Status == InboxStatus.Processing
                    && existing.ProcessingStartedAt.HasValue
                    && (now - existing.ProcessingStartedAt.Value) < processingTimeout)
                    return ValueTask.FromResult<InboxMessage?>(null);

                // 失败或超时 — 重新进入 Processing
                existing.Status = InboxStatus.Processing;
                existing.Attempts++;
                existing.LastError = null;
                existing.ProcessingStartedAt = now;
                return ValueTask.FromResult<InboxMessage?>(existing);
            }

            var record = new InboxMessage
            {
                ConsumerName = consumerName,
                MessageId = messageId,
                Status = InboxStatus.Processing,
                ReceivedAt = now,
                ProcessingStartedAt = now,
                Attempts = 1
            };
            _records[key] = record;
            return ValueTask.FromResult<InboxMessage?>(record);
        }
    }

    /// <inheritdoc/>
    public ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_lock)
        {
            message.Status = InboxStatus.Processed;
            message.ProcessedAt = processedAt;
            message.LastError = null;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        lock (_lock)
        {
            message.Status = InboxStatus.Failed;
            message.LastError = failureReason;
        }
        return ValueTask.CompletedTask;
    }
}
