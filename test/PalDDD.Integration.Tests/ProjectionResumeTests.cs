using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Dapper;
using PalDDD.Projections;
using PalDDD.Testing;
using System.Data.Common;
using System.Globalization;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔁 R6/P0-3（ADR-028，2026-10-14 性能实施轮）增量回放续读锁定测试
// ═══════════════════════════════════════════════════════════════
// 三组锁定：
// A. InMemory 续读三态语义（洞位 / max+1 / null / 非数值回退）。
// B. Rebuilder 续读集成——store+源双能力齐备时从洞位下推读取（OfferedCount 只含增量；
//    红证明：注释 Rebuilder 续读分支后 OfferedCount=9 判红，恢复后 4 绿——变异探针在案）。
// C. Dapper/EF SQLite 真库续读 SQL 正确性（方言 CAST 聚合）。
// ═══════════════════════════════════════════════════════════════

public sealed class InMemoryResumePositionTests
{
    private static async Task MarkAsync(
        InMemoryProjectionCheckpointStore store, string position, bool completed)
    {
        var checkpoint = await store.TryStartAsync(
            "proj", "orders", position, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None);
        if (checkpoint is null)
            throw new InvalidOperationException($"TryStart returned null for {position} (seed path)");
        if (completed)
            await store.MarkCompletedAsync(checkpoint, DateTimeOffset.UtcNow, CancellationToken.None);
        else
            await store.MarkFailedAsync(checkpoint, "seed failure", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task EmptyStore_ReturnsNull()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var resume = await store.GetResumePositionAsync("proj", "orders", CancellationToken.None);
        await Assert.That(resume).IsNull();
    }

    [Test]
    public async Task FailedHole_ReturnsFirstIncompletePosition()
    {
        var store = new InMemoryProjectionCheckpointStore();
        for (var i = 0; i <= 4; i++)
            await MarkAsync(store, i.ToString(), completed: true);
        await MarkAsync(store, "5", completed: false);
        await MarkAsync(store, "6", completed: true);

        var resume = await store.GetResumePositionAsync("proj", "orders", CancellationToken.None);
        await Assert.That(resume).IsEqualTo("5");
    }

    [Test]
    public async Task AllCompleted_ReturnsMaxPlusOne()
    {
        var store = new InMemoryProjectionCheckpointStore();
        for (var i = 0; i <= 4; i++)
            await MarkAsync(store, i.ToString(), completed: true);

        var resume = await store.GetResumePositionAsync("proj", "orders", CancellationToken.None);
        await Assert.That(resume).IsEqualTo("5");
    }

    [Test]
    public async Task NonNumericPosition_ReturnsNullFallback()
    {
        var store = new InMemoryProjectionCheckpointStore();
        await MarkAsync(store, "0", completed: true);
        await MarkAsync(store, "not-a-number", completed: true);

        var resume = await store.GetResumePositionAsync("proj", "orders", CancellationToken.None);
        await Assert.That(resume).IsNull();
    }

    [Test]
    public async Task OtherProjectionOrSource_Isolated()
    {
        var store = new InMemoryProjectionCheckpointStore();
        await MarkAsync(store, "0", completed: true);

        var other = await store.GetResumePositionAsync("proj", "other-source", CancellationToken.None);
        var missing = await store.GetResumePositionAsync("other-proj", "orders", CancellationToken.None);
        await Assert.That(other).IsNull();
        await Assert.That(missing).IsNull();
    }
}

public sealed class RebuilderResumeTests
{
    [Test]
    public async Task ReplayAsync_ResumesFromFirstIncompleteHole()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingResumeHandler();
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store) { CheckpointBatchSize = 1 };
        // 检查点前置状态：0..4 完成、5 失败、6 完成；事件流 0..8
        for (var i = 0; i <= 4; i++)
            await SeedAsync(store, i.ToString(), completed: true);
        await SeedAsync(store, "5", completed: false);
        await SeedAsync(store, "6", completed: true);

