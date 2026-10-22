// ─────────────────────────────────────────────────────────────
// 🧹 EfCoreRetentionStore — 历史表分批删除（EF Core 栈）
// ─────────────────────────────────────────────────────────────

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Data;
using System.Data.Common;

namespace PalDDD.Transactions;

/// <summary>
/// EF Core 栈的保留清理实现（2026-10-20 性能轮；2026-10-21 评审 F5 修正）。
/// </summary>
/// <remarks>
/// EF Core 的默认物理名是 PascalCase（表 = DbSet 名、列 = 属性名：<c>OutboxMessages.Id/Status/ProcessedAt</c>），
/// 而 Dapper/PalORM 是 snake_case。原实现直接复用 <see cref="RetentionSql"/> 的 snake_case
/// SQL，PG/MySQL 在 Linux 下首次清理即报 relation 不存在（SQLite 大小写不敏感会掩盖表名差异，
/// 但列名仍会报 no such column）。本类从 EF 模型解析实际表名与列名并自行构造 SQL。
/// 连接由调用方（DbContext）持有，本类不释放。
/// </remarks>
public sealed class EfCoreRetentionStore : IRetentionStore
{
    /// <summary>物理名集合（表 + 清理谓词涉及的列）。</summary>
    private sealed record Schema(
        string OutboxTable,
        string OutboxId,
        string OutboxStatus,
        string OutboxProcessedAt,
        string InboxTable,
        string InboxId,
        string InboxStatus,
        string InboxProcessedAt,
        string IdempotencyTable,
        string IdempotencyExpiresAt);

    /// <summary>EF 默认映射（未提供模型或模型未映射对应实体时使用）。</summary>
    private static readonly Schema DefaultSchema = new(
        "OutboxMessages", "Id", "Status", "ProcessedAt",
        "InboxMessages", "Id", "Status", "ProcessedAt",
        "IdempotencyRecords", "ExpiresAt");

    private readonly DbConnection _connection;
    private readonly RetentionDialect _dialect;
    private readonly Schema _schema;

    /// <summary>构造清理存储（EF 默认表名/列名）。</summary>
    public EfCoreRetentionStore(DbConnection connection, RetentionDialect dialect)
        : this(connection, dialect, DefaultSchema)
    {
    }

    /// <summary>构造清理存储（自定义表名；列名按 EF 默认映射）。</summary>
    public EfCoreRetentionStore(
        DbConnection connection,
        RetentionDialect dialect,
        string outboxTable,
        string inboxTable,
        string idempotencyTable)
        : this(connection, dialect, DefaultSchema with
        {
            OutboxTable = RequireName(outboxTable, nameof(outboxTable)),
            InboxTable = RequireName(inboxTable, nameof(inboxTable)),
            IdempotencyTable = RequireName(idempotencyTable, nameof(idempotencyTable)),
        })
    {
    }

    /// <summary>构造清理存储：从 <paramref name="context"/> 的 EF 模型解析实际表名/列名。</summary>
    public EfCoreRetentionStore(DbContext context, RetentionDialect dialect)
        : this(
            (context ?? throw new ArgumentNullException(nameof(context))).Database.GetDbConnection(),
            dialect,
            ResolveSchema(context))
    {
    }

