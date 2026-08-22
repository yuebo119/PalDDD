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
/// <b>时间戳读取（ITM-242）</b>：MySQL DATETIME 回读的 DateTime Kind=Unspecified，隐式
/// DateTime→DateTimeOffset 转换对 Unspecified 套<b>本地时区偏移</b>（探针实测 -8h 累积回拨）。
/// <see cref="GetAsync"/> 统一 <see cref="DateTime.SpecifyKind"/>(Utc) 使隐式转换套 offset 0
///（Npgsql timestamptz / SQLite 带偏移文本均返回 Kind=Utc，幂等无害）。
/// </para>
/// <para>
/// <b>事务挂接（ITM-243，原 P0-4 已知限制更新）</b>：<see cref="GetAsync"/> 的 raw command 经
/// <see cref="CreateRawCommand"/> 挂接 <c>PalOrmAmbientTransaction</c>（IUnitOfWork 事务边界
/// Session 键控传导）——MySQL 活动事务下未挂接的命令抛 InvalidOperationException（MySqlConnector
/// 严格校验）。残余限制：仅经 IUnitOfWork 开启的事务被传导，直接调 session.BeginTransactionAsync
/// 绕过 IUnitOfWork 时仍不挂接（待 PalORM 提供公开活动事务访问器后迁移）。
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
        // ITM-243：经 CreateRawCommand 挂接 IUnitOfWork 环境事务
        await using var cmd = CreateRawCommand();
        cmd.CommandText = "SELECT operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error FROM idempotency_records WHERE operation_name = @p0 AND idempotency_key = @p1";
        AddParam(cmd, "@p0", operationName);
        AddParam(cmd, "@p1", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        var record = new IdempotencyRecord(
            reader.GetString(0), reader.GetString(1),
            (IdempotencyRecordStatus)reader.GetInt32(2),
            GetUtc(reader, 3), GetUtc(reader, 4), GetUtc(reader, 5));

        if (record.ExpiresAt <= now) return null;

        if (!reader.IsDBNull(6))
        {
            // 原生二进制列（bytea/BLOB/LONGBLOB）——与 EFCore 栈 ResponsePayload 的 byte[] 转换对齐
            var payloadBytes = reader.GetFieldValue<byte[]>(6);
            if (payloadBytes.Length > 0 && record.Status == IdempotencyRecordStatus.Completed)
            {
                record.MarkCompleted(payloadBytes, record.UpdatedAt);
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

        // ITM-228 修复（三十二轮）：MySQL INSERT IGNORE 会把截断、非法日期等非重复键
        // 错误也降为 warning 并写入调整值——幂等键被静默破坏。改为普通 INSERT + 捕获唯一冲突。
        int affected;
        if (TProvider.SupportsReturningClause)
        {
            affected = await Session.ExecuteAsync($"INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL) ON CONFLICT DO NOTHING", ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                affected = await Session.ExecuteAsync($"INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error) VALUES ({operationName}, {key}, {statusProcessing}, {lockedUntil}, {expiresAt}, {now}, NULL, NULL)", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                affected = 0; // 唯一约束冲突——记录已存在，非错误
            }
        }

        if (affected > 0)
        {
            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now);
        }

        var existing = await GetAsync(operationName, key, now, ct).ConfigureAwait(false);

        // ITM-064：INSERT 冲突已证明记录存在；GetAsync 返回 null 只可能是记录已过期
        // （GetAsync 对 ExpiresAt <= now 返回 null）。过期记录必须重新获取租约
        // （对齐 EFCore 版 TryReuseRecordAsync 复用语义），否则该 key 在 GC 清理前永久被拒。
        if (existing is null)
        {
            // 三十七轮 P2-5：过期回收补 status 守卫——原 WHERE 只有 expires_at <= now，
            // 并发场景下 Completed 终态可能被过期回收覆盖。补 status <> Completed 确保只回收
            // Processing 态过期记录（对齐 :130 行 MarkFailed 的乐观锁模式）。
            affected = await Session.ExecuteAsync(
                $"UPDATE idempotency_records SET status = {statusProcessing}, locked_until = {lockedUntil}, expires_at = {expiresAt}, updated_at = {now}, error = NULL, response_payload = NULL WHERE operation_name = {operationName} AND idempotency_key = {key} AND expires_at <= {now} AND status <> {(int)IdempotencyRecordStatus.Completed}",
                ct).ConfigureAwait(false);
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
            ct).ConfigureAwait(false);
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
        // 原生 byte[] 参数（PalORM ≥5.3 DbType.Binary 显式分派）——列类型 bytea/BLOB/LONGBLOB
        var payloadBytes = responsePayload.ToArray();
        var affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusCompleted}, updated_at = {completedAt}, response_payload = {payloadBytes}, error = NULL WHERE operation_name = {record.OperationName} AND idempotency_key = {record.Key} AND updated_at = {expectedUpdatedAt}",
            ct).ConfigureAwait(false);
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
            ct).ConfigureAwait(false);
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

    /// <summary>
    /// ITM-242：读回时间戳并标记 UTC。MySQL DATETIME 回读的 DateTime Kind=Unspecified，
    /// 隐式 DateTime→DateTimeOffset 转换对 Unspecified 套本地时区偏移（探针实测 -8h 累积回拨）；
    /// SpecifyKind(Utc) 使隐式转换套 offset 0。Npgsql timestamptz 与 SQLite 带偏移文本
    /// 均返回 Kind=Utc（探针实测幂等）。
    /// </summary>
    private static DateTimeOffset GetUtc(DbDataReader reader, int ordinal)
        => DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    /// <summary>
    /// ITM-243：创建 raw command 并挂接 IUnitOfWork 活动事务。GetRawConnection().CreateCommand()
    /// 的手动命令不自动 enlist（PalORM 仅 ExecuteAsync 路径自动挂 GetActiveTransaction()），
    /// MySQL 活动事务下未挂接抛 InvalidOperationException（MySqlConnector 严格校验，探针实测）。
    /// </summary>
    private DbCommand CreateRawCommand()
    {
        var cmd = Session.GetRawConnection().CreateCommand();
        var tx = PalOrmAmbientTransaction.TryGet(Session);
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>
    /// ITM-228：判定异常是否为唯一约束冲突（MySQL 1062/1586、PG 23505、SQLite UNIQUE、SqlServer 2601/2627）。
    /// 仅捕获重复键——INSERT IGNORE 会把截断/非法日期等其他错误也降为 warning。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Aot", "IL2075:RequiresDynamicallyAccessedMembers",
        Justification = "PalORM 适配层为非 AOT（IsAotCompatible=false）；反射读取 provider 异常属性用于错误分类。")]
    private static bool IsDuplicateKeyError(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var type = ex.GetType();
            var typeName = type.Name;

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(ex) is int mysqlNum
                && (mysqlNum == 1062 || mysqlNum == 1586))
                return true;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(ex) is string pgState
                && pgState == "23505")
                return true;

            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(ex) is int sqlNum
                && (sqlNum == 2601 || sqlNum == 2627))
                return true;
        }
        return false;
    }
}
