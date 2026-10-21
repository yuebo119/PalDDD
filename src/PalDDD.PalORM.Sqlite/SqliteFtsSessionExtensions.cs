using System.Data;
using System.Data.Common;
using System.Globalization;
using PalDDD.Shared;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.PalORM.Sqlite;

/// <summary>
/// PalORM SQLite FTS5 索引生命周期扩展。
/// <para>DDL 通过 <see cref="DataSession{TProvider}.GetRawConnection"/> 逐条执行；
/// <see cref="EnsureIndexAsync"/> 在创建前校验源表列与 external-content 主键类型。</para>
/// <para>查询侧不提供 LINQ 抽象：用原生 <see cref="DbCommand"/> 加 <c>@q</c> 参数，
/// 或 <c>FormattableStringFactory.Create</c> 加 <c>{0}</c> 洞。</para>
/// </summary>
public static class SqliteFtsSessionExtensions
{
    /// <summary>
    /// 校验并创建 FTS5 索引（CREATE TABLE/TRIGGER 均为 IF NOT EXISTS，可重复调用）。
    /// <para>standalone 索引只由后续 DML 触发器维护；源表已有数据时须再调用
    /// <see cref="RebuildIndexAsync"/> 回填，否则存量行不会被检索到。</para>
    /// </summary>
    public static async Task EnsureIndexAsync(
        this DataSession<SqliteProvider> session,
        SqliteFtsIndexDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(definition);

        var connection = await GetOpenConnectionAsync(session, cancellationToken).ConfigureAwait(false);
        await SqliteFtsCore.ValidateAsync(connection, definition.Core, cancellationToken).ConfigureAwait(false);
        await ExecuteStatementsAsync(session, connection, definition.Statements, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重建索引：external-content 用 FTS5 rebuild，standalone 从源表全量重灌。</summary>
    public static async Task RebuildIndexAsync(
        this DataSession<SqliteProvider> session,
        SqliteFtsIndexDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(definition);

        var connection = await GetOpenConnectionAsync(session, cancellationToken).ConfigureAwait(false);
        await ExecuteStatementsAsync(session, connection, definition.RebuildStatements(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>合并 FTS5 索引碎片。</summary>
    public static async Task OptimizeIndexAsync(
        this DataSession<SqliteProvider> session,
        SqliteFtsIndexDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(definition);

        var connection = await GetOpenConnectionAsync(session, cancellationToken).ConfigureAwait(false);
        await ExecuteStatementsAsync(session, connection, definition.OptimizeStatements(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除索引的触发器、虚表；standalone 还删除映射表。</summary>
    public static async Task DropIndexAsync(
        this DataSession<SqliteProvider> session,
        SqliteFtsIndexDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(definition);

        var connection = await GetOpenConnectionAsync(session, cancellationToken).ConfigureAwait(false);
        await ExecuteStatementsAsync(session, connection, definition.DropStatements(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>判断索引虚表是否存在于 <c>sqlite_master</c>。</summary>
    public static async Task<bool> IndexExistsAsync(
        this DataSession<SqliteProvider> session,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        var connection = await GetOpenConnectionAsync(session, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = @name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>创建 EventLog 默认索引（standalone，键 <c>global_position</c>）。</summary>
    public static Task EnsureEventLogIndexAsync(
        this DataSession<SqliteProvider> session,
        string sourceTable = "events",
        bool includePayload = true,
        string? tokenizer = null,
        IReadOnlyList<int>? prefixIndexes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var definition = SqliteFts.CreateEventLogIndex(sourceTable, includePayload, tokenizer, prefixIndexes);
        return session.EnsureIndexAsync(definition, cancellationToken);
    }

    /// <summary>创建 Outbox 默认索引（standalone，键 <c>id</c>，TEXT/ULID 安全）。</summary>
    public static Task EnsureOutboxIndexAsync(
        this DataSession<SqliteProvider> session,
        string sourceTable = "outbox_messages",
        bool includePayload = true,
        string? tokenizer = null,
        IReadOnlyList<int>? prefixIndexes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var definition = SqliteFts.CreateOutboxIndex(sourceTable, includePayload, tokenizer, prefixIndexes);
        return session.EnsureIndexAsync(definition, cancellationToken);
    }

    private static async ValueTask<DbConnection> GetOpenConnectionAsync(
        DataSession<SqliteProvider> session,
        CancellationToken cancellationToken)
    {
        var connection = session.GetRawConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// 执行一组 DDL/DML 语句。
    /// </summary>
    /// <remarks>
    /// 2026-10-21 评审 F16/F23：
    /// ① 原实现逐条独立执行且不开事务——standalone Rebuild 的 DELETE 成功后 INSERT 遇
    /// <c>SQLITE_BUSY</c> 会留下空索引；在已有事务中调用时新建命令未挂接 Transaction，
    /// Microsoft.Data.Sqlite 抛 TransactionRequired。
    /// ② 改为单条多语句命令（SQLite 支持）+ 事务：复用 PalORM 环境事务（存在时），
    /// 否则新建并提交/回滚；N 条语句 1 次往返。
    /// </remarks>
    private static async Task ExecuteStatementsAsync(
        DataSession<SqliteProvider> session,
        DbConnection connection,
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        if (statements.Count == 0)
            return;

        var ambient = PalOrmAmbientTransaction.TryGet(session);
        var transaction = ambient;
        var ownsTransaction = false;
        if (transaction is null)
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            ownsTransaction = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = string.Join(";\n", statements);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (ownsTransaction)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (ownsTransaction)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackEx) when (rollbackEx is InvalidOperationException or DbException)
                {
                    // 回滚是 best-effort；保留原始失败异常。
                }
            }
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
