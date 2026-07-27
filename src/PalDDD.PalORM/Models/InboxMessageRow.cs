using PalORM;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Models;

/// <summary>
/// Inbox 消息持久化 Row DTO（PalORM 源生成注册实体）。
/// <para>
/// <b>设计决策</b>：
/// <list type="bullet">
/// <item>主键：Id 为 long 自增（DB 生成）—— PalORM 自增回填自动写入此属性（Row DTO 用 set，领域类型 InboxMessage.Id 保持 init）。</item>
/// <item>枚举存储：统一 int（与 Outbox/Saga 一致）。</item>
/// <item>乐观锁：<see cref="ProcessingStartedAt"/> 标 <see cref="ConcurrencyCheckAttribute"/>（与 EFCore InboxDbContext.ProcessingStartedAt 并发令牌等价），替代手写 WHERE processing_started_at=@orig。</item>
/// <item>唯一约束：(consumer_name, message_id) —— 靠建表 DDL 的 UNIQUE INDEX 保证，与 PalORM 实体注册无关。</item>
/// </list>
/// </para>
/// <para>表名/列名 snake_case，与 Dapper 表结构兼容。</para>
/// </summary>
[Table("inbox_messages")]
public sealed partial class InboxMessageRow
{
    /// <summary>自增主键（DB 生成，PalORM 自动回填）。</summary>
    [Key]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>消息唯一标识（参与 UNIQUE(consumer_name, message_id) 约束）。</summary>
    [Column("message_id")]
    public string MessageId { get; set; } = "";

    /// <summary>消费者名称（参与 UNIQUE 约束）。</summary>
    [Column("consumer_name")]
    public string ConsumerName { get; set; } = "default";

    /// <summary>状态（InboxStatus → int；Pending=0/Processing=1/Processed=2/Failed=3）。</summary>
    [Column("status")]
    public int Status { get; set; }

    /// <summary>接收时间（store 插入时赋值）。</summary>
    [Column("received_at")]
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>处理完成时间（nullable）。</summary>
    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>处理开始时间（僵尸 Processing 检测；非乐观锁 —— PalORM [ConcurrencyCheck] 仅支持 int/long 自增）。</summary>
    [Column("processing_started_at")]
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>处理尝试次数（乐观锁令牌 —— 每次 UPDATE 自增；替代 EFCore 的 ProcessingStartedAt 时间戳并发）。</summary>
    [ConcurrencyCheck]
    [Column("attempts")]
    public int Attempts { get; set; }

    /// <summary>最近错误（Failed 状态时填充）。</summary>
    [Column("last_error")]
    public string? LastError { get; set; }

    // ─── 领域类型映射 ────────────────────────────────────────────

    /// <summary>Row DTO → 领域类型 <see cref="InboxMessage"/>。</summary>
    /// <remarks>Id 通过 object-initializer 赋值（领域类型用 init），其他属性用 set。</remarks>
    public InboxMessage ToDomain() => new()
    {
        Id = Id,
        MessageId = MessageId,
        ConsumerName = ConsumerName,
        Status = (InboxStatus)Status,
        ReceivedAt = ReceivedAt,
        ProcessedAt = ProcessedAt,
        ProcessingStartedAt = ProcessingStartedAt,
        Attempts = Attempts,
        LastError = LastError,
    };

    /// <summary>领域类型 <see cref="InboxMessage"/> → Row DTO（用于 UPDATE 路径）。</summary>
    public static InboxMessageRow FromDomain(InboxMessage m) => new()
    {
        Id = m.Id,
        MessageId = m.MessageId,
        ConsumerName = m.ConsumerName,
        Status = (int)m.Status,
        ReceivedAt = m.ReceivedAt,
        ProcessedAt = m.ProcessedAt,
        ProcessingStartedAt = m.ProcessingStartedAt,
        Attempts = m.Attempts,
        LastError = m.LastError,
    };
}
