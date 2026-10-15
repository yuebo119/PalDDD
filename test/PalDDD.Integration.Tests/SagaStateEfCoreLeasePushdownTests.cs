namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Transactions;
using System.Data.Common;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

/// <summary>
/// R7（EPSL 地毯审计 2026-10-11 P0-4，2026-10-15 性能实施轮）：Saga EF 租约 SQLite
/// 单语句下推的回归网。语义不变量逐项锁定——单语句形态（语句计数，旧跟踪分页形态判红）、
/// 租约即换代（Version+1）、跟踪契约（补偿路径 <c>ISagaStateStore.SaveChangesAsync(state)</c>
/// 的前提）、活跃租约跳过 / 过期接管 / AWD 纳入、头部占满不饥饿（batchSize=1）。
/// </summary>
public sealed class SagaStateEfCoreLeasePushdownTests
{
    [Test]
    public async Task LeaseActiveSagasAsync_Sqlite_SingleUpdateStatement_AndTrackedReturnValue(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var counter = new SqlStatementCounter();
        var options = new DbContextOptionsBuilder<PushdownSagaDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;

        await using (var seed = new PushdownSagaDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(ct);
            for (var i = 0; i < 5; i++)
                seed.SagaStates.Add(CreateState($"state-{i}", FixedNow.AddSeconds(i)));
            await seed.SaveChangesAsync(ct);
        }

        await using var db = new PushdownSagaDbContext(options);
        counter.Reset();
        var leased = await db.LeaseActiveSagasAsync("owner-sql", TimeSpan.FromMinutes(2), 10, ct);

        await Assert.That(leased.Count).IsEqualTo(5);
        // R7 核心机械断言：整批租约恰 1 条 UPDATE 语句（集合式单语句）。
        // 旧跟踪分页形态为逐行 UPDATE（批 5 → ≥5 条 UPDATE 语句）——本断言在旧实现下红，
        // 是「单语句形态」的判别器（语句计数在多行 UPDATE 合批/分命令两形态下都 >1）。
        await Assert.That(counter.UpdateStatementCount).IsEqualTo(1);

        foreach (var state in leased)
        {
            await Assert.That(state.LeasedBy).IsEqualTo("owner-sql");
            await Assert.That(state.LeasedUntil).IsNotNull();
            await Assert.That(state.Version).IsEqualTo(1); // 租约即换代（SQL 内 Version+1，与 v53 P2 四栈契约对齐）
            // 跟踪契约：返回实体已被本上下文跟踪（非 Detached）——补偿路径
            // ISagaStateStore.SaveChangesAsync(state) 以 Detached→0 为前置契约（见基类显式实现）
            await Assert.That(db.Entry(state).State).IsNotEqualTo(EntityState.Detached);
        }

        // 补偿路径端到端：租约返回实体改终态后经接口保存可落库（返回 1 = 写入生效）
        var first = leased[0];
        first.Status = SagaStatus.Compensated;
        first.CurrentState = "CompensatedByOwner";
        var affected = await ((ISagaStateStore<PushdownSagaState>)db).SaveChangesAsync(first, ct);
        await Assert.That(affected).IsEqualTo(1);

        await using var verifier = new PushdownSagaDbContext(options);
        var compensatedCount = await verifier.SagaStates.CountAsync(
            static s => s.Status == SagaStatus.Compensated, ct);
        await Assert.That(compensatedCount).IsEqualTo(1);
    }

