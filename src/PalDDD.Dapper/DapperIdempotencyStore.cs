using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
public sealed class DapperIdempotencyStore : IIdempotencyStore, IIdempotencyStartOrGetStore
{
    private readonly DbConnection _connection;
    private readonly DapperDbType _dbType;
    private readonly DbTransaction? _transaction;
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);

    public DapperIdempotencyStore(
        DbConnection connection,
        DapperDbType dbType,
        DbTransaction? transaction = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DapperConnectionGuard.Validate(connection, dbType);
        _connection = connection;
        _dbType = dbType;
        _transaction = transaction;
        // 保留 timeProvider 构造参数以维持公开签名；当前实现使用调用方传入的 now/数据库时钟。
        _ = timeProvider;
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

        await EnsureOpenAsync(ct).ConfigureAwait(false);
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

        // 终态回放（RestoreTerminalState 不递增 Revision——回放是"恢复原状"非状态转移）。
        // 2026-10-21 评审 F3：与 PalORM 的 ITM-277 口径对齐——区分 NULL（无载荷）与 0 字节
        //（成功完成 + 空响应体），后者同样回放 byte[0]，不得判为"无结果"。
        if (row.ResponsePayload is not null && record.Status == IdempotencyRecordStatus.Completed)
            record.RestoreTerminalState(row.ResponsePayload, null);
        if (row.Error is not null && record.Status == IdempotencyRecordStatus.Failed)
            record.RestoreTerminalState(null, row.Error);

        return record;
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> TryStartAsync(
    string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy, CancellationToken ct = default)
    {
        var (started, _) = await TryStartCoreAsync(
            operationName, key, now, policy, fullConflictRead: false, ct).ConfigureAwait(false);
        return started;
    }

    /// <summary>
    /// TryStart 与 StartOrGet 的共同核心。fullConflictRead=false 保持 TryStart 的 B5 轻量分类读
    /// （不拉 response_payload）；true 时冲突路径直接整行读一次，供 StartOrGet 返回 Existing，
    /// 省掉「四列分类读 + 二次整行读」。CAS/回收语义与旧实现逐字一致。
    /// </summary>
    private async ValueTask<(IdempotencyRecord? Started, IdempotencyRecord? Existing)> TryStartCoreAsync(
        string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy,
        bool fullConflictRead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await EnsureOpenAsync(ct).ConfigureAwait(false);
        var lockedUntil = now + policy.ProcessingTimeout;
        var expiresAt = now + policy.Retention;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;

        var affected = await TryInsertStartAsync(
            operationName, key, now, lockedUntil, expiresAt, statusProcessing).ConfigureAwait(false);
        if (affected > 0)
        {
            return (new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now), null);
        }

        var conflict = fullConflictRead
            ? await GetFullConflictViewAsync(operationName, key, now, ct).ConfigureAwait(false)
            : await GetClassificationConflictViewAsync(operationName, key, now, ct).ConfigureAwait(false);

        if (conflict is null)
        {
            // 过期回收（三十七轮 P2-5 口径：过期即回收无论终态；v54 P2 revision 换代）
            // EPSL 地毯审计 2026-10-16 B 批 #21：PG/SQLite 改 UPDATE ... RETURNING revision
            // 单语句；MySQL 无 RETURNING 维持两步。
            if (_dbType == DapperDbType.PostgreSql || _dbType == DapperDbType.Sqlite)
            {
                const string reclaimReturningSql = """
                UPDATE idempotency_records
                SET status = @Status, locked_until = @LockedUntil, expires_at = @ExpiresAt, updated_at = @UpdatedAt,
                    error = NULL, response_payload = NULL, revision = revision + 1
                WHERE operation_name = @OperationName AND idempotency_key = @Key AND expires_at <= @Now
                RETURNING revision
                """;
                var reclaimedRevisions = await _connection.QueryAsync<long>(reclaimReturningSql,
                    new { OperationName = operationName, Key = key, Status = statusProcessing, LockedUntil = ToTimeParam(lockedUntil), ExpiresAt = ToTimeParam(expiresAt), UpdatedAt = ToTimeParam(now), Now = ToTimeParam(now) },
                    Tx).ConfigureAwait(false);
                var revisionList = reclaimedRevisions.ToList();
                if (revisionList.Count == 0) return (null, null);

                return (new IdempotencyRecord(operationName, key,
                    IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, revisionList[0]), null);
            }

            const string reclaimSql = """
            UPDATE idempotency_records
            SET status = @Status, locked_until = @LockedUntil, expires_at = @ExpiresAt, updated_at = @UpdatedAt,
                error = NULL, response_payload = NULL, revision = revision + 1
            WHERE operation_name = @OperationName AND idempotency_key = @Key AND expires_at <= @Now
            """;
            affected = await _connection.ExecuteAsync(reclaimSql,
                new { OperationName = operationName, Key = key, Status = statusProcessing, LockedUntil = ToTimeParam(lockedUntil), ExpiresAt = ToTimeParam(expiresAt), UpdatedAt = ToTimeParam(now), Now = ToTimeParam(now) },
                Tx).ConfigureAwait(false);
            if (affected == 0) return (null, null);

            const string readBackSql = "SELECT revision FROM idempotency_records WHERE operation_name = @OperationName AND idempotency_key = @Key";
            var newRevision = await _connection.QuerySingleOrDefaultAsync<long>(readBackSql,
                new { OperationName = operationName, Key = key }, Tx).ConfigureAwait(false);

            return (new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, newRevision), null);
        }

        // Completed 非过期：TryStart 返回 null；StartOrGet 用整行记录返回 Existing。
        if (conflict.Value.Status == IdempotencyRecordStatus.Completed)
            return (null, conflict.Value.Record);

        // Processing 且租约未过期：同上。
        if (conflict.Value.Status == IdempotencyRecordStatus.Processing && conflict.Value.LockedUntil > now)
            return (null, conflict.Value.Record);

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
                ExpectedRevision = conflict.Value.Revision,
                Completed = (int)IdempotencyRecordStatus.Completed
            },
            Tx).ConfigureAwait(false);
        if (affected == 0)
        {
            if (!fullConflictRead) return (null, null);
            var current = await GetAsync(operationName, key, now, ct).ConfigureAwait(false);
            return (null, current);
        }

        return (new IdempotencyRecord(operationName, key,
            IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, conflict.Value.Revision + 1), null);
    }

    private async ValueTask<int> TryInsertStartAsync(
        string operationName, string key, DateTimeOffset now,
        DateTimeOffset lockedUntil, DateTimeOffset expiresAt, int statusProcessing)
    {
        // 普通 INSERT + 唯一冲突捕获（ITM-228 口径：MySQL INSERT IGNORE 会吞非重复键错误）
        const string insertSql = """
        INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error)
        VALUES (@OperationName, @Key, @Status, @LockedUntil, @ExpiresAt, @UpdatedAt, NULL, NULL)
        """;
        try
        {
            return await _connection.ExecuteAsync(insertSql,
                new { OperationName = operationName, Key = key, Status = statusProcessing, LockedUntil = ToTimeParam(lockedUntil), ExpiresAt = ToTimeParam(expiresAt), UpdatedAt = ToTimeParam(now) },
                Tx).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            return 0;
        }
    }

    private async ValueTask<ConflictView?> GetClassificationConflictViewAsync(
        string operationName, string key, DateTimeOffset now, CancellationToken ct)
    {
        var info = await GetForConflictClassificationAsync(operationName, key, now, ct).ConfigureAwait(false);
        return info is null
            ? null
            : new ConflictView(info.Value.Status, info.Value.LockedUntil, info.Value.ExpiresAt, info.Value.Revision, null);
    }

    private async ValueTask<ConflictView?> GetFullConflictViewAsync(
        string operationName, string key, DateTimeOffset now, CancellationToken ct)
    {
        var record = await GetAsync(operationName, key, now, ct).ConfigureAwait(false);
        return record is null
            ? null
            : new ConflictView(record.Status, record.LockedUntil, record.ExpiresAt, record.Revision, record);
    }

    /// <summary>冲突视图：轻量分类读（Record=null）或整行读（Record=完整记录）的统一形态。</summary>
    private readonly record struct ConflictView(
        IdempotencyRecordStatus Status, DateTimeOffset LockedUntil, DateTimeOffset ExpiresAt,
        long Revision, IdempotencyRecord? Record);

    /// <inheritdoc />
    /// <remarks>
    /// 2026-10-20 性能轮：处理器走单调用后，新 key/过期回收路径省掉处理器侧的前置
    /// <see cref="GetAsync"/>（新 key 2→1 往返）；已存在且活跃/已完成时 Insert 冲突后回读
    /// 完整记录返回 Existing。与 EF 栈同为 Insert 优先，重复请求多一次冲突探测，
    /// 换取新 key 主路径的一次往返。
    /// </remarks>
    public async ValueTask<IdempotencyStartResult> StartOrGetAsync(
    string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy, CancellationToken ct = default)
    {
        // 2026-10-21：PG/SQLite 走单语句 UPSERT。新建、过期回收、失败重试三种“可启动”
        // 情形由一条 INSERT ... ON CONFLICT DO UPDATE ... RETURNING 判定并落库；只有
        // “已被完成或正被占用”时才追加一次读取。
        // 2026-10-24（#43）：MySQL 等无 RETURNING 方言改为 INSERT + 整行读一次；
        // 旧路径是 INSERT + 四列分类读 + 二次整行读（冲突侧 3 条语句），现在 2 条。
        if (_dbType is DapperDbType.PostgreSql or DapperDbType.Sqlite)
            return await StartOrGetAtomicAsync(operationName, key, now, policy, ct).ConfigureAwait(false);

        var (started, existing) = await TryStartCoreAsync(
            operationName, key, now, policy, fullConflictRead: true, ct).ConfigureAwait(false);
        if (started is not null)
            return new IdempotencyStartResult(IdempotencyStartOutcome.Started, started);
        return existing is null
            ? new IdempotencyStartResult(IdempotencyStartOutcome.Contended, null)
            : new IdempotencyStartResult(IdempotencyStartOutcome.Existing, existing);
    }

    /// <summary>
    /// PG/SQLite 单语句启动或读取：UPSERT 的 WHERE 精确复刻“过期 / Failed / 租约过期”
    /// 三种可回收条件；返回行即本次取得执行权，零行即既有记录仍有效，再读一次返回。
    /// </summary>
    private async ValueTask<IdempotencyStartResult> StartOrGetAtomicAsync(
        string operationName, string key, DateTimeOffset now, IdempotencyPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        await EnsureOpenAsync(ct).ConfigureAwait(false);
        var lockedUntil = now + policy.ProcessingTimeout;
        var expiresAt = now + policy.Retention;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;
        var statusFailed = (int)IdempotencyRecordStatus.Failed;

        var revision = await _connection.QueryFirstOrDefaultAsync<long?>(
            AtomicStartOrGet,
            new
            {
                OperationName = operationName,
                Key = key,
                Status = statusProcessing,
                Failed = statusFailed,
                LockedUntil = ToTimeParam(lockedUntil),
                ExpiresAt = ToTimeParam(expiresAt),
                UpdatedAt = ToTimeParam(now),
                Now = ToTimeParam(now)
            },
            Tx).ConfigureAwait(false);

        if (revision is { } startedRevision)
        {
            // revision=0 为新建，>0 为回收换代；两者都返回 Processing 记录供调用方继续执行。
            return new IdempotencyStartResult(
                IdempotencyStartOutcome.Started,
                new IdempotencyRecord(
                    operationName, key, IdempotencyRecordStatus.Processing,
                    lockedUntil, expiresAt, now, startedRevision));
        }

        // 零行：既有记录为 Completed 或租约未过期的 Processing。读取一次返回其快照。
        var existing = await GetAsync(operationName, key, now, ct).ConfigureAwait(false);
        return existing is null
            ? new IdempotencyStartResult(IdempotencyStartOutcome.Contended, null)
            : new IdempotencyStartResult(IdempotencyStartOutcome.Existing, existing);
    }

    /// <summary>
    /// PG/SQLite 原子启动 SQL。状态值：0=Processing、1=Completed、2=Failed。
    /// 可回收条件与 <see cref="TryStartAsync"/> 的逐语句分支逐条等价：过期（任意状态）、
    /// Failed、租约过期的 Processing；Completed 未过期不回收。
    /// </summary>
    private const string AtomicStartOrGet = """
        INSERT INTO idempotency_records
            (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error)
        VALUES (@OperationName, @Key, @Status, @LockedUntil, @ExpiresAt, @UpdatedAt, NULL, NULL)
        ON CONFLICT (operation_name, idempotency_key) DO UPDATE
        SET status = @Status,
            locked_until = @LockedUntil,
            expires_at = @ExpiresAt,
            updated_at = @UpdatedAt,
            error = NULL,
            response_payload = NULL,
            revision = idempotency_records.revision + 1
        WHERE idempotency_records.expires_at <= @Now
           OR idempotency_records.status = @Failed
           OR (idempotency_records.status = @Status AND idempotency_records.locked_until <= @Now)
        RETURNING revision
        """;

    /// <summary>
    /// B5（EPSL 地毯审计 2026-10-16）：TryStart 冲突路径的轻量读——原 GetAsync 读全行
    /// （含 response_payload 大列），而冲突分类只用 status/locked_until/expires_at/revision
    /// 四列；Completed 情形的 payload 本就丢弃（调用方二次 GetAsync 获取），构成双传。
    /// 本查询只取四列，过期语义与 GetAsync 逐字一致（ExpiresAt &lt;= now 返回 null）。
    /// MySQL DATETIME Kind 归一同 GetAsync（ToUtcOrMin）。
    /// </summary>
    private async ValueTask<IdempotencyConflictInfo?> GetForConflictClassificationAsync(
        string operationName, string key, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string sql = """
            SELECT operation_name, idempotency_key, status, locked_until, expires_at, revision
            FROM idempotency_records
            WHERE operation_name = @OperationName AND idempotency_key = @Key
            """;
        var row = await _connection.QueryFirstOrDefaultAsync<IdempotencyConflictRow>(
            sql, new { OperationName = operationName, Key = key }, Tx)
            .ConfigureAwait(false);
        if (row is null) return null;

        var expiresAt = ToUtcOrMin(row.ExpiresAt);
        if (expiresAt <= now) return null;

        return new IdempotencyConflictInfo(
            (IdempotencyRecordStatus)row.Status,
            ToUtcOrMin(row.LockedUntil),
            expiresAt,
            row.Revision);
    }

    /// <summary>B5：冲突路径轻量读行（六列，无 payload/error/updated_at；internal 可见性
    /// 对齐 IdempotencyRow——Dapper 动态方法 IL 发射对 private 嵌套类型的属性 setter
    /// 可访问性走不同路径）。</summary>
    /// CA1812 抑制：实例化由 Dapper 内部反射完成，编译器不可见（同 IdempotencyRow 先例）。
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper 行映射经反射实例化，编译器静态不可见。")]
    internal sealed class IdempotencyConflictRow
    {
        public string OperationName { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public int Status { get; set; }
        public DateTimeOffset LockedUntil { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public long Revision { get; set; }
    }

    /// <summary>B5：冲突分类所需的最小信息（替代轻量读处的完整 IdempotencyRecord）。</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct IdempotencyConflictInfo(
        IdempotencyRecordStatus Status, DateTimeOffset LockedUntil, DateTimeOffset ExpiresAt, long Revision);

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureOpenAsync(ct).ConfigureAwait(false);
        // 2026-10-20：精确数组直接交还，避免持久化边界的第二次整载荷复制。
        var payloadBytes = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(responsePayload, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length
            ? array
            : responsePayload.ToArray();
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
        // 2026-10-21 评审 F3：三方言时间编码收口——原强类型 DateTimeOffset 经生成拦截器
        // 绑成 DateTimeOffset（SQLite 为 "O" 文本），与 TryStart/Checkpoint 的 ToTimeParam
        // 形态不一致；统一走 ToTimeParam。
        dp.Add("UpdatedAt", ToTimeParam(completedAt));
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
        await EnsureOpenAsync(ct).ConfigureAwait(false);
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
                // 2026-10-21 评审 F3：与 TryStart/MarkCompleted/Checkpoint 统一时间绑定形态。
                UpdatedAt = ToTimeParam(failedAt),
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

    /// <summary>确保连接已打开（与 DapperInbox/Saga/Outbox 同款惰性打开语义）。</summary>
    private async ValueTask EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);
    }

    /// <summary>ITM-242 口径：UTC 归一化（MySQL DATETIME Kind=Unspecified → offset 0）。</summary>
    private static DateTimeOffset ToUtcOrMin(DateTimeOffset value)
        => value == default
            ? value
            : new DateTimeOffset(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Utc), TimeSpan.Zero);


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
