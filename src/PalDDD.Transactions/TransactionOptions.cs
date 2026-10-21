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

/// <summary>Outbox 投递模式（2026-10-19 性能实施轮）。</summary>
public enum OutboxDeliveryMode
{
    /// <summary>逐条确认 + 逐条终态标记（旧行为，兼容回退）。</summary>
    Serial,

    /// <summary>按 broker/store 能力自动选择窗口与批量标记；能力不足时回落 Serial。</summary>
    Auto
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
    /// 投递模式（2026-10-19 性能实施轮）。<see cref="OutboxDeliveryMode.Auto"/> 在 broker
    /// 声明支持并发发布且 store 实现 <see cref="IBatchMarkOutboxStore"/> 时，自动启用
    /// 至少 16 路发布窗口，并把标记阈值提升到与 BatchSize 对齐（上限 400 行），
    /// 用 <see cref="BatchMarkFlushInterval"/> 设时间上界；
    /// <see cref="OutboxDeliveryMode.Serial"/> 保持旧行为（逐条确认/逐条标记）作为兼容回退。
    /// </summary>
    public OutboxDeliveryMode DeliveryMode { get; set; } = OutboxDeliveryMode.Auto;

    /// <summary>
    /// 终态标记时间上界（<see cref="OutboxDeliveryMode.Auto"/> 生效）。标记在“满
    /// <see cref="BatchMarkThreshold"/> 条”或“距上次冲刷满本间隔”时发生，重复窗口由本值
    /// 加上在途发布时长界定，而不是由整批时长界定。必须小于 <see cref="LeaseDuration"/>
    /// 的三分之一（启动期校验）。默认 1 秒。
    /// </summary>
    public TimeSpan BatchMarkFlushInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// 轮询间隔 — OutboxProcessor 的轮询周期。
    /// <para>
    /// ✅ <b>热读声明（T1.2，2026-10-11 优化实施轮）</b>：经 DI 注册（<c>AddPalOutbox</c>）时，
    /// 本值<b>每轮热读取</b>（<c>IOptionsMonitor.CurrentValue</c>），运行时配置热更新即时生效
    /// ——原「构造期一次定格的冷快照（热更新不生效需重启）」声明废止。直构并传显式
    /// <c>pollInterval</c> 覆盖值的形态恒用覆盖值。同组的 <c>BatchSize</c>/<c>LeaseDuration</c>/
    /// <c>LeaseOwner</c>/<c>MaxRetryCount</c> 一直为每 tick 热读取。
    /// </para>
    /// <para>
    /// ⚠️ <b>自适应排空（同轮）</b>：上一轮租到满批时下一轮<b>不等本间隔</b>、仅让步 1ms 即续批
    ///（积压排空吞吐从 batchSize/(tick+interval) 提升到 batchSize/tick）；未满批才按本间隔轮询。
    /// 本值因此是「空/浅队列的检测节奏」而非「最大吞吐节奏」——调大它不再压低积压排空速度。
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