    [Test]
    public async Task LeaseActiveSagasAsync_Sqlite_SkipsLiveLease_TakesOverExpired_BumpsVersion(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<PushdownSagaDbContext>()
            .UseSqlite(connection).Options;

        var now = DateTimeOffset.UtcNow;
        PalUlid liveId, expiredId;
        await using (var seed = new PushdownSagaDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(ct);
            var live = CreateState("LiveLeased", now);
            live.LeasedBy = "other-owner";
            live.LeasedUntil = now.AddMinutes(5); // 他实例活跃租约——本 tick 不可租
            live.Version = 3;
            var expired = CreateState("ExpiredLeased", now.AddMilliseconds(1));
            expired.LeasedBy = "other-owner";
            expired.LeasedUntil = now.AddMinutes(-5); // 已过期——可接管
            expired.Version = 3;
            seed.SagaStates.AddRange(
                live,
                expired,
                CreateState("Unleased", now.AddMilliseconds(2)),
                CreateState("Awaiting", now.AddMilliseconds(3), status: SagaStatus.AwaitingHumanDecision),
                CreateState("Completed", now.AddMilliseconds(4), status: SagaStatus.Completed));
            await seed.SaveChangesAsync(ct);
            liveId = live.SagaId;
            expiredId = expired.SagaId;
        }

        await using var db = new PushdownSagaDbContext(options);
        var leased = await db.LeaseActiveSagasAsync("owner-takeover", TimeSpan.FromMinutes(2), 10, ct);

        var byState = leased.ToDictionary(static s => s.CurrentState, StringComparer.Ordinal);
        await Assert.That(leased.Count).IsEqualTo(3); // 到期接管 + 未租 + AWD；活跃租约与终态不入批
        await Assert.That(byState.ContainsKey("LiveLeased")).IsFalse();
        await Assert.That(byState.ContainsKey("Completed")).IsFalse();
        await Assert.That(byState["ExpiredLeased"].LeasedBy).IsEqualTo("owner-takeover");
        await Assert.That(byState["ExpiredLeased"].Version).IsEqualTo(4); // 接管即换代（快照 3 → 4）
        await Assert.That(byState["Unleased"].Version).IsEqualTo(1);
        await Assert.That(byState["Awaiting"].LeasedBy).IsEqualTo("owner-takeover");

        // DB 真值复核（新 context）：活跃租约未被触碰（owner 与换代均保持）
        await using var verifier = new PushdownSagaDbContext(options);
        var liveFinal = await verifier.GetByIdAsync(liveId, ct);
        await Assert.That(liveFinal!.LeasedBy).IsEqualTo("other-owner");
        await Assert.That(liveFinal.Version).IsEqualTo(3);
        var expiredFinal = await verifier.GetByIdAsync(expiredId, ct);
        await Assert.That(expiredFinal!.LeasedBy).IsEqualTo("owner-takeover");
    }

    [Test]
    public async Task LeaseActiveSagasAsync_Sqlite_HeadBlockedByLiveLeases_StillLeasesTailWithBatchSizeOne(CancellationToken ct)
    {
        // v25 P2-1 语义在 SQLite 单语句路径上的等价锁定：资格谓词在子查询内先过滤后取量
        //（ORDER BY + LIMIT 作用于过滤后集合）——头部 batchSize 条全为他实例活跃租约时
        //（SagaId 序=创建序，老 Saga 恒占头部），本 tick 仍租到窗口外的未租 Saga；
        // 单语句路径无翻页，饥饿结构性不存在。
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<PushdownSagaDbContext>()
            .UseSqlite(connection).Options;

        var now = DateTimeOffset.UtcNow;
        await using (var seed = new PushdownSagaDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(ct);
            for (var i = 0; i < 3; i++)
            {
                var head = CreateState($"HeadLeased-{i}", now.AddMilliseconds(i));
                head.LeasedBy = "owner-1";
                head.LeasedUntil = now.AddMinutes(5);
                seed.SagaStates.Add(head);
            }
            seed.SagaStates.Add(CreateState("TailUnleased", now.AddMilliseconds(10)));
            await seed.SaveChangesAsync(ct);
        }

        await using var db = new PushdownSagaDbContext(options);
        var result = await db.LeaseActiveSagasAsync("owner-2", TimeSpan.FromMinutes(2), 1, ct);

        var list = result.ToList();
        await Assert.That(list).Count().IsEqualTo(1);
        await Assert.That(list[0].CurrentState).IsEqualTo("TailUnleased");
        await Assert.That(list[0].LeasedBy).IsEqualTo("owner-2");
    }

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse(
        "2026-05-31T00:00:00Z",
        CultureInfo.InvariantCulture);

    private static PushdownSagaState CreateState(
        string currentState,
        DateTimeOffset createdAt,
        SagaStatus status = SagaStatus.Active)
        => new()
        {
            CurrentState = currentState,
            CreatedAt = createdAt,
            Status = status
        };

    public sealed class PushdownSagaState : SagaState;

    private sealed class PushdownSagaDbContext(DbContextOptions<PushdownSagaDbContext> options)
        : SagaStateDbContext<PushdownSagaState>(options);

    /// <summary>命令文本记录器：跨全部拦截点登记 CommandText，供语句级断言
    ///（"UPDATE " 出现次数 = 实际 UPDATE 语句数——多行合批与分命令两形态均计入）。</summary>
    private sealed class SqlStatementCounter : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public int UpdateStatementCount
            => _commands.Sum(static text => CountOccurrences(text, "UPDATE "));

        public void Reset() => _commands.Clear();

        private static int CountOccurrences(string text, string token)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }

        private void Record(DbCommand command) => _commands.Add(command.CommandText);

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
    }
}
