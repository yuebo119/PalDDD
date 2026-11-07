namespace PalDDD.Transactions;

/// <summary>收件箱消息实体</summary>
public sealed class InboxMessage
{
    /// <summary>自增主键</summary>
    public long Id { get; init; }

    /// <summary>消息唯一标识（带唯一约束）</summary>
    public string MessageId { get; init; } = "";

    /// <summary>消费者名称，同一消息可被不同消费者各自处理一次。</summary>
    public string ConsumerName { get; init; } = "default";

    /// <summary>当前状态</summary>
    public InboxStatus Status { get; set; } = InboxStatus.Pending;

    /// <summary>接收时间 — 由 store 在插入时显式赋值（InboxDbContext.TryStartProcessingAsync）</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// 终态完成时间（Processed 或 Failed）。
    /// <para>
    /// 2026-11-07 审计 P1-B 前 Failed 行不写本字段（恒 null），导致 Retention 的
    /// <c>includeFailed</c>（<c>status IN (2,3) AND processed_at IS NOT NULL</c>）永远
    /// 清不掉 Failed 行；现四栈 <c>MarkFailedAsync</c> 统一写入（取处理开始时间，
    /// 回退接收时间）。
    /// </para>
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>处理开始时间 — 用于检测僵尸 Processing</summary>
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>处理尝试次数</summary>
    public int Attempts { get; set; }

    /// <summary>最近错误</summary>
    public string? LastError { get; set; }
}
