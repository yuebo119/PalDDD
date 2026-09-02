using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Idempotency;

/// <summary>EF Core 幂等存储基础上下文。</summary>
/// <remarks>
/// v25 P3 行为族 B5：变更跟踪无界增长防护（对齐 ProjectionCheckpointDbContext 三十八轮修复）——
/// <see cref="GetAsync"/> 纯读路径 AsNoTracking；MarkCompletedAsync/MarkFailedAsync 经
/// SaveTerminalStateAsync 在终态保存成功（及全部失败路径）后 Detach，长驻 scope 下
/// ChangeTracker 零残留。
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class IdempotencyDbContext(DbContextOptions options) : DbContext(options), IIdempotencyStore
{
    /// <summary>幂等执行记录表</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <inheritdoc/>
    /// <remarks>
    /// v25 P3 行为族 B5：改 AsNoTracking（对齐 ProjectionCheckpointDbContext 三十八轮修复）——
    /// 长驻 scope 下每次查询的跟踪条目滞留 ChangeTracker（无界增长）。本方法为纯读契约
    ///（调用方 IdempotencyProcessor 只做状态判断/结果反序列化，不进 Mark*+SaveChanges
    /// 写回路径），零跟踪后过期分支的 Detach 同步移除（对未跟踪实体赋 Detached 是 no-op）。
    /// </remarks>
    public async ValueTask<IdempotencyRecord?> GetAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ValidateKeyParts(operationName, key);

        var record = await IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OperationName == operationName && x.Key == key, ct).ConfigureAwait(false);
        if (record is null)
            return null;

        // 过期记录视为不存在 —— 但不在读路径中删除（避免读 API 隐含写入与锁竞争）。
        // 删除是 GC 任务的职责（基于 ExpiresAt 索引批量清理），不嵌入读路径。
        if (record.ExpiresAt > now)
            return record;

        return null;
    }

    /// <inheritdoc/>
    public async ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateKeyParts(operationName, key);

        var record = await IdempotencyRecords.SingleOrDefaultAsync(
            x => x.OperationName == operationName && x.Key == key, ct).ConfigureAwait(false);

        if (record is null)
            return await TryCreateRecordAsync(operationName, key, now, policy, ct).ConfigureAwait(false);

        if (record.ExpiresAt <= now
            || record.Status == IdempotencyRecordStatus.Failed
            || (record.Status == IdempotencyRecordStatus.Processing && record.LockedUntil <= now))
        {
            return await TryReuseRecordAsync(record, now, policy, ct).ConfigureAwait(false);
        }

        // v26 P3 修复：读路径无效分支（活跃 Processing/Completed 未过期）Detach——跟踪查询
        // 物化的实体滞留会在长驻 scope 下累积（对照 ProjectionCheckpointDbContext 三十八轮
        // 同分支已修；v25 B5 只修了 GetAsync 的 AsNoTracking 与终态 Detach，此分支漏）
        Entry(record).State = EntityState.Detached;
        return null;
    }

    /// <inheritdoc/>
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record,
        ReadOnlyMemory<byte> responsePayload,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // v53 P3：仅 Processing 可完成（对齐 InMemory 姊妹 + ProjectionCheckpoint 同轮守卫）
        if (record.Status != IdempotencyRecordStatus.Processing)
            return;

        AttachIfDetached(record);
        record.MarkCompleted(responsePayload, completedAt);
        await SaveTerminalStateAsync(record, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask MarkFailedAsync(
        IdempotencyRecord record,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        // v38 P2 修复：Completed 终态守卫（镜像 InMemoryIdempotencyStore:139 八轮
        // "Completed 终态不可翻转为 Failed" + PalOrmIdempotencyStore:196 SQL
        // "AND status <> Completed"——本栈是 PD24 管线孪生唯一漏网）。同实例续写
        // 场景下时间戳并发令牌失守（Attach 后 original=current，UPDATE 恒命中；v38 时点
        // 令牌是 UpdatedAt，v53 已换 Revision 单调令牌但同实例续写路径仍恒命中），
        // Completed 翻转为 Failed 会使 TryStartAsync 走 CAS 复用 → 副作用重新执行，
        // 幂等保证被绕过
        // v54 P3 口径声明：EFCore 仅拦 Completed（Failed 重复 MarkFailed 允许更新错误信息），
        // InMemory 姊妹更严格（非 Processing 全拦）——系统性口径差，保留宽松口径（同 ProjectionCheckpoint）
        if (record.Status == IdempotencyRecordStatus.Completed)
            return;

        AttachIfDetached(record);
        // v26 P3 修复：存储层截断兜底（FailureReason.Normalize）——Error 列上限内保障，
        // 超长原因使 MarkFailed 持久化自身抛 DbUpdateException 掩盖原始失败（调用层
        // IdempotencyProcessor 已 Normalize，此处防御直调 Store 路径，对齐 Inbox/Checkpoint 姊妹）
        record.MarkFailed(Core.FailureReason.Normalize(failureReason), failedAt);
        await SaveTerminalStateAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>配置幂等记录实体</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => new { x.OperationName, x.Key });
            e.Property(x => x.OperationName).HasMaxLength(256);
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<int>();
            // v53 P2：并发令牌改 Revision（对齐 ProjectionCheckpoint 栈）——UpdatedAt 时间戳
            // 令牌受 DB 列精度截断影响，同刻双 worker 回收同一过期租约可双双命中（幂等失效）
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.ResponsePayload)
                .HasConversion(
                    value => value.HasValue ? value.Value.ToArray() : null,
                    value => value == null ? null : new ReadOnlyMemory<byte>(value));
            e.Property(x => x.Error).HasMaxLength(2048);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.Status, x.LockedUntil });
        });
    }

    private async ValueTask<IdempotencyRecord?> TryCreateRecordAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct)
    {
        var record = new IdempotencyRecord(
            operationName,
            key,
            IdempotencyRecordStatus.Processing,
            now.Add(policy.ProcessingTimeout),
            now.Add(policy.Retention),
            now);
        IdempotencyRecords.Add(record);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // v54 P3：成功路径 Detach（镜像 ProjectionCheckpointDbContext）——中断路径不滞留
            Entry(record).State = EntityState.Detached;
            return record;
        }
        // ITM-065：仅唯一约束冲突返回 null（语义=他人已持有）；连接故障/超时等其他
        // DbUpdateException 必须上抛，否则基础设施故障被误判为幂等冲突，错误路径放大为请求丢失。
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            Entry(record).State = EntityState.Detached;
            return null;
        }
        catch (DbUpdateException)
        {
            // 三十八轮 P2 修复：瞬时故障上抛前同样 Detach——长生命周期 DbContext 中残留的
            // Added 实体会污染后续无关 SaveChanges（幽灵写入）
            Entry(record).State = EntityState.Detached;
            throw;
        }
    }

    private async ValueTask<IdempotencyRecord?> TryReuseRecordAsync(
        IdempotencyRecord record,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct)
    {
        record.MarkProcessing(now.Add(policy.ProcessingTimeout), now.Add(policy.Retention), now);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // v54 P3：成功路径 Detach（同 TryCreate 路径）
            Entry(record).State = EntityState.Detached;
            return record;
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(record).State = EntityState.Detached;
            return null;
        }
        catch (DbUpdateException)
        {
            // 三十八轮 P2 修复：record 已被 MarkProcessing 变异为 Modified——瞬时故障上抛前
            // Detach，防止下次无关 SaveChanges 把幽灵租约续期一并提交（阻塞其他 worker 至租约过期）
            Entry(record).State = EntityState.Detached;
            throw;
        }
    }

    private void AttachIfDetached(IdempotencyRecord record)
    {
        if (Entry(record).State == EntityState.Detached)
            IdempotencyRecords.Attach(record);
    }

    /// <summary>
    /// 判定 DbUpdateException 是否为唯一约束冲突（跨 provider 鸭子类型）。
    /// <para>与 EventLogDbContext/InboxDbContext 的实现对齐（ITM-003 同型，ITM-065 引入第三处）。</para>
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            // PostgreSQL: Npgsql.PostgresException.SqlState == "23505"
            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string sqlState
                && sqlState == "23505")
            {
                return true;
            }

            // MySQL: MySqlException.Number == 1062（ER_DUP_ENTRY）或 1586
            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && (mysqlNumber == 1062 || mysqlNumber == 1586))
            {
                return true;
            }

            // SQL Server: SqlException.Number == 2601 (unique index) 或 2627 (unique constraint / PK)
            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
            {
                return true;
            }

            // SQLite: Microsoft.Data.Sqlite.SqliteException 消息包含 "UNIQUE constraint"
            // P2 修复（二十一轮）：补 SqliteException 类型限定（镜像 InboxDbContext 十七轮修复，PD17）——
            // 裸消息匹配在 TryCreateRecordAsync 主路径误判为幂等冲突返回 null 是请求丢失语义（ITM-065 要防的）
            var message = inner.Message;
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 终态标记持久化（MarkCompletedAsync/MarkFailedAsync 共享）。
    /// </summary>
    private async ValueTask SaveTerminalStateAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // v25 P3 行为族 B5：保存成功后 Detach（镜像 ProjectionCheckpointDbContext:115-118）——
            // 终态已落库，长驻 ChangeTracker 不残留本条目（无界增长）；后续对同一 record 的
            // Mark* 自带 AttachIfDetached 兜底，Detach 后语义不变。
            Entry(record).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 文档声明（七轮评审）：终态写入的并发冲突静默吞掉（Detach 后返回）——
            // 语义：另一并发执行者已写入终态（Completed 或 Failed），本方操作已实际完成，
            // 只是终态标记被抢先。at-least-once 语义下这是可接受的（操作本身幂等）。
            // 调用方收到 Executed 返回值——DB 终态可能是另一节点写入的 Completed 或 Failed。
            Entry(record).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // 三十八轮 P2 修复：瞬时故障上抛前 Detach——record 已被变异为 Modified，
            // 残留 ChangeTracker 会把幽灵终态/租约一并提交到后续无关 SaveChanges
            Entry(record).State = EntityState.Detached;
            throw;
        }
    }

    private static void ValidateKeyParts(string operationName, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }
}
