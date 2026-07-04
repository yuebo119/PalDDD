using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 内存发件箱存储 — 测试和开发用
// ─────────────────────────────────────────────────────────────
//
// 💡 租约模式：
//   ｜ LeasePendingMessagesAsync 原子地获取消息处理权并设置 LockedBy + LockedUntil。
//   ｜ 其他实例在租约未过期前无法获取相同消息——实现多实例去重。
//   ｜
// 💡 RetryCount 递增：
//   ｜ ReleaseForRetry 内递增计数——确保与状态变更在同一逻辑操作中原子化。
//   ｜ 调用方（OutboxBatchProcessor）无需单独维护计数。
// ─────────────────────────────────────────────────────────────

/// <summary>内存发件箱存储 — 用于测试和单进程原型。</summary>
/// <remarks>
/// 💡 <b>时间抽象</b>：构造时可选注入 <see cref="TimeProvider"/>（默认 <see cref="TimeProvider.System"/>），
/// 测试中可传入 <c>FakeTimeProvider</c> 实现确定性租约过期/重试时序，
/// 与 <c>OutboxBatchProcessor</c>、<c>SagaTimeoutProcessor</c>、<c>OutboxDbContext.GetUtcNow()</c> 的时间抽象设计对齐。
/// </remarks>
public sealed class InMemoryOutboxStore : IPalOutboxStore
{
    private readonly Lock _lock = new();
    private readonly List<OutboxMessage> _messages = [];
    private readonly TimeProvider _timeProvider;

    /// <summary>创建内存发件箱存储。</summary>
    /// <param name="timeProvider">时间提供者（默认 <see cref="TimeProvider.System"/>），测试中可注入 <c>FakeTimeProvider</c></param>
    public InMemoryOutboxStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        lock (_lock)
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(
                QueryPending(batchSize, maxRetryCount));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        lock (_lock)
        {
            var pending = QueryPending(batchSize, maxRetryCount);

            var now = _timeProvider.GetUtcNow();
            foreach (var msg in pending)
            {
                msg.LockedBy = owner;
                msg.LockedUntil = now.Add(leaseDuration);
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(pending);
        }
    }

    /// <inheritdoc/>
    public void AddMessage(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock) { _messages.Add(message); }
    }

    /// <inheritdoc/>
    public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_lock)
        {
            foreach (var msg in messages)
                _messages.Add(msg);
        }
        return ValueTask.FromResult(messages.Count);
    }

    /// <inheritdoc/>
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.ProcessedAt = processedAt;
        message.Status = OutboxStatus.Processed;
        message.Error = null;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        message.ProcessedAt = deadAt;
        message.Status = OutboxStatus.Dead;
        message.Error = failureReason;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        message.RetryCount++;
        message.Status = OutboxStatus.Pending;
        message.ProcessedAt = null;
        message.Error = failureReason;
        message.NextAttemptAt = nextAttemptAt;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == messageId && m.Status == OutboxStatus.Dead);
            if (msg is null) return ValueTask.FromResult(0);
            // retry_count 保留失败历史，不重置
            msg.Status = OutboxStatus.Pending;
            msg.ProcessedAt = null;
            msg.Error = $"requeued by {retriedBy} at {now:O}";
            msg.NextAttemptAt = nextAttemptAt;
            msg.LockedBy = null;
            msg.LockedUntil = null;
            return ValueTask.FromResult(1);
        }
    }

    private List<OutboxMessage> QueryPending(int batchSize, int maxRetryCount)
    {
        var now = _timeProvider.GetUtcNow();
        return _messages
            .Where(m => m.Status == OutboxStatus.Pending
                && m.RetryCount < maxRetryCount
                && (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                && (m.LockedUntil == null || m.LockedUntil <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToList();
    }

    /// <inheritdoc/>
    public ValueTask<int> SaveChangesAsync(CancellationToken ct)
        => ValueTask.FromResult(0);
}
