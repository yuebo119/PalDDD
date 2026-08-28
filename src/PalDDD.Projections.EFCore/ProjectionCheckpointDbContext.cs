using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// EF Core 检查点持久化
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core 投影检查点存储基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class ProjectionCheckpointDbContext(DbContextOptions options) : DbContext(options), IProjectionCheckpointStore
{
    /// <summary>投影 checkpoint 表</summary>
    public DbSet<ProjectionCheckpoint> ProjectionCheckpoints => Set<ProjectionCheckpoint>();

    /// <inheritdoc/>
    /// <remarks>
    /// 三十八轮 P3 修复：变更追踪无界增长——长驻 scope 下每次查询的跟踪条目滞留
    /// ChangeTracker。本方法为纯读契约（结果不进 Mark*+SaveChanges 写回路径，接口
    /// doc 与全部调用方已核实），改 AsNoTracking 后零跟踪残留。
    /// </remarks>
    public async ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);

        return await ProjectionCheckpoints
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ProjectionName == projectionName
                    && x.SourceName == sourceName
                    && x.Position == position,
                ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 三十八轮 P3 修复：变更追踪无界增长——本方法保持跟踪查询（僵尸回收分支的
    /// MarkProcessing 突变依赖 ChangeTracker 持久化，AsNoTracking 会使保存静默丢失），
    /// 但在每条返回路径上 Detach 清理跟踪条目；调用方后续的
    /// MarkCompletedAsync/MarkFailedAsync 自带 AttachIfDetached 兜底，Detach 后语义不变。
    /// </remarks>
    public async ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);

        // v33 P3：processingTimeout 必须非负（四栈 Checkpoint 族唯一漏网——镜像 v29 S8
        // InboxDbContext.cs:50-51 / DapperProjectionCheckpointStore ITM-107 姊妹守卫）：
        // 领域方法 MarkProcessing 无守卫，负值使 LeaseUntil = startedAt + timeout < startedAt，
        // 下方"Processing 且 LeaseUntil > startedAt"防抢占判定恒假，刚启动的 Processing
        // 检查点被误判僵尸可抢占。允许 TimeSpan.Zero（仅禁负值，对齐姊妹口径）。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        var checkpoint = await ProjectionCheckpoints.SingleOrDefaultAsync(
            x => x.ProjectionName == projectionName
                && x.SourceName == sourceName
                && x.Position == position,
            ct).ConfigureAwait(false);

        if (checkpoint is null)
            return await TryCreateCheckpointAsync(projectionName, sourceName, position, startedAt, processingTimeout, ct).ConfigureAwait(false);

        // 已完成的位置永远不会重新处理。
        if (checkpoint.Status == ProjectionCheckpointStatus.Completed)
        {
            // 三十八轮 P3 修复：未命中写回路径的只读分支同样清理跟踪条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }

        // 活跃的工作器 —— 租约尚未过期。
        if (checkpoint.Status == ProjectionCheckpointStatus.Processing && checkpoint.LeaseUntil > startedAt)
        {
            // 三十八轮 P3 修复：同上——清理跟踪条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }

        // 僵尸（处理中 + 已过期）或失败 —— 通过 MarkProcessing 回收。
        checkpoint.MarkProcessing(startedAt, processingTimeout);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后立即 Detach——租约已落库，后续 Mark* 走
            // AttachIfDetached 重挂，ChangeTracker 不残留本条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return checkpoint;
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }
        catch (DbUpdateException)
        {
            // v26 P2 修复：非并发瞬时故障（连接闪断/超时）上抛前 Detach——checkpoint 已被
            // MarkProcessing 变异为 Modified（含 Revision++），滞留 ChangeTracker 会被下次
            // 无关 SaveChanges 提交为从未成功获取的幽灵租约（WHERE Revision=orig 匹配 DB
            // 真值必成功），锁死该投影位置至 LeaseDuration 过期——镜像 IdempotencyDbContext
            // 三十八轮 P2 全修样板
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        AttachIfDetached(checkpoint);
        checkpoint.MarkCompleted(completedAt);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后 Detach（与 TryStartAsync 无界增长修复同轮）——
            // 状态已落库，ChangeTracker 不残留本条目
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 修复（八轮评审）：Revision 并发令牌冲突 = 租约已被其他工作器回收，被抢占者的
            // 标记不生效——Detach 清理跟踪状态后静默返回，不掩盖原始业务异常、不中止回放
            // （对齐上方 TryStartAsync 的既有捕获模式）。
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：非并发瞬时故障上抛前 Detach（与 MarkFailedAsync 同型，样板同上）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask MarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        AttachIfDetached(checkpoint);
        // v26 P3 修复：存储层截断兜底（FailureReason.Normalize）——Error 列 HasMaxLength(2048)，
        // 超长原因使 MarkFailed 持久化自身抛 DbUpdateException 掩盖原始投影失败（十七轮
        // InboxDbContext 同型场景）；调用层 ProjectionProcessor 已 Normalize，此处防御
        // 直调 Store 路径（对齐 v22 B 批 DapperProjectionCheckpointStore 2040 先例）
        checkpoint.MarkFailed(Core.FailureReason.Normalize(failureReason), failedAt);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后 Detach（与 MarkCompletedAsync 同型，无界增长）
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 修复（八轮评审）：Revision 并发令牌冲突 = 租约已被其他工作器回收，被抢占者的
            // 标记不生效——Detach 清理跟踪状态后静默返回，不掩盖原始业务异常、不中止回放
            // （对齐上方 TryStartAsync 的既有捕获模式）。
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：非并发瞬时故障上抛前 Detach——checkpoint 已被变异为 Modified
            // （Revision 已递增），滞留 ChangeTracker 会使同 scope 下次 TryStart 的 identity
            // resolution 返回内存 Status=Completed 而 DB 未写的实例，误判"已完成"静默跳过
            // （重启/他节点接管后重复投影）——镜像 IdempotencyDbContext 全修样板
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask ResetAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var matching = ProjectionCheckpoints
            .Where(x => x.ProjectionName == projectionName && x.SourceName == sourceName);

        if (Database.IsRelational())
        {
            // 关系型 provider：单条 SQL DELETE，零内存加载、零变更跟踪。
            await matching.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return;
        }

        // 非关系型 provider（如 InMemory）：回退到加载+RemoveRange 路径。
        // ExecuteDeleteAsync 在 InMemory provider 上会抛 InvalidOperationException。
        var checkpoints = await matching.ToListAsync(ct).ConfigureAwait(false);
        ProjectionCheckpoints.RemoveRange(checkpoints);
        await SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>配置投影 checkpoint 实体</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProjectionCheckpoint>(e =>
        {
            e.HasKey(x => new { x.ProjectionName, x.SourceName, x.Position });
            e.Property(x => x.ProjectionName).HasMaxLength(256);
            e.Property(x => x.SourceName).HasMaxLength(256);
            e.Property(x => x.Position).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.UpdatedAt);
            e.Property(x => x.LeaseUntil);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Error).HasMaxLength(2048);
            e.HasIndex(x => new { x.ProjectionName, x.SourceName, x.Status });
        });
    }

    private async ValueTask<ProjectionCheckpoint?> TryCreateCheckpointAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct)
    {
        // v33 P3：同 TryStartAsync 的非负守卫——本方法是租约创建的唯一汇聚点，防御未来
        // 新增调用路径绕过上游守卫（当前仅 TryStartAsync 可达，纯纵深防御）
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        var checkpoint = new ProjectionCheckpoint(
            projectionName,
            sourceName,
            position,
            ProjectionCheckpointStatus.Processing,
            startedAt);
        checkpoint.MarkProcessing(startedAt, processingTimeout); // set LeaseUntil + Revision
        ProjectionCheckpoints.Add(checkpoint);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后立即 Detach——新建租约已落库，后续 Mark* 走
            // AttachIfDetached 重挂，ChangeTracker 不残留本条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return checkpoint;
        }
        // P2 修复（ITM-065 同型）：仅唯一约束冲突返回 null（语义=他人已持有租约）；
        // 连接故障等其他 DbUpdateException 上抛，避免基础设施故障被误判为租约竞争。
        catch (DbUpdateException ex) when (!IsUniqueConstraintViolation(ex))
        {
            // v10 P3-1：瞬时异常上抛前 Detach（对齐 EventLogDbContext 三十八轮姊妹形态）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>
    /// 判定 DbUpdateException 是否为唯一约束冲突（跨 provider 鸭子类型）。
    /// <para>与 EventLogDbContext/InboxDbContext/IdempotencyDbContext 对齐（ITM-003 同型）。</para>
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
            // ITM-193 修复（三十轮）：补 SqliteException 类型限定（对齐全仓姊妹，PD17）——
            // 裸消息匹配会在 TryCreateCheckpointAsync 主路径把文案含该词组的非唯一约束
            // 误判为租约竞争返回 null，租约被静默让出。原"P3-3 已知局限"声明在姊妹统一
            // 修复后已过时，随本修复删除。
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

    private void AttachIfDetached(ProjectionCheckpoint checkpoint)
    {
        if (Entry(checkpoint).State == EntityState.Detached)
            ProjectionCheckpoints.Attach(checkpoint);
    }

    private static void ValidateKeyParts(string projectionName, string sourceName, string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
    }
}
