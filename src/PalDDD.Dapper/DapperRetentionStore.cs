// ─────────────────────────────────────────────────────────────
// 🧹 DapperRetentionStore — 历史表分批删除（Dapper 栈）
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Data.Common;
using PalDDD.Transactions;

namespace PalDDD.Dapper;

/// <summary>
/// Dapper 栈的历史表清理实现（2026-10-20 性能轮）。单语句批量删除，方言差异只在
/// 限行语法：PG/SQLite 用子查询 LIMIT，MySQL 用 DELETE ... ORDER BY ... LIMIT。
/// </summary>
public sealed class DapperRetentionStore : IRetentionStore
{
    private readonly DbConnection _connection;
    private readonly DapperDbType _dbType;

    /// <summary>构造清理存储。</summary>
    public DapperRetentionStore(DbConnection connection, DapperDbType dbType)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _dbType = dbType;
    }

    private DbTransaction? Tx => DapperAmbientTransaction.TryGet(_connection);

    /// <inheritdoc />
    public async ValueTask<int> DeleteExpiredOutboxAsync(
        DateTimeOffset cutoff,
        int batchSize,
        bool includeDead,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var sql = _dbType == DapperDbType.MySql
            ? (includeDead ? OutboxDeleteAllMySql : OutboxDeleteProcessedMySql)
            : (includeDead ? OutboxDeleteAllSubquery : OutboxDeleteProcessedSubquery);
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(
            sql,
            new { cutoff = ToTimeParam(cutoff), n = batchSize },
            Tx).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteExpiredInboxAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var sql = _dbType == DapperDbType.MySql ? InboxDeleteMySql : InboxDeleteSubquery;
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(
            sql,
            new { cutoff = ToTimeParam(cutoff), n = batchSize },
            Tx).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteExpiredIdempotencyAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var sql = _dbType switch
        {
            DapperDbType.MySql => IdempotencyDeleteMySql,
            DapperDbType.PostgreSql => IdempotencyDeletePostgreSql,
            _ => IdempotencyDeleteSqlite,
        };
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(
            sql,
            new { now = ToTimeParam(now), n = batchSize },
            Tx).ConfigureAwait(false);
    }

    private async ValueTask<DbConnection> EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        return _connection;
    }

    private object ToTimeParam(DateTimeOffset value)
        => _dbType switch
        {
            DapperDbType.PostgreSql => value,
            DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
            _ => DapperAotInitializer.ToSqliteParameter(value),
        };

    // PG/SQLite：DELETE ... WHERE id IN (SELECT ... LIMIT)
    private const string OutboxDeleteProcessedSubquery =
        "DELETE FROM outbox_messages WHERE id IN (SELECT id FROM outbox_messages WHERE status = 1 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n)";

    private const string OutboxDeleteAllSubquery =
        "DELETE FROM outbox_messages WHERE id IN (SELECT id FROM outbox_messages WHERE status IN (1, 2) AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n)";

    private const string InboxDeleteSubquery =
        "DELETE FROM inbox_messages WHERE id IN (SELECT id FROM inbox_messages WHERE status = 2 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n)";

    // MySQL：DELETE ... ORDER BY ... LIMIT
    private const string OutboxDeleteProcessedMySql =
        "DELETE FROM outbox_messages WHERE status = 1 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n";

    private const string OutboxDeleteAllMySql =
        "DELETE FROM outbox_messages WHERE status IN (1, 2) AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n";

    private const string InboxDeleteMySql =
        "DELETE FROM inbox_messages WHERE status = 2 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n";

    // 幂等表是复合主键；PG 用 ctid、SQLite 用 rowid 定位本批，MySQL 直接 ORDER BY LIMIT。
    private const string IdempotencyDeletePostgreSql =
        "DELETE FROM idempotency_records WHERE ctid IN (SELECT ctid FROM idempotency_records WHERE expires_at < @now ORDER BY expires_at LIMIT @n)";

    private const string IdempotencyDeleteSqlite =
        "DELETE FROM idempotency_records WHERE rowid IN (SELECT rowid FROM idempotency_records WHERE expires_at < @now ORDER BY expires_at LIMIT @n)";

    private const string IdempotencyDeleteMySql =
        "DELETE FROM idempotency_records WHERE expires_at < @now ORDER BY expires_at LIMIT @n";
}
