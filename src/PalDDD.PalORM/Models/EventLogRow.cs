using System.Diagnostics.CodeAnalysis;
using ByteAether.Ulid;
using PalDDD.PalORM.Converters;
using PalORM;

namespace PalDDD.PalORM.Models;

/// <summary>
/// EventLog 事件持久化 Row DTO（PalORM 源生成注册实体）。
/// <para>
/// <b>关键决策（v4 实施修正）</b>：原 Dapper/EFCore 用 PascalCase 列名，但 PalORM 手写 SQL
/// 的 FormattableString 字面量不加引号——PG 折叠无引号标识符为小写。为保持三方言一致，
/// 列名统一改为 snake_case（与其他 Row DTO 统一）。
/// </para>
/// </summary>
[Table("events")]
public sealed partial class EventLogRow
{
    /// <summary>全局位置（DB 自增）。</summary>
    [Key(AutoIncrement = true)]
    [Column("global_position")]
    public long GlobalPosition { get; set; }

    /// <summary>事件 ID（Ulid → 26 字符 Base32 字符串存储）。</summary>
    [Column("event_id")]
    [Converter(typeof(UlidStringConverter))]
    public Ulid EventId { get; set; }

    /// <summary>事件名（wire 协议标识）。</summary>
    [Column("event_name")]
    public string EventName { get; set; } = "";

    /// <summary>流名（聚合根标识）。</summary>
    [Column("stream_name")]
    public string StreamName { get; set; } = "";

    /// <summary>流内版本（乐观锁令牌；每个流内单调递增）。</summary>
    [ConcurrencyCheck]
    [Column("stream_version")]
    public long StreamVersion { get; set; }

    /// <summary>Schema 版本。</summary>
    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Content-Type。</summary>
    [Column("content_type")]
    public string ContentType { get; set; } = "application/json";

    /// <summary>事件 payload（二进制 → Base64 string 存储）。</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；EventLog payload 是不可变二进制负载。")]
    [Column("payload")]
    [Converter(typeof(ByteArrayBase64Converter))]
    public byte[] Payload { get; set; } = [];

    /// <summary>事件元数据（二进制 → Base64 string 存储）。</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；EventLog metadata 是不可变二进制负载。")]
    [Column("metadata")]
    [Converter(typeof(ByteArrayBase64Converter))]
    public byte[] Metadata { get; set; } = [];

    /// <summary>记录时间（应用层赋值）。</summary>
    [Column("recorded_at")]
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>操作者 ID（审计；nullable）。</summary>
    [Column("actor_id")]
    public string? ActorId { get; set; }

    /// <summary>操作原因（审计；nullable）。</summary>
    [Column("reason")]
    public string? Reason { get; set; }
}
