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
            SqlTemplates.OutboxSelectPending,
            new { status = OutboxStatus.Pending.ToString(), now, maxRetryCount, n = batchSize }, _transaction).ConfigureAwait(false);
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
                SqlTemplates.OutboxLeaseUpdate + $"({leaseSubSql}) RETURNING *",
                new { owner, until, now, maxRetryCount, n = batchSize }, _transaction).ConfigureAwait(false);
            return msgs.AsList();
        }
        else
        {
            await conn.ExecuteAsync(
                SqlTemplates.OutboxLeaseUpdate + $"({leaseSubSql})",
                new { owner, until, now, maxRetryCount, n = batchSize }, _transaction).ConfigureAwait(false);

            // 🔴 P0 修复：按租约标识回读，不重新评估子查询
            var msgs = await conn.QueryAsync<OutboxMessage>(
                SqlTemplates.OutboxSelectByLease,
                new { owner, until }, _transaction).ConfigureAwait(false);
            return msgs.AsList();
        }
    }

    public void AddMessage(OutboxMessage message)
    {
        var c = EnsureOpen();
        c.Execute(SqlTemplates.OutboxInsert,
            new { message.Id, message.Type, message.Payload, message.ContentType, message.SchemaVersion, CreatedAt = _timeProvider.GetUtcNow() }, _transaction);
    }

    /// <summary>批量添加消息 — 自动选择数据库最优批量路径</summary>
    public async ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        if (messages.Count == 0) return 0;
        var conn = await EnsureOpenAsync().ConfigureAwait(false);
        return await DapperBulkCopy.BulkInsertAsync(
            conn, _dbType, "outbox_messages",
            ["id", "type", "payload", "content_type", "schema_version", "status", "created_at"],
            messages,
            m => [m.Id, m.Type, m.Payload, m.ContentType, m.SchemaVersion, OutboxStatus.Pending.ToString(), _timeProvider.GetUtcNow()]);
    }

    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        var c = EnsureOpen();
        c.Execute(SqlTemplates.OutboxMarkProcessed,
            new { at = processedAt, id = message.Id }, _transaction);
    }

    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        var c = EnsureOpen();
        c.Execute(SqlTemplates.OutboxMarkDead,
            new { reason = failureReason, at = deadAt, id = message.Id }, _transaction);
    }

    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        var c = EnsureOpen();
        c.Execute(SqlTemplates.OutboxReleaseForRetry,
            new { reason = failureReason, next = nextAttemptAt, id = message.Id }, _transaction);
    }

    public async ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        var now = _timeProvider.GetUtcNow();
        var audit = $"requeued by {retriedBy} at {now:O}";
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(
            SqlTemplates.OutboxRequeueDead,
            new { audit, next = nextAttemptAt, id = messageId }, _transaction).ConfigureAwait(false);
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
}
