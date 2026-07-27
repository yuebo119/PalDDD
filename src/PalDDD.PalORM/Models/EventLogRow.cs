using System.Diagnostics.CodeAnalysis;
using ByteAether.Ulid;
using PalDDD.PalORM.Converters;
using PalORM;

namespace PalDDD.PalORM.Models;

/// <summary>
/// EventLog 事件持久化 Row DTO（PalORM 源生成注册实体）。
/// <para>
/// <b>关键例外</b>：EventLog 表的列名是 <b>PascalCase</b>（与 Outbox/Inbox/Saga 的 snake_case 不同）。
/// 这是 Dapper 和 EFCore 双实现的共同契约 —— PalORM 版本必须保留以维持表结构兼容。
/// </para>
/// <para>
/// <b>设计决策</b>：
/// <list type="bullet">
/// <item>主键：GlobalPosition 为 long，但非自增（应用层用 Hi/Lo 预分配，与 EFCore EventLogDbContext 一致）—— <see cref="KeyAttribute"/>(<c>AutoIncrement=false</c>)。</item>
/// <item>Payload/Metadata：byte[] 经 <see cref="ByteArrayBase64Converter"/> 转 Base64 string（PALORM016）。</item>
/// <item>乐观锁：<see cref="StreamVersion"/> 标 <see cref="ConcurrencyCheckAttribute"/>，Event Sourcing 流版本乐观并发控制。</item>
/// <item>EventId：Ulid → string（<see cref="UlidStringConverter"/>）。</item>
/// </list>
/// </para>
/// </summary>
[Table("Events")]
public sealed partial class EventLogRow
{
    /// <summary>全局位置（Hi/Lo 预分配，非自增；应用层 EventLogPositionReserver 分配）。</summary>
    [Key(AutoIncrement = false)]
    [Column("GlobalPosition")]
    public long GlobalPosition { get; set; }

    /// <summary>事件 ID（Ulid → 26 字符 Base32 字符串存储）。</summary>
    [Column("EventId")]
    [Converter(typeof(UlidStringConverter))]
    public Ulid EventId { get; set; }

    /// <summary>事件名（wire 协议标识）。</summary>
    [Column("EventName")]
    public string EventName { get; set; } = "";

    /// <summary>流名（聚合根标识）。</summary>
    [Column("StreamName")]
    public string StreamName { get; set; } = "";

    /// <summary>流内版本（乐观锁令牌；每个流内单调递增）。</summary>
    [ConcurrencyCheck]
    [Column("StreamVersion")]
    public long StreamVersion { get; set; }

    /// <summary>Schema 版本。</summary>
    [Column("SchemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Content-Type。</summary>
    [Column("ContentType")]
    public string ContentType { get; set; } = "application/json";

    /// <summary>事件 payload（二进制 → Base64 string 存储）。</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；EventLog payload 是不可变二进制负载。")]
    [Column("Payload")]
    [Converter(typeof(ByteArrayBase64Converter))]
    public byte[] Payload { get; set; } = [];

    /// <summary>事件元数据（二进制 → Base64 string 存储；nullable）。</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；EventLog metadata 是不可变二进制负载。")]
    [Column("Metadata")]
    [Converter(typeof(ByteArrayBase64Converter))]
    public byte[] Metadata { get; set; } = [];

    /// <summary>记录时间（应用层赋值）。</summary>
    [Column("RecordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>操作者 ID（审计；nullable）。</summary>
    [Column("ActorId")]
    public string? ActorId { get; set; }

    /// <summary>操作原因（审计；nullable）。</summary>
    [Column("Reason")]
    public string? Reason { get; set; }
}
