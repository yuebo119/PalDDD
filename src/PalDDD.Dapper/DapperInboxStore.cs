// ─────────────────────────────────────────────────────────────
// 📥 DapperInboxStore — 收件箱存储的 Dapper 实现
//    纯 Dapper SQL + Dapper.AOT 拦截器路径（2026-09-13 勘正：34 调用点全量拦截接管，
//    调用点直接重载已为生成拦截器目标；经典反射路径不再可达）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：同 DapperOutboxStore（封装 API 面三方言 NativeAOT 实测 13/13，
// 边界与库级 46 警告说明见 docs/persistence-aot-status.md；ADR-020 已修订为退役延后）。
//
// 💡 什么是收件箱模式（Inbox Pattern）？
//   ｜ 当服务消费消息队列中的消息时，可能出现"处理成功但确认失败"
//   ｜ （at-least-once 投递导致同一消息被重复投递）。
//   ｜ 收件箱模式的解决方案：在处理消息前，先将消息 ID + 消费者名
//   ｜ 写入 inbox_messages 表。再次收到相同消息时，检查收件箱：
//   ｜ - 如果已处理（Processed）→ 跳过（幂等）
//   ｜ - 如果正在处理（Processing）→ 等待或跳过
//   ｜ - 如果不存在 → 开始处理
//   ｜ 这样就保证了"每条消息只被处理一次"（Exactly-Once 语义）。
//
// 💡 跨数据库 INSERT + RETURN ID 语法差异：
//   ｜ - PostgreSQL：INSERT ... ON CONFLICT ... RETURNING id（单语句原子幂等，无 TOCTOU）
//   ｜ - MySQL：普通 INSERT ...; SELECT LAST_INSERT_ID();（冲突抛 1062 由
//   ｜   IsUniqueConstraintViolation 捕获转回查分支——三十八轮 P1 回归修复，
//   ｜   弃用 ON DUPLICATE KEY UPDATE 模式：SELECT LAST_INSERT_ID() 恒返回一行，
//   ｜   冲突路径伪造 Processing 记录）
//   ｜ - SQLite：INSERT OR IGNORE ...; SELECT last_insert_rowid() WHERE changes() > 0;
//   ｜
// ⚠️ SQLite / MySQL 路径的幂等是"弱保证"：依赖唯一约束防重复记录，
//   ｜ 但 INSERT 与 SELECT 两步之间存在 TOCTOU 窗口——并发消费者可能
//   ｜ 读到尚未 COMMIT 的行而误判为"无主"。生产场景推荐 PostgreSQL 单语句路径。
//   详见 SqlTemplates.InboxInsertSqlite / InboxInsertPG 注释。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Data.Common;
using System.Text;
using PalDDD.Core;

using PalDDD.Transactions;
using static PalDDD.Dapper.DapperSqlErrorClassifier;
using PalUlid = ByteAether.Ulid.Ulid;
namespace PalDDD.Dapper;

