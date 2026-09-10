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
// AOT 状态（v11 勘正——旧"零反射"块与实际矛盾）：
//   ⚠️ 运行时经典 Dapper 路径（QueryAsync<T> 物化经 IL 发射，AOT 下退化为反射）——
//      真 AOT 不可达；csproj IsAotCompatible=true 是"编译无警告"口径而非运行时承诺
//      （详见 DapperBulkCopy IL2062 注释与 csproj Description）。snake_case 映射
//      （MatchNamesWithUnderscores）本身是纯字符串操作，但物化整链含反射。
//   ✅ DapperDbType 枚举分发 / DapperBulkCopy 委托提取 — 这两处确为零反射。
//   栈级 AOT 策略见 ADR-020（Dapper 退役；Native AOT 场景用 PalORM）。
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
using PalDDD.Core;
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
    /// <summary>生效事务（二轮评审 T5）：显式构造参数优先，否则查同连接 DapperUnitOfWork
    /// 的 ambient 活动事务——DI 解析的 Store（构造时无事务）也能参与 UoW 事务边界。</summary>
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);

    private readonly TimeProvider _timeProvider;

    // 三十八轮统一（状态列 int 化）：status 列持久化契约从字符串 'Pending' 改为 INT 0
    // （对齐 OutboxStatus.Pending 枚举值与 Saga/Checkpoint/Idempotency 三表的既定 int 语义，
    // docs/sql DDL 同步）。编译期常量保留原优化意图——避免热路径枚举格式化分配。
    private const int StatusPending = 0;

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
        // v30 P3 守卫族：batchSize 非正守卫——镜像全族 ThrowIfNegativeOrZero 形态
        //（PalOrmOutboxStore.GetPendingMessagesAsync / OutboxDbContext.GetPendingMessagesAsync）——
        // LIMIT 0/负在各方言下静默空返回，无诊断
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var now = _timeProvider.GetUtcNow();
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // 🟡 P1 修复 (2026-06-21): 替换 SqlKata.QueryFactory.GetAsync 为纯 Dapper SQL
        // 直接使用 Dapper.QueryAsync<OutboxMessage>（v10 勘正：走运行时经典 Dapper 路径——
        // AOT 拦截未启用，与 csproj IsAotCompatible=true 的差异见 DapperBulkCopy IL2062 注释）。
        var messages = await conn.QueryAsync<OutboxMessage>(
            new CommandDefinition(
                SqlTemplates.OutboxSelectPending,
                new { status = StatusPending, now = ToTimeParam(now), maxRetryCount, n = batchSize },
                Tx, cancellationToken: ct)).ConfigureAwait(false);
        return messages.AsList();
    }

    public async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner); // v22 C-2：对齐 EFCore 四方言 ITM-081/216
        // v30 P3 守卫族：batchSize 非正守卫（同 GetPendingMessagesAsync——PalORM/EFCore 姊妹
        // 的 Lease 路径均已补）；子查询 LIMIT 0/负静默空返回，无诊断
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // v27 P3 守卫族（B 片 N3）：leaseDuration 双检守卫（v25/v26 守卫族最后缺口）——镜像
        // DapperSagaStateStore.LeaseActiveSagasAsync 守卫形态。非正租约使租约即刻过期/永不过期
        // 语义错乱（OutboxProcessor 默认配置不触发，此处是直调路径的防御性 fail-fast）；上界
        // 检查为与姊妹对称保留——Dapper 版绑定原生 @until 参数（ToTimeParam 产 DateTimeOffset/
        // "O" 串），无秒数换算（消息文本对齐 PalOrmOutboxStore/EFCore 四方言 LockedUntil 同款）
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LockedUntil value.");
        var now = _timeProvider.GetUtcNow();
        var until = now.Add(leaseDuration);

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
                    // 优化（二十五轮 API 扫描 A-3）：原运行时插值 OutboxLeaseUpdate + $"({leaseSubSql}
                    // FOR UPDATE SKIP LOCKED) RETURNING *" 改为 SqlTemplates 预拼完整常量
                    // OutboxLeaseUpdatePG——消除每次租约的字符串拼接分配，且 SQL 文本稳定，
                    // 可被 Npgsql MaxAutoPrepare 自动预备（PG 数据源侧已默认启用）。
                    SqlTemplates.OutboxLeaseUpdatePG,
                    new { owner, until = ToTimeParam(until), now = ToTimeParam(now), maxRetryCount, n = batchSize },
                    Tx, cancellationToken: ct)).ConfigureAwait(false);
            return msgs.AsList();
        }
        else
        {
            // P1 修复（十一轮·实测发现）：MySQL 不支持 UPDATE ... WHERE id IN (SELECT ... LIMIT)
            // （真实库实测报 1235）——JOIN 形态替代（对齐 PalORM 版）；SQLite 支持子查询内 LIMIT 保持原状
            // 优化（二十五轮 API 扫描 A-3）：SQLite 路径同步常量化（原 OutboxLeaseUpdate + $"({leaseSubSql})"）
            var leaseSql = _dbType == DapperDbType.MySql
                ? SqlTemplates.OutboxLeaseUpdateMySql
                : SqlTemplates.OutboxLeaseUpdateSqlite;
            await conn.ExecuteAsync(
                new CommandDefinition(
                    leaseSql,
                    new { owner, until = ToTimeParam(until), now = ToTimeParam(now), maxRetryCount, n = batchSize },
                    Tx, cancellationToken: ct)).ConfigureAwait(false);

            // 🔴 P0 修复：按租约标识回读，不重新评估子查询
            // ITM-109 修复（声明，对齐 PalORM P3 声明）：回读按 (locked_by, locked_until)
            // 匹配——同一 tick（同 now → 同 until）内的第二次租约会回读到上一批遗留行，
            // 属已知限制（PalORM 已声明同限制）；PG RETURNING 路径（SupportsOutboxReturning
            // 分支）按行锁语义返回刚锁定行，无此窗口。生产多实例建议用 PG 路径。
            var msgs = await conn.QueryAsync<OutboxMessage>(
                new CommandDefinition(
                    SqlTemplates.OutboxSelectByLease,
                    new { owner, until = ToTimeParam(until) },
                    Tx, cancellationToken: ct)).ConfigureAwait(false);
            return msgs.AsList();
        }
    }

    public void AddMessage(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
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
                // ITM-634 修复（跨栈契约一致）：created_at 持久化领域赋值 OutboxMessage.CreatedAt——
                // 原实现用 Store 时钟 _timeProvider.GetUtcNow() 覆盖，与 PalORM（OutboxMessageRow.FromDomain）、
                // EFCore（OutboxMessages.Add(message)）、InMemory（列表直存）三栈保留领域值分叉：
                // 同消息跨栈落库 created_at 不同，ORDER BY created_at 投递序与 CreatedAt 往返断言随之分叉。
                CreatedAt = ToTimeParam(message.CreatedAt),
                CorrelationId = message.CorrelationId?.ToString(),
                CausationId = message.CausationId?.ToString(),
                message.TraceParent,
                message.TraceState
            }, Tx);
    }

    /// <summary>批量添加消息 — 自动选择数据库最优批量路径。
    /// <para>三十八轮 P2 修复：批量插入现支持参与 UnitOfWork 外部事务——Tx 经
    /// BulkInsertAsync 贯通三方言（PG COPY 自动入连接事务；MySQL 显式挂接；SQLite 挂接外部
    /// 事务不 Commit）。未开启事务时行为不变。</para>
    /// </summary>
    public async ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;
        // ITM-634 修复（跨栈契约一致）：created_at 改持久化各消息领域赋值 CreatedAt——
        // 原实现方法开头取一次 Store 时钟 now 作为整批 created_at（P3-SRC-207 纯提取契约修复），
        // 与 PalORM/EFCore/InMemory 三栈保留领域值分叉（同批各行时间被抹平为同刻，且与领域值不等）。
        // ⚠️ v16 声明（下游漂移，与本方法无关仍成立）：OutboxBatchProcessor 的 nextAttemptAt 基于
        // 其自身的批次起始 now——长批次尾部消息的重试时间提前（漂移=批耗时）；受 batchSize 上限
        // 约束可接受。本方法现不再取批次时钟，该漂移属处理器时间语义。
        // v8 声明：接口 IPalOutboxStore.AddMessagesAsync 无 CancellationToken 参数，本路径
        // 无法响应取消——v3.0 契约窗口（ADR-020）随接口异步化一并补。
        var conn = await EnsureOpenAsync().ConfigureAwait(false);
        // P2 修复（八轮评审 PD17）：批量路径补 correlation/causation/trace 4 追踪列——
        // 单条路径 AddMessage（七轮）已补，批量漏列导致追踪链在批量写入时丢失；
        // extractor 末 4 项与单条 AddMessage 的参数语义逐一对齐。
        return await DapperBulkCopy.BulkInsertAsync(
            conn, _dbType, "outbox_messages",
            ["id", "type", "payload", "content_type", "schema_version", "status", "created_at", "correlation_id", "causation_id", "trace_parent", "trace_state"],
            messages,
            m => [m.Id, m.Type, m.Payload, m.ContentType, m.SchemaVersion, StatusPending, m.CreatedAt,
                m.CorrelationId?.ToString(), m.CausationId?.ToString(), m.TraceParent, m.TraceState],
            Tx).ConfigureAwait(false);
    }

    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        var c = EnsureOpen();
        // P1 修复（八轮评审）：时间参数统一走 ToTimeParam——ToSqliteParameter 产出 "O" string，
        // PG 下 timestamptz 列收 text 参数无比较/赋值运算符（详见 ToTimeParam 的 PG 分支注释）
        // 三十四轮 ITM-210 token 化：补租约 token 参数（owner/until 调用时快照；无租约时均传
        // null → SQL 走 locked_by IS NULL 分支）。affected 返回值不消费——与原语义一致
        //（token 拒绝时 DB 行不变，内存入参仍按下方 ITM-130 同步清租约字段）。
        // P3-SRC-301 声明：affected=0（token 拒绝）时内存对象仅清租约字段不回写 Status——
        // 与 InMemory 版（守卫内联设 Processed）/PalORM 版（affected>0 才全套回写）的分叉属
        // ITM-210 历史语义，调用方（OutboxBatchProcessor）不读该状态故无实害。
        c.Execute(SqlTemplates.OutboxMarkProcessed,
            new { at = ToTimeParam(processedAt), id = DapperAotInitializer.ToSqliteParameter(message.Id), owner = message.LockedBy, until = LeaseUntilParam(message), retryCount = message.RetryCount }, Tx); // P1 修复（八轮评审）：时间参数走 ToTimeParam；三十四轮 ITM-210：租约 token 参数；v37 P3：retry_count fencing 快照
        // ITM-130 修复：SQL 清除 DB 租约列后同步入参——调用方读入参不应再见陈旧持有者
        // （对齐 EFCore/PalORM/InMemory 三姊妹的对象字段语义）
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // P3-TST-601（R45，对齐 PalORM/EFCore ITM-082 截断族）：存储层兜底截断——
        // 调用方 OutboxBatchProcessor 2000 为第一层；error 列 TEXT 在 Dapper 栈无上限但
        // 跨栈共用表场景（Dapper DDL 列可空 TEXT）防 DDL 收紧后超列失败
        // v29 P3：改经 FailureReason.Truncate 共享收口——[..2040] 切片可能切半 UTF-16
        // 代理对（超长含 emoji 的消息），末位高代理回退一位防孤立高代理入库（S1 五处
        // 截断点同款）
        failureReason = FailureReason.Truncate(failureReason, 2040);
        var c = EnsureOpen();
        // P3-SRC-301 声明（同 MarkProcessed）：affected=0（token 拒绝）时内存对象仅清租约字段
        // 不回写 Status——与 InMemory 版（守卫内联设 Dead）/PalORM 版（affected>0 才全套回写）
        // 的分叉属 ITM-210 历史语义，调用方（OutboxBatchProcessor）不读该状态故无实害。
        c.Execute(SqlTemplates.OutboxMarkDead,
            new { reason = failureReason, at = ToTimeParam(deadAt), id = DapperAotInitializer.ToSqliteParameter(message.Id), owner = message.LockedBy, until = LeaseUntilParam(message), retryCount = message.RetryCount }, Tx); // P1 修复（八轮评审）：时间参数走 ToTimeParam；三十四轮 ITM-210：租约 token 参数；v37 P3：retry_count fencing 快照
        // ITM-130 修复：SQL 清除 DB 租约列后同步入参（对齐 EFCore/PalORM/InMemory 三姊妹）
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // P3-TST-601：同 MarkDead——存储层兜底截断（v29 P3：经 FailureReason.Truncate
        // 共享收口，含 UTF-16 代理对守卫，见 MarkDead 注释）
        failureReason = FailureReason.Truncate(failureReason, 2040);
        var c = EnsureOpen();
        // P2 修复（八轮评审）：补租约守卫（对齐 PalORM 版 PalOrmOutboxStore）——租约过期被其他 worker
        // 抢占后，原 worker 的失败释放不再清掉新 worker 的锁或误增 retry_count；三十四轮 ITM-210
        // 升级为 token 完全匹配（owner/until 调用时快照，无租约时均传 null 走 IS NULL 分支）。
        var affected = c.Execute(SqlTemplates.OutboxReleaseForRetry,
            new { reason = failureReason, next = ToTimeParam(nextAttemptAt), id = DapperAotInitializer.ToSqliteParameter(message.Id), owner = message.LockedBy, until = LeaseUntilParam(message), retryCount = message.RetryCount }, Tx); // v37 P3：retry_count fencing 快照（PD17 姊妹同步，v33 Status 守卫之外的另一半）
        // ITM-130 修复：SQL 成功（affected>0）后同步入参到 DB 终态（对齐 PalORM ReleaseForRetry
        // 成功路径）；守卫拒绝（affected=0，租约已被他人持有）时零变异——保持 PalORM 零变异语义。
        if (affected > 0)
        {
            message.Status = OutboxStatus.Pending;
            message.Error = failureReason;
            message.NextAttemptAt = nextAttemptAt;
            message.RetryCount += 1;
            message.LockedBy = null;
            message.LockedUntil = null;
            message.ProcessedAt = null;
        }
    }

    public async ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        // P3-TST-601：对齐 EFCore ITM-216（256 截断）
        // v30 P3：改经 FailureReason.Truncate 共享收口——[..256] 裸切片可能切半 UTF-16
        // 代理对（超长含 emoji 的操作者标识），末位高代理回退一位防孤立高代理入库
        //（Dapper/PalORM/EFCore/InMemory 四栈 retriedBy 截断点同款，见 FailureReason.Truncate）
        retriedBy = FailureReason.Truncate(retriedBy, 256);
        var now = _timeProvider.GetUtcNow();
        var audit = $"requeued by {retriedBy} at {now:O}";
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // P3 修复（八轮评审）：ExecuteAsync 改 CommandDefinition 传 ct——原重载不接收取消令牌，
        // 取消信号在 RequeueDead 执行阶段不可传递；EnsureOpenAsync(ct) 此前已传。
        return await conn.ExecuteAsync(
            new CommandDefinition(
                SqlTemplates.OutboxRequeueDead,
                new { audit, next = ToTimeParam(nextAttemptAt), id = DapperAotInitializer.ToSqliteParameter(messageId) },
                Tx, cancellationToken: ct)).ConfigureAwait(false);
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

    /// <summary>
    /// 租约 token 的 until 参数（三十四轮 ITM-210）——持租时经 <see cref="ToTimeParam"/> 方言编码
    ///（租约值来自 DB 回读，再编码后与库内值逐字节一致）；无租约/租约标识不完整时返回 null
    ///（SQL 走 <c>locked_by IS NULL</c> 分支或 token 比较不命中——防御性拒绝）。
    /// </summary>
    private object? LeaseUntilParam(OutboxMessage message)
        => message.LockedBy is null || message.LockedUntil is null
            ? null
            : ToTimeParam(message.LockedUntil.Value);
}
