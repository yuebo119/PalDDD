// ─────────────────────────────────────────────────────────────
// 📥 DapperInboxStore — 收件箱存储的 Dapper 实现
//    纯 Dapper SQL + Dapper.AOT 编译时拦截
// ─────────────────────────────────────────────────────────────
// AOT 安全性：同 DapperOutboxStore，Dapper snake_case 纯字符串映射，零反射。
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
//   ｜ - MySQL：INSERT IGNORE ...; SELECT LAST_INSERT_ID();
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
        var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // P2/P3 修复（十七轮）：全部查询/执行改 CommandDefinition 传递 ct（对齐 DapperOutboxStore.RequeueDeadAsync 模式）——
        // 原重载不接收取消令牌，取消信号只在 EnsureOpenAsync 阶段可传递，SQL 执行阶段不可取消
        var insertedId = await c.QueryFirstOrDefaultAsync<long?>(
            new CommandDefinition(_dialect.InboxInsert,
                new { c = consumerName, m = messageId, now = ToTimeParam(now) }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
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
                new { c = consumerName, m = messageId }, _transaction, cancellationToken: ct)).ConfigureAwait(false);

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
                    }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
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
        await c.ExecuteAsync(
            new CommandDefinition(SqlTemplates.InboxMarkProcessed,
                new { at = ToTimeParam(processedAt), id = message.Id }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        // ITM-163 修复：补 message null 守卫（failureReason 空白守卫已存在）
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // P2/P3 修复（十七轮）：CommandDefinition 传 ct（见 TryStartProcessingAsync 同款注释）
        await c.ExecuteAsync(
            new CommandDefinition(SqlTemplates.InboxMarkFailed,
                new { err = failureReason, id = message.Id }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
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
}
