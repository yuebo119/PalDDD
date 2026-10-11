using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 发件箱存储抽象
// ─────────────────────────────────────────────────────────────

/// <summary>发件箱存储抽象 — 解耦 OutboxProcessor 与具体数据库实现</summary>
/// <remarks>
/// 生产实现必须提供原子租约获取语义，避免多实例重复发布。<br/>
/// EF Core / SQL Server 实现由 PalDDD.Transactions.EFCore 适配包提供。
/// <para>
/// 📣 <b>已在 v4.0 执行的破坏性变更（ADR-020 → ADR-026，维护者裁决 2026-08-26 / 2026-10-10）</b>：
/// ① <b>跨栈 fencing 契约统一</b>——已执行（ADR-026 ②，2026-10-10）：三栈终态写
/// （MarkProcessedAsync/MarkDeadAsync/ReleaseForRetryAsync）统一为「门控拒绝（租约 token 失配/非当前
/// 持有者）→ 零 DB 写 + 零内存变异；成功（affected&gt;0）→ DB 全套更新 + 入参对象全套
/// 六字段回写」。历史分叉（Dapper 成功仅回写租约两字段、MarkDead 无视守卫无条件清租约、
/// EF 主路径零入参回写）同轮收口；EF 跟踪实体的并发令牌边界见各实现 remarks。
/// ② <b>异步化</b>——已执行（ADR-026 ③④，2026-10-10）：AddMessage/MarkProcessed/MarkDead/
/// ReleaseForRetry 同步方法删除改异步签名 + AddMessagesAsync 补 CancellationToken（PalORM 栈
/// 7 处 sync-over-async 清零）。迁移指引见 CHANGELOG [4.0.0] Changed [Breaking] 条目。
/// </para>
/// </remarks>
public interface IPalOutboxStore
{
    /// <summary>默认最大重试次数。</summary>
    const int DefaultMaxRetryCount = 10;

