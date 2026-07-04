// ─────────────────────────────────────────────────────────────
// ⚙️ 事务选项 — Outbox/Inbox/Saga 的 Options 模式配置
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 事务配置选项
// ─────────────────────────────────────────────────────────────

/// <summary>发件箱发布器运行时选项。</summary>
public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 100;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public int MaxRetryCount { get; set; } = IPalOutboxStore.DefaultMaxRetryCount;

    /// <summary>
    /// 重试退避策略 — 计算失败后的下次重试延迟。<br/>
    /// 默认指数退避（2^n 秒，上限 64 秒，与原硬编码语义一致）。<br/>
    /// 生产环境建议设置 <c>RetryBackoffPolicy = new ExponentialBackoffPolicy(withJitter: true)</c>，
    /// 通过 ±20% 抖动避免多实例 thundering herd。
    /// </summary>
    public IRetryBackoffPolicy RetryBackoffPolicy { get; set; } = new ExponentialBackoffPolicy();

    /// <summary>
    /// 重试延迟上限 — 仅用于观测/健康检查展示。<br/>
    /// 实际上限由 <see cref="RetryBackoffPolicy"/> 内部控制。
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>租约持有者标识 — 用于区分多实例部署中的不同节点。默认为 {机器名}:{随机ID}。</summary>
    public string LeaseOwner { get; set; } = $"{Environment.MachineName}:{PalUlid.New()}";
}

/// <summary>收件箱幂等性运行时选项。</summary>
public sealed class InboxOptions
{
    public string DefaultConsumerName { get; set; } = "default";
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Saga 超时处理器运行时选项。</summary>
public sealed class SagaProcessorOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int TimeoutScanBatchSize { get; set; } = 256;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public string LeaseOwner { get; set; } = $"{Environment.MachineName}:{PalUlid.New()}";
}