public sealed class DapperInboxStore : IInboxStore, IBatchInboxStore
{
    private readonly DbConnection _connection;
    private readonly DapperSqlDialect _dialect;
    private readonly DapperDbType _dbType;
    private readonly DbTransaction? _transaction;
    /// <summary>生效事务（二轮评审 T5）：显式构造参数优先，否则查同连接 DapperUnitOfWork
    /// 的 ambient 活动事务——DI 解析的 Store（构造时无事务）也能参与 UoW 事务边界。</summary>
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);


    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）</param>
    public DapperInboxStore(DbConnection connection, DapperDbType dbType, DbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DapperConnectionGuard.Validate(connection, dbType);
        _connection = connection;
        _dialect = DapperSqlDialect.For(dbType);
        _dbType = dbType;
        _transaction = transaction;
    }

    public async ValueTask<InboxMessage?> TryStartProcessingAsync(
        string consumerName, string messageId, DateTimeOffset now,
        TimeSpan processingTimeout, CancellationToken ct)
    {
        // ITM-163 修复：补空白守卫（对齐 InMemoryInboxStore 同款）
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        // v28 P3（ITM-107 姊妹守卫，镜像 DapperProjectionCheckpointStore.TryStartAsync 的负值守卫）：
        // processingTimeout 必须非负——负值使 cutoff = now - processingTimeout 落到 now 之后，
        // 刚启动的 Processing 记录（now - ProcessingStartedAt ≈ 0 < 负 timeout 的反向区间）被
        // 误判超时可抢占，防并发保护失效（僵尸接管窗口）。允许 TimeSpan.Zero：超时接管
        //（timeout=0）恒可重入是方言探针的合法测试语义（对齐 Checkpoint 侧"仅禁负值"口径）。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");
        var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // 2026-10-22 性能轮：PG/SQLite 单语句认领——新建、失败重试、租约过期接管三种
        // 「取得执行权」情形由一条 UPSERT 判定并落库，零行即「已处理或租约未过期」。
        // 原路径为 INSERT →（冲突）回读 → CAS 更新：重复消息 2 次往返、可重试失败 3 次，
        // 现分别收敛为 1 次；同时消掉回读与 CAS 之间的 TOCTOU 窗口。条件与批量路径
        // （TryStartProcessingManyAsync 的 RetryableLeaseCondition）逐条等价，跨路径语义一致。
        if (_dbType is DapperDbType.PostgreSql or DapperDbType.Sqlite)
        {
            var claimed = await c.QueryFirstOrDefaultAsync<InboxMessage>(
                UpsertClaim,
                new
                {
                    c = consumerName,
                    m = messageId,
                    now = ToTimeParam(now),
                    cutoff = ToTimeParam(now - processingTimeout)
                },
                Tx).ConfigureAwait(false);
            return claimed;
        }

        // ct 传递口径（M1-3 勘正 2026-09-19）：原「改 CommandDefinition 传递 ct」为十七轮方案，
        // 已被 2026-09-13 裁决推翻（拦截器下 DAP057 + NativeAOT 异常，34 调用点改直接重载——
        // 现行口径见 DapperAotInitializer.cs:15-22，勿按旧注释「恢复」）。
        // 三十八轮 P1 回归修复：MySQL 路径 InboxInsertMySql 为普通 INSERT——唯一约束冲突抛
        // MySqlException(1062)，捕获后转下方 existing 回查分支（Processed 判断/超时抢占语义恢复可达）。
        // PG（RETURNING）/SQLite（INSERT OR IGNORE）路径冲突不抛异常，此 catch 仅 MySQL 可达。
        long? insertedId;
        try
        {
            insertedId = await c.QueryFirstOrDefaultAsync<long?>(
                _dialect.InboxInsert,
                    new { c = consumerName, m = messageId, now = ToTimeParam(now) }, Tx).ConfigureAwait(false);
        }
        catch (DbException ex) when (_dbType == DapperDbType.MySql && IsUniqueConstraintViolation(ex))
        {
            insertedId = null; // 唯一约束冲突——记录已存在，非错误
        }
        if (insertedId.HasValue)
        {
            return new InboxMessage
            {
                Id = insertedId.Value,
                ConsumerName = consumerName,
                MessageId = messageId,
                Status = InboxStatus.Processing,
                ReceivedAt = now,
                ProcessingStartedAt = now,
                Attempts = 1
            };
        }

        var existing = await c.QueryFirstOrDefaultAsync<InboxMessage>(
            SqlTemplates.InboxSelect,
                new { c = consumerName, m = messageId }, Tx).ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.Status == InboxStatus.Processed) return null;
            if (existing.Status == InboxStatus.Processing
                && existing.ProcessingStartedAt.HasValue
                && (now - existing.ProcessingStartedAt.Value) < processingTimeout) return null;

            var rows = await c.ExecuteAsync(
                SqlTemplates.InboxStartProcessing,
                    new
                    {
                        now = ToTimeParam(now),
                        id = existing.Id,
                        // P1 修复（超时接管）：cutoff = now - processingTimeout——超时前的 Processing
                        // 记录可被抢占，刚开始的不可（CAS 由 processing_started_at 原子更新保证）
                        cutoff = ToTimeParam(now - processingTimeout)
                    }, Tx).ConfigureAwait(false);
            if (rows == 0) return null;

            // ITM-168 修复：抢占后本地字段同步 DB 真值——原实现只改 Status/Attempts，
            // ProcessingStartedAt 仍为旧值、LastError 残留旧失败原因（SQL 现已同步清
            // last_error），调用方与监控看到的本地对象陈旧失真（对齐 InMemory successor
            // 与 EFCore/PalORM 抢占路径）。
            existing.Status = InboxStatus.Processing;
            existing.Attempts++;
            existing.ProcessingStartedAt = now;
            existing.LastError = null;
            return existing;
        }

        return null;
    }

    // P2 修复：Mark 系列从同步 IO（EnsureOpen + Execute）改为异步路径并真正传递 ct——
    // 此前签名带 ct 但体内零使用，取消后 SQL 仍执行至自然完成
    public async ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
    {
        // ITM-163 修复：补 message null 守卫（对齐 InMemoryInboxStore/InboxDbContext 同款）
        ArgumentNullException.ThrowIfNull(message);
        var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // 三十八轮 P2 修复（ITM-210 Inbox 姊妹）：processing_started_at 抢占 token 守卫——
        // 被抢占的旧 worker token 不匹配零命中，不覆盖新 worker 的行；affected=0 时
        // 零内存变异（对齐 Outbox ITM-210 语义）。ProcessingStartedAt 为 null 属调用方误用——
        // 实际失败点在 C# 层：下方 message.ProcessingStartedAt!.Value 先抛 InvalidOperationException
        //（R41 ITM-271 勘正原"SQL 等值比较 NULL 永假"的失实描述——SQL 层不可达）；行为仍 fail-closed。
        var affected = await c.ExecuteAsync(
            SqlTemplates.InboxMarkProcessed,
                new { at = ToTimeParam(processedAt), id = message.Id, startedAt = ToTimeParam(message.ProcessingStartedAt!.Value) }, Tx).ConfigureAwait(false);
        // R11 F16：成功命中时同步调用方持有的实例（对齐 PalORM/EF）——原实现丢弃
        // affected 且不回写，复用同一对象的调用方看到陈旧 Processing/ProcessedAt。
        // 0 行（token 不匹配）零变异，保持被抢占者的本地快照不变。
        if (affected > 0)
        {
            message.Status = InboxStatus.Processed;
            message.ProcessedAt = processedAt;
            message.LastError = null;
        }
    }

    public async ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        // ITM-163 修复：补 message null 守卫（failureReason 空白守卫已存在）
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // v22 B 批：截断兜底——PD24 管线截断族
        // v27 P3（B 片 N1）：守卫前置——原截断先于 null/空白守卫执行，null 输入抛 NRE 而非
        // ArgumentException（对照姊妹 DapperOutboxStore.MarkDead 的守卫在前形态）
        // ITM-638 修复（跨栈契约一致）：Truncate(2040) 改 FailureReason.Normalize——PalORM
        // PalOrmInboxStore.MarkFailedAsync 走 Normalize（截 MaxLength=2000 + 空白归一
        // "(no message)"），Dapper 用 2040 使同输入跨栈存储值分叉；Normalize 内部复用 Truncate
        //（含 UTF-16 代理对守卫），无需另行处理截断。Outbox 族保持 2040 不变——Error=null 是
        // "未出错"语义，空白归一会破坏该语义（见 FailureReason.Truncate 文档）。
        failureReason = FailureReason.Normalize(failureReason);
        var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // 三十八轮 P2 修复：同 MarkProcessedAsync——processing_started_at 抢占 token 守卫
        var affected = await c.ExecuteAsync(
            SqlTemplates.InboxMarkFailed,
                new { err = failureReason, id = message.Id, startedAt = ToTimeParam(message.ProcessingStartedAt!.Value) }, Tx).ConfigureAwait(false);
        // R11 F16：同 MarkProcessed——命中时同步入参对象（0 行不清洗被抢占者快照）。
        if (affected > 0)
        {
            message.Status = InboxStatus.Failed;
            message.LastError = failureReason;
        }
    }


    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<InboxMessage>> TryStartProcessingManyAsync(
        string consumerName,
        IReadOnlyList<string> messageIds,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(messageIds);
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");
        ct.ThrowIfCancellationRequested();
        if (messageIds.Count == 0)
            return [];

        var unique = new List<string>(messageIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var messageId in messageIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            if (seen.Add(messageId))
                unique.Add(messageId);
        }

        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var claimed = new List<InboxMessage>(unique.Count);
        if (_dbType == DapperDbType.MySql)
            await ClaimManyMySqlAsync(connection, consumerName, unique, now, processingTimeout, claimed, ct).ConfigureAwait(false);
        else
            await ClaimManyReturningAsync(connection, consumerName, unique, now, processingTimeout, claimed, ct).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>
    /// PG/SQLite 批量认领：单语句 UPSERT。新建行走 INSERT 返回，既有可重入行（Pending、
    /// Failed、租约过期 Processing）走 DO UPDATE 返回；Processed 与活跃 Processing 不返回。
    /// 每块 1 次往返，块内 400 行（SQLite 参数上限 999 防御）。
    /// </summary>
    private async Task ClaimManyReturningAsync(
        DbConnection connection,
        string consumerName,
        List<string> messageIds,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        List<InboxMessage> claimed,
        CancellationToken ct)
    {
        _ = ct;
        const int chunkRows = 400;
        var cutoff = now - processingTimeout;
        var nowParam = ToTimeParam(now);
        var cutoffParam = ToTimeParam(cutoff);

        for (var start = 0; start < messageIds.Count; start += chunkRows)
        {
            var count = Math.Min(chunkRows, messageIds.Count - start);
            var parameters = new DynamicParameters();
            parameters.Add("c", consumerName);
            parameters.Add("now", nowParam);
            parameters.Add("cutoff", cutoffParam);
            var sql = new StringBuilder(320 + count * 24)
                .Append("INSERT INTO inbox_messages (consumer_name,message_id,status,received_at,processing_started_at,attempts) VALUES ");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sql.Append(',');
                var name = "m" + (start + i);
                sql.Append("(@c,@").Append(name).Append(",1,@now,@now,1)");
                parameters.Add(name, messageIds[start + i]);
            }
            sql.Append(" ON CONFLICT (consumer_name,message_id) DO UPDATE SET status=1,attempts=inbox_messages.attempts+1,processing_started_at=@now,last_error=NULL WHERE ")
                .Append(RetryableLeaseCondition)
                .Append(" RETURNING ")
                .Append(InboxColumns);
            claimed.AddRange(await connection.QueryAsync<InboxMessage>(
                sql.ToString(), parameters, Tx).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// MySQL 批量认领（2026-10-22 性能轮）：单语句 UPSERT + 按认领令牌回读，每块 2 次往返。
    /// <para>
    /// 原形态是「SELECT 既有行 → 插入缺失行（冲突整块回落逐条）→ 回读本次插入 → 既有可重入行
    /// 逐条 CAS」：全新增 3 次/批，但**可重入失败行按每行 2 到 3 次往返**处理（400 行重投约
    /// 800 次），重投风暴下成为吞吐瓶颈。
    /// </para>
    /// <para>
    /// 令牌列 <c>claim_token</c> 是本路径唯一可靠的归属身份：MySQL 无
    /// <c>UPDATE ... RETURNING</c>，而用 <c>processing_started_at=@now</c> 做身份在同一 now 的
    /// 并发认领下会把他人刚写入的行回读成自己的（R14 F4 实证，真库用例
    /// <c>Inbox_MySql_BatchClaim_SameTimestamp_DoesNotClaimTwice</c> 锁定）。令牌每次调用新生成，
    /// 回读按 <c>(consumer_name, claim_token, message_id IN 本块)</c>，归属确定。
    /// </para>
    /// <para>
    /// 可重入条件直接以被 DO UPDATE 命中的既有行为准；赋值按左到右求值，<c>status</c> 放在最后
    /// 赋值，保证前面各列的 <c>IF</c> 读到的都是旧状态（否则 status 先改成 1 会让后续判断误判）。
    /// </para>
    /// </summary>
    private async Task ClaimManyMySqlAsync(
        DbConnection connection,
        string consumerName,
        List<string> messageIds,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        List<InboxMessage> claimed,
        CancellationToken ct)
    {
        const int chunkRows = 400;
        _ = ct; // 与 PG/SQLite 批量路径同口径：批量认领的取消在连接层生效
        var cutoff = now - processingTimeout;
        var nowParam = ToTimeParam(now);
        var cutoffParam = ToTimeParam(cutoff);
        // 每次调用一枚令牌（块间共用无碍：回读同时按本块 message_id 过滤）。
        var token = PalUlid.New().ToString(null, System.Globalization.CultureInfo.InvariantCulture);

        for (var start = 0; start < messageIds.Count; start += chunkRows)
        {
            var count = Math.Min(chunkRows, messageIds.Count - start);
            var chunkIds = messageIds.GetRange(start, count);

            // ① UPSERT：无行则插入（获得执行权）；有行且可重入则就地接管；其余（Processed、
            //    活跃 Processing）保持原状，claim_token 不变，因此不会被②回读到。
            var parameters = new DynamicParameters();
            parameters.Add("c", consumerName);
            parameters.Add("now", nowParam);
            parameters.Add("cutoff", cutoffParam);
            parameters.Add("token", token);
            var upsert = new StringBuilder(420 + (count * 46))
                .Append("INSERT INTO inbox_messages (consumer_name,message_id,status,received_at,processing_started_at,attempts,claim_token) VALUES ");
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                    upsert.Append(',');
                var name = "m" + (start + i);
                upsert.Append("(@c,@").Append(name).Append(",1,@now,@now,1,@token)");
                parameters.Add(name, messageIds[start + i]);
            }
            upsert.Append(" ON DUPLICATE KEY UPDATE")
                  .Append(" claim_token=IF(").Append(RetryableLeaseCondition).Append(",@token,claim_token),")
                  .Append(" attempts=IF(").Append(RetryableLeaseCondition).Append(",attempts+1,attempts),")
                  .Append(" processing_started_at=IF(").Append(RetryableLeaseCondition).Append(",@now,processing_started_at),")
                  .Append(" last_error=IF(").Append(RetryableLeaseCondition).Append(",NULL,last_error),")
                  .Append(" status=IF(").Append(RetryableLeaseCondition).Append(",1,status)");
            try
            {
                await connection.ExecuteAsync(upsert.ToString(), parameters, Tx).ConfigureAwait(false);
            }
            catch (DbException ex) when (ex.Message.Contains("claim_token", StringComparison.OrdinalIgnoreCase))
            {
                // 升级未执行 ALTER 的库会报「Unknown column 'claim_token'」——provider 文案已点名，
                // 这里补上迁移入口，避免升级者在 SQL 层反复试错（R13 F24 可行动异常同款）。
                throw new InvalidOperationException(
                    "MySQL 批量认领需要 inbox_messages.claim_token 列（2026-10-22 新增）。"
                    + "请先执行 docs/migration/inbox-claim-token.md 中的 ALTER TABLE，再重试。", ex);
            }

            // ② 按令牌回读本块命中的行：令牌唯一，归属不依赖时间戳。
            var rereadParams = new DynamicParameters();
            rereadParams.Add("c", consumerName);
            rereadParams.Add("token", token);
            var rereadSql = new StringBuilder(180 + (count * 12))
                .Append("SELECT ").Append(InboxColumns)
                .Append(" FROM inbox_messages WHERE consumer_name=@c AND claim_token=@token AND message_id IN (");
            AppendInList(rereadSql, rereadParams, chunkIds);
            claimed.AddRange(await connection.QueryAsync<InboxMessage>(
                rereadSql.ToString(), rereadParams, Tx).ConfigureAwait(false));
        }
    }

    /// <summary>拼 <c>@m0,@m1,...</c> 参数列表并追加右括号（调用方负责写左括号）。</summary>
    private static void AppendInList(
        StringBuilder sql, DynamicParameters parameters, List<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0)
                sql.Append(',');
            var name = "m" + i;
            sql.Append('@').Append(name);
            parameters.Add(name, ids[i]);
        }
        sql.Append(')');
    }

    /// <inheritdoc/>
    public async ValueTask<int> MarkProcessedManyAsync(
        string consumerName,
        IReadOnlyList<InboxMessage> messages,
        DateTimeOffset processedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(messages);
        ct.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return 0;

        // R13 F19：同批必须属于同一消费者（接口契约）——原实现按 consumerName 静默过滤，
        // 异消费者元素停留 Processing 至租约到期后被当僵尸重处理（副作用重复），
        // 返回值只少不报、调用方无法定位被跳过者。契约违规应在入口 fail-fast。
        foreach (var message in messages)
        {
            if (!string.Equals(message.ConsumerName, consumerName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"MarkProcessedManyAsync requires a single-consumer batch: message '{message.MessageId}' "
                    + $"belongs to consumer '{message.ConsumerName}', not '{consumerName}'.",
                    nameof(messages));
            }
        }

        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var affected = 0;
        foreach (var group in messages.GroupBy(static m => m.ProcessingStartedAt))
        {
            var startedAt = group.Key
                ?? throw new InvalidOperationException(
                    "MarkProcessedManyAsync requires messages with ProcessingStartedAt (claimed lease token).");
            var ids = group
                .Select(static m => m.MessageId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0)
                continue;

            const int chunkRows = 400;
            for (var start = 0; start < ids.Length; start += chunkRows)
            {
                var count = Math.Min(chunkRows, ids.Length - start);
                var parameters = new DynamicParameters();
                parameters.Add("at", ToTimeParam(processedAt));
                parameters.Add("c", consumerName);
                parameters.Add("startedAt", ToTimeParam(startedAt));
                var sql = new StringBuilder(160 + count * 12)
                    .Append("UPDATE inbox_messages SET status=2,processed_at=@at,last_error=NULL WHERE consumer_name=@c AND status=1 AND processing_started_at=@startedAt AND message_id IN (");
                for (var i = 0; i < count; i++)
                {
                    if (i > 0) sql.Append(',');
                    var name = "m" + (start + i);
                    sql.Append('@').Append(name);
                    parameters.Add(name, ids[start + i]);
                }
                sql.Append(')');
                affected += await connection.ExecuteAsync(sql.ToString(), parameters, Tx).ConfigureAwait(false);
            }
        }
        return affected;
    }

    private static IEnumerable<string> CollectChunkIds(
        List<string> ids, int start, int count)
    {
        for (var i = 0; i < count; i++)
            yield return ids[start + i];
    }

    private const string InboxColumns =
        "id,message_id,consumer_name,status,received_at,processed_at,processing_started_at,attempts,last_error";

    /// <summary>
    /// 单条认领 UPSERT（2026-10-22 性能轮，PG/SQLite）——新建插入、可重入行（Pending、
    /// Failed、租约过期 Processing）经 DO UPDATE 取得执行权，其余（Processed、活跃
    /// Processing）零行返回。条件与批量路径的 <see cref="RetryableLeaseCondition"/> 等价；
    /// RETURNING 列显式 PascalCase 别名，不依赖 MatchNamesWithUnderscores 全局开关。
    /// </summary>
    private const string UpsertClaim = """
        INSERT INTO inbox_messages (consumer_name,message_id,status,received_at,processing_started_at,attempts)
        VALUES (@c,@m,1,@now,@now,1)
        ON CONFLICT (consumer_name,message_id) DO UPDATE
        SET status=1, attempts=inbox_messages.attempts+1, processing_started_at=@now, last_error=NULL
        WHERE inbox_messages.status=0
           OR inbox_messages.status=3
           OR (inbox_messages.status=1
               AND inbox_messages.processing_started_at IS NOT NULL
               AND inbox_messages.processing_started_at<@cutoff)
        RETURNING id AS Id, message_id AS MessageId, consumer_name AS ConsumerName,
                  status AS Status, received_at AS ReceivedAt, processed_at AS ProcessedAt,
                  processing_started_at AS ProcessingStartedAt, attempts AS Attempts,
                  last_error AS LastError
        """;

    /// <summary>可重入条件（Pending、Failed、租约过期 Processing）；Processed/活跃 Processing 不命中。</summary>
    private const string RetryableLeaseCondition =
        "inbox_messages.status=0 OR inbox_messages.status=3 OR (inbox_messages.status=1 AND inbox_messages.processing_started_at IS NOT NULL AND inbox_messages.processing_started_at<@cutoff)";

    /// <summary>
    /// P2 修复（ToMySqlParameter 接线）：按方言选择时间参数格式。
    /// <para>
    /// P2/P3 修复（十七轮）：返回 <c>object</c>（DateTimeOffset 装箱一次）是刻意的收口防线——
    /// 强类型返回会诱导调用方绕过本方法自行格式化，方言错配（PG text OID / MySQL session tz）
    /// 将重新进入；五 Store 同款声明（Outbox/Inbox/Saga/EventLog/Checkpoint）。装箱开销相对 SQL 执行成本可忽略。
    /// </para>
    /// </summary>
    private object ToTimeParam(DateTimeOffset value)
        => _dbType switch
        {
            DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
            // P1 修复（八轮评审）：PG 传原生 DateTimeOffset——Npgsql 映射 timestamptz；
            // "O" string 按 text OID 发送，timestamptz <= text 无比较运算符，WHERE 必炸 42883
            DapperDbType.PostgreSql => value,
            _ => DapperAotInitializer.ToSqliteParameter(value)
        };

    /// <summary>确保连接已打开（异步版本），避免线程池阻塞</summary>
    private async ValueTask<DbConnection> EnsureOpenAsync(CancellationToken ct = default)
    {
        var c = _connection;
        if (c.State != ConnectionState.Open) { await c.OpenAsync(ct).ConfigureAwait(false); }
        return c;
    }

    // ITM-796（2026-09-19）：IsUniqueConstraintViolation 私有副本收口至
    // DapperSqlErrorClassifier（码集并集：MySQL 1062/1586/1022、PG 23505、SQLite
    // 19/2067 码+message 兜底、SqlServer 2601/2627）——IL2075 抑制随类迁移
}
