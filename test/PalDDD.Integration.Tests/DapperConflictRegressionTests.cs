using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Testing;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// R14 冲突分类回归锁（PG/MySQL 真库，Testcontainers；Docker 不可达时 Skip）
//
// F2：PG 批量 StreamExists 并发追加必须转 EventStreamConcurrencyException
//     （原 `(actual ?? -1) < 0` 在流已存在时恒 false，原始 PostgresException 逃逸）。
// F4：MySQL 批量认领同一 now 的第二次调用不得重复认领
//     （原按 processing_started_at=@now 回读，同刻碰撞命中他人写入行）。
// ═══════════════════════════════════════════════════════════════
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class DapperConflictRegressionTests
{
    [Test]
    public async Task EventLog_PostgreSql_BatchStreamExists_ConcurrentAppend_ThrowsConcurrencyException()
    {
        if (!TestEnvironment.UsePostgreSqlTestcontainers)
            Skip.Test("PG 方言测试仅走 Testcontainers（对齐 MultiDialectFixture 禁外部库裁决）；当前配置指向外部连接串，跳过。");

        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await using var competingConnection = new NpgsqlConnection(probe.Connection.ConnectionString);
        await competingConnection.OpenAsync();

        var stream = $"r14/{Guid.NewGuid():N}";
        var primary = new DapperEventLog(probe.Connection, dbType: DapperDbType.PostgreSql);
        var competing = new DapperEventLog(competingConnection, dbType: DapperDbType.PostgreSql);

        await primary.AppendAsync(stream, ExpectedStreamVersion.NoStream, [Ev("seed")]);

        await using var tx = await probe.Connection.BeginTransactionAsync();
        var inTransaction = new DapperEventLog(probe.Connection, tx, DapperDbType.PostgreSql);
        await inTransaction.AppendAsync(stream, ExpectedStreamVersion.StreamExists, [Ev("a1"), Ev("a2")]);

        var observed = Task.Run(async () =>
        {
            try
            {
                await competing.AppendAsync(stream, ExpectedStreamVersion.StreamExists, [Ev("b1"), Ev("b2")]);
                return (Exception?)null;
            }
            catch (EventStreamConcurrencyException ex)
            {
                return ex;
            }
        });

        await Task.Delay(500);
        await tx.CommitAsync();
        var failure = await observed;

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.GetType().Name).IsEqualTo("EventStreamConcurrencyException");
    }

    [Test]
    public async Task Inbox_MySql_BatchClaim_SameTimestamp_DoesNotClaimTwice()
    {
        if (!TestEnvironment.UseMySqlTestcontainers)
            Skip.Test("MySQL 方言测试仅走 Testcontainers（对齐 MultiDialectFixture 禁外部库裁决）；当前配置指向外部连接串，跳过。");

        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        var store = new DapperInboxStore(probe.Connection, DapperDbType.MySql);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await store.TryStartProcessingManyAsync(
            "orders", ["m1"], now, TimeSpan.FromMinutes(5), CancellationToken.None);
        var second = await store.TryStartProcessingManyAsync(
            "orders", ["m1"], now, TimeSpan.FromMinutes(5), CancellationToken.None);

        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(second.Count).IsEqualTo(0);
    }

    private static EventData Ev(string name)
        => new(PalUlid.New(), name, 1, "application/json",
            new byte[] { 1 }, ReadOnlyMemory<byte>.Empty,
            new EventAuditMetadata(null, null, null, null, null, null));
}
