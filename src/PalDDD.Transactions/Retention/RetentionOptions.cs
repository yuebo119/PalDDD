// ─────────────────────────────────────────────────────────────
// 🧹 RetentionOptions — 历史表保留与清理配置
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// 历史表保留配置（2026-10-20 性能轮）。默认不注册、不运行；调用
/// <c>AddPalDapperRetention</c> 显式启用后由后台服务按轮询周期分批删除。
/// </summary>
/// <remarks>
/// 清理只作用于终态行：Outbox 默认只删 Processed（Dead 需人工处置，除非显式开启
/// <see cref="DeleteDeadOutboxMessages"/>）；Inbox 只删 Processed；幂等记录按
/// <c>expires_at</c> 删除（Retention 窗口即业务幂等窗口）。每轮最多
/// <see cref="MaxBatchesPerTick"/> 批，批间不共享事务，避免长事务与 WAL 膨胀。
/// </remarks>
public sealed class RetentionOptions
{
    /// <summary>轮询周期，默认 5 分钟。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>已处理 Outbox 行的保留时长，默认 7 天。</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>已处理 Inbox 行的保留时长，默认 30 天（覆盖下游重投递窗口）。</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>单条 DELETE 的行数上限，默认 1000。</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>每轮每表最多执行的批数，默认 10（单轮上限 = BatchSize × 本值）。</summary>
    public int MaxBatchesPerTick { get; set; } = 10;

    /// <summary>是否同时删除 Dead 状态的 Outbox 行。默认 false：死信保留给运维处置。</summary>
    public bool DeleteDeadOutboxMessages { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(OutboxRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InboxRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchesPerTick);
    }
}

/// <summary>一轮清理的删除计数。</summary>
public readonly record struct RetentionResult(int OutboxDeleted, int InboxDeleted, int IdempotencyDeleted)
{
    /// <summary>本轮删除总数。</summary>
    public int TotalDeleted => OutboxDeleted + InboxDeleted + IdempotencyDeleted;
}
