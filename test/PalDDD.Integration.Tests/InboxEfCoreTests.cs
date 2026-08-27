namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Transactions;
using System.Data.Common;
using System.Globalization;

public sealed class InboxEfCoreTests
{
    /// <summary>v17 P1 回归：空白键契约（ITM-163 四姊妹中 EFCore 漏网）——
    /// consumerName/messageId 空白必须抛 ArgumentException（对齐 Dapper/InMemory/PalORM）。</summary>
    [Test]
    public async Task TryStartProcessingAsync_BlankKeys_ThrowsArgumentException(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;

        await Assert.That(async () => await store.TryStartProcessingAsync(
            "", "msg-1", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), cancellationToken))
            .Throws<ArgumentException>();
        await Assert.That(async () => await store.TryStartProcessingAsync(
            "consumer", "", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), cancellationToken))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task TryStartProcessingAsync_PersistsProcessingRecord(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);

        var record = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(record).IsNotNull();
        await Assert.That(record.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(record.Attempts).IsEqualTo(1);

        db.ChangeTracker.Clear();
        var loaded = await db.InboxMessages.SingleAsync(
            x => x.ConsumerName == "orders" && x.MessageId == "message-1",
            cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(loaded.ProcessingStartedAt).IsEqualTo(now);
    }

    [Test]
    public async Task TryStartProcessingAsync_ReturnsNullWhenProcessingLeaseIsStillActive(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        await using (var first = new TestInboxDbContext(options))
        {
            var firstStore = (IInboxStore)first;
            await firstStore.TryStartProcessingAsync(
                "orders",
                "message-1",
                now,
                TimeSpan.FromMinutes(5),
                cancellationToken);
        }

        await using var second = new TestInboxDbContext(options);
        var secondStore = (IInboxStore)second;
        var duplicate = await secondStore.TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(duplicate).IsNull();
    }

    [Test]
    public async Task TryStartProcessingAsync_ReusesFailedMessage(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        var record = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await store.MarkFailedAsync(record!, "handler failed", cancellationToken);
        db.ChangeTracker.Clear();

        var retry = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(retry).IsNotNull();
        await Assert.That(retry.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(retry.Attempts).IsEqualTo(2);
        await Assert.That(retry.LastError).IsNull();
    }

    [Test]
    public async Task MarkProcessedAsync_PreventsDuplicateProcessing(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        var record = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await store.MarkProcessedAsync(record!, now.AddSeconds(1), cancellationToken);
        db.ChangeTracker.Clear();

        var duplicate = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(10),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(duplicate).IsNull();
        var loaded = await db.InboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processed);
        await Assert.That(loaded.ProcessedAt).IsEqualTo(now.AddSeconds(1));
    }

    [Test]
    public async Task MarkFailedAsync_DoesNotOverwriteRecordCompletedByAnotherProcessor(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        InboxMessage staleRecord;

        await using (var first = new TestInboxDbContext(options))
        {
            staleRecord = (await ((IInboxStore)first).TryStartProcessingAsync(
                "orders",
                "message-1",
                now,
                TimeSpan.FromSeconds(5),
                cancellationToken))!;
        }

        await using (var second = new TestInboxDbContext(options))
        {
            var freshRecord = (await ((IInboxStore)second).TryStartProcessingAsync(
                "orders",
                "message-1",
                now.AddSeconds(6),
                TimeSpan.FromSeconds(5),
                cancellationToken))!;
            await ((IInboxStore)second).MarkProcessedAsync(
                freshRecord,
                now.AddSeconds(7),
                cancellationToken);
        }

        await using (var stale = new TestInboxDbContext(options))
        {
            await ((IInboxStore)stale).MarkFailedAsync(
                staleRecord,
                "stale handler failed",
                cancellationToken);
        }

        await using var reader = new TestInboxDbContext(options);
        var loaded = await reader.InboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processed);
        await Assert.That(loaded.LastError).IsNull();
    }

    [Test]
    public async Task TryStartProcessingAsync_PreemptsZombieRecordAfterTimeout(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var startedAt = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var first = new TestInboxDbContext(options))
        {
            var firstStore = (IInboxStore)first;
            var record = await firstStore.TryStartProcessingAsync(
                "orders",
                "message-1",
                startedAt,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            await Assert.That(record).IsNotNull();
        }

        await using var second = new TestInboxDbContext(options);
        var secondStore = (IInboxStore)second;
        var preempted = await secondStore.TryStartProcessingAsync(
            "orders",
            "message-1",
            startedAt.AddSeconds(31),
            TimeSpan.FromSeconds(30),
            cancellationToken);

        await Assert.That(preempted).IsNotNull();
        await Assert.That(preempted.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(preempted.Attempts).IsEqualTo(2);
        await Assert.That(preempted.ProcessingStartedAt).IsEqualTo(startedAt.AddSeconds(31));
    }

    [Test]
    public async Task TryStartProcessingAsync_Sqlite_RoundtripAndDuplicateReturnsNull(CancellationToken cancellationToken)
    {
        // ITM-254（F14/PD26）：InMemory 无真实索引——SQLite 内存库 EnsureCreated 建真 schema，
        // (ConsumerName,MessageId) 唯一索引真实在场，重复 TryStart 的幂等判定在关系型下验证
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var first = new TestInboxDbContext(options))
        {
            await first.Database.EnsureCreatedAsync(cancellationToken);
            var record = await ((IInboxStore)first).TryStartProcessingAsync(
                "orders",
                "message-1",
                now,
                TimeSpan.FromMinutes(5),
                cancellationToken);
            await Assert.That(record).IsNotNull();
        }

        await using var second = new TestInboxDbContext(options);
        var duplicate = await ((IInboxStore)second).TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(duplicate).IsNull();

        // 写读 roundtrip（新 context 读回 Processing 记录）
        var loaded = await second.InboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(loaded.Attempts).IsEqualTo(1);
        await Assert.That(loaded.ProcessingStartedAt).IsEqualTo(now);
    }

    [Test]
    public async Task AddDuplicateConsumerMessage_Sqlite_ViolatesUniqueIndex(CancellationToken cancellationToken)
    {
        // ITM-254（F14）：真实唯一索引存在性守护——同 (ConsumerName,MessageId) 直插第二行
        // 必须触发 UNIQUE 约束（TryStart 的幂等冲突回查路径依赖此索引真实在场）
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var seed = new TestInboxDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            await ((IInboxStore)seed).TryStartProcessingAsync(
                "orders",
                "message-1",
                now,
                TimeSpan.FromMinutes(5),
                cancellationToken);
        }

        await using var db = new TestInboxDbContext(options);
        db.InboxMessages.Add(new InboxMessage
        {
            ConsumerName = "orders",
            MessageId = "message-1",
            Status = InboxStatus.Processing,
            Attempts = 1,
            ReceivedAt = now,
            ProcessingStartedAt = now
        });

        await Assert.That(async () =>
            await db.SaveChangesAsync(cancellationToken)).Throws<DbUpdateException>();
    }

    [Test]
    public async Task TryStartProcessingAsync_UniqueConflictRequeryFails_PropagatesOriginalDbUpdateException(CancellationToken cancellationToken)
    {
        // v28 P3 回归（镜像 EventLogPositionReserverTests 的 mock 手法，补 Inbox 侧）：
        // 唯一冲突后回查失败（PG aborted 事务 25P02 形态——拦截器在 SaveChanges 冲突后使
        // 后续 Reader 命令抛 DbException）时必须 throw; 保留原始 DbUpdateException 语义，
        // 不得被回查异常替换/掩盖、误导上层重试策略
        var state = new RequeryFailureState();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<ThrowingSaveInboxDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new RequeryFailureInterceptor(state))
            .Options;

        await using var db = new ThrowingSaveInboxDbContext(options, state);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => ((IInboxStore)db).TryStartProcessingAsync(
                "orders",
                "message-1",
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(5),
                cancellationToken).AsTask());
        // 断言上抛的是原始冲突异常（内层为注入的唯一约束 SqliteException），而非回查注入的失败
        await Assert.That(exception!.InnerException!.Message).Contains("UNIQUE constraint");
    }

    [Test]
    public async Task TryStartProcessingAsync_UniqueConflictRequerySucceeds_ReturnsNullIdempotently(CancellationToken cancellationToken)
    {
        // v28 P3 回归姊妹：唯一冲突后回查成功且查无行（冲突行由并发消费者插入但其事务未提交，
        // 本 mock 空库等价于 MySQL REPEATABLE READ 快照不可见）→ 按"他人正在处理"返回 null
        // （幂等语义，调用方走重投递），不得抛 SingleAsync 的多行/零行异常
        var options = new DbContextOptionsBuilder<ThrowingSaveInboxDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;
        await using var db = new ThrowingSaveInboxDbContext(options, new RequeryFailureState());
        var store = (IInboxStore)db;

        var duplicate = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(duplicate).IsNull();
    }

    private static DbContextOptions<TestInboxDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestInboxDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    /// <summary>SQLite 内存库 options——需共享同一打开的 <see cref="SqliteConnection"/>（:memory: 库随连接存活）。</summary>
    private static DbContextOptions<TestInboxDbContext> CreateSqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<TestInboxDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestInboxDbContext(DbContextOptions<TestInboxDbContext> options)
        : InboxDbContext(options);

    /// <summary>回查失败注入状态——SaveChangesAsync 冲突抛出后置位，使后续 Reader 命令（回查）失败。</summary>
    private sealed class RequeryFailureState
    {
        public bool SaveThrew;
    }

    /// <summary>
    /// v28 回归注入器（镜像 EventLogPositionReserverTests.ThrowingSaveEventLogDbContext 手法）——
    /// SaveChangesAsync 恒抛 DbUpdateException（内层为 UNIQUE 约束 SqliteException，构造唯一冲突）。
    /// </summary>
    private sealed class ThrowingSaveInboxDbContext(
        DbContextOptions<ThrowingSaveInboxDbContext> options, RequeryFailureState state)
        : InboxDbContext(options)
    {
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            state.SaveThrew = true;
            throw new DbUpdateException(
                "save failed",
                new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: InboxMessages.ConsumerName, InboxMessages.MessageId'", 19));
        }
    }

    /// <summary>
    /// Save 冲突抛出后的 Reader 命令注入 DbException——模拟 PG aborted 事务上回查抛 25P02
    /// （首个查询在 SaveThrew 置位前执行，正常返回）。
    /// </summary>
    private sealed class RequeryFailureInterceptor(RequeryFailureState state) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => state.SaveThrew
                ? throw new SqliteException("SQLite Error 25: 'injected aborted-transaction requery failure'", 25)
                : ValueTask.FromResult(result);
    }
}
