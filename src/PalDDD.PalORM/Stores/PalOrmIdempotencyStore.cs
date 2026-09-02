using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalDDD.Core;
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
    public PalOrmIdempotencyStore(DataSession<TProvider> session)
    {
        ArgumentNullException.ThrowIfNull(session); // v22 C-1
        Session = session;
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> GetAsync(
        string operationName, string key, DateTimeOffset now, CancellationToken ct = default)
    {
        // 复合主键表未注册实体 —— 用 GetRawConnection + 手动 reader（QueryFirstAsync 对未注册类型返回空对象）
        // ITM-243：经 CreateRawCommand 挂接 IUnitOfWork 环境事务
        await using var cmd = CreateRawCommand();
        cmd.CommandText = "SELECT operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error, revision FROM idempotency_records WHERE operation_name = @p0 AND idempotency_key = @p1";
        AddParam(cmd, "@p0", operationName);
        AddParam(cmd, "@p1", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        // v43 P2：locked_until/expires_at/updated_at 三时间戳列补 IsDBNull 容错——此前仅
        // response_payload/error 两列（姊妹 6/7）有 IsDBNull 检查，3/4/5 直读 GetDateTime 遇
        // DBNull 抛 provider 空值异常（正常写入路径三列恒非空，损坏行/手工数据路径无容错）。
        // 容错口径（分支形态镜像 PalOrmSagaStateStore.ReadSagaRow 的 IsDBNull 三元式；回退
        // DateTimeOffset.MinValue 而非 null——IdempotencyRecord 构造三时间戳为非空契约）：
        // expires_at=MinValue 使 GetAsync 视为已过期返回 null（无法判定的记录不当作有效放行）；
        // locked_until=MinValue 使 Processing 租约判定恒过期、不阻塞重租；updated_at=MinValue
        // 作为乐观锁基准时 UPDATE 的 NULL 等值比较恒不命中，Mark* 不写本地对象（P1-3 行为：
        // affected=0 不假装成功）。
        // v54 P2：internal 物化构造携带 DB 真值 revision（public ctor 恒 0，CAS 基准失配）；
        // revision 列 DBNull 容错回退 0（对齐三时间戳容错口径——损坏行不炸物化）
        var record = new IdempotencyRecord(
            reader.GetString(0), reader.GetString(1),
            (IdempotencyRecordStatus)reader.GetInt32(2),
            GetUtcOrMin(reader, 3), GetUtcOrMin(reader, 4), GetUtcOrMin(reader, 5),
            reader.IsDBNull(8) ? 0 : reader.GetInt64(8));

        if (record.ExpiresAt <= now) return null;

        if (!reader.IsDBNull(6))
        {
            // 原生二进制列（bytea/BLOB/LONGBLOB）——与 EFCore 栈 ResponsePayload 的 byte[] 转换对齐。
            // ITM-277（R43）往返保真：空 bytea（成功完成 + 空响应体，落库为空字节序列而非 NULL）
            // 同样回放 MarkCompleted——原 `Length > 0` 守卫使空响应读回 ResponsePayload=null，
            // 与"无响应"不可区分，幂等命中方以 null 判"无可复用响应"会重放副作用。
            if (record.Status == IdempotencyRecordStatus.Completed)
            {
                // v54 P2：回放改 RestoreTerminalState——借用 Mark* 会使 Revision 偏移 +1，
                // 该 record 若被用作 CAS 基准（TryStart 抢占路径）恒失配
                record.RestoreTerminalState(reader.GetFieldValue<byte[]>(6), null);
            }
        }
        if (!reader.IsDBNull(7) && record.Status == IdempotencyRecordStatus.Failed)
        {
            record.RestoreTerminalState(null, reader.GetString(7)); // v54 P2：回放不递增 Revision
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
            catch (Exception ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
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
            // 三十七轮 P2-5 → v8 评审 P2-2 修正：过期回收不再排除 Completed——原
            // `AND status <> Completed` 守卫使"过期 Completed"记录 affected=0 → return null
            // → 该幂等 key 永久拒绝，而 EFCore（ExpiresAt<=now 即回收）与 InMemory（过期
            // Remove 重建）同场景均可重新执行，三栈分叉。expires_at <= now 已含过期语义；
            // 该守卫真正要防的"未过期 Completed 被回收"由 expires_at 条件天然排除。
            // 三栈契约自此统一：过期即回收（无论终态），Retention 语义 = 可重新执行窗口。
            // v54 P2：补 revision = revision + 1（换代）——Mark* 的 CAS 基准从 updated_at 换 revision
            //（时间戳受 DB 列精度截断，同刻双 worker 回收可双双命中绕过幂等）
            affected = await Session.ExecuteAsync(
                $"UPDATE idempotency_records SET status = {statusProcessing}, locked_until = {lockedUntil}, expires_at = {expiresAt}, updated_at = {now}, error = NULL, response_payload = NULL, revision = revision + 1 WHERE operation_name = {operationName} AND idempotency_key = {key} AND expires_at <= {now}",
                ct).ConfigureAwait(false);
            if (affected == 0) return null;

            // 换代后回读 DB 新 revision（existing is null 分支无内存基准可算；低频路径可接受一次回读）
            await using var readBack = CreateRawCommand();
            readBack.CommandText = "SELECT revision FROM idempotency_records WHERE operation_name = @p0 AND idempotency_key = @p1";
            AddParam(readBack, "@p0", operationName);
            AddParam(readBack, "@p1", key);
            await using var rr = await readBack.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var newRevision = await rr.ReadAsync(ct).ConfigureAwait(false) ? rr.GetInt64(0) : 0;

            return new IdempotencyRecord(operationName, key,
                IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, newRevision);
        }

        // ITM-078 修复：Completed 非过期记录返回 null（语义=他人已持有终态，本调用未获得租约）——
        // 契约对齐：EFCore（IdempotencyDbContext.TryStartAsync）与 InMemory（InMemoryIdempotencyStore）
        // 对非过期终态记录均返回 null，读取已完成响应走 GetAsync（含 response_payload）；
        // 原实现返回 existing 会让调用方把终态记录误当"本次已开始处理"
        if (existing.Status == IdempotencyRecordStatus.Completed)
            return null;

        if (existing.Status == IdempotencyRecordStatus.Processing && existing.LockedUntil > now)
            return null;

        // v54 P2：CAS 基准 updated_at → revision（existing.Revision 经 internal ctor 物化，
        // 是 DB 真值；回放走 RestoreTerminalState 不污染）。换代后内存可算新值（existing+1），无需回读
        var expectedRevision = existing.Revision;
        affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusProcessing}, locked_until = {lockedUntil}, expires_at = {expiresAt}, updated_at = {now}, error = NULL, response_payload = NULL, revision = revision + 1 WHERE operation_name = {operationName} AND idempotency_key = {key} AND revision = {expectedRevision} AND status <> {(int)IdempotencyRecordStatus.Completed}",
            ct).ConfigureAwait(false);
        if (affected == 0) return null;

        return new IdempotencyRecord(operationName, key,
            IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, expectedRevision + 1);
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        // ITM-163 修复：补 record null 守卫（对齐 IdempotencyDbContext/InMemoryIdempotencyStore）
        ArgumentNullException.ThrowIfNull(record);
        var expectedRevision = record.Revision;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        var statusProcessing = (int)IdempotencyRecordStatus.Processing;
        // 原生 byte[] 参数（PalORM ≥5.3 DbType.Binary 显式分派）——列类型 bytea/BLOB/LONGBLOB
        var payloadBytes = responsePayload.ToArray();
        // v54 P2：CAS 基准 updated_at → revision + 补 status = Processing 守卫
        //（对齐 EFCore 版 MarkCompletedAsync 本地守卫与 MarkFailedAsync 的 SQL 守卫形态——
        // 直调终态实例不再翻转状态落库）
        var affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusCompleted}, updated_at = {completedAt}, response_payload = {payloadBytes}, error = NULL, revision = revision + 1 WHERE operation_name = {record.OperationName} AND idempotency_key = {record.Key} AND revision = {expectedRevision} AND status = {statusProcessing}",
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
        // v26 P3 截断族：failureReason 入库前 Core.FailureReason.Normalize 截断（对齐
        // PalOrmOutboxStore ITM-082 存储层兜底先例，收敛到 Core 公共方法：2000 上限 +
        // 空白归一）——超长 ex.Message 会让 MarkFailed 的持久化抛列截断异常（error 列
        // 上限 2048 族），终态保存本身失败掩盖原始异常（ITM-167/175）；本地对象同步截断值
        var reason = FailureReason.Normalize(failureReason);
        var expectedRevision = record.Revision;
        var statusFailed = (int)IdempotencyRecordStatus.Failed;
        var statusCompleted = (int)IdempotencyRecordStatus.Completed;
        // v54 P2：CAS 基准 updated_at → revision（单调令牌不受列精度截断）
        var affected = await Session.ExecuteAsync(
            $"UPDATE idempotency_records SET status = {statusFailed}, updated_at = {failedAt}, error = {reason}, revision = revision + 1 WHERE operation_name = {record.OperationName} AND idempotency_key = {record.Key} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct).ConfigureAwait(false);
        if (affected > 0)
            record.MarkFailed(reason, failedAt);
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
    /// v43 P2：时间戳列 DBNull 容错读取——DBNull 回退 DateTimeOffset.MinValue（容错口径见
    /// <see cref="GetAsync"/> 注释），非空走 <see cref="GetUtc"/>。
    /// </summary>
    private static DateTimeOffset GetUtcOrMin(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? default : GetUtc(reader, ordinal);

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
}
