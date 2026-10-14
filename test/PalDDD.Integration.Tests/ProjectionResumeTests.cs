using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.Projections;

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
        var processor = new ProjectionProcessor<ResumeEvent>(handler, store);
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

    private sealed class TestResumeCheckpointDbContext(DbContextOptions<TestResumeCheckpointDbContext> options)
        : ProjectionCheckpointDbContext(options);
}
