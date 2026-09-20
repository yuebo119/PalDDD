using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using PalDDD.Core;
using PalDDD.Idempotency;
using static PalDDD.Dapper.DapperSqlErrorClassifier;

namespace PalDDD.Dapper;

// ═══════════════════════════════════════════════════════════════
// 💾 DapperIdempotencyStore — 幂等记录持久化（ITM-667 缺口清偿）
// ═══════════════════════════════════════════════════════════════
//
// 💡 设计说明：
//   ｜ 与 DapperOutboxStore/DapperInboxStore 同一模式：
//   ｜   DbConnection + DapperDbType + 可选 DbTransaction + TimeProvider
//   ｜ SQL 内联（对齐 DapperInboxStore——单消费者无需提取 SqlTemplates 常量）
//   ｜ Revision CAS 令牌（v54 P2 口径对齐 PalOrmIdempotencyStore——时间戳受列精度截断）
//   ｜ InternalsVisibleTo PalDDD.Dapper（v87 补充）——物化构造/回放恢复 internal 通道
//
// ⚠️ 事务挂接：Dapper 栈事务由构造参数或 DapperAmbientTransaction（同连接键控）传导，
//   本类所有 Command 均通过 Tx 属性自动挂接——与 DapperOutboxStore/DapperInboxStore 一致。

