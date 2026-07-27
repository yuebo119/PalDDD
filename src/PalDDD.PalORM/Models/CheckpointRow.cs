using PalDDD.Projections;

namespace PalDDD.PalORM.Models;

/// <summary>
/// ProjectionCheckpoint 查询投影 DTO（<b>非 PalORM 注册实体</b>）。
/// <para>
/// <b>为什么不用 <c>[Table]</c> 注册？</b> ProjectionCheckpoint 是 <b>三列复合主键</b>
/// (projection_name, source_name, position) —— PALORM019 编译期拒绝复合主键实体注册。
/// Store 全程手写 <c>ExecuteAsync(FormattableString)</c> + <c>QueryFirstAsync&lt;T&gt;</c>。
/// </para>
/// <para>列名 snake_case（与 Dapper 表结构兼容）。</para>
/// </summary>
public sealed class CheckpointRow
{
    /// <summary>投影名（复合主键第 1 列）。</summary>
    public string ProjectionName { get; set; } = "";

    /// <summary>源名（复合主键第 2 列）。</summary>
    public string SourceName { get; set; } = "";

    /// <summary>位置（复合主键第 3 列）。</summary>
    public string Position { get; set; } = "";

    /// <summary>状态（ProjectionCheckpointStatus → int；Processing=0/Completed=1/Failed=2）。</summary>
    public int Status { get; set; }

    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>租约过期时间。</summary>
    public DateTimeOffset LeaseUntil { get; set; }

    /// <summary>乐观锁 Revision（单调递增）。</summary>
    public long Revision { get; set; }

    /// <summary>错误信息（Failed 状态时填充；nullable）。</summary>
    public string? Error { get; set; }

    /// <summary>Row DTO → 领域类型 <see cref="ProjectionCheckpoint"/>（用 Rehydrate 工厂保留 Revision/LeaseUntil）。</summary>
    public ProjectionCheckpoint ToDomain() =>
        ProjectionCheckpoint.Rehydrate(
            ProjectionName, SourceName, Position,
            (ProjectionCheckpointStatus)Status, UpdatedAt,
            LeaseUntil, Revision, Error);
}
