// ─────────────────────────────────────────────────────────────
// 📜 EventLogDbContext — EF Core 事件日志（Hi/Lo 位置分配 + 乐观并发）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using PalDDD.Core.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// EF Core 事件日志持久化基类
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core 持久化事件日志基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class EventLogDbContext(
    DbContextOptions options,
    TimeProvider? timeProvider = null,
    EventLogPositionReserver? positionReserver = null) : DbContext(options), IEventLog
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly EventLogPositionReserver _positionReserver = positionReserver ?? new EventLogPositionReserver();

    /// <summary>持久化事件日志表。</summary>
    public DbSet<StoredEvent> Events => Set<StoredEvent>();

    /// <summary>持久化全局事件位置分配器状态。</summary>
    public DbSet<EventLogGlobalPositionAllocator> GlobalPositionAllocators => Set<EventLogGlobalPositionAllocator>();

    /// <inheritdoc />
    public async ValueTask<AppendEventsResult> AppendAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();

        if (events.Count == 0)
            throw new ArgumentException("At least one event is required.", nameof(events));

        foreach (var @event in events)
            ArgumentNullException.ThrowIfNull(@event);

        using var activity = PalActivitySource.StartEventLogAppend(streamName, events.Count);

        if (Database.IsRelational())
        {
            // Hi/Lo 分配消除了全局序列化瓶颈：
            // 位置预留由 EventLogPositionReserver 处理（进程内 chunk 缓存 + 乐观 CAS），
            // 因此不再需要 Serializable 隔离级别来保护分配器行。流级别的并发由
            // (StreamName, StreamVersion) 唯一索引 + 下方 DbUpdateException 捕获来保证。
            // 使用默认隔离级别（大多数 provider 上为 ReadCommitted）允许
            // 对不同流的并发追加并行执行。
            await using var transaction = await Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = await AppendCoreAsync(
                streamName,
                expectedVersion,
                events,
                cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            SetAppendActivityTags(activity, result);
            PalMetrics.EventLogAppended.Add(events.Count);
            return result;
        }

        var inMemoryResult = await AppendCoreAsync(
            streamName,
            expectedVersion,
            events,
            cancellationToken)
            .ConfigureAwait(false);
        SetAppendActivityTags(activity, inMemoryResult);
        PalMetrics.EventLogAppended.Add(events.Count);
        return inMemoryResult;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName,
        long fromVersion = 0,
        int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        using var activity = PalActivitySource.StartEventLogReadStream(streamName, fromVersion);

        var read = 0;
        var query = Events
            .AsNoTracking()
            .Where(e => e.StreamName == streamName && e.StreamVersion >= fromVersion)
            .OrderBy(e => e.StreamVersion)
            .Take(maxCount)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var @event in query)
        {
            yield return @event.ToRecordedEvent();
            checked { read++; }
        }

        activity?.SetTag("pal.eventlog.read_count", read);
        PalMetrics.EventLogRead.Add(read);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0,
        int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fromPosition, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        using var activity = PalActivitySource.StartEventLogReadAll(fromPosition);

        var read = 0;
        var query = Events
            .AsNoTracking()
            .Where(e => e.GlobalPosition >= fromPosition)
            .OrderBy(e => e.GlobalPosition)
            .Take(maxCount)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var @event in query)
        {
            yield return @event.ToRecordedEvent();
            checked { read++; }
        }

        activity?.SetTag("pal.eventlog.read_count", read);
        PalMetrics.EventLogRead.Add(read);
    }

    /// <summary>配置持久化事件日志实体。</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StoredEvent>(e =>
        {
            e.HasKey(x => x.GlobalPosition);
            e.Property(x => x.GlobalPosition).ValueGeneratedNever();
            e.Property(x => x.StreamName).HasMaxLength(512);
            e.Property(x => x.EventName).HasMaxLength(256);
            e.Property(x => x.ContentType).HasMaxLength(128);
            e.Property(x => x.ActorId).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(2048);
            e.Property(x => x.TraceParent).HasMaxLength(128);
            e.Property(x => x.TraceState).HasMaxLength(512);
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.Metadata).IsRequired();
            e.HasIndex(x => new { x.StreamName, x.StreamVersion }).IsUnique();
            e.HasIndex(x => x.EventId).IsUnique();
        });

        modelBuilder.Entity<EventLogGlobalPositionAllocator>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.NextGlobalPosition);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
    }

    private async ValueTask<AppendEventsResult> AppendCoreAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken cancellationToken)
    {
        var actualVersion = await GetActualStreamVersionAsync(streamName, cancellationToken).ConfigureAwait(false);
        if (!expectedVersion.Matches(actualVersion))
            throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion);

        var firstStreamVersion = actualVersion + 1;
        var firstGlobalPosition = await ReserveGlobalPositionsAsync(events.Count, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        for (var i = 0; i < events.Count; i++)
        {
            Events.Add(StoredEvent.From(
                streamName,
                firstStreamVersion + i,
                firstGlobalPosition + i,
                now,
                events[i]));
        }

        try
        {
            await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            DetachAddedEvents();
            var currentVersion = await GetActualStreamVersionAsync(streamName, cancellationToken).ConfigureAwait(false);
            throw new EventStreamConcurrencyException(streamName, expectedVersion, currentVersion);
        }

        return new AppendEventsResult(
            streamName,
            firstStreamVersion,
            firstStreamVersion + events.Count - 1,
            firstGlobalPosition,
            firstGlobalPosition + events.Count - 1);
    }

    private static void SetAppendActivityTags(
        System.Diagnostics.Activity? activity,
        AppendEventsResult result)
    {
        activity?.SetTag("pal.eventlog.first_stream_version", result.FirstStreamVersion);
        activity?.SetTag("pal.eventlog.last_stream_version", result.LastStreamVersion);
        activity?.SetTag("pal.eventlog.first_global_position", result.FirstGlobalPosition);
        activity?.SetTag("pal.eventlog.last_global_position", result.LastGlobalPosition);
    }

    private async ValueTask<long> GetActualStreamVersionAsync(string streamName, CancellationToken cancellationToken)
    {
        // 合并为单次 MaxAsync：无事件时返回 -1（NoStream 语义），省去 AnyAsync 预检查的一次 DB 往返。
        // MaxAsync 在 SQL 上翻译为 SELECT MAX(StreamVersion) ... WHERE StreamName=@p，
        // 无匹配行时 MAX 返回 NULL → EF Core 映射为 nullable long，空流落到 default(-1) 分支。
        return await Events
            .Where(e => e.StreamName == streamName)
            .MaxAsync(e => (long?)e.StreamVersion, cancellationToken)
            ?? -1;
    }

    private async ValueTask<long> ReserveGlobalPositionsAsync(int count, CancellationToken cancellationToken)
    {
        return await _positionReserver.ReserveAsync(this, count, cancellationToken).ConfigureAwait(false);
    }

    private void DetachAddedEvents()
    {
        foreach (var entry in ChangeTracker.Entries<StoredEvent>())
        {
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
        }
    }
}
