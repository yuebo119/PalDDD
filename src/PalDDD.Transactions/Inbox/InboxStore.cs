namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 收件箱存储抽象 — AOT 安全，零反射
// （InboxStatus/InboxMessage 已拆分至同目录独立文件，对齐 OutboxStore 单类型组织，
//  精炼重组 2026-08-26）
// ─────────────────────────────────────────────────────────────

/// <summary>收件箱存储抽象 — 解耦 InboxProcessor 与具体数据库实现</summary>
/// <remarks>
/// ⚠️ 消费语义边界（v17 补文）：TryStartProcessingAsync 成功后 handler 若抛 OCE（取消传播），
/// InboxProcessor 不标 Failed——记录滞留 Processing 至 ProcessingTimeout 才可重入。取舍理由：
/// OCE 语义 = 执行进度未知，标 Failed 会重放可能已完成的副作用。
/// </remarks>
/// <remarks>
/// 生产实现必须提供 (ConsumerName, MessageId) 唯一约束，保证每个消费者的消息只处理一次。<br/>
/// EF Core 实现由 PalDDD.Transactions.EFCore 适配包提供。
/// </remarks>
public interface IInboxStore
{
    /// <summary>尝试获取消息处理权。返回 null 表示消息已处理或仍在其他消费者处理中。</summary>
    ValueTask<InboxMessage?> TryStartProcessingAsync(
        string consumerName,
        string messageId,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct);

    /// <summary>标记消息处理成功。</summary>
    ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct);

    /// <summary>标记消息处理失败。</summary>
    ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct);

    /// <summary>
    /// 按状态列举收件箱消息（批次 C，2026-11-04）——读侧补齐，供运维查询（失败列表、
    /// 滞留处理中）与诊断。此前 IInboxStore 只有三个写方法，无任何读侧入口，
    /// Dapper/PalORM 栈只能连库写裸 SQL（三栈语义对齐承诺在此处断裂）。
    /// </summary>
    /// <param name="status">目标状态（<see cref="InboxStatus.Pending"/> /
    /// <see cref="InboxStatus.Processing"/> / <see cref="InboxStatus.Processed"/> /
    /// <see cref="InboxStatus.Failed"/>）。
    /// 必须是已定义值——传未定义值抛 <see cref="ArgumentOutOfRangeException"/>。</param>
    /// <param name="maxCount">最多返回条数（正数）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// 四栈语义统一为 <c>WHERE status = @status ORDER BY id LIMIT @maxCount</c>，
    /// 返回该状态下 id 最小的前 <paramref name="maxCount"/> 条（自增主键 = 插入序）。
    /// 走既有 <c>(status)</c> 索引。
    /// </para>
    /// <para><b>为什么不给默认实现</b>：DIM 返回空列表会让未更新的自定义存储静默给出
    /// 「查无此状态」，与本轮要消灭的假 API 形态相同。自定义实现必须显式提供。</para>
    /// </remarks>
    ValueTask<IReadOnlyList<InboxMessage>> GetByStatusAsync(
        InboxStatus status,
        int maxCount,
        CancellationToken ct = default);
}

/// <summary>
/// Inbox 批量能力（可选实现）。2026-10-21：消费侧每次认领一批消息时，把 N 次
/// 认领与 N 次完成标记各收敛为一条语句（按方言分块），把每消息 2 次往返降到
/// 每批 2 到 4 次。未实现本接口的存储由 <see cref="InboxProcessor"/> 自动回落
/// 逐条路径，语义不变。
/// </summary>
/// <remarks>
/// 返回的 <see cref="InboxMessage"/> 在部分实现（MySQL 多行插入）中 <c>Id</c> 可能为 0；
/// 批量完成标记按 <c>(ConsumerName, MessageId, ProcessingStartedAt)</c> 定位，不依赖 Id。
/// 同一批认领的消息共享同一个 <see cref="InboxMessage.ProcessingStartedAt"/>。
/// </remarks>
public interface IBatchInboxStore
{
    /// <summary>
    /// 批量获取处理权。返回本次成功认领的子集；已处理或仍在租约内的消息不出现在结果中。
    /// 输入消息 ID 会去重；调用方应保证同一批消息来自同一消费者。
    /// </summary>
    ValueTask<IReadOnlyList<InboxMessage>> TryStartProcessingManyAsync(
        string consumerName,
        IReadOnlyList<string> messageIds,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct);

    /// <summary>
    /// 批量标记处理成功。返回实际命中的行数；按每条的 ProcessingStartedAt 令牌分组，
    /// 被抢占的旧持有者写入零命中。
    /// <para>R13 F19：<paramref name="messages"/> 必须全部属于 <paramref name="consumerName"/>
    /// （同批同消费者）；实现应以 <see cref="ArgumentException"/> 拒绝异消费者元素——
    /// 静默过滤会让被过滤消息滞留 Processing 至租约到期后被重复执行，且返回值只少不报。</para>
    /// </summary>
    ValueTask<int> MarkProcessedManyAsync(
        string consumerName,
        IReadOnlyList<InboxMessage> messages,
        DateTimeOffset processedAt,
        CancellationToken ct);
}
