// ─────────────────────────────────────────────────────────────
// 📥 DapperInboxStore — 收件箱存储的 Dapper 实现
//    纯 Dapper SQL + 运行时经典 Dapper 路径（v10 勘正：AOT 拦截未启用，非零反射）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：同 DapperOutboxStore（v10 勘正：运行时经典路径含反射，真 AOT 不可达——
// 技术依据是 csproj IL2062/IL3058 注释；ADR-020 为退役路线，v11 补注）。
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

using PalDDD.Transactions;
namespace PalDDD.Dapper;

public sealed class DapperInboxStore : IInboxStore
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
        // P2/P3 修复（十七轮）：全部查询/执行改 CommandDefinition 传递 ct（对齐 DapperOutboxStore.RequeueDeadAsync 模式）——
        // 原重载不接收取消令牌，取消信号只在 EnsureOpenAsync 阶段可传递，SQL 执行阶段不可取消
        // 三十八轮 P1 回归修复：MySQL 路径 InboxInsertMySql 为普通 INSERT——唯一约束冲突抛
        // MySqlException(1062)，捕获后转下方 existing 回查分支（Processed 判断/超时抢占语义恢复可达）。
        // PG（RETURNING）/SQLite（INSERT OR IGNORE）路径冲突不抛异常，此 catch 仅 MySQL 可达。
        long? insertedId;
        try
        {
            insertedId = await c.QueryFirstOrDefaultAsync<long?>(
                new CommandDefinition(_dialect.InboxInsert,
                    new { c = consumerName, m = messageId, now = ToTimeParam(now) }, Tx, cancellationToken: ct)).ConfigureAwait(false);
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
            new CommandDefinition(SqlTemplates.InboxSelect,
                new { c = consumerName, m = messageId }, Tx, cancellationToken: ct)).ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.Status == InboxStatus.Processed) return null;
            if (existing.Status == InboxStatus.Processing
                && existing.ProcessingStartedAt.HasValue
                && (now - existing.ProcessingStartedAt.Value) < processingTimeout) return null;

            var rows = await c.ExecuteAsync(
                new CommandDefinition(SqlTemplates.InboxStartProcessing,
                    new
                    {
                        now = ToTimeParam(now),
                        id = existing.Id,
                        // P1 修复（超时接管）：cutoff = now - processingTimeout——超时前的 Processing
                        // 记录可被抢占，刚开始的不可（CAS 由 processing_started_at 原子更新保证）
                        cutoff = ToTimeParam(now - processingTimeout)
                    }, Tx, cancellationToken: ct)).ConfigureAwait(false);
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
        // P2/P3 修复（十七轮）：CommandDefinition 传 ct（见 TryStartProcessingAsync 同款注释）
        // 三十八轮 P2 修复（ITM-210 Inbox 姊妹）：processing_started_at 抢占 token 守卫——
        // 被抢占的旧 worker token 不匹配零命中，不覆盖新 worker 的行；affected=0 时
        // 零内存变异（对齐 Outbox ITM-210 语义）。ProcessingStartedAt 为 null 属调用方误用——
        // 实际失败点在 C# 层：下方 message.ProcessingStartedAt!.Value 先抛 InvalidOperationException
        //（R41 ITM-271 勘正原"SQL 等值比较 NULL 永假"的失实描述——SQL 层不可达）；行为仍 fail-closed。
        await c.ExecuteAsync(
            new CommandDefinition(SqlTemplates.InboxMarkProcessed,
                new { at = ToTimeParam(processedAt), id = message.Id, startedAt = ToTimeParam(message.ProcessingStartedAt!.Value) }, Tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        // ITM-163 修复：补 message null 守卫（failureReason 空白守卫已存在）
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // v22 B 批：截断兜底——对齐 DapperOutboxStore.MarkDead/ReleaseForRetry 的 2040（PD24 管线截断族）
        // v27 P3（B 片 N1）：守卫前置——原截断先于 null/空白守卫执行，null 输入抛 NRE 而非
        // ArgumentException（对照姊妹 DapperOutboxStore.MarkDead 的守卫在前形态）
        if (failureReason.Length > 2040) failureReason = failureReason[..2040];
        var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // P2/P3 修复（十七轮）：CommandDefinition 传 ct（见 TryStartProcessingAsync 同款注释）
        // 三十八轮 P2 修复：同 MarkProcessedAsync——processing_started_at 抢占 token 守卫
        await c.ExecuteAsync(
            new CommandDefinition(SqlTemplates.InboxMarkFailed,
                new { err = failureReason, id = message.Id, startedAt = ToTimeParam(message.ProcessingStartedAt!.Value) }, Tx, cancellationToken: ct)).ConfigureAwait(false);
    }

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

    /// <summary>
    /// 三十八轮 P1 回归修复：判定异常是否为唯一约束冲突（MySQL 1062/1586、PG 23505、SQLite UNIQUE）。
    /// 仅捕获重复键——其他错误原样上抛。与 DapperEventLog/DapperSagaStateStore 同型
    /// （含 SqlServer 2601/2627 分支——v19 B5 勘正：原称"不含"与代码矛盾，分支为跨 provider 鸭子类型防御性保留，与 SagaStateStore 口径统一）。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级）。")]
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && (mysqlNumber == 1062 || mysqlNumber == 1586))
                return true;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string pgState
                && pgState == "23505")
                return true;

            // v25 P3 守卫族：message 使用前防护（镜像 DapperEventLog ITM-188 / DapperSagaStateStore
            // ITM-192 姊妹形态，PD17）——补 !string.IsNullOrEmpty 防 null/空消息进 Contains
            var message = inner.Message;
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                return true;

            // v20 F1：补 SqlServer 2601/2627 分支——v19 B5 注释称含但代码无（EventLog/Saga
            // 真含），代码侧补齐对齐。DapperDbType 无 SqlServer 值现状下属防御性保留。
            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
                return true;
        }
        return false;
    }
}