    private EfCoreRetentionStore(DbConnection connection, RetentionDialect dialect, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Enum.IsDefined(dialect))
            throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown retention dialect.");
        _connection = connection;
        _dialect = dialect;
        _schema = schema;
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredOutboxAsync(
        DateTimeOffset cutoff, int batchSize, bool includeDead, CancellationToken ct = default)
        => ExecuteAsync(BuildOutboxDelete(includeDead), cutoff, batchSize, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredInboxAsync(
        DateTimeOffset cutoff, int batchSize, CancellationToken ct = default)
        => ExecuteAsync(BuildInboxDelete(), cutoff, batchSize, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteExpiredIdempotencyAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct = default)
        => ExecuteAsync(BuildIdempotencyDelete(), now, batchSize, ct);

    private string BuildOutboxDelete(bool includeDead)
    {
        var table = Quote(_schema.OutboxTable);
        var id = Quote(_schema.OutboxId);
        var status = Quote(_schema.OutboxStatus);
        var processed = Quote(_schema.OutboxProcessedAt);
        var statusPredicate = includeDead ? $"{status} IN (1, 2)" : $"{status} = 1";
        return _dialect == RetentionDialect.MySql
            ? $"DELETE FROM {table} WHERE {statusPredicate} AND {processed} IS NOT NULL AND {processed} < @cutoff ORDER BY {processed} LIMIT @n"
            : $"DELETE FROM {table} WHERE {id} IN (SELECT {id} FROM {table} WHERE {statusPredicate} AND {processed} IS NOT NULL AND {processed} < @cutoff ORDER BY {processed} LIMIT @n)";
    }

    private string BuildInboxDelete()
    {
        var table = Quote(_schema.InboxTable);
        var id = Quote(_schema.InboxId);
        var status = Quote(_schema.InboxStatus);
        var processed = Quote(_schema.InboxProcessedAt);
        return _dialect == RetentionDialect.MySql
            ? $"DELETE FROM {table} WHERE {status} = 2 AND {processed} IS NOT NULL AND {processed} < @cutoff ORDER BY {processed} LIMIT @n"
            : $"DELETE FROM {table} WHERE {id} IN (SELECT {id} FROM {table} WHERE {status} = 2 AND {processed} IS NOT NULL AND {processed} < @cutoff ORDER BY {processed} LIMIT @n)";
    }

    private string BuildIdempotencyDelete()
    {
        var table = Quote(_schema.IdempotencyTable);
        var expires = Quote(_schema.IdempotencyExpiresAt);
        return _dialect switch
        {
            RetentionDialect.MySql =>
                $"DELETE FROM {table} WHERE {expires} < @cutoff ORDER BY {expires} LIMIT @n",
            RetentionDialect.PostgreSql =>
                $"DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM {table} WHERE {expires} < @cutoff ORDER BY {expires} LIMIT @n)",
            _ =>
                $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {expires} < @cutoff ORDER BY {expires} LIMIT @n)",
        };
    }

    private string Quote(string identifier)
        => _dialect == RetentionDialect.MySql
            ? "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`"
            : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async ValueTask<int> ExecuteAsync(
        string sql, DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        // R11 F18：同 Dapper/PalORM——SQLite LIMIT -1 无上限，入参必须 >=1。
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

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

    private static string RequireName(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    /// <summary>从 EF 模型解析表名/列名；未映射的实体回退 EF 默认名。</summary>
    private static Schema ResolveSchema(DbContext context)
    {
        var outbox = context.Model.FindEntityType(typeof(OutboxMessage));
        var inbox = context.Model.FindEntityType(typeof(InboxMessage));
        // IdempotencyRecord 在 PalDDD.Idempotency 包，本适配包不引用它——按 CLR 类型名查。
        var idempotency = context.Model.GetEntityTypes()
            .FirstOrDefault(static e => string.Equals(e.ClrType.Name, "IdempotencyRecord", StringComparison.Ordinal));

        return new Schema(
            Table(outbox, DefaultSchema.OutboxTable),
            Column(outbox, nameof(OutboxMessage.Id), DefaultSchema.OutboxId),
            Column(outbox, nameof(OutboxMessage.Status), DefaultSchema.OutboxStatus),
            Column(outbox, nameof(OutboxMessage.ProcessedAt), DefaultSchema.OutboxProcessedAt),
            Table(inbox, DefaultSchema.InboxTable),
            Column(inbox, nameof(InboxMessage.Id), DefaultSchema.InboxId),
            Column(inbox, nameof(InboxMessage.Status), DefaultSchema.InboxStatus),
            Column(inbox, nameof(InboxMessage.ProcessedAt), DefaultSchema.InboxProcessedAt),
            Table(idempotency, DefaultSchema.IdempotencyTable),
            Column(idempotency, "ExpiresAt", DefaultSchema.IdempotencyExpiresAt));
    }

    private static string Table(IEntityType? entityType, string fallback)
        => entityType?.GetTableName() ?? fallback;

    private static string Column(IEntityType? entityType, string propertyName, string fallback)
        => entityType?.FindProperty(propertyName)?.GetColumnName() ?? fallback;
}
