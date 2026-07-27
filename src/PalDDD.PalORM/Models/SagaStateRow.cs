namespace PalDDD.PalORM.Models;

/// <summary>
/// Saga 状态持久化 Row DTO（<b>非 PalORM 注册实体</b> —— 仅用于手写 SQL 查询反序列化）。
/// <para>
/// <b>为什么不用 <c>[Table]</c> 注册？</b> 开放泛型 <c>TState</c> 在编译期未知，源生成器无法静态绑定 ——
/// Saga Store 全程手写 <c>ExecuteAsync(FormattableString)</c>，通过 <see cref="PalORM.DataSession{TProvider}"/>.<c>QueryFirstAsync&lt;T&gt;</c> 反序列化。
/// </para>
/// <para>
/// <b>关键事实</b>：PalORM 的 <c>QueryFirstAsync&lt;T&gt;</c> 仅要求 T 是 public class + 无参构造 + 属性可写，
/// 不强制要求 T 注册为 <c>[Table]</c> 实体（<c>[Table]</c> 注册仅对 QueryBuilder/InsertAsync/UpdateAsync 等 CRUD API 必需）。
/// </para>
/// <para>
/// <b>saga_data 列</b>：JSON 字符串（手写 <c>JsonSerializer.Serialize(state, jsonTypeInfo)</c> 序列化）。
/// <see cref="SagaData"/> 为 nullable string —— null 表示不持久化完整状态快照（与 Dapper 实现一致）。
/// </para>
/// <para>列名 snake_case（与 Dapper 表结构兼容）；与 <c>DapperSagaStateStore.SagaStateRow</c> 字段对齐。</para>
/// </summary>
public sealed class SagaStateRow
{
    /// <summary>Saga ID（snake_case 列 saga_id；Ulid 字符串）。</summary>
    public string SagaId { get; set; } = "";

    /// <summary>当前状态名（不含 '|' 字符；领域校验在 SagaState.CurrentState setter）。</summary>
    public string CurrentState { get; set; } = "Initial";

    /// <summary>SagaStatus 枚举值（int 存储；Active=0/Completed=1/Compensated=2/...）。</summary>
    public int Status { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>完成时间（nullable）。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>错误信息（nullable）。</summary>
    public string? Error { get; set; }

    /// <summary>错误时间（nullable）。</summary>
    public DateTimeOffset? ErrorAt { get; set; }

    /// <summary>乐观锁版本号。</summary>
    public int Version { get; set; }

    /// <summary>完整状态快照（JSON 字符串；nullable 表示不持久化）。</summary>
    public string? SagaData { get; set; }

    /// <summary>租约持有者（nullable）。</summary>
    public string? LeasedBy { get; set; }

    /// <summary>租约过期时间（nullable）。</summary>
    public DateTimeOffset? LeasedUntil { get; set; }
}
