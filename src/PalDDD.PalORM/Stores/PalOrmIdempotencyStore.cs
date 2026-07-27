using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalDDD.Idempotency;
using PalDDD.PalORM.Models;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Idempotency Store 的 PalORM 实现 —— 双泛型核心基类（全程手写 SQL）。
/// <para>
/// <b>复合主键限制</b>：表 <c>idempotency_records</c>（或 <c>IdempotencyRecords</c>，按方言建表 DDL）
/// 是两列复合主键 (operation_name, key) —— PALORM019 拒绝复合主键实体注册。
/// 本 Store 不注册实体，全程手写 SQL。
/// </para>
/// <para>
/// <b>与 Inbox 的本质区别</b>：Idempotency 缓存 <c>ResponsePayload</c> 用于响应回放（不只是状态机），
/// Inbox 仅消息去重（无响应字节）。两者并存而非合并。
/// </para>
/// <para>
/// <b>乐观锁</b>：<c>updated_at</c> 列（DateTimeOffset）作为乐观令牌 —— 与 EFCore 实现一致；
/// PalORM 不支持 DateTimeOffset 并发令牌（PALORM012），故手写 WHERE updated_at=@expected。
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
        try
        {
            var row = await Session.QueryFirstAsync<IdempotencyRecordRow>(
                $"SELECT operation_name AS OperationName, key AS Key, status AS Status, locked_until AS LockedUntil, expires_at AS ExpiresAt, updated_at AS UpdatedAt, response_payload AS ResponsePayload, error AS Error FROM idempotency_records WHERE operation_name = {operationName} AND key = {key}",
                ct);
            // 已过期 → 返回 null 但不删除（GC 任务职责）
            if (row.ExpiresAt <= now) return null;
            return row.ToDomain();
        }
        catch (InvalidOperationException)
        {
            return null;  // 无行
        }
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy, CancellationToken ct = default)
    {
        var lockedUntil = now + policy.ProcessingTimeout;
        var expiresAt = now + policy.Retention;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;

        // 方言分叉：PG/SQLite 用 ON CONFLICT DO NOTHING；MySQL 用 INSERT IGNORE
        // 三元运算符退化为 string —— 必须分支独立 $"..." 字面量
        var affected = TProvider.SupportsReturningClause
            ? await Session.ExecuteAsync($"INSERT INTO idempotency_records (operation_name, key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL) ON CONFLICT DO NOTHING", ct)
            : await Session.ExecuteAsync($"INSERT IGNORE INTO idempotency_records (operation_name, key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL)", ct);
        if (affected > 0)
        {
            // 新插入成功
            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
        }

        // 冲突 —— 回查现有决定返回语义
        IdempotencyRecordRow? existing;
        try
        {
            existing = await Session.QueryFirstAsync<IdempotencyRecordRow>(
                $"SELECT operation_name AS OperationName, key AS Key, status AS Status, locked_until AS LockedUntil, expires_at AS ExpiresAt, updated_at AS UpdatedAt, response_payload AS ResponsePayload, error AS Error FROM idempotency_records WHERE operation_name = {operationName} AND key = {key}",
                ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        // 已完成 → 幂等跳过（GetAsync 会处理 ResponsePayload 回放）
        if ((IdempotencyRecordStatus)existing.Status == IdempotencyRecordStatus.Completed)
            return existing.ToDomain();

        // 仍在 Processing 且锁未过期 → 跳过
        if ((IdempotencyRecordStatus)existing.Status == IdempotencyRecordStatus.Processing
            && existing.LockedUntil > now)
        {
            return null;
        }

        // 抢占（过期锁或 Failed）—— 手写乐观锁 WHERE updated_at=@expected
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
        // status<>Completed 守卫 —— 不覆盖已完成的记录
        await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusFailed}, updated_at = {failedAt}, error = {failureReason} WHERE operation_name = {record.OperationName} AND key = {record.Key} AND updated_at = {expectedUpdatedAt} AND status <> {statusCompleted}",
            ct);
        record.MarkFailed(failureReason, failedAt);
    }
}
