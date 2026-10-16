using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;
using System.Data.Common;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔬 B4 探针（2026-10-15 性能实施轮 S1）：EF Outbox 栈零变更 SaveChanges 往返实测
// 问题：OutboxBatchProcessor.PersistSingleAsync 每条消息处理后调 SaveChangesAsync——
// EF 栈的 Mark*/ReleaseForRetry 走 ExecuteUpdate 直写 DB（ChangeTracker 无待存实体），
// 该 SaveChanges 是否仍产生 DB 往返（开连接/BEGIN/COMMIT）？本探针以命令计数实证，
// 为 B4（能力接口消空转）提供判定依据：零命令 = 确认空转（收益仅方法调用级，
// 优先级维持低）；非零 = 每消息一次隐藏往返，升级为 P0 级整改。
// ═══════════════════════════════════════════════════════════════

public sealed class OutboxPersistSingleProbeTests
{
    [Test]
    public async Task SaveChanges_AfterExecuteUpdateMark_IssuesNoDbCommands(CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<ProbeOutboxDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;

        await using (var init = new ProbeOutboxDbContext(options))
            await init.Database.EnsureCreatedAsync(cancellationToken);

        // 种 2 条（各自 SaveChanges——与本探针判定无关，计数在其后 Reset 隔离）
        for (var i = 0; i < 2; i++)
        {
            await using var seed = new ProbeOutboxDbContext(options);
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Id = PalUlid.New(),
                Type = "test.event",
                Payload = [],
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await seed.SaveChangesAsync(cancellationToken);
        }

        // 租约 + 批量标记（ExecuteUpdate 直写 DB——ChangeTracker 零待存实体，
        // 与 PersistSingleAsync 调用点的 store 状态同构）
        await using var db = new ProbeOutboxDbContext(options);
        var leased = await ((IPalOutboxStore)db).LeasePendingMessagesAsync(
            10, "worker-a", TimeSpan.FromMinutes(1), 10, cancellationToken);
        var affected = await db.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, cancellationToken);
        await Assert.That(affected).IsEqualTo(2);

        counter.Reset();
        var saved = await db.SaveChangesAsync(cancellationToken);

        // 探针判定：零变更 SaveChanges 零 DB 命令（EF 不开连接、不发 BEGIN/COMMIT）
        await Assert.That(saved).IsEqualTo(0);
        await Assert.That(counter.CommandCount).IsEqualTo(0);
    }

    private sealed class ProbeOutboxDbContext(DbContextOptions<ProbeOutboxDbContext> options)
        : SqliteOutboxDbContext(options);

    /// <summary>命令计数器（B4 探针量具）——跨 Reader/NonQuery 全部拦截点登记
    ///（镜像 SagaStateEfCoreLeasePushdownTests 的 SqlStatementCounter 形态）。</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int CommandCount => Volatile.Read(ref _count);

        public void Reset() => Volatile.Write(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
