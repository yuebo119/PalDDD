using System.Diagnostics.CodeAnalysis;
using ByteAether.Ulid;
using PalDDD.Core; // v30 P3：Outbox 截断点接入 FailureReason.Truncate 共享收口（对齐姊妹 PalOrmInboxStore）
using PalORM;
using PalDDD.PalORM.Models;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Outbox Store 的 PalORM 实现 —— 双泛型核心基类。
/// <para>
/// 由各方言包（PalDDD.PalORM.Sqlite 等）派生具体类固化 <typeparamref name="TProvider"/>，
/// 如 <c>SqliteOutboxStore : PalOrmOutboxStore&lt;SqliteProvider&gt;</c>。
/// </para>
/// <para><b>设计要点</b>：
/// <list type="bullet">
/// <item>单 Scoped <see cref="DataSession{TProvider}"/> 共享 —— 事务经 UnitOfWork.BeginTransactionAsync 后自动传播。</item>
/// <item>GetPending 走 QueryAsync&lt;T&gt;（FormattableString 自动参数化）；Lease 必须<b>降级手写 SQL</b>（QueryBuilder UPDATE 拒绝子查询+RETURNING 整行）。</item>
/// <item>SQL 显式列出列名（按 <see cref="OutboxMessageRow"/> 属性声明序对齐，避免 PalORM ColumnOrderValidator 列序错位）。</item>
/// <item>[ConcurrencyCheck]RetryCount 在 UpdateAsync 路径自动加并发谓词；ReleaseForRetry 走手写 SQL 避免 [ConcurrencyCheck] 干扰原子自增。</item>
/// </list>
/// </para>
/// <para>v70 P3 声明：持租终态写（Mark*/ReleaseForRetry）对"owner 非空而 until 为 null"
/// 的异常半租约态，PalORM 的 <c>locked_until = {until}</c>（null 参数化 = NULL 恒假）
/// 拒绝放行（affected=0 静默），EFCore 的 null 常量翻译为 <c>IS NULL</c> 放行——跨栈
/// 防御方向分叉（PalORM 更严）。仅手工构造 OutboxMessage 直调可达（正常 Lease 恒
/// 同写 owner+until），按更严方向保留。</para>
/// </summary>
public class PalOrmOutboxStore<TProvider> : IPalOutboxStore
    where TProvider : IDbProvider
{
    /// <summary>共享的 Scoped 数据库会话（事务自动传播源）。</summary>
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类（方言包中间类）需直接访问 Session 以扩展方言特有能力。")]
    protected readonly DataSession<TProvider> Session;

    /// <summary>时间提供者（用于 created_at/processed_at 应用层赋值）。</summary>
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需访问 Clock 以统一时间源。")]
    protected readonly TimeProvider Clock;

    /// <summary>构造 Outbox Store。</summary>
    public PalOrmOutboxStore(DataSession<TProvider> session, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(session); // v22 C-1
        Session = session;
        Clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize, int maxRetryCount, CancellationToken ct)
    {
        // v25 P3 守卫族：batchSize 非正守卫——与 Dapper/EFCore 姊妹实现对齐
        //（镜像同文件姊妹 PalOrmSagaStateStore.GetActiveSagasAsync :71 的 P3 修复形态）——
        // LIMIT 0/负在各方言下静默空返回，无诊断
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // ITM-659 守卫族收口：maxRetryCount 非正守卫（对齐同方法 batchSize 守卫形态）——
        // 非正值使 retry_count < maxRetryCount 恒假（retry_count >= 0），三方言下静默
        // 空返回无诊断（直调路径防御性 fail-fast；Options 层已校验正数）
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryCount);
        var now = Clock.GetUtcNow();
        // 列名内联到 SQL 字面量（PalORM 要求 FormattableString 类型，字符串拼接会退化为 string）
        var rows = await Session.QueryAsync<OutboxMessageRow>(
            $"SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages WHERE status = {(int)OutboxStatus.Pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize}",
            ct).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner); // v22 C-2：对齐 EFCore 四方言 ITM-081/216
        // v25 P3 守卫族：batchSize 非正守卫——与 Dapper/EFCore 姊妹实现对齐
        //（镜像 EFCore 四方言 LeasePendingMessagesAsync 的 v22 C-3 形态）——
        // 子查询 LIMIT 0/负静默空返回，无诊断
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // ITM-659 守卫族收口：maxRetryCount 非正守卫（同 GetPendingMessagesAsync——
        // 本方法三处 SQL 分支共用同一谓词，一处守卫三分支生效）
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryCount);
        // v25 P3 守卫族：leaseDuration 边界守卫——对照 EFCore 四方言 LeasePendingMessagesAsync
        //（MySql/PG/Sqlite，ITM-167/216 对齐系列）同型漏网——leaseDuration 非正时租约
        // 即刻过期/永不过期语义错乱；TotalSeconds 超过 int.MaxValue 时
        // until = now + leaseDuration 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LockedUntil value.");
        var now = Clock.GetUtcNow();
        var until = now + leaseDuration;
        var pending = (int)OutboxStatus.Pending;

        // 核心原子租约 SQL —— 必须 QueryAsync/ExecuteAsync（QueryBuilder UPDATE 拒绝子查询+LIMIT+RETURNING 整行）
        // 方言分支：PG/SQLite 走 RETURNING 单语句；MySQL 走 UPDATE + SELECT 两步。
        if (TProvider.SupportsReturningClause)
        {
            // ITM-210 修复（三十二轮）：PG 子查询补 FOR UPDATE SKIP LOCKED——对齐
            // PalOrmSagaStateStore.cs:95-106 的方言分支（ITM-173）。PG READ COMMITTED 下
            // 并发 UPDATE...WHERE id IN(子查询) 后到 worker 阻塞后 EPQ 重查仍命中同 id 集
            // → last-writer-wins 覆盖 locked_by → 双 worker 同批租约 → 重复投递。
            // SQLite 无 FOR UPDATE（库级单写者串行，无需），按 Dialect 分支整句构造（PD18）。
            if (TProvider.Dialect == global::PalORM.SqlDialect.PostgreSql)
            {
                var rows = await Session.QueryAsync<OutboxMessageRow>(
                    $"UPDATE outbox_messages SET locked_by = {owner}, locked_until = {until} WHERE id IN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED) RETURNING id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state",
                    ct).ConfigureAwait(false);
                return rows.Select(r => r.ToDomain()).ToList();
            }
            else
            {
                var rows = await Session.QueryAsync<OutboxMessageRow>(
                    $"UPDATE outbox_messages SET locked_by = {owner}, locked_until = {until} WHERE id IN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize}) RETURNING id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state",
                    ct).ConfigureAwait(false);
                return rows.Select(r => r.ToDomain()).ToList();
            }
        }
        else
        {
            // MySQL 路径：不支持 UPDATE...WHERE id IN (SELECT...LIMIT)。
            // 用 JOIN 子查询替代（MySQL 特化）+ 按 lease 标识回读（两步避免重跑子查询 P0 bug）
            // v13 分叉声明：MySQL 分支 JOIN 无 FOR UPDATE SKIP LOCKED——语义为 last-writer-wins
            // （并发双 worker 同批时后写覆盖先写、败者空批，消息延迟至下轮租约），与 EFCore 侧
            // MySqlOutboxDbContext 的 SKIP LOCKED（败者租他行）不同。取舍：JOIN 写法避开 MySQL
            // 派生表锁的版本兼容矩阵（8.0.18 以下行为差异），正确性由 (locked_by,locked_until)
            // token 终态守卫兜底（败者租约被覆盖后其终态写 0 行）。
            // v43 P3 时序完整性补充（同 EFCore 侧声明）：last-writer-wins 未覆盖 (A写,A读,
            // B写,B读) 交错时序——两 worker 子查询均在对方提交前选中同一行集时，后写者
            // JOIN-SET 直接覆盖先写者租约并回读到整批，双 worker 同批重复投递（而非延迟）；
            // fencing 只防双终态写不防双执行。重复投递在 at-least-once 投递契约内，
            // 由下游幂等消费兜底。
            //
            // ⚠️ 已知限制（八轮评审 P3，声明不修）：回读按 (locked_by, locked_until) 匹配——同一 owner
            // 在同一 tick（until 完全相等，如 FakeTimeProvider 冻结时间）发起两次租约时，第二次回读
            // 会混入第一次已锁定的批次。生产触发条件近乎为零（DATETIME(6) 微秒精度 + 单 owner 串行租约）；
            // PG/SQLite 走 RETURNING 单语句天然免疫。候选 id 预取方案需要 IN 列表参数化——PalORM 的
            // FormattableString 路径每个格式参数只绑一个 DbParameter（BindFormattableParameters），
            // WhereIn 仅存在于 QueryBuilder 实体路径（无法表达此 UPDATE+JOIN 手写 SQL），改动面大，
            // 待 PalORM 支持 IN 参数化后与 Saga 路径统一修。
            await Session.ExecuteAsync(
                $"UPDATE outbox_messages t JOIN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) AND (locked_until IS NULL OR locked_until <= {now}) ORDER BY created_at LIMIT {batchSize}) AS sub ON t.id = sub.id SET t.locked_by = {owner}, t.locked_until = {until}",
                ct).ConfigureAwait(false);
            var rows = await Session.QueryAsync<OutboxMessageRow>(
                $"SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages WHERE locked_by = {owner} AND locked_until = {until} ORDER BY created_at",
                ct).ConfigureAwait(false);
            return rows.Select(r => r.ToDomain()).ToList();
        }
    }

    /// <inheritdoc />
    public void AddMessage(OutboxMessage message)
    {
        // ITM-163 修复：补 message null 守卫（对齐 InMemoryOutboxStore）
        ArgumentNullException.ThrowIfNull(message);
        // InsertAsync 对 [Key(AutoIncrement=false)] 的 Ulid 主键不回填 —— 实体已带 Id。
        // created_at 由领域对象在构造时赋值（init-only，Store 不覆盖）。
        var row = OutboxMessageRow.FromDomain(message);
        Session.InsertAsync(row, default).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        // ITM-163 修复：补 messages null 守卫（对齐 InMemoryOutboxStore/OutboxDbContext）
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;
        var rows = messages.Select(OutboxMessageRow.FromDomain).ToList();
        // BulkInsertAsync 自动选方言最优路径（PG COPY / MySQL BulkCopy / SQLite 多值 INSERT）
        return (int)await Session.BulkInsertAsync(rows, batchSize: 1000, ct: default).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        // ITM-163 修复：补 message null 守卫（对齐 InMemoryOutboxStore/OutboxDbContext）
        ArgumentNullException.ThrowIfNull(message);
        // 三十四轮 ITM-210 token 化：UpdateAsync 的 [ConcurrencyCheck]RetryCount 乐观锁
        // 无法防"同 owner 复用/租约释放后旧 worker 终态写"（retry_count 不随租约变化）——
        // 改手写 SQL 双守卫：retry_count 乐观锁 + (locked_by, locked_until) 租约 token
        //（LockedUntil 随每次租约单调变化，免 DDL fencing）。affected=0（token 拒绝/并发冲突）
        // 时零内存变异（镜像 ReleaseForRetry 模式，对齐 OutboxProcessor 的"视为已处理"语义）。
        // ⚠️ SET/WHERE 只插值"值"（参数位合法）；SQL 片段变量会被 PalORM 整体参数化成
        // SET @pN 语法错误（十七轮实证，见 PalOrmSagaStateStore PD18 注释）。
        var id = message.Id.ToString();
        var retry = message.RetryCount;
        var owner = message.LockedBy;
        var until = message.LockedUntil;
        var statusProcessed = (int)OutboxStatus.Processed;
        // v44 P2：Status 终态守卫（镜像 v43 EFCore FencedTarget 收口——防 Dead↔Processed 翻转）
        var statusPending = (int)OutboxStatus.Pending;
        var affected = owner is null
            ? Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusProcessed}, processed_at = {processedAt}, error = NULL, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by IS NULL",
                default).AsTask().GetAwaiter().GetResult()
            : Session.ExecuteAsync(
                // v74 P1 修复：v72 勘正说明曾以 SQL 行内注释（--）误嵌本字符串——单行 SQL 中
                // -- 到语句末尾全部被数据库当注释，AND locked_until 谓词随之失效（同 owner
                // 重租后旧快照终态写放行，探针实证红）；注释移出字符串，(locked_by, locked_until)
                // 双谓词恢复（形态对齐姊妹 ReleaseForRetry 单次双谓词）
                $"UPDATE outbox_messages SET status = {statusProcessed}, processed_at = {processedAt}, error = NULL, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by = {owner} AND locked_until = {until}",
                default).AsTask().GetAwaiter().GetResult();
        if (affected > 0)
        {
            message.Status = OutboxStatus.Processed;
            message.ProcessedAt = processedAt;
            message.Error = null;
            message.NextAttemptAt = null;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc />
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        // ITM-163 修复：补 message null + failureReason 空白守卫（对齐 InMemoryOutboxStore/OutboxDbContext）
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // ITM-082 姊妹对齐（P3-SRC-302）：failureReason 存储层截断兜底 2040——对齐 OutboxDbContext.
        // MarkDead（Error 列上限 2048 族的截断值）。三层截断的存储层兜底：调用方 OutboxBatchProcessor
        // 的 2000 截断为第一层；PalORM 三方言 error 列 TEXT 无上限（栈内无害），本兜底为跨栈姊妹
        // 对称性 + 防 DDL 收紧后超列失败（消息滞留租约过期态）
        // v30 P3：改经 FailureReason.Truncate 共享收口——裸 [..2040] 切片可能切半 UTF-16
        // 代理对（超长含 emoji 的消息），末位高代理回退一位防孤立高代理入库
        var reason = FailureReason.Truncate(failureReason, 2040);
        // 三十四轮 ITM-210 token 化：同 MarkProcessed——retry_count 乐观锁 + 租约 token 双守卫
        //（SET/WHERE 只插值"值"；SQL 片段变量会被 PalORM 整体参数化成语法错误，见 MarkProcessed 注释）
        var id = message.Id.ToString();
        var retry = message.RetryCount;
        var owner = message.LockedBy;
        var until = message.LockedUntil;
        var statusDead = (int)OutboxStatus.Dead;
        var statusPending = (int)OutboxStatus.Pending;
        var affected = owner is null
            ? Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusDead}, error = {reason}, processed_at = {deadAt}, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by IS NULL",
                default).AsTask().GetAwaiter().GetResult()
            : Session.ExecuteAsync(
                // v74 P1 修复：同 MarkProcessed——v72 勘正说明曾误嵌 SQL 字符串（-- 注释吞掉
                // AND locked_until 谓词，同 owner 重租旧写放行）；双谓词恢复
                $"UPDATE outbox_messages SET status = {statusDead}, error = {reason}, processed_at = {deadAt}, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by = {owner} AND locked_until = {until}",
                default).AsTask().GetAwaiter().GetResult();
        if (affected > 0)
        {
            message.Status = OutboxStatus.Dead;
            message.Error = reason;
            message.ProcessedAt = deadAt;
            message.NextAttemptAt = null;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc />
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        // ITM-163 修复：补 message null + failureReason 空白守卫（对齐 InMemoryOutboxStore/OutboxDbContext）
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // ITM-082 姊妹对齐（P3-SRC-302）：failureReason 存储层截断兜底 2040——对齐 OutboxDbContext.
        // ReleaseForRetry（同 MarkDead，见其注释的三层截断说明）
        // v30 P3：改经 FailureReason.Truncate 共享收口（代理对守卫，见 MarkDead 注释）
        var reason = FailureReason.Truncate(failureReason, 2040);
        // 手写 SQL 路径：原子自增 retry_count（避免读-改-写竞态）
        // 不走 UpdateAsync —— 避免 [ConcurrencyCheck] 干扰原子自增语义
        // P2 修复：补租约守卫——原 WHERE 仅按 id，租约过期被其他 worker 抢占后，
        // 原 worker 的失败释放会清掉新 worker 的锁并误增 retry_count。
        // 守卫语义：仅防"他人持有"——locked_by IS NULL（未租约/已释放）放行。
        // ⚠️ 实证教训：守卫必须以字面量分支写进 SQL，不能经变量插值——PalORM 的
        // FormattableString 会把字符串变量参数化（AND (@p) 恒假，UPDATE 0 行，
        // file-based app 探针定位）。
        var leaseOwner = message.LockedBy;
        var leaseUntil = message.LockedUntil;
        var statusPending = (int)OutboxStatus.Pending;
        var id = message.Id.ToString();
        // P3 修复（九轮→十轮修正）：SQL 先行且按受影响行数门控内存同步——守卫拦截
        // （租约被抢占，affected=0）时内存对象保持抢占前的真实状态（镜像
        // PalOrmIdempotencyStore 的 affected>0 模式）。
        // P2/P3 修复（十七轮·注释收敛）：成功时同步内存（affected>0 修改入参字段）为 PalORM 特有——
        // Dapper/EFCore 版 ReleaseForRetry 不修改入参对象；本版守卫拒绝时零变异，
        // 成功时同步内存与 DB 终态一致（调用方后续读取入参即见真实状态）。
        // P2/P3 修复（十七轮）：UPDATE 补 processed_at = NULL——对齐 EFCore/Dapper 版
        // ReleaseForRetry 与 RequeueDeadAsync 语义（消息重回 Pending 后清除上次完成时间，
        // 防止监控/报表误判），三方统一。
        // 三十四轮 ITM-210 token 化：owner 分支从"仅防他人持有"升级为 (locked_by, locked_until)
        // 租约 token 完全匹配——原 NULL 放行分支正是 fencing 缺口（租约释放后旧 worker 复活
        // Processed 消息）；无租约直呼分支保持 locked_by IS NULL 字面量。
        // v33 P3：两分支 WHERE 同补 status = Pending 守卫——无租约直呼（运维/测试路径）时防把
        // Processed/Dead 行复活为 Pending（对齐同族 RequeueDeadAsync 的 status = Dead 守卫；
        // EFCore OutboxDbContext.ReleaseForRetry / Dapper SqlTemplates.OutboxReleaseForRetry
        // 同轮收口）。合法持租路径不误伤：租约只落在 Pending 行上，持租处理中的行恒为 Pending。
        // statusPending 为 int 插值（值位参数化合法，非 SQL 片段——PD18 教训），字面量分支
        // 构造与既有 owner 分支一致。
        // v36 P3：两分支 WHERE 同补 retry_count = {retry} 快照守卫——对齐 EFCore
        // OutboxDbContext.FencedTarget 的 originalRetry 双分支形态（v30 无租约分支 + v33 持租
        // 分支收口；PalORM 版 MarkProcessed/MarkDead 同款快照守卫已有，本方法为姊妹漏网）。
        // 缺口：本方法语义为 retry_count = retry_count + 1 原子自增，行被并发推进（他人
        // ReleaseForRetry 已自增 / RequeueDead 推进）后，持旧快照的调用方再释放会在新值上
        // 二次自增（重试计数虚高提前 Dead）。快照守卫使自增严格基于捕获点版本，并发推进后
        // 旧写不命中（affected=0，走下方既有门控零内存变异）。
        var retry = message.RetryCount;
        var affected = leaseOwner is null
            ? Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusPending}, processed_at = NULL, error = {reason}, next_attempt_at = {nextAttemptAt}, retry_count = retry_count + 1, locked_by = NULL, locked_until = NULL WHERE id = {id} AND status = {statusPending} AND retry_count = {retry} AND (locked_by IS NULL)",
                default).AsTask().GetAwaiter().GetResult()
            : Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusPending}, processed_at = NULL, error = {reason}, next_attempt_at = {nextAttemptAt}, retry_count = retry_count + 1, locked_by = NULL, locked_until = NULL WHERE id = {id} AND status = {statusPending} AND retry_count = {retry} AND locked_by = {leaseOwner} AND locked_until = {leaseUntil}",
                default).AsTask().GetAwaiter().GetResult();
        if (affected > 0)
        {
            message.Status = OutboxStatus.Pending;
            message.Error = reason;
            message.NextAttemptAt = nextAttemptAt;
            message.RetryCount += 1;
            message.LockedBy = null;
            message.LockedUntil = null;
            message.ProcessedAt = null; // P2/P3 修复（十七轮）：内存同步 DB 语义（processed_at 清 NULL）
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> RequeueDeadAsync(Ulid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        // ITM-163 修复：补 retriedBy 空白守卫（对齐 DapperOutboxStore/InMemoryOutboxStore/OutboxDbContext）
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        // ITM-216 姊妹对齐（P3-SRC-102/201）：retriedBy 截断兜底 256——对齐 OutboxDbContext/
        // InMemoryOutboxStore 的 RequeueDeadAsync（审计串预留 " at {时间戳}" 后缀空间，Error 列
        // 上限族 2048）。入参保持不变，仅审计串使用截断值
        // v30 P3：改经 FailureReason.Truncate 共享收口——裸 [..256] 切片可能切半 UTF-16 代理对
        //（超长含 emoji 的操作者标识），四栈 retriedBy 截断点同款
        var retriedByToken = FailureReason.Truncate(retriedBy, 256);
        var now = Clock.GetUtcNow();
        var audit = $"requeued by {retriedByToken} at {now:O}";
        // 条件 UPDATE：status=Dead(2) 守卫防止重复重投；返回受影响行数用于幂等判断
        return await Session.ExecuteAsync(
            $"UPDATE outbox_messages SET status = {(int)OutboxStatus.Pending}, processed_at = NULL, error = {audit}, next_attempt_at = {nextAttemptAt}, locked_by = NULL, locked_until = NULL WHERE id = {messageId.ToString()} AND status = {(int)OutboxStatus.Dead}",
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> SaveChangesAsync(CancellationToken ct)
    {
        // 即时执行模式（与 Dapper 实现一致）—— 无 ChangeTracker
        // 事务边界由 UnitOfWork.BeginTransactionAsync/CommitAsync 控制
        return new ValueTask<int>(0);
    }
}
