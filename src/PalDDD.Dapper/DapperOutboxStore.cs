// ─────────────────────────────────────────────────────────────
// 📤 DapperOutboxStore — 纯 Dapper SQL（snake_case 映射）
// ─────────────────────────────────────────────────────────────
//
// 💡 发件箱模式（Outbox Pattern）是什么？
//   ｜ 业务操作（如"创建订单"）需要同时做两件事：
//   ｜   1. 持久化订单数据到数据库
//   ｜   2. 发布"订单已创建"事件给其他系统
//   ｜
//   ｜ 如果分两步做（先写数据库、再发消息），可能出现不一致：
//   ｜   - 数据库写成功、消息发送失败 → 其他系统不知道订单已创建
//   ｜   - 数据库写失败、消息已发送 → 其他系统收到了不存在的事件
//   ｜
//   ｜ 发件箱模式解决这个问题：
//   ｜   把"事件"当作数据一起写入数据库（同一事务），
//   ｜   后台处理器（OutboxProcessor）异步读取并发布事件。
//   ｜   保证了"数据库和消息"的最终一致性。
//   ｜
//   ｜ 这个类的职责：
//   ｜   1. 写入事件到 outbox_messages 表（与业务数据在同一事务）
//   ｜   2. 原子租约获取（多实例部署时避免重复发布）
//   ｜   3. 标记已处理/死信/重试
//
// ✅ AOT 安全性：
//   ✅ Dapper.QueryAsync<T> + MatchNamesWithUnderscores
//      自动将 snake_case 列名映射到 PascalCase 属性
//      纯字符串操作（Split('_') + 拼接），零反射
//   ✅ DapperDbType 枚举分发 — 编译时已知值，零运行时类型推断
//   ✅ DapperBulkCopy — Func<T, object[]> 委托，零反射
//   ⚠️ 运行时 Dapper IL 发射在 NativeAOT 下不可用 — Dapper Store 适配器层
//      依赖 DbConnection 运行时注入，本身不参与 AOT 发布（AotSample 不引用 Dapper Store）
//
// ⚡ 性能：
//   ✅ 查询使用手写 SQL + Dapper 执行
//   ✅ 批量插入使用 DapperBulkCopy（PG COPY / MySQL BulkCopy / SQLite 事务）
//   ✅ ConfigureAwait(false) — 所有异步调用避免 SynchronizationContext 捕获
//
// 📐 DDD 位置：基础设施层 — 实现 IPalOutboxStore 接口，不涉及领域逻辑。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Data.Common;
using PalUlid = ByteAether.Ulid.Ulid;

using PalDDD.Transactions;
namespace PalDDD.Dapper;

/// <summary>
/// Dapper 发件箱存储 — 实现 <see cref="IPalOutboxStore"/> 接口。<br/>
/// 使用纯 Dapper SQL 执行。
/// </summary>
/// <remarks>
/// 💡 构造参数说明：
///   <br/>- <paramref name="connection"/>: ADO.NET 数据库连接（由 DI 容器管理生命周期）
///   <br/>- <paramref name="dbType"/>: 数据库类型枚举（用于选择 SQL 方言分支）
///   <br/>- <paramref name="transaction"/>: 可选共享事务（UnitOfWork 模式下使用）
/// <br/><br/>
/// ⚠️ <b>连接生命周期</b>：<paramref name="connection"/> 由 DI 容器管理（通常为 Scoped），
/// 调用方不应调用 <c>Close()</c>/<c>Dispose()</c>。EnsureOpen/EnsureOpenAsync 仅确保连接状态，不拥有连接所有权。
/// </remarks>
public sealed class DapperOutboxStore : IPalOutboxStore
{
    private readonly DbConnection _connection;
    private readonly DapperDbType _dbType;
    private readonly DapperSqlDialect _dialect;
    private readonly DbTransaction? _transaction;
    private readonly TimeProvider _timeProvider;

    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）</param>
    public DapperOutboxStore(
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

    public async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // 🟡 P1 修复 (2026-06-21): 替换 SqlKata.QueryFactory.GetAsync 为纯 Dapper SQL
        // 直接使用 Dapper.QueryAsync<OutboxMessage> 走 Dapper.AOT 拦截器路径。
        var messages = await conn.QueryAsync<OutboxMessage>(
            new CommandDefinition(
                SqlTemplates.OutboxSelectPending,
                new { status = OutboxStatus.Pending.ToString(), now = ToTimeParam(now), maxRetryCount, n = batchSize },
                _transaction, cancellationToken: ct)).ConfigureAwait(false);
        return messages.AsList();
    }

    public async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var until = now.Add(leaseDuration);

