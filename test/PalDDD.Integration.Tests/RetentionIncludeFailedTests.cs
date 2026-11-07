using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PalDDD.Dapper;
using PalDDD.PalORM.Sqlite;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>
/// 2026-11-07 审计 P1-B：Retention 的 <c>includeFailed</c> 在 Dapper / PalORM / EF 三栈的
/// 端到端清理（SQLite 内存库）。
/// <para>
/// 机制：Failed 行的 <c>processed_at</c> 此前恒 null，而 <c>RetentionSql.InboxDelete</c> /
/// <c>EfCoreRetentionStore.BuildInboxDelete</c> 的 includeFailed 谓词是
/// <c>status IN (2,3) AND processed_at IS NOT NULL AND processed_at &lt; @cutoff</c>
/// ——即使开启 <c>includeFailed</c>，Failed 行也永不被清理（假 API）。
/// </para>
/// <para>
/// 每栈插入 Processed-old / Failed-old / Failed-fresh 三行，依次断言：
/// <c>includeFailed:false</c> 只删 Processed-old；<c>includeFailed:true</c> 再删 Failed-old；
/// Failed-fresh 保留（时间窗口仍生效）。
/// </para>
/// </summary>
// 与 dapper-global 组串行（Dapper 全局类型映射为进程级状态）。
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class RetentionIncludeFailedTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Inbox_FailedRows_DeletedOnlyWhenIncludeFailed_ThreeStacks(CancellationToken ct)
    {
        // ── Dapper SQLite（时间编码 "O" 字符串，sqliteProviderNative: false；物理名 snake_case）──
        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var names = await CreateInboxTableSnakeAsync(conn, ct);
            await SeedAsync(conn, names, static at => at.ToString("O", CultureInfo.InvariantCulture), ct);
            var store = new DapperRetentionStore(conn, DapperDbType.Sqlite);
            await AssertIncludeFailedAsync(store, conn, names, ct);
        }

        // ── PalORM SQLite（时间编码 DateTimeOffset 值，sqliteProviderNative: true；物理名 snake_case）──
        await using (var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct))
        {
            var conn = session.GetRawConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);
            var names = await CreateInboxTableSnakeAsync(conn, ct);
            await SeedAsync(conn, names, static at => at, ct);
            var store = new PalOrmRetentionStore<SqliteProvider>(session);
            await AssertIncludeFailedAsync(store, conn, names, ct);
        }

        // ── EF Core SQLite（默认 schema：InboxMessages/Id/Status/ProcessedAt，PascalCase）──
        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var names = await CreateInboxTableEfAsync(conn, ct);
            await SeedAsync(conn, names, static at => at, ct);
            var store = new EfCoreRetentionStore(conn, RetentionDialect.Sqlite);
            await AssertIncludeFailedAsync(store, conn, names, ct);
        }
    }

    // 物理名三元组——Dapper/PalORM 的 RetentionSql 硬编码 snake_case，EF 硬编码 PascalCase，
    // 两者不能共用一张表（下划线差异无法靠 SQLite 大小写不敏感抹平）。
    private sealed record InboxNames(string Table, string StatusColumn, string ProcessedAtColumn);

    private static async Task<InboxNames> CreateInboxTableSnakeAsync(DbConnection connection, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TABLE inbox_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, status INTEGER NOT NULL, processed_at TEXT)";
            await command.ExecuteNonQueryAsync(ct);
        }
        return new InboxNames("inbox_messages", "status", "processed_at");
    }

    private static async Task<InboxNames> CreateInboxTableEfAsync(DbConnection connection, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TABLE InboxMessages (Id INTEGER PRIMARY KEY AUTOINCREMENT, Status INTEGER NOT NULL, ProcessedAt TEXT)";
            await command.ExecuteNonQueryAsync(ct);
        }
        return new InboxNames("InboxMessages", "Status", "ProcessedAt");
    }

    private static async Task AssertIncludeFailedAsync(
        IRetentionStore store,
        DbConnection connection,
        InboxNames names,
        CancellationToken ct)
    {
        // includeFailed:false —— 只删 Processed 旧行（Failed-old 必须保留）。
        var deletedProcessedOnly = await store.DeleteExpiredInboxAsync(
            Cutoff, batchSize: 100, includeFailed: false, ct);
        await Assert.That(deletedProcessedOnly).IsEqualTo(1)
            .Because("includeFailed:false 只应删 Processed 旧行");

        // includeFailed:true —— 再删 Failed 旧行（依赖 MarkFailedAsync 写入 processed_at）。
        var deletedIncludingFailed = await store.DeleteExpiredInboxAsync(
            Cutoff, batchSize: 100, includeFailed: true, ct);
        await Assert.That(deletedIncludingFailed).IsEqualTo(1)
            .Because("includeFailed:true 应删 Failed 旧行（否则 Failed 行 processed_at 为 null，谓词永不命中）");

        // 剩余只有 Failed-fresh（窗口外）。
        await Assert.That(await CountAsync(connection, names, ct)).IsEqualTo(1);
        await Assert.That(await RemainingStatusAsync(connection, names, ct)).IsEqualTo((int)InboxStatus.Failed);
    }

    private static async Task SeedAsync(
        DbConnection connection,
        InboxNames names,
        Func<DateTimeOffset, object> encode,
        CancellationToken ct)
    {
        // Processed 旧行（status=2）。
        await InsertAsync(connection, names, 2, encode(Cutoff.AddDays(-1)), ct);
        // Failed 旧行（status=3）——includeFailed 的目标。
        await InsertAsync(connection, names, 3, encode(Cutoff.AddDays(-1)), ct);
        // Failed 新鲜行（窗口外，任何 includeFailed 都不得删）。
        await InsertAsync(connection, names, 3, encode(Cutoff.AddDays(1)), ct);
    }

    private static async Task InsertAsync(
        DbConnection connection,
        InboxNames names,
        int status,
        object processedAt,
        CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"INSERT INTO {names.Table} ({names.StatusColumn}, {names.ProcessedAtColumn}) VALUES (@status, @at)";
            AddParameter(command, "@status", status);
            AddParameter(command, "@at", processedAt);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> CountAsync(DbConnection connection, InboxNames names, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) FROM {names.Table}";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
    }

    private static async Task<int> RemainingStatusAsync(DbConnection connection, InboxNames names, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {names.StatusColumn} FROM {names.Table}";
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
