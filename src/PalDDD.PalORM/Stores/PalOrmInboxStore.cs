using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalDDD.PalORM.Models;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Inbox Store 的 PalORM 实现 —— 双泛型核心基类。
/// <para>
/// <b>核心挑战</b>：<see cref="IInboxStore.TryStartProcessingAsync"/> 需要原子幂等 INSERT —— 三方言分叉：
/// <list type="bullet">
/// <item><b>PG/SQLite</b>：<c>INSERT ... ON CONFLICT (consumer_name, message_id) DO NOTHING RETURNING id</c>（单语句原子）</item>
/// <item><b>MySQL</b>：<c>INSERT IGNORE ...; SELECT LAST_INSERT_ID()</c>（两步）+ 冲突时回查</item>
/// </list>
/// </para>
/// <para>
/// <b>乐观锁</b>：[ConcurrencyCheck]Attempts（int 自增）—— PALORM012 不接受 DateTimeOffset 时间戳。
/// 替代 EFCore 的 ProcessingStartedAt 并发令牌；语义等价（每次更新自增）。
/// </para>
/// </summary>
public class PalOrmInboxStore<TProvider> : IInboxStore
    where TProvider : IDbProvider
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    /// <summary>构造 Inbox Store。</summary>
    public PalOrmInboxStore(DataSession<TProvider> session) => Session = session;

    /// <inheritdoc />
    public async ValueTask<InboxMessage?> TryStartProcessingAsync(
        string consumerName, string messageId, DateTimeOffset now, TimeSpan processingTimeout, CancellationToken ct)
    {
        var statusProcessing = (int)InboxStatus.Processing;

        if (TProvider.SupportsReturningClause)
        {
            // PG/SQLite 单语句原子幂等 —— ON CONFLICT DO NOTHING 保证 (consumer_name, message_id) 唯一
            // RETURNING id 返回新插入的 id；冲突时返回 0 行（NULL）→ 回查现有记录
            // 注：QueryFirstAsync<T> 约束 T:class，不接受值类型；用 ScalarAsync<long?> 取标量
            var newId = await Session.ScalarAsync<long?>(
                $"INSERT INTO inbox_messages (message_id, consumer_name, status, received_at, processing_started_at, attempts) VALUES ({messageId}, {consumerName}, {statusProcessing}, {now}, {now}, 1) ON CONFLICT (consumer_name, message_id) DO NOTHING RETURNING id",
                ct);
            if (newId is long id)
            {
                return new InboxMessage
                {
                    Id = id,
                    MessageId = messageId,
                    ConsumerName = consumerName,
                    Status = InboxStatus.Processing,
                    ReceivedAt = now,
                    ProcessingStartedAt = now,
                    Attempts = 1,
                };
            }
        }
        else
        {
            // MySQL 路径：INSERT IGNORE + LAST_INSERT_ID（无 RETURNING）
            // 注意：MySQL 用复合 INSERT ...; SELECT 结构 —— PalORM ExecuteAsync 仅返回影响行数，不返回查询结果
            // 改用两步：先 INSERT IGNORE（捕获影响行数），再按复合键查
            var affected = await Session.ExecuteAsync(
                $"INSERT IGNORE INTO inbox_messages (message_id, consumer_name, status, received_at, processing_started_at, attempts) VALUES ({messageId}, {consumerName}, {statusProcessing}, {now}, {now}, 1)",
                ct);
            if (affected > 0)
            {
                // 新插入成功 —— 查回自增 id（ScalarAsync 支持 long）
                var newId = await Session.ScalarAsync<long>(
                    $"SELECT id FROM inbox_messages WHERE consumer_name = {consumerName} AND message_id = {messageId}",
                    ct);
                return new InboxMessage
                {
                    Id = newId,
                    MessageId = messageId,
                    ConsumerName = consumerName,
                    Status = InboxStatus.Processing,
                    ReceivedAt = now,
                    ProcessingStartedAt = now,
                    Attempts = 1,
                };
            }
        }

        // INSERT 冲突（记录已存在）—— 回查现有记录决定返回语义
        InboxMessageRow? existing;
        try
        {
            existing = await Session.QueryFirstAsync<InboxMessageRow>(
                $"SELECT * FROM inbox_messages WHERE consumer_name = {consumerName} AND message_id = {messageId}",
                ct);
        }
        catch (InvalidOperationException)
        {
            // 异常路径：回查也无（极罕见，如并发 DELETE）—— 返回 null 让调用方重试
            return null;
        }

        // 已 Processed → 幂等跳过
        if ((InboxStatus)existing.Status == InboxStatus.Processed)
            return null;

        // 仍在 Processing 且未超时 → 返回 null
        if ((InboxStatus)existing.Status == InboxStatus.Processing
            && existing.ProcessingStartedAt is DateTimeOffset started
            && started + processingTimeout > now)
        {
            return null;
        }

        // 超时或 Failed —— 尝试抢占（原子条件 UPDATE：status<>'Processed' 守卫）
        // 走 UpdateAsync（[ConcurrencyCheck]Attempts 自动加并发谓词）
        existing.Status = statusProcessing;
        existing.Attempts += 1;
        existing.ProcessingStartedAt = now;
        existing.LastError = null;
        try
        {
            await Session.UpdateAsync(existing, ct);
        }
        catch (ConcurrencyConflictException)
        {
            // 并发抢占失败 —— 返回 null
            return null;
        }

        return existing.ToDomain();
    }

    /// <inheritdoc />
    public async ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
    {
        message.Status = InboxStatus.Processed;
        message.ProcessedAt = processedAt;
        var row = InboxMessageRow.FromDomain(message);
        // UpdateAsync 自动加 WHERE attempts=@orig（[ConcurrencyCheck]）
        await Session.UpdateAsync(row, ct);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        message.Status = InboxStatus.Failed;
        message.LastError = failureReason;
        var row = InboxMessageRow.FromDomain(message);
        await Session.UpdateAsync(row, ct);
    }
}
