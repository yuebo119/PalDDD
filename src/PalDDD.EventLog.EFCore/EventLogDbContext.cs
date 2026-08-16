// ─────────────────────────────────────────────────────────────
// 📜 EventLogDbContext — EF Core 事件日志（Hi/Lo 位置分配 + 乐观并发）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using PalDDD.Core.Diagnostics;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PalUlid = ByteAether.Ulid.Ulid;

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
        // ITM-167 修复：metrics 尾部语句移入 finally——迭代器被消费方提前 Dispose
        //（await foreach 中 break/抛异常）时循环后语句不执行，已产出事件的计数丢失；
        // finally 在迭代器任何退出路径都会执行（对齐 InMemoryEventLog 二十一轮），
        // 且先于上方 using activity 的 Dispose（SetTag 先于 Activity 结束生效）。
        // 计数同样改为 yield 前（对齐 InMemoryEventLog ITM-120）。
        try
        {
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
                checked { read++; }
                yield return @event.ToRecordedEvent();
            }
        }
        finally
        {
            activity?.SetTag("pal.eventlog.read_count", read);
            PalMetrics.EventLogRead.Add(read);
        }
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
        // ITM-167 修复：metrics 尾部语句移入 finally + yield 前计数（同 ReadStreamAsync，
        // 对齐 InMemoryEventLog 二十一轮 / ITM-120）。
        try
        {
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
                checked { read++; }
                yield return @event.ToRecordedEvent();
            }
        }
        finally
        {
            activity?.SetTag("pal.eventlog.read_count", read);
            PalMetrics.EventLogRead.Add(read);
        }
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
            // P1 修复（二十一轮）：Ulid 属性需显式转换——关系型 provider 无 Ulid 原生映射，
            // 缺转换时 ctx.Model 即抛"无法映射类型"（探针实证关系型完全不可用；InMemory-only
            // 测试网掩盖）。对齐 OutboxDbContext.Id / SagaStateDbContext.SagaId 姊妹模式。
            e.Property(x => x.EventId).HasConversion(v => v.ToString(), v => PalUlid.Parse(v));
            e.Property(x => x.CorrelationId).HasConversion(v => v.HasValue ? v.Value.ToString() : default(string?), v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
            e.Property(x => x.CausationId).HasConversion(v => v.HasValue ? v.Value.ToString() : default(string?), v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
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
        catch (DbUpdateException ex)
        {
            // 🔴 P1 修复 (2026-07-28): 仅当异常来源于唯一约束冲突时才视为流并发冲突
            // （(StreamName,StreamVersion) 或 EventId 唯一索引）。
            // 其它 DbUpdateException（外键约束、CHECK 约束、字段过长、空值违约等）
            // 是真实的数据错误，必须原样向上传播，否则会掩盖真实错误并误导重试策略。
            if (!IsUniqueConstraintViolation(ex))
                throw;

            DetachAddedEvents();
            // ITM-126 修复：PG 显式事务内 SaveChanges 抛 23505 后事务进入 aborted 状态，
            // 同事务重查会抛 25P02 并替换原始 DbUpdateException——重查失败时放弃分类，
            // 走外层 throw 原样上抛原始冲突异常（对齐 DapperEventLog/PalOrmEventLog 姊妹实现）
            long? currentVersion = null;
            var requerySucceeded = false;
            try
            {
                currentVersion = await GetActualStreamVersionAsync(streamName, cancellationToken).ConfigureAwait(false);
                requerySucceeded = true;
            }
            catch (DbException)
            {
                // PG aborted transaction（25P02）等重查失败——保留原始 DbUpdateException 语义
            }
            // 验证轮返工（V-R2-1）：重查失败必须 `throw;` 保留原始 DbUpdateException——
            // 初版实现重查失败仍抛 EventStreamConcurrencyException(-1)，与姊妹实现
            // （DapperEventLog/PalOrmEventLog 重查失败走外层 throw）不一致，重试契约依旧失真
            if (!requerySucceeded)
                throw;
            // P2 修复（EventId 冲突误译）：版本仍满足期望说明不是流版本冲突
            // 而是 EventId 唯一索引撞——重复事件 ID 是数据错误，原样上抛（转并发异常会让
            // 盲目重试的调用方无限循环）
            if (expectedVersion.Matches(currentVersion!.Value))
                throw;
            throw new EventStreamConcurrencyException(streamName, expectedVersion, currentVersion.Value);
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

    /// <summary>
    /// 判定 <see cref="DbUpdateException"/> 内部异常是否为唯一约束冲突。<br/>
    /// 覆盖支持的主要 provider：
    /// <list type="bullet">
    ///   <item>PostgreSQL: <c>PostgresException.SqlState == "23505"</c>（unique_violation）</item>
    ///   <item>MySQL: <c>MySqlException.Number == 1062</c>（ER_DUP_ENTRY，兼容 1586）</item>
    ///   <item>SQL Server: <c>SqlException.Number == 2601 / 2627</c>（unique index / constraint）</item>
    ///   <item>SQLite: 异常消息包含 <c>"UNIQUE constraint"</c></item>
    /// </list>
    /// 此处不直接引用任何 provider 包，避免 EventLog.EFCore 对具体 provider 的硬依赖；
    /// 而是通过反射鸭子类型读取属性，保证可移植性。
    /// </summary>
    /// <param name="exception">EF Core 抛出的 DbUpdateException。</param>
    /// <returns>内部异常是唯一约束冲突时返回 true；否则 false。</returns>
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
            // P2 修复（二十一轮）：补 SqliteException 类型限定——裸消息匹配会把文案恰好含该词组的
            // 非唯一约束异常误判（在 AppendAsync 主路径被误转 EventStreamConcurrencyException 诱导重试）。
            // 镜像 InboxDbContext 十七轮修复（PD17 姊妹同步）；局限声明中的字符串匹配风险由此收窄。
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
}
