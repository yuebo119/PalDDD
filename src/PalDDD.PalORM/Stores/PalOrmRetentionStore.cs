// ─────────────────────────────────────────────────────────────
// 🧹 PalOrmRetentionStore — 历史表分批删除（PalORM 栈）
// ─────────────────────────────────────────────────────────────

using System.Data;
using System.Data.Common;
using PalORM;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// PalORM 栈的保留清理实现（2026-10-20 性能轮）。SQL 与时间编码来自
/// <see cref="RetentionSql"/>，本类负责 ADO 执行与 ambient 事务接入。
/// </summary>
public sealed class PalOrmRetentionStore<TProvider> : IRetentionStore
    where TProvider : IDbProvider
{
    private readonly DbConnection _connection;
    private readonly DataSession<TProvider> _session;
    private readonly RetentionDialect _dialect;

    /// <summary>构造清理存储。</summary>
    public PalOrmRetentionStore(DataSession<TProvider> session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _connection = session.GetRawConnection();
        _dialect = Map(TProvider.Dialect);
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
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = PalOrmAmbientTransaction.TryGet(_session);
        AddParam(command, "@cutoff", RetentionSql.ToTimeParam(_dialect, cutoff, sqliteProviderNative: true));
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

    private static RetentionDialect Map(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.PostgreSql => RetentionDialect.PostgreSql,
            SqlDialect.MySql => RetentionDialect.MySql,
            _ => RetentionDialect.Sqlite,
        };
}
