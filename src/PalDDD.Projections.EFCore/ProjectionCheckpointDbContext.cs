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
            ct);
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
            ct);

        if (checkpoint is null)
            return await TryCreateCheckpointAsync(projectionName, sourceName, position, startedAt, processingTimeout, ct);

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
            await SaveChangesAsync(ct);
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
        await SaveChangesAsync(ct);
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
        await SaveChangesAsync(ct);
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
            await SaveChangesAsync(ct);
            return checkpoint;
        }
        catch (DbUpdateException)
        {
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }
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
