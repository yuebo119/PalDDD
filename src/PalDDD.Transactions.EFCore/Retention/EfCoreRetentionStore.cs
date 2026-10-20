// ─────────────────────────────────────────────────────────────
// 🧹 EfCoreRetentionStore — 历史表分批删除（EF Core 栈）
// ─────────────────────────────────────────────────────────────

using System.Data;
using System.Data.Common;

namespace PalDDD.Transactions;

/// <summary>
/// EF Core 栈的保留清理实现（2026-10-20 性能轮）。SQL 与时间编码来自
/// <see cref="RetentionSql"/>；连接由调用方（DbContext）持有，本类不释放。
/// </summary>
public sealed class EfCoreRetentionStore : IRetentionStore
{
    private readonly DbConnection _connection;
    private readonly RetentionDialect _dialect;

    /// <summary>构造清理存储。</summary>
    public EfCoreRetentionStore(DbConnection connection, RetentionDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _dialect = dialect;
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
}
