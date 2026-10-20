using System.Diagnostics.CodeAnalysis;
using PalORM;

namespace PalDDD.PalORM.Models;

/// <summary>二进制 EventId 读投影：不映射文本 event_id，避免物化时解析 26 字符 ULID。</summary>
[Table("events")]
internal sealed partial class EventLogBinaryRow
{
    [Key(AutoIncrement = true)]
    [Column("global_position")]
    public long GlobalPosition { get; set; }

    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；event_id_bin 是不可变二进制列。")]
    [Column("event_id_bin")]
    public byte[]? EventIdBin { get; set; }

    [Column("event_name")]
    public string EventName { get; set; } = "";

    [Column("stream_name")]
    public string StreamName { get; set; } = "";

    [Column("stream_version")]
    public long StreamVersion { get; set; }

    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [Column("content_type")]
    public string ContentType { get; set; } = "application/json";

    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；payload 是不可变二进制负载。")]
    [Column("payload")]
    public byte[] Payload { get; set; } = [];

    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；metadata 是不可变二进制负载。")]
    [Column("metadata")]
    public byte[] Metadata { get; set; } = [];

    [Column("recorded_at")]
    public DateTimeOffset RecordedAt { get; set; }

    [Column("actor_id")]
    public string? ActorId { get; set; }

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [Column("causation_id")]
    public string? CausationId { get; set; }

    [Column("trace_parent")]
    public string? TraceParent { get; set; }

    [Column("trace_state")]
    public string? TraceState { get; set; }
}
