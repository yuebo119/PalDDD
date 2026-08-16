using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ByteAether.Ulid;
using PalDDD.PalORM.Converters;
using PalORM;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Models;

/// <summary>
/// Outbox 消息持久化 Row DTO（PalORM 源生成注册实体）。
/// <para>
/// <b>设计决策（v4 方案）</b>：
/// <list type="bullet">
/// <item>命名约定：snake_case（与 PalDDD.Dapper 兼容，通过显式 <see cref="ColumnAttribute"/> 编译期固化）。</item>
/// <item>枚举存储：统一 int（<see cref="OutboxStatus"/> → int），替代 Dapper 的 string 字面量。破坏性变更，配套 migration.md。</item>
/// <item>主键：Id 为 Ulid（构造时 <see cref="Ulid.New()"/> 赋值），<see cref="KeyAttribute"/>(<c>AutoIncrement=false</c>) 显式声明应用层赋值（PALORM022）。</item>
/// <item>乐观锁：<see cref="RetryCount"/> 标 <see cref="ConcurrencyCheckAttribute"/>，替代 Dapper 手写 WHERE retry_count=@v（声明式，由 PalORM 自动生成并发谓词）。</item>
/// <item>Payload：byte[] 经 <see cref="ByteArrayBase64Converter"/> 转 Base64 string 存储（PALORM016 拒绝 byte[]，必须转换）。</item>
/// </list>
/// </para>
/// <para>
/// <b>多租户未启用</b>：当前表结构契约（DapperStoreTests.cs:92-178 DDL）无 tenant_id 列。
/// 如需启用 <see cref="TenantAwareAttribute"/>，需同步在建表 DDL 添加 tenant_id 列。
/// </para>
/// <para>
/// 与领域类型 <see cref="OutboxMessage"/> 解耦 —— 通过 <see cref="ToDomain"/>/<see cref="FromDomain"/> 工厂方法显式映射，
/// 避免反射绕过业务校验。Row DTO 用 <c>set</c>（非 init），满足 PalORM 物化路径对可写 setter 的要求。
/// </para>
/// </summary>
[Table("outbox_messages")]
public sealed partial class OutboxMessageRow
{
    /// <summary>消息 ID（Ulid → 26 字符 Base32 字符串存储；应用层赋值，非自增）。</summary>
    [Key(AutoIncrement = false)]
    [Column("id")]
    [Converter(typeof(UlidStringConverter))]
    public Ulid Id { get; set; }

    /// <summary>消息类型标识（领域事件全名）。</summary>
    [Column("type")]
    public string Type { get; set; } = "";

    /// <summary>消息负载（二进制 → Base64 string 存储）。</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "PalORM Row DTO 持久化边界；Payload 是 Outbox 的不可变二进制负载，与领域类型 OutboxMessage.Payload 保持一致。")]
    [Column("payload")]
    [Converter(typeof(ByteArrayBase64Converter))]
    public byte[] Payload { get; set; } = [];

    /// <summary>Content-Type（默认 application/json）。</summary>
    [Column("content_type")]
    public string ContentType { get; set; } = "application/json";

    /// <summary>Schema 版本（向前兼容标识）。</summary>
    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>状态（OutboxStatus 枚举转 int 存储；Pending=0/Processed=1/Dead=2）。</summary>
    [Column("status")]
    public int Status { get; set; }

    /// <summary>重试次数（乐观锁令牌 —— <see cref="ConcurrencyCheckAttribute"/> 自动加 WHERE retry_count=@orig）。</summary>
    [ConcurrencyCheck]
    [Column("retry_count")]
    public int RetryCount { get; set; }

    /// <summary>创建时间（应用层赋值，不依赖数据库 DEFAULT；拦截器不覆盖 INSERT 路径）。</summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>处理完成时间（nullable）。</summary>
    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>下次尝试时间（nullable，用于退避重试调度）。</summary>
    [Column("next_attempt_at")]
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>锁持有者标识（worker ID，用于 LeasePending 原子租约）。</summary>
    [Column("locked_by")]
    public string? LockedBy { get; set; }

    /// <summary>锁过期时间（nullable，租约到期后可被其他 worker 抢占）。</summary>
    [Column("locked_until")]
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>错误原因（Dead 状态时填充）。</summary>
    [Column("error")]
    public string? Error { get; set; }

    // 审计追踪列（三方均持久化——Dapper / EFCore / PalORM 适配层均读写以下四列；
    // P3 勘正（二十一轮）：旧注释“当前 Dapper 实现未持久化”已过时）
    // ── CorrelationId/CausationId/TraceParent/TraceState 与领域类型 OutboxMessage 一一映射 ──

    /// <summary>关联 ID（nullable Ulid → string）。</summary>
    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    /// <summary>因果 ID（nullable Ulid → string）。</summary>
    [Column("causation_id")]
    public string? CausationId { get; set; }

    /// <summary>W3C TraceParent（nullable）。</summary>
    [Column("trace_parent")]
    public string? TraceParent { get; set; }

    /// <summary>W3C TraceState（nullable）。</summary>
    [Column("trace_state")]
    public string? TraceState { get; set; }

    // ─── 领域类型映射 ────────────────────────────────────────────

    /// <summary>Row DTO → 领域类型 <see cref="OutboxMessage"/>。</summary>
    public OutboxMessage ToDomain() => new()
    {
        Id = Id,
        Type = Type,
        Payload = Payload,
        ContentType = ContentType,
        SchemaVersion = SchemaVersion,
        Status = (OutboxStatus)Status,
        RetryCount = RetryCount,
        CreatedAt = CreatedAt,
        ProcessedAt = ProcessedAt,
        NextAttemptAt = NextAttemptAt,
        LockedBy = LockedBy,
        LockedUntil = LockedUntil,
        Error = Error,
        CorrelationId = TryParseUlid(CorrelationId),
        CausationId = TryParseUlid(CausationId),
        TraceParent = TraceParent,
        TraceState = TraceState,
    };

    /// <summary>领域类型 <see cref="OutboxMessage"/> → Row DTO。</summary>
    public static OutboxMessageRow FromDomain(OutboxMessage m) => new()
    {
        Id = m.Id,
        Type = m.Type,
        Payload = m.Payload,
        ContentType = m.ContentType,
        SchemaVersion = m.SchemaVersion,
        Status = (int)m.Status,
        RetryCount = m.RetryCount,
        CreatedAt = m.CreatedAt,
        ProcessedAt = m.ProcessedAt,
        NextAttemptAt = m.NextAttemptAt,
        LockedBy = m.LockedBy,
        LockedUntil = m.LockedUntil,
        Error = m.Error,
        CorrelationId = m.CorrelationId?.ToString(),
        CausationId = m.CausationId?.ToString(),
        TraceParent = m.TraceParent,
        TraceState = m.TraceState,
    };

    /// <summary>安全解析 Ulid 字符串 —— 脏数据返回 null 而非抛异常（P0-6 修复）。</summary>
    private static Ulid? TryParseUlid(string? value)
        => value is not null && Ulid.TryParse(value, CultureInfo.InvariantCulture, out var ulid) ? ulid : (Ulid?)null;
}