        // 🔴 P0 修复：参数化 lease 子查询，消除 SQL 注入 + 格式不一致
        //    原实现用字符串插值 now:O 拼接 SQL，与参数化写入格式可能不一致导致比较错配。
        //    改为 @now/@n 参数化，与 OutboxSelectPending 风格一致。
        var leaseSubSql =
            "SELECT id FROM outbox_messages WHERE status='Pending' AND retry_count<@maxRetryCount" +
            " AND (next_attempt_at IS NULL OR next_attempt_at<=@now)" +
            " AND (locked_until IS NULL OR locked_until<=@now)" +
            " ORDER BY created_at LIMIT @n";

        // ⚡ 跨数据库 UPDATE + RETURN 语法
        //    PG：UPDATE ... RETURNING * — 单次 SQL 原子租约获取 + 回读
        //    非 PG：两步——UPDATE 锁定后，按 locked_by/until 回读精确匹配本次租约
        //    🔴 P0 修复 (2026-06-21)：原实现第二步重新执行子查询，由于 locked_until 已被更新，
        //    子查询条件 (locked_until<=now) 会把刚锁定行排除，导致结果集为空。
        //    改用 OutboxSelectByLease 按租约标识回读，消除并发窗口。

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        if (_dialect.SupportsOutboxReturning)
        {
            var msgs = await conn.QueryAsync<OutboxMessage>(
                new CommandDefinition(
                    SqlTemplates.OutboxLeaseUpdate + $"({leaseSubSql} FOR UPDATE SKIP LOCKED) RETURNING *",
                    new { owner, until = ToTimeParam(until), now = ToTimeParam(now), maxRetryCount, n = batchSize },
                    _transaction, cancellationToken: ct)).ConfigureAwait(false);
            return msgs.AsList();
        }
        else
        {
            // P1 修复（十一轮·实测发现）：MySQL 不支持 UPDATE ... WHERE id IN (SELECT ... LIMIT)
            // （真实库实测报 1235）——JOIN 形态替代（对齐 PalORM 版）；SQLite 支持子查询内 LIMIT 保持原状
            var leaseSql = _dbType == DapperDbType.MySql
                ? SqlTemplates.OutboxLeaseUpdateMySql
                : SqlTemplates.OutboxLeaseUpdate + $"({leaseSubSql})";
            await conn.ExecuteAsync(
                new CommandDefinition(
                    leaseSql,
                    new { owner, until = ToTimeParam(until), now = ToTimeParam(now), maxRetryCount, n = batchSize },
                    _transaction, cancellationToken: ct)).ConfigureAwait(false);

            // 🔴 P0 修复：按租约标识回读，不重新评估子查询
            var msgs = await conn.QueryAsync<OutboxMessage>(
                new CommandDefinition(
                    SqlTemplates.OutboxSelectByLease,
                    new { owner, until = ToTimeParam(until) },
                    _transaction, cancellationToken: ct)).ConfigureAwait(false);
            return msgs.AsList();
        }
    }

    public void AddMessage(OutboxMessage message)
    {
        var c = EnsureOpen();
        // P2 修复（七轮评审）：补 correlation/causation/trace 4 列——此前模板加了列但参数对象未传
        c.Execute(SqlTemplates.OutboxInsert,
            new
            {
                Id = DapperAotInitializer.ToSqliteParameter(message.Id),
                message.Type,
                message.Payload,
                message.ContentType,
                message.SchemaVersion,
                CreatedAt = ToTimeParam(_timeProvider.GetUtcNow()),
                CorrelationId = message.CorrelationId?.ToString(),
                CausationId = message.CausationId?.ToString(),
                message.TraceParent,
                message.TraceState
            }, _transaction);
    }

    /// <summary>批量添加消息 — 自动选择数据库最优批量路径。
    /// <para>⚠️ <b>已知限制（P1）</b>：批量插入不参与 UnitOfWork 外部事务（BulkCopy 各方言自管事务）。
    /// 如需事务原子性，使用单条 <see cref="AddMessage"/>。后续可扩展 BulkInsertAsync 传 transaction 参数。
    /// </para>
    /// </summary>
    public async ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        if (messages.Count == 0) return 0;
        var conn = await EnsureOpenAsync().ConfigureAwait(false);
        // P2 修复（八轮评审 PD17）：批量路径补 correlation/causation/trace 4 追踪列——
        // 单条路径 AddMessage（七轮）已补，批量漏列导致追踪链在批量写入时丢失；
        // extractor 末 4 项与单条 AddMessage 的参数语义逐一对齐。
        return await DapperBulkCopy.BulkInsertAsync(
            conn, _dbType, "outbox_messages",
            ["id", "type", "payload", "content_type", "schema_version", "status", "created_at", "correlation_id", "causation_id", "trace_parent", "trace_state"],
            messages,
            m => [m.Id, m.Type, m.Payload, m.ContentType, m.SchemaVersion, OutboxStatus.Pending.ToString(), _timeProvider.GetUtcNow(),
                m.CorrelationId?.ToString(), m.CausationId?.ToString(), m.TraceParent, m.TraceState]);
    }

    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        var c = EnsureOpen();
        // P1 修复（八轮评审）：时间参数统一走 ToTimeParam——ToSqliteParameter 产出 "O" string，
        // PG 下 timestamptz 列收 text 参数无比较/赋值运算符（详见 ToTimeParam 的 PG 分支注释）
        c.Execute(SqlTemplates.OutboxMarkProcessed,
            new { at = ToTimeParam(processedAt), id = DapperAotInitializer.ToSqliteParameter(message.Id) }, _transaction);
    }

    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        var c = EnsureOpen();
        c.Execute(SqlTemplates.OutboxMarkDead,
            new { reason = failureReason, at = ToTimeParam(deadAt), id = DapperAotInitializer.ToSqliteParameter(message.Id) }, _transaction); // P1 修复（八轮评审）：时间参数走 ToTimeParam
    }

    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        var c = EnsureOpen();
        // P2 修复（八轮评审）：补租约守卫（对齐 PalORM 版 PalOrmOutboxStore）——租约过期被其他 worker
        // 抢占后，原 worker 的失败释放不再清掉新 worker 的锁并误增 retry_count；被他人持有时
        // affected=0。owner 取调用时快照 message.LockedBy（string? 直传，调用方 OutboxBatchProcessor
        // 在租约回读后未清空该字段）。
        c.Execute(SqlTemplates.OutboxReleaseForRetry,
            new { reason = failureReason, next = ToTimeParam(nextAttemptAt), id = DapperAotInitializer.ToSqliteParameter(message.Id), owner = message.LockedBy }, _transaction);
    }

    public async ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        var now = _timeProvider.GetUtcNow();
        var audit = $"requeued by {retriedBy} at {now:O}";
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // P3 修复（八轮评审）：ExecuteAsync 改 CommandDefinition 传 ct——原重载不接收取消令牌，
        // 取消信号在 RequeueDead 执行阶段不可传递；EnsureOpenAsync(ct) 此前已传。
        return await conn.ExecuteAsync(
            new CommandDefinition(
                SqlTemplates.OutboxRequeueDead,
                new { audit, next = ToTimeParam(nextAttemptAt), id = DapperAotInitializer.ToSqliteParameter(messageId) },
                _transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    public ValueTask<int> SaveChangesAsync(CancellationToken ct) => ValueTask.FromResult(0);

    /// <summary>
    /// 确保数据库连接已打开（同步版本，用于同步方法路径）。
    /// 连接生命周期由 DI 容器管理的 Scoped DbConnection 控制，此处不负责关闭。
    /// </summary>
    private DbConnection EnsureOpen()
    {
        var conn = _connection;
        if (conn.State != ConnectionState.Open) conn.Open();
        return conn;
    }

    /// <summary>
    /// 确保数据库连接已打开（异步版本，避免线程池阻塞）。
    /// 连接生命周期由 DI 容器管理的 Scoped DbConnection 控制，此处不负责关闭。
    /// </summary>
    private async ValueTask<DbConnection> EnsureOpenAsync(CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    // P3 修复（八轮评审）：XML doc 错位修复——ToTimeParam 的 summary 此前叠放在 EnsureOpenAsync
    // 的 summary 之后，导致 EnsureOpenAsync 出现两段 <summary>、ToTimeParam 反而无 doc。
    /// <summary>
    /// P2 修复（四轮评审 ToMySqlParameter 接线）：按方言选择时间参数格式——
    /// MySQL DATETIME(6) 列与带偏移 "O" 格式比较依赖 session tz，统一无偏移 UTC。
    /// <para>
    /// P2/P3 修复（十七轮）：返回 <c>object</c>（DateTimeOffset 装箱一次）是刻意的收口防线——
    /// 强类型返回会诱导调用方绕过本方法自行格式化，方言错配（PG text OID / MySQL session tz）
    /// 将重新进入；五 Store 同款声明（Outbox/Inbox/Saga/EventLog/Checkpoint）。装箱开销相对 SQL 执行成本可忽略。
    /// </para>
    /// </summary>
    private object ToTimeParam(DateTimeOffset value)
        => _dbType switch
        {
            // P1 修复（八轮评审）：Npgsql 原生映射 DateTimeOffset→timestamptz；"O" string 按 text OID 发送，
            // timestamptz <= text 无比较运算符，WHERE 必炸（此前 PG 走默认分支产 "O" string）
            DapperDbType.PostgreSql => value,
            DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
            _ => DapperAotInitializer.ToSqliteParameter(value),
        };
}
