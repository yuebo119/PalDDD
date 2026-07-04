using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 发件箱存储抽象
// ─────────────────────────────────────────────────────────────

/// <summary>发件箱存储抽象 — 解耦 OutboxProcessor 与具体数据库实现</summary>
/// <remarks>
/// 生产实现必须提供原子租约获取语义，避免多实例重复发布。<br/>
/// EF Core / SQL Server 实现由 PalDDD.Transactions.EFCore 适配包提供。
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

    /// <summary>添加新消息到发件箱</summary>
    void AddMessage(OutboxMessage message);

    /// <summary>批量添加消息 — 实现应选择数据库最优批量路径（COPY/BulkCopy/事务批处理）</summary>
    ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages);

    /// <summary>标记消息发布成功。</summary>
    void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt);

    /// <summary>标记消息不可恢复。</summary>
    void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt);

    /// <summary>释放租约并等待下次重试。</summary>
    void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt);

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
