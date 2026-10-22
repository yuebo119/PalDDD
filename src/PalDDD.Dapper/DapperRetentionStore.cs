// ─────────────────────────────────────────────────────────────
// 🧹 DapperRetentionStore — 历史表分批删除（Dapper 栈）
// ─────────────────────────────────────────────────────────────

using System.Data;
using System.Data.Common;
using PalDDD.Transactions;

namespace PalDDD.Dapper;

/// <summary>
/// Dapper 栈的历史表清理实现（2026-10-20 性能轮）。SQL 与时间编码来自
/// <see cref="RetentionSql"/>，本类负责 ADO 执行与 ambient 事务接入。
/// </summary>
public sealed class DapperRetentionStore : IRetentionStore
{
    private readonly DbConnection _connection;
    private readonly RetentionDialect _dialect;

    /// <summary>构造清理存储。</summary>
    public DapperRetentionStore(DbConnection connection, DapperDbType dbType)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DapperConnectionGuard.Validate(connection, dbType);
        _connection = connection;
        _dialect = Map(dbType);
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredOutboxAsync(
        DateTimeOffset cutoff, int batchSize, bool includeDead, CancellationToken ct = default)
        => ExecuteAsync(RetentionSql.OutboxDelete(_dialect, includeDead), cutoff, batchSize, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredInboxAsync(
        DateTimeOffset cutoff, int batchSize, CancellationToken ct = default)
        => ExecuteAsync(RetentionSql.InboxDelete(_dialect), cutoff, batchSize, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredIdempotencyAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct = default)
        => ExecuteAsync(RetentionSql.IdempotencyDelete(_dialect), now, batchSize, ct);

    private async ValueTask<int> ExecuteAsync(
        string sql, DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        // R11 F18：batchSize 是单语句删除上限，必须 >=1。SQLite 的 LIMIT -1 表示
        // 无上限（一次删光积压，正是分批要避免的长事务/WAL 膨胀），MySQL/PG 的
        // LIMIT 0 又会静默返回 0 与「无更多数据」不可区分。入口 fail-fast。
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = DapperAmbientTransaction.TryGet(_connection);
        AddParam(command, "@cutoff", RetentionSql.ToTimeParam(_dialect, cutoff));
        AddParam(command, "@n", batchSize);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static RetentionDialect Map(DapperDbType dbType)
    {
        // 2026-10-21 评审 F24：未知值 fail-fast，不再静默映射 SQLite。
        if (!Enum.IsDefined(dbType))
            throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unknown Dapper database type.");
        return dbType switch
        {
            DapperDbType.PostgreSql => RetentionDialect.PostgreSql,
            DapperDbType.MySql => RetentionDialect.MySql,
            DapperDbType.Sqlite => RetentionDialect.Sqlite,
            _ => throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unknown Dapper database type."),
        };
    }
}
