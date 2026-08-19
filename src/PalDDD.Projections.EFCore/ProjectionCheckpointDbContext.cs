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
    public async ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);

        return await ProjectionCheckpoints.SingleOrDefaultAsync(
            x => x.ProjectionName == projectionName
                && x.SourceName == sourceName
                && x.Position == position,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);

        var checkpoint = await ProjectionCheckpoints.SingleOrDefaultAsync(
            x => x.ProjectionName == projectionName
                && x.SourceName == sourceName
                && x.Position == position,
            ct).ConfigureAwait(false);

        if (checkpoint is null)
            return await TryCreateCheckpointAsync(projectionName, sourceName, position, startedAt, processingTimeout, ct).ConfigureAwait(false);

        // 已完成的位置永远不会重新处理。
        if (checkpoint.Status == ProjectionCheckpointStatus.Completed)
            return null;

        // 活跃的工作器 —— 租约尚未过期。
        if (checkpoint.Status == ProjectionCheckpointStatus.Processing && checkpoint.LeaseUntil > startedAt)
            return null;

        // 僵尸（处理中 + 已过期）或失败 —— 通过 MarkProcessing 回收。
        checkpoint.MarkProcessing(startedAt, processingTimeout);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            return checkpoint;
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(checkpoint).State = EntityState.Detached;
            return null;
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
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 修复（八轮评审）：Revision 并发令牌冲突 = 租约已被其他工作器回收，被抢占者的
            // 标记不生效——Detach 清理跟踪状态后静默返回，不掩盖原始业务异常、不中止回放
            // （对齐上方 TryStartAsync 的既有捕获模式）。
            Entry(checkpoint).State = EntityState.Detached;
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
        checkpoint.MarkFailed(failureReason, failedAt);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 修复（八轮评审）：Revision 并发令牌冲突 = 租约已被其他工作器回收，被抢占者的
            // 失败标记不生效——Detach 清理跟踪状态后静默返回，不掩盖原始业务异常、不中止回放
            // （对齐上方 TryStartAsync 的既有捕获模式）。
            Entry(checkpoint).State = EntityState.Detached;
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
            return checkpoint;
        }
        // P2 修复（ITM-065 同型）：仅唯一约束冲突返回 null（语义=他人已持有租约）；
        // 连接故障等其他 DbUpdateException 上抛，避免基础设施故障被误判为租约竞争。
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
