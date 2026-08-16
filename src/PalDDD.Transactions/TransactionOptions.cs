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

    /// <summary>
    /// 租约持有者标识 — 用于区分多实例部署中的不同节点。默认为 {机器名}:{随机ID}。
    /// <para>
    /// ITM-108 声明：默认值在属性初始化时求值（每次 new 一个新随机后缀）——本选项必须经
    /// <c>IOptions&lt;T&gt;</c>/单例配置绑定（Options 模式启动期绑定一次），直接多次
    /// <c>new OutboxOptions()</c> 会得到不同 LeaseOwner，同一节点自认为多实例（租约互抢/
    /// 自锁漂移）。改动默认值为 static 会破坏多实例隔离语义，故仅作声明。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 租约持有者标识 — 用于区分多实例部署中的不同节点。默认为 {机器名}:{随机ID}。
    /// <para>
    /// ITM-108 声明：同 <see cref="OutboxOptions.LeaseOwner"/>——默认值每次 new 求值，
    /// 必须经 <c>IOptions&lt;T&gt;</c>/单例配置绑定使用；多次直构会得到不同 owner，
    /// 同一节点自认为多实例（租约互抢/自锁漂移）。
    /// </para>
    /// </summary>
    public string LeaseOwner { get; set; } = $"{Environment.MachineName}:{PalUlid.New()}";
}
