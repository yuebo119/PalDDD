using System.Diagnostics.CodeAnalysis;
using ByteAether.Ulid;
using PalORM;
using PalDDD.PalORM.Models;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Outbox Store 的 PalORM 实现 —— 双泛型核心基类。
/// <para>
/// 由各方言包（PalDDD.PalORM.Sqlite 等）派生具体类固化 <typeparamref name="TProvider"/>，
/// 如 <c>SqliteOutboxStore : PalOrmOutboxStore&lt;SqliteProvider&gt;</c>。
/// </para>
/// <para><b>设计要点</b>：
/// <list type="bullet">
/// <item>单 Scoped <see cref="DataSession{TProvider}"/> 共享 —— 事务经 UnitOfWork.BeginTransactionAsync 后自动传播。</item>
/// <item>GetPending 走 QueryAsync&lt;T&gt;（FormattableString 自动参数化）；Lease 必须<b>降级手写 SQL</b>（QueryBuilder UPDATE 拒绝子查询+RETURNING 整行）。</item>
/// <item>SQL 显式列出列名（按 <see cref="OutboxMessageRow"/> 属性声明序对齐，避免 PalORM ColumnOrderValidator 列序错位）。</item>
/// <item>[ConcurrencyCheck]RetryCount 在 UpdateAsync 路径自动加并发谓词；ReleaseForRetry 走手写 SQL 避免 [ConcurrencyCheck] 干扰原子自增。</item>
/// </list>
/// </para>
/// </summary>
public class PalOrmOutboxStore<TProvider> : IPalOutboxStore
    where TProvider : IDbProvider
{
    /// <summary>共享的 Scoped 数据库会话（事务自动传播源）。</summary>
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类（方言包中间类）需直接访问 Session 以扩展方言特有能力。")]
    protected readonly DataSession<TProvider> Session;

    /// <summary>时间提供者（用于 created_at/processed_at 应用层赋值）。</summary>
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需访问 Clock 以统一时间源。")]
    protected readonly TimeProvider Clock;

    /// <summary>构造 Outbox Store。</summary>
    public PalOrmOutboxStore(DataSession<TProvider> session, TimeProvider? clock = null)
    {
        Session = session;
        Clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize, int maxRetryCount, CancellationToken ct)
    {
        var now = Clock.GetUtcNow();
        // 列名内联到 SQL 字面量（PalORM 要求 FormattableString 类型，字符串拼接会退化为 string）
        var rows = await Session.QueryAsync<OutboxMessageRow>(
            $"SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages WHERE status = {(int)OutboxStatus.Pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize}",
            ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
    {
        var now = Clock.GetUtcNow();
        var until = now + leaseDuration;
        var pending = (int)OutboxStatus.Pending;

        // 核心原子租约 SQL —— 必须 QueryAsync/ExecuteAsync（QueryBuilder UPDATE 拒绝子查询+LIMIT+RETURNING 整行）
        // 方言分支：PG/SQLite 走 RETURNING 单语句；MySQL 走 UPDATE + SELECT 两步。
        if (TProvider.SupportsReturningClause)
        {
            var rows = await Session.QueryAsync<OutboxMessageRow>(
                $"UPDATE outbox_messages SET locked_by = {owner}, locked_until = {until} WHERE id IN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize}) RETURNING id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state",
                ct);
            return rows.Select(r => r.ToDomain()).ToList();
        }
        else
        {
            // MySQL 路径：不支持 UPDATE...WHERE id IN (SELECT...LIMIT)。
            // 用 JOIN 子查询替代（MySQL 特化）+ 按 lease 标识回读（两步避免重跑子查询 P0 bug）
            //
            // ⚠️ 已知限制（八轮评审 P3，声明不修）：回读按 (locked_by, locked_until) 匹配——同一 owner
            // 在同一 tick（until 完全相等，如 FakeTimeProvider 冻结时间）发起两次租约时，第二次回读
            // 会混入第一次已锁定的批次。生产触发条件近乎为零（DATETIME(6) 微秒精度 + 单 owner 串行租约）；
            // PG/SQLite 走 RETURNING 单语句天然免疫。候选 id 预取方案需要 IN 列表参数化——PalORM 的
            // FormattableString 路径每个格式参数只绑一个 DbParameter（BindFormattableParameters），
            // WhereIn 仅存在于 QueryBuilder 实体路径（无法表达此 UPDATE+JOIN 手写 SQL），改动面大，
            // 待 PalORM 支持 IN 参数化后与 Saga 路径统一修。
            await Session.ExecuteAsync(
                $"UPDATE outbox_messages t JOIN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize}) AS sub ON t.id = sub.id SET t.locked_by = {owner}, t.locked_until = {until}",
                ct);
            var rows = await Session.QueryAsync<OutboxMessageRow>(
                $"SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages WHERE locked_by = {owner} AND locked_until = {until} ORDER BY created_at",
                ct);
            return rows.Select(r => r.ToDomain()).ToList();
        }
    }

    /// <inheritdoc />
    public void AddMessage(OutboxMessage message)
    {
        // InsertAsync 对 [Key(AutoIncrement=false)] 的 Ulid 主键不回填 —— 实体已带 Id。
        // created_at 由领域对象在构造时赋值（init-only，Store 不覆盖）。
        var row = OutboxMessageRow.FromDomain(message);
        Session.InsertAsync(row, default).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        if (messages.Count == 0) return 0;
        var rows = messages.Select(OutboxMessageRow.FromDomain).ToList();
        // BulkInsertAsync 自动选方言最优路径（PG COPY / MySQL BulkCopy / SQLite 多值 INSERT）
        return (int)await Session.BulkInsertAsync(rows, batchSize: 1000, ct: default);
    }

    /// <inheritdoc />
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        // UpdateAsync 经 [ConcurrencyCheck]RetryCount 自动加 WHERE retry_count=@orig
        // 失败抛 ConcurrencyConflictException —— 调用方（OutboxProcessor）应捕获并视为已处理
        message.Status = OutboxStatus.Processed;
        message.ProcessedAt = processedAt;
        message.Error = null;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
        var row = OutboxMessageRow.FromDomain(message);
        Session.UpdateAsync(row, default).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        message.Status = OutboxStatus.Dead;
        message.Error = failureReason;
        message.ProcessedAt = deadAt;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
        var row = OutboxMessageRow.FromDomain(message);
        Session.UpdateAsync(row, default).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        // 手写 SQL 路径：原子自增 retry_count（避免读-改-写竞态）
        // 不走 UpdateAsync —— 避免 [ConcurrencyCheck] 干扰原子自增语义
        // P2 修复：补租约守卫——原 WHERE 仅按 id，租约过期被其他 worker 抢占后，
        // 原 worker 的失败释放会清掉新 worker 的锁并误增 retry_count。
        // 守卫语义：仅防"他人持有"——locked_by IS NULL（未租约/已释放）放行。
        // ⚠️ 实证教训：守卫必须以字面量分支写进 SQL，不能经变量插值——PalORM 的
        // FormattableString 会把字符串变量参数化（AND (@p) 恒假，UPDATE 0 行，
        // file-based app 探针定位）。
        var leaseOwner = message.LockedBy;
        message.Status = OutboxStatus.Pending;
        message.Error = failureReason;
        message.NextAttemptAt = nextAttemptAt;
        message.RetryCount += 1;
        message.LockedBy = null;
        message.LockedUntil = null;
        var statusPending = (int)OutboxStatus.Pending;
        var id = message.Id.ToString();
        if (leaseOwner is null)
        {
            Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusPending}, error = {failureReason}, next_attempt_at = {nextAttemptAt}, retry_count = retry_count + 1, locked_by = NULL, locked_until = NULL WHERE id = {id} AND (locked_by IS NULL)",
                default).AsTask().GetAwaiter().GetResult();
        }
        else
        {
            Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusPending}, error = {failureReason}, next_attempt_at = {nextAttemptAt}, retry_count = retry_count + 1, locked_by = NULL, locked_until = NULL WHERE id = {id} AND (locked_by IS NULL OR locked_by = {leaseOwner})",
                default).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> RequeueDeadAsync(Ulid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        var now = Clock.GetUtcNow();
        var audit = $"requeued by {retriedBy} at {now:O}";
        // 条件 UPDATE：status='Dead' 守卫防止重复重投；返回受影响行数用于幂等判断
        return await Session.ExecuteAsync(
            $"UPDATE outbox_messages SET status = {(int)OutboxStatus.Pending}, processed_at = NULL, error = {audit}, next_attempt_at = {nextAttemptAt}, locked_by = NULL, locked_until = NULL WHERE id = {messageId.ToString()} AND status = {(int)OutboxStatus.Dead}",
            ct);
    }

    /// <inheritdoc />
    public ValueTask<int> SaveChangesAsync(CancellationToken ct)
    {
        // 即时执行模式（与 Dapper 实现一致）—— 无 ChangeTracker
        // 事务边界由 UnitOfWork.BeginTransactionAsync/CommitAsync 控制
        return new ValueTask<int>(0);
    }
}