/// <summary>Idempotency Store 的 Dapper 实现 —— 5 方法全量覆盖（ITM-667 缺口清偿）。</summary>
public sealed class DapperIdempotencyStore : IIdempotencyStore
{
    private readonly DbConnection _connection;
    private readonly DapperDbType _dbType;
    private readonly DapperSqlDialect _dialect;
    private readonly DbTransaction? _transaction;
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);
    private readonly TimeProvider _timeProvider;

    public DapperIdempotencyStore(
        DbConnection connection,
        DapperDbType dbType,
        DbTransaction? transaction = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _dbType = dbType;
        _dialect = DapperSqlDialect.For(dbType);
        _transaction = transaction;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>时间参数方言编码（镜像 DapperOutboxStore.ToTimeParam——PG 原生 DateTimeOffset，
    /// MySQL 无偏移 UTC 串，SQLite "O" 串；SQLite TypeHandler 声明式注册只服务无显式编码的
    /// 遗留路径，显式编码后 PG timestamptz 不再收 text）。第三份副本——提取重构留主线任务。</summary>
    private object ToTimeParam(DateTimeOffset value)
        => _dbType switch
        {
            DapperDbType.PostgreSql => value,
            DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
            _ => DapperAotInitializer.ToSqliteParameter(value),
        };

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> GetAsync(
        string operationName, string key, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        const string sql = """
            SELECT operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error, revision
            FROM idempotency_records
            WHERE operation_name = @OperationName AND idempotency_key = @Key
            """;
        var row = await _connection.QueryFirstOrDefaultAsync<IdempotencyRow>(
            sql, new { OperationName = operationName, Key = key }, Tx)
            .ConfigureAwait(false);
        if (row is null) return null;

        // ITM-242 口径对齐（PalOrmIdempotencyStore）：MySQL DATETIME Kind=Unspecified → SpecifyKind(Utc)
        var lockedUntil = ToUtcOrMin(row.LockedUntil);
        var expiresAt = ToUtcOrMin(row.ExpiresAt);
        var updatedAt = ToUtcOrMin(row.UpdatedAt);

        var record = new IdempotencyRecord(
            row.OperationName, row.IdempotencyKey,
            (IdempotencyRecordStatus)row.Status,
            lockedUntil, expiresAt, updatedAt, row.Revision);

        if (record.ExpiresAt <= now) return null;

        // 终态回放（RestoreTerminalState 不递增 Revision——回放是"恢复原状"非状态转移）
        if (row.ResponsePayload is { Length: > 0 } && record.Status == IdempotencyRecordStatus.Completed)
            record.RestoreTerminalState(row.ResponsePayload, null);
        if (row.Error is not null && record.Status == IdempotencyRecordStatus.Failed)
            record.RestoreTerminalState(null, row.Error);

        return record;
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var lockedUntil = now + policy.ProcessingTimeout;
        var expiresAt = now + policy.Retention;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;

        // 普通 INSERT + 唯一冲突捕获（ITM-228 口径：MySQL INSERT IGNORE 会吞非重复键错误）
        const string insertSql = """
            INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error)
            VALUES (@OperationName, @Key, @Status, @LockedUntil, @ExpiresAt, @UpdatedAt, NULL, NULL)
            """;
        int affected;
        try
        {
            affected = await _connection.ExecuteAsync(insertSql,
                new { OperationName = operationName, Key = key, Status = statusProcessing, LockedUntil = ToTimeParam(lockedUntil), ExpiresAt = ToTimeParam(expiresAt), UpdatedAt = ToTimeParam(now) },
                Tx).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            affected = 0;
        }

        if (affected > 0)
            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);

        // INSERT 冲突 → 记录已存在 → GetAsync 检查是否过期可回收
        var existing = await GetAsync(operationName, key, now, ct).ConfigureAwait(false);
        if (existing is null)
        {
            // 过期回收（三十七轮 P2-5 口径：过期即回收无论终态；v54 P2 revision 换代）
            const string reclaimSql = """
                UPDATE idempotency_records
                SET status = @Status, locked_until = @LockedUntil, expires_at = @ExpiresAt, updated_at = @UpdatedAt,
                    error = NULL, response_payload = NULL, revision = revision + 1
                WHERE operation_name = @OperationName AND idempotency_key = @Key AND expires_at <= @Now
                """;
            affected = await _connection.ExecuteAsync(reclaimSql,
                new { OperationName = operationName, Key = key, Status = statusProcessing, LockedUntil = ToTimeParam(lockedUntil), ExpiresAt = ToTimeParam(expiresAt), UpdatedAt = ToTimeParam(now), Now = ToTimeParam(now) },
                Tx).ConfigureAwait(false);
            if (affected == 0) return null;

            // 换代后回读 DB 新 revision
            const string readBackSql = "SELECT revision FROM idempotency_records WHERE operation_name = @OperationName AND idempotency_key = @Key";
            var newRevision = await _connection.QuerySingleOrDefaultAsync<long>(readBackSql,
                new { OperationName = operationName, Key = key }, Tx).ConfigureAwait(false);

            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, newRevision);
        }

        // Completed 非过期 → 返回 null（ITM-078：他人已持有终态，读取走 GetAsync 获取 response_payload）
        if (existing.Status == IdempotencyRecordStatus.Completed)
            return null;

        // Processing 且租约未过期 → 返回 null（他人正在处理）
        if (existing.Status == IdempotencyRecordStatus.Processing && existing.LockedUntil > now)
            return null;

        // 过期 Processing / Failed → Revision CAS 抢占（v54 P2：revision 令牌防同刻双 worker）
        const string reclaimCasSql = """
            UPDATE idempotency_records
            SET status = @Status, locked_until = @LockedUntil, expires_at = @ExpiresAt, updated_at = @UpdatedAt,
                error = NULL, response_payload = NULL, revision = revision + 1
            WHERE operation_name = @OperationName AND idempotency_key = @Key
              AND revision = @ExpectedRevision AND status <> @Completed
            """;
        affected = await _connection.ExecuteAsync(reclaimCasSql,
            new
            {
                OperationName = operationName,
                Key = key,
                Status = statusProcessing,
                LockedUntil = ToTimeParam(lockedUntil),
                ExpiresAt = ToTimeParam(expiresAt),
                UpdatedAt = ToTimeParam(now),
                ExpectedRevision = existing.Revision,
                Completed = (int)IdempotencyRecordStatus.Completed
            },
            Tx).ConfigureAwait(false);
        if (affected == 0) return null;

        return new IdempotencyRecord(operationName, key,
            IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, existing.Revision + 1);
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var payloadBytes = responsePayload.ToArray();
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;

        // v54 P2：CAS 基准 revision + status = Processing 守卫（直调终态实例不翻转）
        const string sql = """
            UPDATE idempotency_records
            SET status = @Status, updated_at = @UpdatedAt, response_payload = @Payload, error = NULL, revision = revision + 1
            WHERE operation_name = @OperationName AND idempotency_key = @Key
              AND revision = @ExpectedRevision AND status = @Processing
            """;
        // 🔬 AOT 实验：byte[] 参数走 DynamicParameters + 显式 DbType.Binary——1.1.0 List
        //    expansion 把匿名对象里的 byte[] 误判为 IN 列表做 PackListParameters 展开
        //    （SQL 变行值，SQLite "row value misused"）；DynamicParameters 是 1.1.0 官方
        //    支持路径（按 bag 协议绑定，绕开类型推断误判）。
        var dp = new DynamicParameters();
        dp.Add("OperationName", record.OperationName);
        dp.Add("Key", record.Key);
        dp.Add("Status", statusCompleted);
        dp.Add("UpdatedAt", completedAt);
        dp.Add("Payload", payloadBytes, DbType.Binary);
        dp.Add("ExpectedRevision", record.Revision);
        dp.Add("Processing", statusProcessing);
        var affected = await _connection.ExecuteAsync(sql, dp, Tx).ConfigureAwait(false);
        if (affected > 0)
            record.MarkCompleted(responsePayload, completedAt);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(
        IdempotencyRecord record, string failureReason, DateTimeOffset failedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        var reason = FailureReason.Normalize(failureReason);
        var statusFailed = (int)IdempotencyRecordStatus.Failed;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;

        // v55 P3：仅拦 Completed（Failed 重复 MarkFailed 允许更新错误信息）
        const string sql = """
            UPDATE idempotency_records
            SET status = @Status, updated_at = @UpdatedAt, error = @Error, revision = revision + 1
            WHERE operation_name = @OperationName AND idempotency_key = @Key
              AND revision = @ExpectedRevision AND status <> @Completed
            """;
        var affected = await _connection.ExecuteAsync(sql,
            new
            {
                OperationName = record.OperationName,
                Key = record.Key,
                Status = statusFailed,
                UpdatedAt = failedAt,
                Error = reason,
                ExpectedRevision = record.Revision,
                Completed = statusCompleted
            },
            Tx).ConfigureAwait(false);
        if (affected > 0)
            record.MarkFailed(reason, failedAt);
    }

    // ITM-796（2026-09-19）：原强类型判定（MySQL 1062/1022、SQLite 19/2067 码）收口至
    // DapperSqlErrorClassifier（鸭子形态，码集并集含本版 1022/19/2067——分类行为兼容：
    // 首个命中分支的短路语义不变，新增的 1586/SqlServer 分支只在原返回 false 的
    // 异常上可能多判 true，方向为更多冲突被识别）

    /// <summary>ITM-242 口径：UTC 归一化（MySQL DATETIME Kind=Unspecified → offset 0）。</summary>
    private static DateTimeOffset ToUtcOrMin(DateTimeOffset value)
        => value == default ? value : DateTime.SpecifyKind(value.DateTime, DateTimeKind.Utc);


    /// <summary>Dapper 物化 DTO（public setters 供 Dapper 映射——非领域实体）。
    /// CA1812 抑制：实例化由 Dapper 内部反射完成，编译器不可见。</summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper QueryFirstOrDefaultAsync<T> 通过 AOT 拦截器/物化管线实例化此 DTO，编译器不可见。")]
    internal sealed class IdempotencyRow
    {
        public string OperationName { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public int Status { get; set; }
        public DateTimeOffset LockedUntil { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public byte[]? ResponsePayload { get; set; }
        public string? Error { get; set; }
        public long Revision { get; set; }
    }

}
