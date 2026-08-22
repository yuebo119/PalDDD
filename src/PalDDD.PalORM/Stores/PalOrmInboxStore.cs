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
/// <item><b>MySQL</b>：普通 INSERT + 唯一约束冲突异常捕获（IsDuplicateKeyError）→ 回查现有记录。
/// 三十八轮 P1 回归修复：弃用三十七轮 A1 的 <c>ON DUPLICATE KEY UPDATE id = id</c> + affected&gt;0 判断——
/// MySqlConnector 默认 UseAffectedRows=false 报告 found rows，冲突时也返回 1，伪造 Processing 记录。</item>
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
            // MySQL 路径：普通 INSERT + 唯一约束冲突异常捕获（三十八轮 P1 回归修复）。
            // 三十七轮 A1 曾改为 ON DUPLICATE KEY UPDATE id = id + affected>0 判断新插入——
            // 但 MySqlConnector 默认 UseAffectedRows=false 报告 found rows，冲突时也返回 1，
            // 伪造 Processing 记录绕过 Processed/超时/抢占分支（幂等破坏）。
            // 现对齐 PalOrmIdempotencyStore ITM-228 同款模式：冲突抛 1062 → affected=0 → 回查分支。
            var affected = 0;
            try
            {
                affected = await Session.ExecuteAsync(
                    $"INSERT INTO inbox_messages (message_id, consumer_name, status, received_at, processing_started_at, attempts) VALUES ({messageId}, {consumerName}, {statusProcessing}, {now}, {now}, 1)",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                affected = 0; // 唯一约束冲突——记录已存在，非错误
            }
            if (affected > 0)
            {
                // 新插入成功 —— 查回自增 id。P3-SRC-212：改 ScalarAsync<long?>（对齐上方
                // PG/SQLite 路径的可空标量形态）——行不存在时（INSERT 成功后被并发 DELETE，
                // 实际不可达）原非空断言会抛 InvalidCastException 掩盖真实根因；显式判空抛
                // 语义化异常，加固不可达路径
                var newId = await Session.ScalarAsync<long?>(
                    $"SELECT id FROM inbox_messages WHERE consumer_name = {consumerName} AND message_id = {messageId}",
                    ct).ConfigureAwait(false);
                if (newId is null)
                    throw new InvalidOperationException(
                        $"INSERT 成功但回查 id 不存在（consumer_name={consumerName}, message_id={messageId}）——可能被并发 DELETE");
                return new InboxMessage
                {
                    Id = newId.Value,
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
        // 显式列序：与 InboxMessageRow 属性声明序对齐（QueryAsync 按序号映射）
        // ITM-250 修复（F8）：弃用 QueryFirstAsync + catch(InvalidOperationException)——
        // PalORM QueryFirstAsync 无结果抛 InvalidOperationException，该 catch 把"无行"
        // 与真实 DB 故障/会话门禁异常一并吞为 null → 消息静默跳过无失败痕迹。
        // 改"查空"语义：QueryAsync 无行返回空列表，FirstOrDefault 显式判空，
        // 删除异常控制流（对齐 DapperInboxStore.QueryFirstOrDefaultAsync 姊妹形态，PD17——
        // 无行=非异常路径，DB 故障原样上抛）。唯一约束保证 (consumer_name, message_id)
        // 至多一行，FirstOrDefault 无歧义。
        var existing = (await Session.QueryAsync<InboxMessageRow>(
            $"SELECT id, message_id, consumer_name, status, received_at, processed_at, processing_started_at, attempts, last_error FROM inbox_messages WHERE consumer_name = {consumerName} AND message_id = {messageId}",
            ct).ConfigureAwait(false)).FirstOrDefault();
        if (existing is null)
        {
            // 回查无行（极罕见，如并发 DELETE）—— 返回 null 让调用方重试
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
        // ITM-274（R42；R43 ITM-275 勘正异常类型）：ProcessingStartedAt 为 null 属调用方误用
        //（未经 TryStartProcessingAsync 的手工构造 message）——显式 fail-fast 而非插值参数化
        // DBNull 使 SQL `= NULL` 永假、affected=0 静默零变更。对齐 Dapper 姊妹的 fail-fast 语义
        //（其失败点为 ProcessingStartedAt!.Value 附带抛 InvalidOperationException；本栈为设计守卫
        // 抛 ArgumentNullException——异常类型差异属刻意，fail-fast 语义一致）
        if (message.ProcessingStartedAt is null)
            throw new ArgumentNullException(nameof(message) + "." + nameof(message.ProcessingStartedAt));
        // 手写 SQL（不走 UpdateAsync）—— 避免 [ConcurrencyCheck]attempts 干扰并发场景
        // WHERE status=Processing(1) 守卫，防止重复标记（与 Dapper 实现一致）
        // 三十八轮 P2 修复（ITM-210 Inbox 姊妹）：processing_started_at 抢占 token 守卫——
        // 被抢占的旧 worker token 不匹配零命中，不覆盖新 worker 的行（对齐 Dapper 版同款）
        var affected = await Session.ExecuteAsync(
            $"UPDATE inbox_messages SET status = {(int)InboxStatus.Processed}, processed_at = {processedAt} WHERE id = {message.Id} AND status = {(int)InboxStatus.Processing} AND processing_started_at = {message.ProcessingStartedAt}",
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
        // ITM-274（R42）：同 MarkProcessedAsync——null ProcessingStartedAt 显式 fail-fast（对齐 Dapper 姊妹）
        if (message.ProcessingStartedAt is null)
            throw new ArgumentNullException(nameof(message) + "." + nameof(message.ProcessingStartedAt));
        // 手写 SQL（不走 UpdateAsync）—— 避免 [ConcurrencyCheck]attempts 在并发场景抛异常
        // WHERE status=Processing(1) 守卫，防止覆盖已 Processed 的记录（与 Dapper 实现一致）
        // 三十八轮 P2 修复（ITM-210 Inbox 姊妹）：processing_started_at 抢占 token 守卫（对齐 Dapper 版同款）
        var affected = await Session.ExecuteAsync(
            $"UPDATE inbox_messages SET status = {(int)InboxStatus.Failed}, last_error = {failureReason} WHERE id = {message.Id} AND status = {(int)InboxStatus.Processing} AND processing_started_at = {message.ProcessingStartedAt}",
            ct).ConfigureAwait(false);
        // ITM-168 修复：affected > 0 才变更本地对象（同 MarkProcessedAsync 陈旧语义修复）。
        if (affected > 0)
        {
            message.Status = InboxStatus.Failed;
            message.LastError = failureReason;
        }
    }

    /// <summary>
    /// 三十八轮 P1 回归修复：判定异常是否为唯一约束冲突（MySQL 1062/1586、PG 23505、SQLite UNIQUE、SqlServer 2601/2627）。
    /// 仅捕获重复键——其他错误原样上抛。与 PalOrmIdempotencyStore/PalOrmSagaStateStore 同型。
    /// </summary>
    [UnconditionalSuppressMessage("Aot", "IL2075:RequiresDynamicallyAccessedMembers",
        Justification = "PalORM 适配层为非 AOT（IsAotCompatible=false）；反射读取 provider 异常属性用于错误分类。")]
    private static bool IsDuplicateKeyError(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var type = ex.GetType();
            var typeName = type.Name;

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(ex) is int mysqlNum
                && (mysqlNum == 1062 || mysqlNum == 1586))
                return true;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(ex) is string pgState
                && pgState == "23505")
                return true;

            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(ex) is int sqlNum
                && (sqlNum == 2601 || sqlNum == 2627))
                return true;
        }
        return false;
    }
}
