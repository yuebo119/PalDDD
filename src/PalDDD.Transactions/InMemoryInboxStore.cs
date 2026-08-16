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
        // ITM-166 修复：补取消前置检查与超时非负守卫——ct 未检查时已取消请求仍进入
        // 内存表写入（异步签名形同虚设）；负 processingTimeout 使 Processing 租约
        // 即刻过期（now - started < negative 恒 false），与数据库三实现语义分叉。
        // 允许 TimeSpan.Zero：租约即刻过期是"超时接管可重入"的合法测试语义。
        ct.ThrowIfCancellationRequested();
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

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
                // ITM-105 修复：对齐 InMemoryIdempotencyStore/InMemoryProjectionCheckpointStore
                // 的 successor 隔离模式——原实现直接复用字典内 existing 实例并返回，被抢占的
                // 旧持有者仍持同一引用，其 MarkProcessedAsync/MarkFailedAsync 会改到字典当前
                // 条目（抢占失效，旧持有者能覆盖新持有者的状态）。改为：构造后继实例（拷贝
                // Id/ConsumerName/MessageId/ReceivedAt，重置状态与错误、递增尝试次数）替换
                // 字典条目——旧引用自此不是字典持有者，其标记被 IsCurrentLeaseHolder 守卫忽略。
                var successor = new InboxMessage
                {
                    Id = existing.Id,
                    ConsumerName = existing.ConsumerName,
                    MessageId = existing.MessageId,
                    Status = InboxStatus.Processing,
                    ReceivedAt = existing.ReceivedAt,
                    ProcessingStartedAt = now,
                    Attempts = existing.Attempts + 1,
                    LastError = null,
                    ProcessedAt = null
                };
                _records[key] = successor;
                return ValueTask.FromResult<InboxMessage?>(successor);
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
            // ITM-105 修复：所有权守卫——仅字典当前持有者可标记（对齐
            // InMemoryIdempotencyStore.IsCurrentLeaseHolder）：被超时/失败接管替换后的
            // 旧引用标记静默忽略，不覆盖新持有者状态
            if (!IsCurrentLeaseHolder(message))
                return ValueTask.CompletedTask;

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
            // ITM-105 修复：所有权守卫——同 MarkProcessedAsync
            if (!IsCurrentLeaseHolder(message))
                return ValueTask.CompletedTask;

            message.Status = InboxStatus.Failed;
            message.LastError = failureReason;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 判定传入 message 是否仍为字典当前持有的活跃处理实例
    /// （引用一致 + Processing 状态；须在 <see cref="_lock"/> 内调用）。
    /// </summary>
    private bool IsCurrentLeaseHolder(InboxMessage message)
    {
        var key = (message.ConsumerName, message.MessageId);
        return _records.TryGetValue(key, out var current)
            && ReferenceEquals(current, message)
            && current.Status == InboxStatus.Processing;
    }
}
