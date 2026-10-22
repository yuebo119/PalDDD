using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
public class PalOrmOutboxStore<TProvider> : IPalOutboxStore, IImmediateWriteOutboxStore, IBatchMarkOutboxStore
    where TProvider : IDbProvider
{
    /// <inheritdoc />
    public bool RequiresSaveAfterMark => false;

    /// <summary>共享的 Scoped 数据库会话（事务自动传播源）。</summary>
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类（方言包中间类）需直接访问 Session 以扩展方言特有能力。")]
    protected readonly DataSession<TProvider> Session;

    /// <summary>时间提供者（用于 created_at/processed_at 应用层赋值）。</summary>
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需访问 Clock 以统一时间源。")]
    protected readonly TimeProvider Clock;

    /// <summary>可选唤醒信号（2026-10-22 性能轮）——写入成功后通知处理循环立即续批。</summary>
    private readonly IOutboxWakeUpSignal? _wakeUpSignal;

    /// <summary>构造 Outbox Store。</summary>
    public PalOrmOutboxStore(DataSession<TProvider> session, TimeProvider? clock = null, IOutboxWakeUpSignal? wakeUpSignal = null)
    {
        ArgumentNullException.ThrowIfNull(session); // v22 C-1
        Session = session;
        Clock = clock ?? TimeProvider.System;
        _wakeUpSignal = wakeUpSignal;
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
        // 读副本标注（2026-09-25 特性审计裁决）：本查询为租约**前置候选读**，非 read-after-write
        // 敏感点——读到副本陈旧候选（已处理仍显示 Pending）只会让主库 Lease 的原子 UPDATE
        // 谓词不命中而返回 0 行（无害空批）；正确性由主库租约裁决保证。上游契约：未配置
        // ReadConnectionString 时逐位回落主库。Lease/回查等敏感点保持默认 false（见 :109/:116/:147）。
        var rows = await Session.QueryAsync<OutboxMessageRow>(
            $"SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages WHERE status = {(int)OutboxStatus.Pending} AND retry_count < {maxRetryCount} AND next_attempt_at <= {now} ORDER BY next_attempt_at, created_at LIMIT {batchSize}",
            readFromReplica: true, ct: ct).ConfigureAwait(false);
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
        // 2026-10-21 评审 F15（C3）：新增第 4 方言需同步 8 个 SQL Store 文件（SqlErrorClassifier/
        // Retention/EventLog/Idempotency/Inbox/Outbox/Projection/Saga）；本轮按 ADR-020 只做声明，
        // 不做策略表迁移。
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
                    $"UPDATE outbox_messages SET locked_by = {owner}, locked_until = {until}, next_attempt_at = {until} WHERE id IN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND next_attempt_at <= {now} ORDER BY next_attempt_at, created_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED) RETURNING id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state",
                    ct: ct).ConfigureAwait(false);
                return rows.Select(r => r.ToDomain()).ToList();
            }
            else
            {
                var rows = await Session.QueryAsync<OutboxMessageRow>(
                    $"UPDATE outbox_messages SET locked_by = {owner}, locked_until = {until}, next_attempt_at = {until} WHERE id IN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND next_attempt_at <= {now} ORDER BY next_attempt_at, created_at LIMIT {batchSize}) RETURNING id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state",
                    ct: ct).ConfigureAwait(false);
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
            //
            // ADR-025（2026-10-09）修订：MySQL ≥8.0.18（非 MariaDB）在派生表内加 FOR UPDATE
            // SKIP LOCKED——无锁形态的并发后果是 InnoDB 死锁异常（ADR-024 补记实证一），
            // SKIP LOCKED 形态同库实测零死锁+互斥+1.75× 加速（MySqlSkipLockedShapeBenchmarks）；
            // 旧版/MariaDB/探测失败回退无锁 JOIN（MySqlLeasePolicy fail-safe，行为与
            // ADR-024 接受的现状逐字一致）。v13/v43 分叉声明对回退路径继续有效。
            // 实现注：锁子句是 SQL 字面量，不能经 FormattableString 插值孔传递（每孔绑一个
            // DbParameter——上方已知限制同源），故 if/else 双完整模板（对齐本文件 PG/SQLite 分支风格）。
            var skipLocked = await MySqlSupportsSkipLockedAsync(ct).ConfigureAwait(false);
            FormattableString leaseSql = skipLocked
                ? (FormattableString)$"UPDATE outbox_messages t JOIN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND next_attempt_at <= {now} ORDER BY next_attempt_at, created_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED) AS sub ON t.id = sub.id SET t.locked_by = {owner}, t.locked_until = {until}, t.next_attempt_at = {until}"
                : (FormattableString)$"UPDATE outbox_messages t JOIN (SELECT id FROM outbox_messages WHERE status = {pending} AND retry_count < {maxRetryCount} AND next_attempt_at <= {now} ORDER BY next_attempt_at, created_at LIMIT {batchSize}) AS sub ON t.id = sub.id SET t.locked_by = {owner}, t.locked_until = {until}, t.next_attempt_at = {until}";
            await Session.ExecuteAsync(leaseSql, ct).ConfigureAwait(false);
            var rows = await Session.QueryAsync<OutboxMessageRow>(
                $"SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages WHERE locked_by = {owner} AND locked_until = {until} ORDER BY created_at",
                ct: ct).ConfigureAwait(false);
            return rows.Select(r => r.ToDomain()).ToList();
        }
    }

    /// <summary>
    /// MySQL 版本探测（ADR-025）：首次 MySQL 租约读 <c>DbConnection.ServerVersion</c>
    ///（握手包已带，零额外往返）并缓存于本实例。探测失败（连接异常）按空串处理 →
    /// fail-safe 回退无锁模板。2026-10-21 评审勘正：原实现为 SELECT VERSION() 往返探针，
    /// 已随 2026-10-19 性能轮改为 ServerVersion；配套的 <c>VersionProbeRow</c> 已删除。
    /// </summary>
    private string? _mySqlServerVersion;

    private async Task<bool> MySqlSupportsSkipLockedAsync(CancellationToken ct)
    {
        if (_mySqlServerVersion is null)
        {
            try
            {
                // 2026-10-19 性能实施轮：DbConnection.ServerVersion 来自握手包，已连接时
                // 零往返；连接未打开则先打开（后续租约查询本来也要打开同一连接）。
                var connection = Session.GetRawConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(ct).ConfigureAwait(false);
                _mySqlServerVersion = connection.ServerVersion;
            }
#pragma warning disable CA1031 // 探测失败按空串处理（fail-safe 回退无锁模板），不中断租约路径
            catch (Exception)
#pragma warning restore CA1031
            {
                _mySqlServerVersion = "";
            }
        }
        return Transactions.MySqlLeasePolicy.UseSkipLocked(_mySqlServerVersion);
    }

    /// <inheritdoc />
    public async ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default)
    {
        // ITM-163 修复：补 message null 守卫（对齐 InMemoryOutboxStore）
        ArgumentNullException.ThrowIfNull(message);
        // InsertAsync 对 [Key(AutoIncrement=false)] 的 Ulid 主键不回填 —— 实体已带 Id。
        // created_at 由领域对象在构造时赋值（init-only，Store 不覆盖）。
        // 2026-10-21：next_attempt_at 是唯一可用时刻，新建消息默认在写入时立即可用。
        message.NextAttemptAt = message.NextAttemptAt ?? Clock.GetUtcNow();
        // v4.0 ③（ADR-026）：原同步 AddMessage 内部对 Session.InsertAsync 阻塞桥接
        //（GetAwaiter().GetResult()）——接口异步化后桥接删除，直连异步原语
        var row = OutboxMessageRow.FromDomain(message);
        await Session.InsertAsync(row, ct).ConfigureAwait(false);
        _wakeUpSignal?.Notify();
    }

    /// <inheritdoc />
    public async ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default)
    {
        // ITM-163 修复：补 messages null 守卫（对齐 InMemoryOutboxStore/OutboxDbContext）
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;
        var now = Clock.GetUtcNow();
        foreach (var message in messages)
            message.NextAttemptAt = message.NextAttemptAt ?? now;
        var rows = messages.Select(OutboxMessageRow.FromDomain).ToList();
        // 6.6.0 单参重载：PG 取整段行数走单次 COPY（2 万行不再付 20 次 BeginBinaryImport/
        // Complete），MySQL/SQLite 内部沿用 1000 批宽；需要限制单次 COPY/事务持锁行数时用
        // 带 batchSize 的重载（本 store 的批量入口由调用方控制单次列表大小）。
        // v4.0 ④（ADR-026）：ct 传播进批量路径
        var inserted = (int)await Session.BulkInsertAsync(rows, ct).ConfigureAwait(false);
        _wakeUpSignal?.Notify();
        return inserted;
    }

    /// <inheritdoc />
    public async ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
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
        // v4.0 ③（ADR-026）：同步阻塞桥（GetAwaiter().GetResult()）删除，直连异步原语
        var id = message.Id.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        var retry = message.RetryCount;
        var owner = message.LockedBy;
        var until = message.LockedUntil;
        var statusProcessed = (int)OutboxStatus.Processed;
        // v44 P2：Status 终态守卫（镜像 v43 EFCore FencedTarget 收口——防 Dead↔Processed 翻转）
        var statusPending = (int)OutboxStatus.Pending;
        var affected = owner is null
            ? await Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusProcessed}, processed_at = {processedAt}, error = NULL, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by IS NULL",
                ct).ConfigureAwait(false)
            : await Session.ExecuteAsync(
                // v74 P1 修复：v72 勘正说明曾以 SQL 行内注释（--）误嵌本字符串——单行 SQL 中
                // -- 到语句末尾全部被数据库当注释，AND locked_until 谓词随之失效（同 owner
                // 重租后旧快照终态写放行，探针实证红）；注释移出字符串，(locked_by, locked_until)
                // 双谓词恢复（形态对齐姊妹 ReleaseForRetry 单次双谓词）
                $"UPDATE outbox_messages SET status = {statusProcessed}, processed_at = {processedAt}, error = NULL, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by = {owner} AND locked_until = {until}",
                ct).ConfigureAwait(false);
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
    public async ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default)
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
        // v4.0 ③（ADR-026）：同步阻塞桥删除，直连异步原语
        var id = message.Id.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        var retry = message.RetryCount;
        var owner = message.LockedBy;
        var until = message.LockedUntil;
        var statusDead = (int)OutboxStatus.Dead;
        var statusPending = (int)OutboxStatus.Pending;
        var affected = owner is null
            ? await Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusDead}, error = {reason}, processed_at = {deadAt}, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by IS NULL",
                ct).ConfigureAwait(false)
            : await Session.ExecuteAsync(
                // v74 P1 修复：同 MarkProcessed——v72 勘正说明曾误嵌 SQL 字符串（-- 注释吞掉
                // AND locked_until 谓词，同 owner 重租旧写放行）；双谓词恢复
                $"UPDATE outbox_messages SET status = {statusDead}, error = {reason}, processed_at = {deadAt}, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id = {id} AND retry_count = {retry} AND status = {statusPending} AND locked_by = {owner} AND locked_until = {until}",
                ct).ConfigureAwait(false);
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
    public async ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
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
        var id = message.Id.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
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
            ? await Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusPending}, processed_at = NULL, error = {reason}, next_attempt_at = {nextAttemptAt}, retry_count = retry_count + 1, locked_by = NULL, locked_until = NULL WHERE id = {id} AND status = {statusPending} AND retry_count = {retry} AND (locked_by IS NULL)",
                ct).ConfigureAwait(false)
            : await Session.ExecuteAsync(
                $"UPDATE outbox_messages SET status = {statusPending}, processed_at = NULL, error = {reason}, next_attempt_at = {nextAttemptAt}, retry_count = retry_count + 1, locked_by = NULL, locked_until = NULL WHERE id = {id} AND status = {statusPending} AND retry_count = {retry} AND locked_by = {leaseOwner} AND locked_until = {leaseUntil}",
                ct).ConfigureAwait(false); // v4.0 ③（ADR-026）：同步阻塞桥删除，直连异步原语
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
            $"UPDATE outbox_messages SET status = {(int)OutboxStatus.Pending}, processed_at = NULL, error = {audit}, next_attempt_at = {nextAttemptAt}, locked_by = NULL, locked_until = NULL WHERE id = {messageId.ToString(null, System.Globalization.CultureInfo.InvariantCulture)} AND status = {(int)OutboxStatus.Dead}",
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ADR-027 延后段清算（2026-10-16，三方言 until 编码探针后实施）：批量 WHERE 的
    /// <c>locked_until = {until}</c> 取<b>租约回读值</b>（messages[0].LockedUntil，非重算）且以
    /// DateTimeOffset 形态插值，与租约写入绑定逐字节一致。探针实测（SQLite 本地 + PG/MySQL
    /// wslc 外部实例，各正腿命中全批 + until±1s/换 owner/retry+1/已终态四负腿全拒）：库内
    /// 形态分别为 SQLite TEXT "…+00:00"（100ns 全精度）/ PG timestamptz（μs，第 7 位被截）/
    /// MySQL DATETIME(6)（μs 无 offset 裸 UTC）——三者回读值重编码均精确命中。<b>勿"简化"为
    /// UtcDateTime</b>：同值以 DateTime 形态传递三方言实测全部拒命（编码形态敏感）。
    /// </remarks>
    public async ValueTask<int> MarkProcessedBatchAsync(IReadOnlyList<OutboxMessage> messages, DateTimeOffset processedAt, CancellationToken ct = default)
    {
        // 守卫与同租约校验：与 DapperOutboxStore / OutboxDbContext 两实现逐字同构（ADR-027 契约）
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;

        // R11 F15：null 元素校验必须先于 messages[0] 解引用——原顺序对 [null] 抛 NRE，
        // 而 Dapper/EF 抛 ArgumentNullException（同一契约异常类型分叉）。
        foreach (var message in messages)
        {
            if (message is null)
                throw new ArgumentNullException(nameof(messages), "The messages collection contains a null element.");
        }

        var owner = messages[0].LockedBy;
        var until = messages[0].LockedUntil;
        if (owner is null || until is null)
            throw new InvalidOperationException(
                "MarkProcessedBatchAsync requires leased messages (LockedBy/LockedUntil non-null) — batch inputs must come from a single LeasePendingMessagesAsync result.");
        foreach (var message in messages)
        {
            if (message.LockedBy != owner || message.LockedUntil != until)
                throw new InvalidOperationException(
                    "MarkProcessedBatchAsync requires a single-lease batch — all messages must share the same LockedBy/LockedUntil (mixed-lease batches are not supported).");
        }

        var statusProcessed = (int)OutboxStatus.Processed;
        var statusPending = (int)OutboxStatus.Pending;
        var total = 0;
        // 按 RetryCount 分组（快照等值可下推单组 WHERE retry_count=孔）+ 组内 400 行分块
        foreach (var group in messages.GroupBy(static m => m.RetryCount))
        {
            var ids = group.Select(static m => m.Id.ToString(null, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            for (var start = 0; start < ids.Length; start += OutboxBatchMarkChunkRows)
            {
                var count = Math.Min(OutboxBatchMarkChunkRows, ids.Length - start);
                // FormattableStringFactory.Create + {N} 索引孔：PalORM 每个插值孔只绑一个
                // DbParameter（BindFormattableParameters），动态 IN 列表按此构造——PalOrmEventLog
                // A11 多行 VALUES 同款先例。SET/WHERE 只插值"值"（参数位合法）；SQL 片段
                // （IN / 括号 / AND）以字面量进 format 文本（PD18 教训：字符串变量会被整体
                // 参数化成 @pN 语法错误）。
                var sql = new System.Text.StringBuilder(220 + count * 30)
                    .Append("UPDATE outbox_messages SET status = {0}, processed_at = {1}, error = NULL, next_attempt_at = NULL, locked_by = NULL, locked_until = NULL WHERE id IN (");
                var args = new List<object?>(count + 6) { statusProcessed, processedAt };
                for (var i = 0; i < count; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.Append('{').Append(args.Count).Append('}');
                    args.Add(ids[start + i]);
                }
                sql.Append(") AND retry_count = {").Append(args.Count).Append('}');
                args.Add(group.Key);
                sql.Append(" AND status = {").Append(args.Count).Append('}');
                args.Add(statusPending);
                sql.Append(" AND locked_by = {").Append(args.Count).Append('}');
                args.Add(owner);
                sql.Append(" AND locked_until = {").Append(args.Count).Append('}');
                args.Add(until.Value);
                total += await Session.ExecuteAsync(
                    FormattableStringFactory.Create(sql.ToString(), args.ToArray()), ct).ConfigureAwait(false);
            }
        }

        // 全命中才回写（部分拒绝时无法逐行定位被拒者，保守零回写——接口契约与 Dapper/EFCore/
        // InMemory 三实现同款）
        if (total == messages.Count)
        {
            foreach (var message in messages)
            {
                message.Status = OutboxStatus.Processed;
                message.ProcessedAt = processedAt;
                message.Error = null;
                message.NextAttemptAt = null;
                message.LockedBy = null;
                message.LockedUntil = null;
            }
        }
        return total;
    }

    /// <summary>批量标记单语句组内行数上限——SQLite 参数上限 999（每行 1 参数 + 共享 6 参数）的防御分块。</summary>
    private const int OutboxBatchMarkChunkRows = 400;

    /// <inheritdoc />
    public ValueTask<int> SaveChangesAsync(CancellationToken ct)
    {
        // 即时执行模式（与 Dapper 实现一致）—— 无 ChangeTracker
        // 事务边界由 UnitOfWork.BeginTransactionAsync/CommitAsync 控制
        return new ValueTask<int>(0);
    }
}