    /// <summary>
    /// 发布并行度 — 批内消息的并行发布 worker 数（默认 1 = 串行，兼容既有行为）。
    /// <para>
    /// ⚠️ C1（perf-opt-sweep，2026-09-20）：<c>&gt;1</c> 时批内消息按分区交由
    /// per-worker scope（独立 store/DbContext 实例）并行「反序列化 → 发布 → 标记」。
    /// 前提：① broker 发布须线程安全（Kafka producer 天然支持；RabbitMQ 单 channel
    /// 并发发布已核实——RabbitMQ.Client 7.x 的 publisher-confirms 由 channel 内部
    /// <c>_confirmSemaphore</c> 仅护帧发送段、确认等待按发布序号键的 ConcurrentDictionary
    /// 不串行，官方集成测试 <c>TestFloodPublishing</c> 以 8 个并发 Task 在同一共享 channel
    /// 发布；注意 <c>MaxOutstandingConfirms</c> 仅在 <c>CreateAsync</c> 工厂路径生效，
    /// 注入单 channel 的构造路径无该限流器）；② 消费方幂等（并行加速下乱序提交概率上升）。
    /// 租约/fencing 互斥不受影响——各 worker 的 Mark* 走各自 store 实例，fencing 守卫
    /// 逐消息独立。启动期经 <c>ValidateOnStart</c> 校验 ≥ 1。
    /// </para>
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>
    /// 发布确认流水线窗口（T3.1，2026-10-11 优化实施轮）——分区内同时处于 in-flight 的
    /// 发布确认数上限。<b>默认 1 = 串行逐条确认（既有行为逐字不变）</b>；<c>&gt;1</c> 启用
    /// 流水线：broker 确认往返不再逐条串行相乘（收益量级见 performance.md 投递吞吐段：
    /// RTT 主导场景为窗口量级）。
    /// <para>
    /// ⚠️ 语义边界：①发布按分区序启动，<b>出窗处理按完成序</b>（2026-10-21 起）——
    /// 单条慢投递只占自己的窗口槽，不阻塞已完成消息的标记与新发布启动；标记顺序不再
    /// 与启动序一致，消费方应具备 Inbox 幂等（乱序窗口随本值增大）。②失败路径
    /// （退避重试/死信）与串行形态逐项一致。③broker 线程安全前提与
    /// <see cref="MaxDegreeOfParallelism"/> 同（Kafka producer 天然多路 / RabbitMQ.Client 7.x
    /// 共享 channel 并发发布已核实；Kafka 重试乱序须启用 enable.idempotence，框架在调用方
    /// 未显式配置 acks/retries/in-flight 时默认开启）。④与
    /// <see cref="MaxDegreeOfParallelism"/> 正交：后者分 worker（各自串行窗口），本值在
    /// 每个 worker 的分区内生效。启动期经 <c>ValidateOnStart</c> 校验 ≥ 1。
    /// </para>
    /// </summary>
    public int MaxInFlightPublishes { get; set; } = 1;

    /// <summary>
    /// 终态标记批量化阈值（R4/P0-2，ADR-027，2026-10-14 性能实施轮）——成功路径的
    /// MarkProcessed 累积到本值后经 <c>IBatchMarkOutboxStore.MarkProcessedBatchAsync</c>
    /// 单语句批量写（按 RetryCount 分组，组数 = 批内去重重试计数数）。<b>默认 1 = 逐条
    /// 标记（既有行为逐字不变）</b>；<c>&gt;1</c> 启用批量：标记往返从每消息 1 次降为
    /// 每批（组）1 次——远程 DB 上与 T3.1 流水线化同为主吞吐杠杆（T0.2 量具实测逐条
    /// 确认串行化占投递耗时 80% @RTT20ms）。
    /// <para>
    /// ⚠️ 语义边界：①标记时点从「发布后立即」延迟到「累积满阈值/批末」（崩溃窗口内
    /// 已发布未标记的消息经租约过期重投——at-least-once 契约内，重复窗口随本值增大，
    /// 消费方应具备 Inbox 幂等，与 <see cref="MaxInFlightPublishes"/> 同族声明）；②需
    /// store 实现 <c>IBatchMarkOutboxStore</c>（Dapper/EF Core/InMemory/PalORM 四栈实现，
    /// PalORM 2026-10-16 补齐——until 编码三方言探针实证，见 ADR-027）；③失败路径（死信/重试释放）仍逐条即时标记不变；
    /// ④分区内标记批序 = 租约序（保序声明同 T3.1）。启动期经 <c>ValidateOnStart</c>
    /// 校验 ≥ 1。单语句组内行数上限 400（SQLite 参数上限 999 防御，实现内分块）。
    /// </para>
    /// </summary>
    public int BatchMarkThreshold { get; set; } = 1;
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
    /// 启用到期时间过滤（2026-10-19 M1）。默认 false：仍走全量活跃扫描，供存量行通过
    /// 正常保存路径回填 <see cref="SagaState.NextCheckAt"/>。回填完成并确认所有活跃行
    /// 的 NextCheckAt 正确后置 true，只租约 <c>NextCheckAt &lt;= now</c> 的行。
    /// 自定义 store 未实现 <see cref="IDueTimeSagaStore{TState}"/> 时自动回落全量路径。
    /// </summary>
    public bool EnableDueTimeFilter { get; set; }

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
