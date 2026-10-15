using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Testing;
using System.Data.Common;
using System.Text;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.EventLog.Tests;

// ═══════════════════════════════════════════════════════════════
// 📊 S1（A3+A4，2026-10-15 性能实施轮）：EF EventLog append 路径语句/事务计数锁定
// ①A3：单事件 append 不创建显式事务——EF Core 7+ 默认
//   AutoTransactionBehavior.WhenNeeded 对单语句不开显式事务（微软官方文档实证省
//   2 往返；收益在 PG/MySQL/SQL Server 等远程库成立，本地 SQLite 的 BEGIN/COMMIT
//   不经 DbCommand 拦截点，故以 DbTransactionInterceptor.TransactionStarted 作
//   本地可观测判别信号）；旧码显式 BeginTransactionAsync + CommitAsync 触发一次。
// ②A4：NoStream 期望无 MAX 预查——空流约定版本即 -1，插入版本 1 撞唯一索引即
//   证明流已存在（版本自 1 连续分配是不变量）；旧码 1 条 MAX 预查。
// 对照腿：Exact 期望仍走 MAX（版本验证 + 防版本间隙），证明跳过只作用于 NoStream。
// 红证明：还原旧形态后①②分别判红（事务创建 1→0 / MAX 1→0），对照腿不变绿。
// ═══════════════════════════════════════════════════════════════

// [NotInParallel]：SQLite 共享缓存内存库 + 计数器为进程级共享面（对齐
// EventLogEfCoreTests 类级范式——同程序集并行执行会互相污染）。
[TUnit.Core.NotInParallel]
public sealed class EventLogStatementCountTests
{
    [Test]
    public async Task AppendAsync_SingleEvent_OnRelationalProvider_NoBeginCommitStatements()
    {
        var statements = new StatementCounter();
        var transactions = new TransactionCounter();
        await using var db = CreateDbContext(statements, transactions);

        await db.AppendAsync(
            "ordering-stmt-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "p")]);

        // 旧码：显式 BeginTransactionAsync 触发一次事务创建（新码 0——单语句自身原子，
        // EF7+ 不开显式事务）。SQLite 的命令级 BEGIN/COMMIT 不经拦截点，以事务创建计数判别。
        await Assert.That(transactions.StartedCount).IsEqualTo(0);
    }

    [Test]
    public async Task AppendAsync_NoStream_OnRelationalProvider_SkipsMaxPrecheck()
    {
        var statements = new StatementCounter();
        var transactions = new TransactionCounter();
        await using var db = CreateDbContext(statements, transactions);

        await db.AppendAsync(
            "ordering-stmt-2",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "p")]);

        // 旧码：MAX 预查 1 条（新码 0——空流约定版本 -1，唯一索引兜底）
        await Assert.That(statements.MaxAggregateCount).IsEqualTo(0);
    }

    [Test]
    public async Task AppendAsync_ExactVersion_OnRelationalProvider_StillQueriesMax()
    {
        // 对照腿：Exact 期望仍走 MAX 预查（版本验证 + 防陈旧低期望造成版本间隙），
        // 证明 A4 的跳过只作用于 NoStream 而非全局移除预查
        var statements = new StatementCounter();
        var transactions = new TransactionCounter();
        await using var db = CreateDbContext(statements, transactions);

        await db.AppendAsync(
            "ordering-stmt-3",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "p1")]);
        statements.Reset();

        await db.AppendAsync(
            "ordering-stmt-3",
            ExpectedStreamVersion.Exact(0),
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "p2")]);

        await Assert.That(statements.MaxAggregateCount).IsEqualTo(1);
    }

    private static StatementCountDbContext CreateDbContext(
        StatementCounter statements, TransactionCounter transactions)
    {
        var options = new DbContextOptionsBuilder<StatementCountDbContext>()
            .UseSqlite($"DataSource=file:mem{Guid.NewGuid():N}?mode=memory&cache=shared")
            .AddInterceptors(statements, transactions)
            .Options;
        var db = new StatementCountDbContext(options);
        db.Database.EnsureCreatedAsync().GetAwaiter().GetResult();
        statements.Reset(); // EnsureCreated 的 DDL 不入计数
        transactions.Reset();
        return db;
    }

    private static EventData CreateEvent(PalUlid eventId, string name, string payload)
        => new(
            eventId,
            name,
            schemaVersion: 1,
            "application/json",
            Encoding.UTF8.GetBytes(payload),
            metadata: "metadata"u8.ToArray(),
            new EventAuditMetadata(
                "user-123",
                "submit order",
                PalUlid.New(Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64")),
                PalUlid.New(Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7")),
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                "tenant=ordering"));

    private sealed class StatementCountDbContext(DbContextOptions<StatementCountDbContext> options)
        : EventLogDbContext(options);

    /// <summary>命令文本计数器：MAX( 聚合跨 Reader/NonQuery 全部命令拦截点的出现次数
    ///（镜像 SagaStateEfCoreLeasePushdownTests 的 SqlStatementCounter 形态）。</summary>
    private sealed class StatementCounter : DbCommandInterceptor
    {
        private int _maxAggregate;

        public int MaxAggregateCount => Volatile.Read(ref _maxAggregate);

        public void Reset() => Volatile.Write(ref _maxAggregate, 0);

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
            if (text.Contains("MAX(", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _maxAggregate);
            }
        }
    }

    /// <summary>事务创建计数器——SQLite 上显式 BEGIN/COMMIT 不经 DbCommand 拦截点
    ///（Microsoft.Data.Sqlite 在连接内本地执行），命令级计数无法判别，事务创建回调可以：
    /// 显式 BeginTransactionAsync 触发一次，EF7+ 单语句不开显式事务时不触发。</summary>
    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        private int _started;

        public int StartedCount => Volatile.Read(ref _started);

        public void Reset() => Volatile.Write(ref _started, 0);

        public override DbTransaction TransactionStarted(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            Interlocked.Increment(ref _started);
            return base.TransactionStarted(connection, eventData, result);
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }
    }
}
