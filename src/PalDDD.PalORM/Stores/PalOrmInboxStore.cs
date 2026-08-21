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
        // ITM-163 修复：补空白守卫（对齐 InMemoryInboxStore 同款）
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var statusProcessing = (int)InboxStatus.Processing;

        if (TProvider.SupportsReturningClause)
        {
            // PG/SQLite 单语句原子幂等 —— ON CONFLICT DO NOTHING 保证 (consumer_name, message_id) 唯一
            // RETURNING id 返回新插入的 id；冲突时返回 0 行（NULL）→ 回查现有记录
            // 注：QueryFirstAsync<T> 约束 T:class，不接受值类型；用 ScalarAsync<long?> 取标量
            var newId = await Session.ScalarAsync<long?>(
                $"INSERT INTO inbox_messages (message_id, consumer_name, status, received_at, processing_started_at, attempts) VALUES ({messageId}, {consumerName}, {statusProcessing}, {now}, {now}, 1) ON CONFLICT (consumer_name, message_id) DO NOTHING RETURNING id",
                ct).ConfigureAwait(false);
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
            // MySQL 路径：INSERT + ON DUPLICATE KEY UPDATE（三十七轮 A1：ITM-228 姊妹修复——
            // INSERT IGNORE 会把截断/非法日期等非重复键错误静默降为 warning）
            var affected = await Session.ExecuteAsync(
                $"INSERT INTO inbox_messages (message_id, consumer_name, status, received_at, processing_started_at, attempts) VALUES ({messageId}, {consumerName}, {statusProcessing}, {now}, {now}, 1) ON DUPLICATE KEY UPDATE id = id",
                ct).ConfigureAwait(false);
            if (affected > 0)
            {
                // 新插入成功 —— 查回自增 id（ScalarAsync 支持 long）
                var newId = await Session.ScalarAsync<long>(
                    $"SELECT id FROM inbox_messages WHERE consumer_name = {consumerName} AND message_id = {messageId}",
                    ct).ConfigureAwait(false);
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
        // 显式列序：与 InboxMessageRow 属性声明序对齐（QueryFirstAsync 按序号映射）
        InboxMessageRow? existing;
        try
        {
            existing = await Session.QueryFirstAsync<InboxMessageRow>(
                $"SELECT id, message_id, consumer_name, status, received_at, processed_at, processing_started_at, attempts, last_error FROM inbox_messages WHERE consumer_name = {consumerName} AND message_id = {messageId}",
                ct).ConfigureAwait(false);
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

        // 超时或 Failed —— 尝试抢占（手写 SQL，避免 [ConcurrencyCheck] 干扰）
        // P1 修复（五轮评审，第七轮 CAS 反弹终结）：条件守卫替代硬排他——
        // 允许抢占超时的 Processing 记录（僵尸恢复），CAS 由 processing_started_at
        // 原子更新保证（第一个 worker 的 @now 生效后第二个的超时条件失效）
        var cutoff = now - processingTimeout;
        var leaseAffected = await Session.ExecuteAsync(
            $"UPDATE inbox_messages SET status = {statusProcessing}, attempts = attempts + 1, processing_started_at = {now}, last_error = NULL WHERE id = {existing.Id} AND (status = {(int)InboxStatus.Pending} OR (status = {statusProcessing} AND processing_started_at < {cutoff}) OR status = {(int)InboxStatus.Failed})",
            ct).ConfigureAwait(false);
        if (leaseAffected == 0) return null;

        existing.Status = statusProcessing;
        existing.Attempts += 1;
        existing.ProcessingStartedAt = now;
        existing.LastError = null;
        return existing.ToDomain();
    }

    /// <inheritdoc />
    public async ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
    {
        // ITM-163 修复：补 message null 守卫（对齐 InMemoryInboxStore/InboxDbContext 同款）
        ArgumentNullException.ThrowIfNull(message);
        // 手写 SQL（不走 UpdateAsync）—— 避免 [ConcurrencyCheck]attempts 干扰并发场景
        // WHERE status='Processing' 守卫，防止重复标记（与 Dapper 实现一致）
        var affected = await Session.ExecuteAsync(
            $"UPDATE inbox_messages SET status = {(int)InboxStatus.Processed}, processed_at = {processedAt} WHERE id = {message.Id} AND status = {(int)InboxStatus.Processing}",
            ct).ConfigureAwait(false);
        // ITM-168 修复：本地对象仅在 DB 行确实受影响（affected > 0）时变更——原实现先改
        // 本地再执行 SQL 且不看 affected：记录已被并发者标记终态时 DB 未变，本地对象却
        // 已显示 Processed（陈旧语义，与 PalOrmProjectionCheckpointStore rows>0 才变更同款）。
        if (affected > 0)
        {
            message.Status = InboxStatus.Processed;
            message.ProcessedAt = processedAt;
        }
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        // ITM-163 修复：补 message null 守卫（对齐 InMemoryInboxStore/InboxDbContext 同款）
        ArgumentNullException.ThrowIfNull(message);
        // ITM-077 修复：补 failureReason 空白校验（对齐 DapperInboxStore.MarkFailedAsync/InboxDbContext/
        // InMemoryInboxStore 同款守卫）——缺守卫时空/空白失败原因会写入 last_error 列，破坏跨实现
        // 契约一致（其余三版均抛 ArgumentException）
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        // 手写 SQL（不走 UpdateAsync）—— 避免 [ConcurrencyCheck]attempts 在并发场景抛异常
        // WHERE status='Processing' 守卫，防止覆盖已 Processed 的记录（与 Dapper 实现一致）
        var affected = await Session.ExecuteAsync(
            $"UPDATE inbox_messages SET status = {(int)InboxStatus.Failed}, last_error = {failureReason} WHERE id = {message.Id} AND status = {(int)InboxStatus.Processing}",
            ct).ConfigureAwait(false);
        // ITM-168 修复：affected > 0 才变更本地对象（同 MarkProcessedAsync 陈旧语义修复）。
        if (affected > 0)
        {
            message.Status = InboxStatus.Failed;
            message.LastError = failureReason;
        }
    }
}
