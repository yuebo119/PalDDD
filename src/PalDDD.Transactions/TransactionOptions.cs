// ─────────────────────────────────────────────────────────────
// ⚙️ 事务选项 — Outbox/Inbox/Saga 的 Options 模式配置
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 事务配置选项
// ─────────────────────────────────────────────────────────────

/// <summary>租约持有者默认标识工厂（精炼提取 2026-08-26）——"机器名:ULID" 保证同节点
/// 多选项实例天然互异（多实例隔离语义见 OutboxOptions.LeaseOwner remarks）。原内联表达式
/// 在 OutboxOptions 与 SagaProcessorOptions 两处逐字重复（v8 勘正：InboxOptions 无 LeaseOwner）。</summary>
internal static class LeaseOwnerFactory
{
    internal static string Create() => $"{Environment.MachineName}:{PalUlid.New()}";
}

/// <summary>发件箱发布器运行时选项。</summary>
/// <remarks>
/// ITM-166 声明：<see cref="LeaseDuration"/> 与 <see cref="LeaseOwner"/> 的集中校验在
/// <c>ServiceCollectionExtensions.AddPalOutbox</c> 的 <c>AddOptions&lt;OutboxOptions&gt;().Validate(...)</c>
/// 启动期执行（<c>ValidateOnStart</c>）；Store 层因此不重复校验租约参数，直接信任 Options 已约束。
/// 直接 <c>new OutboxOptions()</c> 并绕过 DI 传给 Store 时不受该校验保护（默认值合法）。
/// </remarks>
public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 100;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 轮询间隔 — OutboxProcessor 的 PeriodicTimer 周期。
    /// <para>
    /// ⚠️ <b>冷快照声明（二十四轮）</b>：仅在 Processor 构造时读取一次（<c>PeriodicTimer</c>
    /// 间隔构造后固定），运行时经配置热更新本值<b>不生效</b>，需重启进程；同组的
    /// <see cref="BatchSize"/>/<see cref="LeaseDuration"/>/<see cref="LeaseOwner"/>/
    /// <see cref="MaxRetryCount"/> 经 <c>IOptionsMonitor.CurrentValue</c> 每 tick 热读取，
    /// 热更新即时生效（v35 P3 补记：MaxRetryCount 属热更新组，语义见其属性声明）。
    /// </para>
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 最大重试次数 — 失败消息重试超过本值后转 Dead（死信），不再拾取。
    /// <para>
    /// ⚠️ <b>热更新滞留声明（v35 P3·EA3）</b>：本值经 <c>IOptionsMonitor</c> 每 tick 热读取，
    /// 运行时调小即时生效——但 <c>RetryCount &gt;= 新值</c> 的 Pending 消息将被拾取查询
    /// 永久过滤（既不派发也不转 Dead：转 Dead 仅发生在发布失败路径），消息<b>滞留无出口</b>。
    /// 运维止血（调小止血后）需手工清理这些滞留消息（<c>RequeueDeadAsync</c> 不适用——
    /// 它只处理 Dead 状态），或将 MaxRetryCount 调回 ≥ 滞留消息的 RetryCount。
    /// </para>
    /// </summary>
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
    /// 实际封顶由 <see cref="RetryBackoffPolicy"/> 内部控制（默认 Exponential 封顶 64s——与本
    /// 展示默认值 60s 存在 4s 差异，v19 声明：仅影响观测读数不影响行为；如需一致请两处同步修改）。
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
    public string LeaseOwner { get; set; } = LeaseOwnerFactory.Create();
}

/// <summary>收件箱幂等性运行时选项。</summary>
public sealed class InboxOptions
{
    public string DefaultConsumerName { get; set; } = "default";
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Saga 超时处理器运行时选项。</summary>
/// <remarks>
/// ITM-166 声明：<see cref="LeaseDuration"/> 与 <see cref="LeaseOwner"/> 的集中校验在
/// <c>ServiceCollectionExtensions.AddPalSaga</c> 的 <c>AddOptions&lt;SagaProcessorOptions&gt;().Validate(...)</c>
/// 启动期执行；Store 层不重复校验租约参数。绕过 DI 直构时不受该校验保护（默认值合法）。
/// </remarks>
public sealed class SagaProcessorOptions
{
    /// <summary>
    /// 轮询间隔 — SagaProcessor 的 PeriodicTimer 周期。
    /// <para>
    /// ⚠️ <b>冷快照声明（二十四轮）</b>：仅在 Processor 构造时读取一次（<c>PeriodicTimer</c>
    /// 间隔构造后固定），运行时热更新不生效，需重启进程；同组的
    /// <see cref="TimeoutScanBatchSize"/>/<see cref="LeaseDuration"/>/<see cref="LeaseOwner"/>
    /// 经 <c>IOptionsMonitor.CurrentValue</c> 每次扫描热读取，热更新即时生效。
    /// </para>
    /// </summary>
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
    public string LeaseOwner { get; set; } = LeaseOwnerFactory.Create();
}