        List<ReplayEvent<ResumeEvent>> events = [];
        for (var i = 0; i <= 8; i++)
            events.Add(new ReplayEvent<ResumeEvent>("orders", i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i), new ResumeEvent()));
        var source = new RecordingResumableSource(events);
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor);

        var processed = await rebuilder.ReplayAsync(CancellationToken.None);

        // 续读核心断言：源从洞位 5 起读（OfferedCount=4：5/6/7/8——旧全量形态 9 判红）；
        // 处理计数：5 重试成功 + 7/8 新位置 = 3（6 已完成被 TryStart 跳过）
        await Assert.That(source.RequestedFromPosition).IsEqualTo(5);
        await Assert.That(source.OfferedCount).IsEqualTo(4);
        await Assert.That(processed).IsEqualTo(3);
    }

    [Test]
    public async Task ReplayAsync_EmptyCheckpoints_ReadsFromStart()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingResumeHandler();
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store);
        List<ReplayEvent<ResumeEvent>> events = [new("orders", "0", DateTimeOffset.UnixEpoch, new ResumeEvent())];
        var source = new RecordingResumableSource(events);
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor);

        var processed = await rebuilder.ReplayAsync(CancellationToken.None);

        // 无检查点（首跑）→ 续读 null → 全量（fromPosition 0，OfferedCount=全部）
        await Assert.That(source.OfferedCount).IsEqualTo(1);
        await Assert.That(processed).IsEqualTo(1);
    }

    [Test]
    public async Task ReplayDetailedAsync_GroupHeldByAnotherWorker_ReportsStoppedForLease()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingResumeHandler();
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store) { CheckpointBatchSize = 2 };
        // 组首位置被另一个 worker 持有（Processing 未完成）：本轮回放必须提前停止，
        // 且通过 ReplayDetailedAsync.StoppedForLease 让调用方区分「追平」与「租约冲突」。
        var held = await store.TryStartAsync(
            handler.ProjectionName, "orders", "0", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);
        await Assert.That(held).IsNotNull();

        List<ReplayEvent<ResumeEvent>> events = [];
        for (var i = 0; i <= 3; i++)
            events.Add(new ReplayEvent<ResumeEvent>("orders", i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i), new ResumeEvent()));
        var source = new RecordingResumableSource(events);
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor);

        var result = await rebuilder.ReplayDetailedAsync(CancellationToken.None);

        await Assert.That(result.Processed).IsEqualTo(0);
        await Assert.That(result.StoppedForLease).IsTrue();
    }

    [Test]
    public async Task ReplayAsync_BatchHandler_ReceivesWholeGroupsOnce()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new BatchCountingResumeHandler();
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store) { CheckpointBatchSize = 4 };
        List<ReplayEvent<ResumeEvent>> events = [];
        for (var i = 0; i <= 9; i++)
            events.Add(new ReplayEvent<ResumeEvent>("orders", i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i), new ResumeEvent()));
        var source = new RecordingResumableSource(events);
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor);

        var processed = await rebuilder.ReplayAsync(CancellationToken.None);

        await Assert.That(processed).IsEqualTo(10);
        // 实现批量能力后逐条入口不再被调用：三组 4/4/2 各一次。
        await Assert.That(handler.ProjectAsyncCalls).IsEqualTo(0);
        await Assert.That(string.Join(",", handler.BatchSizes)).IsEqualTo("4,4,2");
        await Assert.That(handler.ItemCount).IsEqualTo(10);
    }

    [Test]
    public async Task ReplayAsync_BatchedCheckpoints_ProcessesAllAndLeavesHighWater()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingResumeHandler();
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store) { CheckpointBatchSize = 4 };
        List<ReplayEvent<ResumeEvent>> events = [];
        for (var i = 0; i <= 9; i++)
            events.Add(new ReplayEvent<ResumeEvent>("orders", i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i), new ResumeEvent()));
        var source = new RecordingResumableSource(events);
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor);

        var processed = await rebuilder.ReplayAsync(CancellationToken.None);

        await Assert.That(processed).IsEqualTo(10);
        await Assert.That(handler.Count).IsEqualTo(10);
        // 每组写组首锚点（0/4/8）与组末位置（3/7/9），全部 Completed；max+1 = 10，
        // 组内中间位置无检查点行。
        await Assert.That(await store.GetResumePositionAsync("order-summary", "orders", CancellationToken.None))
            .IsEqualTo("10");
    }

    [Test]
    public async Task ReplayAsync_TimeBoundFlush_CommitsPartialBatch()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingResumeHandler();
        // 默认批量 100：本测试靠 1 秒时间界提前提交，证明慢回放不会攒满批。
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store);
        List<ReplayEvent<ResumeEvent>> events = [];
        for (var i = 0; i <= 9; i++)
            events.Add(new ReplayEvent<ResumeEvent>("orders", i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i), new ResumeEvent()));
        var source = new TimeAdvancingSource(events, time, TimeSpan.FromSeconds(1));
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor)
        {
            TimeProvider = time,
            CheckpointFlushInterval = TimeSpan.FromSeconds(1),
        };

        var processed = await rebuilder.ReplayAsync(CancellationToken.None);

        await Assert.That(processor.CheckpointBatchSize).IsEqualTo(100);
        await Assert.That(processed).IsEqualTo(10);
        await Assert.That(handler.Count).IsEqualTo(10);
        // 两项各推进 1s：第二项到达时距组首满 1s，触发时间界提交位置 1。
        var partial = await store.GetAsync("order-summary", "orders", "1", CancellationToken.None);
        await Assert.That(partial!.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
    }

    [Test]
    public async Task ReplayAsync_BatchFailure_ResumesFromGroupStartNotAfterGroup()
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new FailingResumeHandler(failAt: "5");
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store) { CheckpointBatchSize = 4 };
        List<ReplayEvent<ResumeEvent>> events = [];
        for (var i = 0; i <= 9; i++)
            events.Add(new ReplayEvent<ResumeEvent>("orders", i.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(i), new ResumeEvent()));
        var source = new RecordingResumableSource(events);
        var rebuilder = new ProjectionRebuilder<ResumeEvent>(
            handler.ProjectionName, "orders", source, store, processor);

        await Assert.That(async () => await rebuilder.ReplayAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        // 第一批 0..3 完成；第二批 4..7 在 5 失败。组首 4 必须标 Failed，
        // 否则续读会从组末 7 之后开始，跳过 4/5/6。
        await Assert.That(await store.GetResumePositionAsync("order-summary", "orders", CancellationToken.None))
            .IsEqualTo("4");
    }

    private static async Task SeedAsync(InMemoryProjectionCheckpointStore store, string position, bool completed)
    {
        var checkpoint = await store.TryStartAsync(
            "order-summary", "orders", position, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None)
            ?? throw new InvalidOperationException($"seed TryStart null for {position}");
        if (completed)
            await store.MarkCompletedAsync(checkpoint, DateTimeOffset.UtcNow, CancellationToken.None);
        else
            await store.MarkFailedAsync(checkpoint, "seed failure", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    // ─── stubs ─────────────────────────────────────────────────

    private sealed record ResumeEvent;

    private sealed class CountingResumeHandler : IProjectionHandler<ResumeEvent>
    {
        public int Count;

        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(ResumeEvent message, ProjectionContext context, CancellationToken ct = default)
        {
            Count++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>批量能力探针：记录每批大小与总项数，逐条入口只计不应被调用。</summary>
    private sealed class BatchCountingResumeHandler : IProjectionBatchHandler<ResumeEvent>
    {
        public List<int> BatchSizes { get; } = [];
        public int ItemCount { get; private set; }
        public int ProjectAsyncCalls { get; private set; }

        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(ResumeEvent message, ProjectionContext context, CancellationToken ct = default)
        {
            ProjectAsyncCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ProjectBatchAsync(
            IReadOnlyList<(ResumeEvent Message, ProjectionContext Context)> batch,
            CancellationToken ct = default)
        {
            BatchSizes.Add(batch.Count);
            ItemCount += batch.Count;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingResumeHandler(string failAt) : IProjectionHandler<ResumeEvent>
    {
        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(ResumeEvent message, ProjectionContext context, CancellationToken ct = default)
            => context.Position == failAt
                ? throw new InvalidOperationException("simulated projection failure")
                : ValueTask.CompletedTask;
    }

    /// <summary>每次产出前推进 FakeTimeProvider，驱动回放的时间界冲刷。</summary>
    private sealed class TimeAdvancingSource(
        IReadOnlyList<ReplayEvent<ResumeEvent>> events,
        FakeTimeProvider time,
        TimeSpan step) : IEventReplaySource<ResumeEvent>
    {
        public async IAsyncEnumerable<ReplayEvent<ResumeEvent>> ReadAsync(
            string sourceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var @event in events)
            {
                ct.ThrowIfCancellationRequested();
                if (@event.SourceName != sourceName)
                    continue;
                time.Advance(step);
                yield return @event;
                await Task.Yield();
            }
        }
    }

    /// <summary>续读可观测桩——记录请求的 fromPosition 与实际产出事件数（OfferedCount）。</summary>
    private sealed class RecordingResumableSource(IReadOnlyList<ReplayEvent<ResumeEvent>> events)
        : IResumableEventReplaySource<ResumeEvent>
    {
        public long? RequestedFromPosition;
        public int OfferedCount;

        public async IAsyncEnumerable<ReplayEvent<ResumeEvent>> ReadAsync(
            string sourceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var @event in events)
            {
                ct.ThrowIfCancellationRequested();
                if (@event.SourceName != sourceName)
                    continue;
                OfferedCount++;
                yield return @event;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<ReplayEvent<ResumeEvent>> ReadAsync(
            string sourceName,
            long fromPosition,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            RequestedFromPosition = fromPosition;
            foreach (var @event in events)
            {
                ct.ThrowIfCancellationRequested();
                if (@event.SourceName != sourceName)
                    continue;
                if (!long.TryParse(@event.Position, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var position)
                    || position < fromPosition)
                    continue;
                OfferedCount++;
                yield return @event;
                await Task.Yield();
            }
        }
    }
}

/// <summary>R12 C-02：EF InMemory 分支的续租（Entry.CurrentValue + SaveChanges）。</summary>
public sealed class ProjectionLeaseRenewalEfInMemoryTests
{
    [Test]
    public async Task EfCore_InMemory_RenewLease_ExtendsAndBlocksTakeover(CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<InMemoryCheckpointDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;
        await using var db = new InMemoryCheckpointDbContext(options);
        var t0 = DateTimeOffset.UtcNow;

        var checkpoint = await db.TryStartAsync(
            "p", "s", "0", t0, TimeSpan.FromSeconds(1), cancellationToken);
        var renewed = await db.RenewLeaseAsync(
            checkpoint!, t0.AddMilliseconds(800), TimeSpan.FromSeconds(3), cancellationToken);
        var takeover = await db.TryStartAsync(
            "p", "s", "0", t0.AddMilliseconds(1500), TimeSpan.FromSeconds(1), cancellationToken);

        await Assert.That(renewed).IsTrue();
        await Assert.That(takeover).IsNull();
    }

    private sealed class InMemoryCheckpointDbContext(DbContextOptions<InMemoryCheckpointDbContext> options)
        : ProjectionCheckpointDbContext(options);
}

/// <summary>R12 C-03：EF 检查点续读的异常分类——只有 CAST 失败才回退全量。</summary>
public sealed class ProjectionResumeFailureClassificationTests
{
    [Test]
    public async Task IsPositionCastFailure_OnlyPgCastSqlStates()
    {
        await Assert.That(ProjectionCheckpointDbContext.IsPositionCastFailure(new FakeDbException("22P02"))).IsTrue();
        await Assert.That(ProjectionCheckpointDbContext.IsPositionCastFailure(new FakeDbException("22007"))).IsTrue();
        // 连接中断/超时/死锁等基础设施故障必须上抛，不得被当成脏数据回退全量。
        await Assert.That(ProjectionCheckpointDbContext.IsPositionCastFailure(new FakeDbException("08006"))).IsFalse();
        await Assert.That(ProjectionCheckpointDbContext.IsPositionCastFailure(new FakeDbException("40001"))).IsFalse();
        await Assert.That(ProjectionCheckpointDbContext.IsPositionCastFailure(new FakeDbException(null))).IsFalse();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1032:Implement standard exception constructors",
        Justification = "测试桩：只承载 SqlState，不参与序列化/包装。")]
    private sealed class FakeDbException(string? sqlState) : DbException("fake provider failure")
    {
        public override string? SqlState => sqlState;
        public override int ErrorCode => 0;
    }
}

/// <summary>续读查询的 Dapper/EF SQLite 真库验证（R6/P0-3，方言 CAST 聚合 SQL 正确性）。</summary>
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class ProjectionResumeSqliteTests
{
    private const string CheckpointDdl =
        "CREATE TABLE projection_checkpoints (" +
        "projection_name TEXT NOT NULL, source_name TEXT NOT NULL, position TEXT NOT NULL, " +
        "status INTEGER NOT NULL, updated_at TEXT NOT NULL, lease_until TEXT NOT NULL, " +
        "revision INTEGER NOT NULL, error TEXT, " +
        "PRIMARY KEY (projection_name, source_name, position))";

    private SqliteConnection _conn = null!;
    private static bool s_previousUnderscoreSetting;

    [Before(Class)]
    public static void ClassInitialize()
    {
        s_previousUnderscoreSetting = global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores;
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    [After(Class)]
    public static void ClassCleanup()
        => global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = s_previousUnderscoreSetting;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync(cancellationToken);
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = CheckpointDdl;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync() => await _conn.DisposeAsync();

    [Test]
    public async Task Dapper_HoleAndAllCompleted_AndEmpty_ThreeStates(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, DapperDbType.Sqlite);

        // 空表 → null
        await Assert.That(await store.GetResumePositionAsync("p", "s", cancellationToken)).IsNull();

        // 0..2 完成、3 失败 → "3"
        for (var i = 0; i <= 2; i++)
            await CompleteAsync(store, i.ToString(), cancellationToken);
        await FailAsync(store, "3", cancellationToken);
        await Assert.That(await store.GetResumePositionAsync("p", "s", cancellationToken)).IsEqualTo("3");

        // 3 补完成后全完成 → max+1 = "4"
        var retry = await store.TryStartAsync("p", "s", "3", DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.FromSeconds(30), cancellationToken);
        await store.MarkCompletedAsync(retry!, DateTimeOffset.UtcNow, cancellationToken);
        await Assert.That(await store.GetResumePositionAsync("p", "s", cancellationToken)).IsEqualTo("4");
    }

    [Test]
    public async Task Dapper_NonNumericPosition_ReturnsNullFallback(CancellationToken cancellationToken)
    {
        // R11 F4 回归锁：SQLite CAST('99abc' AS INTEGER)=99，原实现返回 "100" 并跳过
        // 6..99；接口契约要求非数值 Position 回退全量（null）。
        var store = new DapperProjectionCheckpointStore(_conn, DapperDbType.Sqlite);
        await CompleteAsync(store, "5", cancellationToken);
        await CompleteAsync(store, "99abc", cancellationToken);

        await Assert.That(await store.GetResumePositionAsync("p", "s", cancellationToken)).IsNull();
    }

    [Test]
    public async Task EfCore_NonNumericPosition_ReturnsNullFallback(CancellationToken cancellationToken)
    {
        // R11 F4：EF SQLite 走客户端聚合兜底——非数值 Position 必须返回 null。
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestResumeCheckpointDbContext>()
            .UseSqlite(conn)
            .Options;
        await using (var boot = new TestResumeCheckpointDbContext(options))
            await boot.Database.EnsureCreatedAsync(cancellationToken);
        await using var db = new TestResumeCheckpointDbContext(options);

        await CompleteAsync(db, "5", cancellationToken);
        await CompleteAsync(db, "99abc", cancellationToken);

        await Assert.That(await db.GetResumePositionAsync("p", "s", cancellationToken)).IsNull();
    }

    [Test]
    public async Task EfCore_HoleAndAllCompleted_AndEmpty_ThreeStates(CancellationToken cancellationToken)
    {
        // 独立 :memory: 连接——EF 模型默认表名 ProjectionCheckpoints 与 Dapper DDL 的
        // snake_case 表不同（栈内 schema 刻意分离），EnsureCreated 自建
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestResumeCheckpointDbContext>()
            .UseSqlite(conn)
            .Options;
        await using (var boot = new TestResumeCheckpointDbContext(options))
            await boot.Database.EnsureCreatedAsync(cancellationToken);
        await using var db = new TestResumeCheckpointDbContext(options);

        // 空表 → null
        await Assert.That(await db.GetResumePositionAsync("p", "s", cancellationToken)).IsNull();

        // 0..2 完成、3 失败 → "3"
        for (var i = 0; i <= 2; i++)
            await CompleteAsync(db, i.ToString(), cancellationToken);
        await FailAsync(db, "3", cancellationToken);
        await Assert.That(await db.GetResumePositionAsync("p", "s", cancellationToken)).IsEqualTo("3");

        // 3 补完成后全完成 → "4"
        var retry = await db.TryStartAsync("p", "s", "3", DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.FromSeconds(30), cancellationToken);
        await db.MarkCompletedAsync(retry!, DateTimeOffset.UtcNow, cancellationToken);
        await Assert.That(await db.GetResumePositionAsync("p", "s", cancellationToken)).IsEqualTo("4");
    }

    [Test]
    public async Task Dapper_RenewLease_ExtendsAndBlocksTakeover(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, DapperDbType.Sqlite);
        var t0 = DateTimeOffset.UtcNow;
        var checkpoint = await store.TryStartAsync(
            "p", "s", "0", t0, TimeSpan.FromSeconds(1), cancellationToken);

        var renewed = await store.RenewLeaseAsync(
            checkpoint!, t0.AddMilliseconds(800), TimeSpan.FromSeconds(3), cancellationToken);
        var takeover = await store.TryStartAsync(
            "p", "s", "0", t0.AddMilliseconds(1500), TimeSpan.FromSeconds(1), cancellationToken);

        await Assert.That(renewed).IsTrue();
        await Assert.That(takeover).IsNull();
    }

    [Test]
    public async Task EfCore_RenewLease_ExtendsAndBlocksTakeover(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestResumeCheckpointDbContext>()
            .UseSqlite(conn)
            .Options;
        await using (var boot = new TestResumeCheckpointDbContext(options))
            await boot.Database.EnsureCreatedAsync(cancellationToken);
        await using var db = new TestResumeCheckpointDbContext(options);

        var t0 = DateTimeOffset.UtcNow;
        var checkpoint = await db.TryStartAsync(
            "p", "s", "0", t0, TimeSpan.FromSeconds(1), cancellationToken);

        var renewed = await db.RenewLeaseAsync(
            checkpoint!, t0.AddMilliseconds(800), TimeSpan.FromSeconds(3), cancellationToken);
        var takeover = await db.TryStartAsync(
            "p", "s", "0", t0.AddMilliseconds(1500), TimeSpan.FromSeconds(1), cancellationToken);

        await Assert.That(renewed).IsTrue();
        await Assert.That(takeover).IsNull();
    }

    private static async Task CompleteAsync(IProjectionCheckpointStore store, string position, CancellationToken ct)
    {
        var checkpoint = await store.TryStartAsync("p", "s", position, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), ct)
            ?? throw new InvalidOperationException($"seed TryStart null for {position}");
        await store.MarkCompletedAsync(checkpoint, DateTimeOffset.UtcNow, ct);
    }

    private static async Task FailAsync(IProjectionCheckpointStore store, string position, CancellationToken ct)
    {
        var checkpoint = await store.TryStartAsync("p", "s", position, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), ct)
            ?? throw new InvalidOperationException($"seed TryStart null for {position}");
        await store.MarkFailedAsync(checkpoint, "seed failure", DateTimeOffset.UtcNow, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // ⚡ B7(a)（EPSL 地毯审计 2026-10-16 B 批 #7）：新位路径直插优先
    // 旧形态 3 语句（跟踪 SELECT + INSERT + 调用方终态 UPDATE）；新形态 2（INSERT + UPDATE）。
    // SQLite 真库 + DbCommandInterceptor 命令计数（EnsureCreated 的 DDL 经 Reset 隔离）。
    // 红证明：还原预 SELECT 形态后 SELECT=1 判红（镜像 Inbox A9 测试先例）。
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task TryStartAsync_NewPosition_OnRelationalProvider_InsertsWithoutPreselect(CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var counter = new ProjectionStatementCounter();
        var options = new DbContextOptionsBuilder<TestResumeCheckpointDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;
        await using (var init = new TestResumeCheckpointDbContext(options))
            await init.Database.EnsureCreatedAsync(cancellationToken);
        counter.Reset();

        await using var db = new TestResumeCheckpointDbContext(options);
        var store = (IProjectionCheckpointStore)db;
        var checkpoint = await store.TryStartAsync(
            "p", "s", "0",
            DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        // 行为断言直取属性（checkpoint 为 null 时 NRE 即红，不用 IsNotNull 弱断言——
        // AssertionStrengthGateTests 棘轮口径；租约已创建由后续语句计数与位置语义锁定）
        await Assert.That(checkpoint!.Position).IsEqualTo("0");
        // 新位路径：0 SELECT + 1 INSERT（旧形态：SELECT miss 1 + INSERT 1）
        await Assert.That(counter.SelectCount).IsEqualTo(0);
        await Assert.That(counter.InsertCount).IsEqualTo(1);
    }

    /// <summary>命令文本计数器（B7(a)）：SELECT/INSERT 语句数——Executing 拦截点登记
    /// （镜像 InboxEfCoreTests.InboxStatementCounter 形态，Volatile 字段防并行套件污染）。</summary>
    private sealed class ProjectionStatementCounter : DbCommandInterceptor
    {
        private int _select;
        private int _insert;

        public int SelectCount => Volatile.Read(ref _select);

        public int InsertCount => Volatile.Read(ref _insert);

        public void Reset()
        {
            Volatile.Write(ref _select, 0);
            Volatile.Write(ref _insert, 0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            var text = command.CommandText;
            if (text.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _select);
            else if (text.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _insert);
        }
    }

    private sealed class TestResumeCheckpointDbContext(DbContextOptions<TestResumeCheckpointDbContext> options)
        : ProjectionCheckpointDbContext(options);
}
