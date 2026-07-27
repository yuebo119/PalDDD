using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalDDD.Idempotency;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Idempotency Store 的 PalORM 实现 —— 双泛型核心基类（全程手写 SQL）。
/// <para>
/// <b>复合主键限制</b>：表 <c>idempotency_records</c> 是两列复合主键 —— PALORM019 拒绝实体注册。
/// <see cref="GetAsync"/> 用 <see cref="DbDataReader"/> 手动映射（QueryFirstAsync 对未注册类型返回空对象）。
/// </para>
/// </summary>
public class PalOrmIdempotencyStore<TProvider> : IIdempotencyStore
    where TProvider : IDbProvider
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    /// <summary>构造 Idempotency Store。</summary>
    public PalOrmIdempotencyStore(DataSession<TProvider> session) => Session = session;

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> GetAsync(
        string operationName, string key, DateTimeOffset now, CancellationToken ct = default)
    {
        // 复合主键表未注册实体 —— 用 GetRawConnection + 手动 reader（QueryFirstAsync 对未注册类型返回空对象）
        await using var cmd = Session.GetRawConnection().CreateCommand();
        cmd.CommandText = "SELECT operation_name, key, status, locked_until, expires_at, updated_at, response_payload, error FROM idempotency_records WHERE operation_name = @p0 AND key = @p1";
        AddParam(cmd, "@p0", operationName);
        AddParam(cmd, "@p1", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var record = new IdempotencyRecord(
            reader.GetString(0), reader.GetString(1),
            (IdempotencyRecordStatus)reader.GetInt32(2),
            reader.GetDateTime(3), reader.GetDateTime(4), reader.GetDateTime(5));

        if (record.ExpiresAt <= now) return null;

        if (!reader.IsDBNull(6))
        {
            var payloadBase64 = reader.GetString(6);
            if (!string.IsNullOrEmpty(payloadBase64) && record.Status == IdempotencyRecordStatus.Completed)
            {
                record.MarkCompleted(Convert.FromBase64String(payloadBase64), record.UpdatedAt);
            }
        }
        if (!reader.IsDBNull(7) && record.Status == IdempotencyRecordStatus.Failed)
        {
            record.MarkFailed(reader.GetString(7), record.UpdatedAt);
        }
        return record;
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy, CancellationToken ct = default)
    {
        var lockedUntil = now + policy.ProcessingTimeout;
        var expiresAt = now + policy.Retention;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;

        var affected = TProvider.SupportsReturningClause
            ? await Session.ExecuteAsync($"INSERT INTO idempotency_records (operation_name, key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL) ON CONFLICT DO NOTHING", ct)
            : await Session.ExecuteAsync($"INSERT IGNORE INTO idempotency_records (operation_name, key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL)", ct);

        if (affected > 0)
        {
            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
        }

        var existing = await GetAsync(operationName, key, now, ct);
        if (existing is null) return null;

        if (existing.Status == IdempotencyRecordStatus.Completed)
            return existing;

        if (existing.Status == IdempotencyRecordStatus.Processing && existing.LockedUntil > now)
            return null;

        var expectedUpdatedAt = existing.UpdatedAt;
        affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusProcessing}, locked_until = {lockedUntil}, expires_at = {expiresAt}, updated_at = {now}, error = NULL, response_payload = NULL WHERE operation_name = {operationName} AND key = {key} AND updated_at = {expectedUpdatedAt} AND status <> {(int)IdempotencyRecordStatus.Completed}",
            ct);
        if (affected == 0) return null;

        return new IdempotencyRecord(operationName, key,
            IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        var expectedUpdatedAt = record.UpdatedAt;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        var payloadBase64 = Convert.ToBase64String(responsePayload.ToArray());
        await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusCompleted}, updated_at = {completedAt}, response_payload = {payloadBase64}, error = NULL WHERE operation_name = {record.OperationName} AND key = {record.Key} AND updated_at = {expectedUpdatedAt}",
            ct);
        record.MarkCompleted(responsePayload, completedAt);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(
        IdempotencyRecord record, string failureReason, DateTimeOffset failedAt, CancellationToken ct = default)
    {
        var expectedUpdatedAt = record.UpdatedAt;
        var statusFailed = (int)IdempotencyRecordStatus.Failed;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusFailed}, updated_at = {failedAt}, error = {failureReason} WHERE operation_name = {record.OperationName} AND key = {record.Key} AND updated_at = {expectedUpdatedAt} AND status <> {statusCompleted}",
            ct);
        record.MarkFailed(failureReason, failedAt);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
