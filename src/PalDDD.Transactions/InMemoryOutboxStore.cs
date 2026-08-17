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
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var pending = QueryPending(batchSize, maxRetryCount);

            var now = _timeProvider.GetUtcNow();
            var leased = new List<OutboxMessage>(pending.Count);
            foreach (var msg in pending)
            {
                // ITM-174 修复（二十九轮）：successor 替换——对齐 InMemoryInboxStore/
                // InMemoryIdempotencyStore 模式（ITM-105）。原实现原地改写并返回同一实例：
                // worker A 租约到期后 worker B 重租同一实例，A 的 MarkProcessed/MarkDead
                // 无任何校验即可覆盖 B 的活跃租约（僵尸标记）。替换后旧引用不再是列表
                // 持有者，其 Mark 被 IsCurrentLeaseHolder 守卫忽略。
                var successor = new OutboxMessage
                {
                    Id = msg.Id,
                    Type = msg.Type,
                    Payload = msg.Payload,
                    ContentType = msg.ContentType,
                    SchemaVersion = msg.SchemaVersion,
                    CorrelationId = msg.CorrelationId,
                    CausationId = msg.CausationId,
                    TraceParent = msg.TraceParent,
                    TraceState = msg.TraceState,
                    CreatedAt = msg.CreatedAt,
                    RetryCount = msg.RetryCount,
                    Status = OutboxStatus.Pending,
                    LockedBy = owner,
                    LockedUntil = now.Add(leaseDuration),
                    NextAttemptAt = null,
                    Error = null,
                    ProcessedAt = null
                };
                _messages[_messages.IndexOf(msg)] = successor;
                leased.Add(successor);
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(leased);
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
        // P2 修复：三个状态变更方法与同文件其余 7 个方法对齐持锁，
        // 不再依赖字段书写顺序这一隐式契约保证可见性
        lock (_lock)
        {
            // ITM-174 修复（二十九轮）：所有权守卫——仅列表当前持有者可标记
            // （对齐 InMemoryInboxStore.IsCurrentLeaseHolder）。被 successor 替换后的
            // 旧引用（租约被其他 worker 重租）标记静默忽略，不覆盖新持有者状态。
            if (!IsCurrentLeaseHolder(message))
                return;

            message.ProcessedAt = processedAt;
            message.Status = OutboxStatus.Processed;
            message.Error = null;
            message.NextAttemptAt = null;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc/>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        lock (_lock)
        {
            if (!IsCurrentLeaseHolder(message))
                return;

            message.ProcessedAt = deadAt;
            message.Status = OutboxStatus.Dead;
            message.Error = failureReason;
            message.NextAttemptAt = null;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc/>
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        lock (_lock)
        {
            if (!IsCurrentLeaseHolder(message))
                return;

            message.RetryCount++;
            message.Status = OutboxStatus.Pending;
            message.ProcessedAt = null;
            message.Error = failureReason;
            message.NextAttemptAt = nextAttemptAt;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
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
            // 📐 P2 定案（三轮评审裁决）：retry_count 保留失败历史不重置（既有测试固化 +
            // PalORM 版 PD14 对齐）。语义：RequeueDeadAsync 是运维干预 API——重排后
            // GetPendingMessagesAsync 的 RetryCount < maxRetryCount 过滤仍生效，调用方
            // （运维工具）需确保 maxRetryCount > 消息当前 RetryCount 才能被拾取。
            msg.Status = OutboxStatus.Pending;
            msg.ProcessedAt = null;
            msg.Error = $"requeued by {retriedBy} at {now:O}";
            msg.NextAttemptAt = nextAttemptAt;
            msg.LockedBy = null;
            msg.LockedUntil = null;
            return ValueTask.FromResult(1);
        }
    }

    /// <summary>
    /// 判定传入 message 是否仍为列表当前持有的活跃租约实例。
    /// 对齐 InMemoryInboxStore.IsCurrentLeaseHolder 守卫强度：引用一致（未被 successor
    /// 替换）+ 仍处租约中（LockedBy 非空）——不校验 LockedUntil 是否过期：真库语义
    /// （DapperOutboxStore.MarkProcessed 仅 WHERE id AND locked_by）允许处理时长超过
    /// 租约期限时仍标记（须在 <see cref="_lock"/> 内调用）。
    /// </summary>
    private bool IsCurrentLeaseHolder(OutboxMessage message)
        => _messages.Contains(message)
            && message.Status == OutboxStatus.Pending
            && message.LockedBy is not null;

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
