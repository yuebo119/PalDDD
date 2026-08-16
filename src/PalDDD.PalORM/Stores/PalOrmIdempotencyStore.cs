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
/// <para>
/// ⚠️ <b>已知限制（P0-4）</b>：<see cref="GetAsync"/> 通过 <c>GetRawConnection().CreateCommand()</c>
/// 创建的 DbCommand 不自动 enlist 活动事务（PalORM 的 ExecuteAsync 路径才会自动 enlist）。
/// 只读路径在大多数场景正确（读已提交），但事务内脏读检查不可靠。
/// 待 PalORM 提供 <c>Session.CreateCommand(FormattableString)</c> 公开 API 后迁移。
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
        cmd.CommandText = "SELECT operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error FROM idempotency_records WHERE operation_name = @p0 AND idempotency_key = @p1";
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
        // ITM-163 修复：补 policy null + op/key 空白守卫（对齐 IdempotencyDbContext/InMemoryIdempotencyStore）
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var lockedUntil = now + policy.ProcessingTimeout;
        var expiresAt = now + policy.Retention;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;

        var affected = TProvider.SupportsReturningClause
            ? await Session.ExecuteAsync($"INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL) ON CONFLICT DO NOTHING", ct)
            : await Session.ExecuteAsync($"INSERT IGNORE INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL)", ct);

        if (affected > 0)
        {
            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
        }

        var existing = await GetAsync(operationName, key, now, ct);

        // ITM-064：INSERT 冲突已证明记录存在；GetAsync 返回 null 只可能是记录已过期
        // （GetAsync 对 ExpiresAt <= now 返回 null）。过期记录必须重新获取租约
        // （对齐 EFCore 版 TryReuseRecordAsync 复用语义），否则该 key 在 GC 清理前永久被拒。
        if (existing is null)
        {
            affected = await Session.ExecuteAsync(
                $"UPDATE idempotency_records SET status = {statusProcessing}, locked_until = {lockedUntil}, expires_at = {expiresAt}, updated_at = {now}, error = NULL, response_payload = NULL WHERE operation_name = {operationName} AND idempotency_key = {key} AND expires_at <= {now}",
                ct);
            if (affected == 0) return null;

            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
        }

        // ITM-078 修复：Completed 非过期记录返回 null（语义=他人已持有终态，本调用未获得租约）——
        // 契约对齐：EFCore（IdempotencyDbContext.TryStartAsync）与 InMemory（InMemoryIdempotencyStore）
        // 对非过期终态记录均返回 null，读取已完成响应走 GetAsync（含 response_payload）；
        // 原实现返回 existing 会让调用方把终态记录误当"本次已开始处理"
        if (existing.Status == IdempotencyRecordStatus.Completed)
            return null;

        if (existing.Status == IdempotencyRecordStatus.Processing && existing.LockedUntil > now)
            return null;

        var expectedUpdatedAt = existing.UpdatedAt;
        affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusProcessing}, locked_until = {lockedUntil}, expires_at = {expiresAt}, updated_at = {now}, error = NULL, response_payload = NULL WHERE operation_name = {operationName} AND idempotency_key = {key} AND updated_at = {expectedUpdatedAt} AND status <> {(int)IdempotencyRecordStatus.Completed}",
            ct);
        if (affected == 0) return null;

        return new IdempotencyRecord(operationName, key,
            IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        // ITM-163 修复：补 record null 守卫（对齐 IdempotencyDbContext/InMemoryIdempotencyStore）
        ArgumentNullException.ThrowIfNull(record);
        var expectedUpdatedAt = record.UpdatedAt;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        var payloadBase64 = Convert.ToBase64String(responsePayload.ToArray());
        var affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusCompleted}, updated_at = {completedAt}, response_payload = {payloadBase64}, error = NULL WHERE operation_name = {record.OperationName} AND idempotency_key = {record.Key} AND updated_at = {expectedUpdatedAt}",
            ct);
        // P1-3 修复：乐观锁竞争失败（affected=0，租约已被他方重新获取）时 DB 未落库——
        // 不再变更本地对象假装成功。语义契约见接口注释：终态写入是尽力而为，
        // 冲突意味着另一执行者持有租约并将完成同样的终态（幂等操作重复执行无害）。
        if (affected > 0)
            record.MarkCompleted(responsePayload, completedAt);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(
        IdempotencyRecord record, string failureReason, DateTimeOffset failedAt, CancellationToken ct = default)
    {
        // ITM-163 修复：补 record null + failureReason 空白守卫（对齐 IdempotencyDbContext/InMemoryIdempotencyStore）
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        var expectedUpdatedAt = record.UpdatedAt;
        var statusFailed = (int)IdempotencyRecordStatus.Failed;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        var affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusFailed}, updated_at = {failedAt}, error = {failureReason} WHERE operation_name = {record.OperationName} AND idempotency_key = {record.Key} AND updated_at = {expectedUpdatedAt} AND status <> {statusCompleted}",
            ct);
        if (affected > 0)
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