    /// <summary>获取待处理消息（只用于观测/健康检查，不获取租约）</summary>
    ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct);

    /// <summary>原子租约获取待处理消息，避免多实例重复发布。</summary>
    ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct);

    /// <summary>添加新消息到发件箱。<br/>v4.0 ③（ADR-026）：同步 <c>AddMessage</c> 已删除，
    /// 改异步签名（消除 PalORM 栈 sync-over-async 根因；迁移：调用点 <c>await</c> 化）。</summary>
    ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default);

    /// <summary>批量添加消息 — 实现应选择数据库最优批量路径（COPY/BulkCopy/事务批处理）。<br/>
    /// v4.0 ④（ADR-026）：补 <c>CancellationToken</c>（v8 声明的契约缺口收口）。</summary>
    ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default);

    /// <summary>标记消息发布成功。</summary>
    /// <remarks>实现必须清空 <c>LockedBy</c> 和 <c>LockedUntil</c> 字段，确保租约显式释放、不被观测到陈旧持有者。
    /// <para>
    /// ⚠️ <b>前置条件（ITM-269 声明；ITM-272 勘正 Dapper 阵营）</b>：应传入 <see cref="LeasePendingMessagesAsync"/>
    /// 返回的租约实例。对<b>未租约</b>消息（LockedBy=null）直接标记：<b>四栈一致放行</b>
    /// （3.1.0 T-15 起，InMemory 原"拒绝"已对齐——PalORM/Dapper/EF 的 owner-null SQL 分支
    /// 命中即 UPDATE，EF 的 <c>OutboxDbContext</c> 明写该路径是运维/测试路径的有意能力；
    /// InMemory 保留 <c>Status == Pending</c> 终态守卫与引用相等性僵尸标记保护）。对已被其他
    /// worker 重租的消息（successor 替换）仍拒绝标记。fencing 统一契约（v4.0 ②，ADR-026）：
    /// 门控拒绝零 DB 写 + 零内存变异；成功后入参全套六字段回写（EF 栈仅对 Detached 快照
    /// 回写，跟踪实体自动跳过——并发令牌守卫，见各实现 remarks）。处理管线一律走 Lease 路径；
    /// GetPending 观测结果原则上不构成标记资格——例外：租约已过期且未被他人重租的消息经
    /// token 分支仍可标记成功（原 worker 过期后完成属合理语义）。
    /// </para></remarks>
    ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default);

    /// <summary>标记消息不可恢复。</summary>
    /// <remarks>实现必须清空 <c>LockedBy</c> 和 <c>LockedUntil</c> 字段，确保租约显式释放、不被观测到陈旧持有者。
    /// v4.0 ③（ADR-026）：异步化，fencing 契约同 <see cref="MarkProcessedAsync"/>。</remarks>
    ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default);

    /// <summary>释放租约并等待下次重试。</summary>
    /// <remarks>实现必须清空 <c>LockedBy</c> 和 <c>LockedUntil</c> 字段，确保租约显式释放、不被观测到陈旧持有者。
    /// v4.0 ③（ADR-026）：异步化，fencing 契约同 <see cref="MarkProcessedAsync"/>。</remarks>
    ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default);

    /// <summary>
    /// 将一条死信消息重新投递（状态从 <c>Dead</c> 改为 <c>Pending</c>），供 ops 在死信根因消除后批量重投使用。<br/>
    /// 💡 <b>幂等前提</b>：调用方必须保证下游消费者通过 Inbox / Idempotency 模式保证幂等，
    /// 否则重投递可能导致重复副作用（详见 ADR-011）。<br/>
    /// <c>retryCount</c> <b>不</b>被重置——保留失败历史供可观测性查询；
    /// 调用方按需传入 <paramref name="nextAttemptAt"/> 控制首次重投时间窗。
    /// </summary>
    /// <param name="messageId">死信消息 ID</param>
    /// <param name="nextAttemptAt">下次尝试时间</param>
    /// <param name="retriedBy">操作者标识（写入 <c>Error</c> 列用于审计："requeued by {retriedBy} at {now}"）</param>
    /// <returns>受影响行数：1 表示成功从 Dead 改为 Pending；0 表示消息不存在或当前状态非 Dead（拒绝重投）</returns>
    ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct);

    /// <summary>持久化所有更改</summary>
    /// <remarks>
    /// <b>实现语义差异</b>：<br/>
    /// - <b>EF Core 实现</b>（<c>OutboxDbContext</c>）：批量提交 ChangeTracker 中的挂起更改，<br/>
    ///   与 <c>UnitOfWork</c> 模式配合，在事务边界统一 <c>SaveChanges</c>。<br/>
    /// - <b>Dapper 实现</b>（<c>DapperOutboxStore</c>）：每个 <c>AddMessage</c>/<c>MarkProcessed</c>/<c>MarkDead</c>/<c>ReleaseForRetry</c>
    ///   已立即执行 SQL，<c>SaveChangesAsync</c> 是无操作（返回 0）。<br/>
    ///   Dapper 的事务边界由 <c>DbTransaction</c> 参数（构造函数注入）控制，<br/>
    ///   而非由 <c>SaveChangesAsync</c> 触发。调用方应通过 <c>UnitOfWork.BeginTransactionAsync</c>
    ///   和 <c>CommitAsync</c> 管理事务边界。<br/>
    /// - <b>内存实现</b>（<c>InMemoryOutboxStore</c>）：无操作，数据即时生效。
    /// </remarks>
    ValueTask<int> SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// 立即写 Outbox store——<see cref="IPalOutboxStore.AddMessagesAsync"/> 语义在「立即执行」上的强化契约
///（优化轮四，2026-10-10）。
/// <para>
/// 实现方声明：本方法的全部写入在调用内立即完成（对 store 自身连接/事务），不依赖、也不触发
/// 调用方的 <c>SaveChanges</c>——因此可在 EF <c>SavingChanges(Async)</c> 拦截相位安全调用
///（<c>OutboxDomainEventInterceptor</c> 的异步批量路径据此分派：立即写族多事件合并单次调用
/// 并消除逐条同步写在远程库上的往返级线程阻塞）。
/// </para>
/// <para>
/// <b>OutboxDbContext（EF 栈）刻意不实现本接口</b>：其 <c>AddMessage</c> 是 ChangeTracker
/// 跟踪添加（写入延迟到上下文自身的 SaveChanges），且其 <c>AddMessagesAsync</c> 内嵌自持
/// <c>SaveChangesAsync</c>——在拦截相位调用构成 EF 重入（SaveChanges-in-SavingChanges，
/// 未定义行为）。拦截器对其实例维持逐条 <c>AddMessage</c> 跟踪路径。
/// </para>
/// <para>
/// 成员签名与 <see cref="IPalOutboxStore.AddMessagesAsync"/> 完全一致——实现类的既有公共
/// <c>AddMessagesAsync</c> 同时满足两接口（单一方法映射），标注本接口即完成契约声明。
/// </para>
/// </summary>
public interface IImmediateWriteOutboxStore
{
    /// <summary>立即批量写入并返回写入行数——语义与 <see cref="IPalOutboxStore.AddMessagesAsync"/> 一致
    ///（含 v4.0 ④ 的 <c>CancellationToken</c>），叠加「立即执行、无调用方 SaveChanges 依赖」契约。</summary>
    ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default);
}
