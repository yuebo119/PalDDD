using PalDDD.Idempotency;

namespace PalDDD.PalORM.Models;

/// <summary>
/// IdempotencyRecord 查询投影 DTO（<b>非 PalORM 注册实体</b>）。
/// <para>
/// <b>为什么不用 <c>[Table]</c> 注册？</b> IdempotencyRecord 是 <b>两列复合主键</b>
/// (operation_name, key) —— PALORM019 编译期拒绝复合主键实体注册。
/// Store 全程手写 <c>ExecuteAsync(FormattableString)</c> + <c>QueryFirstAsync&lt;T&gt;</c>。
/// </para>
/// <para>
/// <b>ResponsePayload</b>：Base64 string 存储（<see cref="ToDomain"/> 时解码为 byte[]）。
/// 与领域类型 <c>ReadOnlyMemory&lt;byte&gt;?</c> 转换在 <see cref="ToDomain"/> 中处理。
/// </para>
/// <para>列名 snake_case。</para>
/// </summary>
public sealed class IdempotencyRecordRow
{
    /// <summary>操作名（复合主键第 1 列）。</summary>
    public string OperationName { get; set; } = "";

    /// <summary>幂等键（复合主键第 2 列）。</summary>
    public string Key { get; set; } = "";

    /// <summary>状态（IdempotencyRecordStatus → int；Processing=0/Completed=1/Failed=2）。</summary>
    public int Status { get; set; }

    /// <summary>锁过期时间（租约检测）。</summary>
    public DateTimeOffset LockedUntil { get; set; }

    /// <summary>记录过期时间（GC 清理依据）。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>更新时间（EFCore 并发令牌；PalORM 手写 SQL 维护）。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>响应负载（Base64 string；nullable）。</summary>
    public string? ResponsePayload { get; set; }

    /// <summary>错误信息（Failed 状态时填充；nullable）。</summary>
    public string? Error { get; set; }

    /// <summary>Row DTO → 领域类型 <see cref="IdempotencyRecord"/>。</summary>
    public IdempotencyRecord ToDomain()
    {
        var record = new IdempotencyRecord(
            OperationName, Key, (IdempotencyRecordStatus)Status,
            LockedUntil, ExpiresAt, UpdatedAt);

        // ResponsePayload 经 Base64 解码后通过 MarkCompleted 写回（保留领域类型不可变性）
        if (ResponsePayload is { Length: > 0 } && (IdempotencyRecordStatus)Status == IdempotencyRecordStatus.Completed)
        {
            var bytes = Convert.FromBase64String(ResponsePayload);
            record.MarkCompleted(bytes, UpdatedAt);
        }
        if (Error is { Length: > 0 } && (IdempotencyRecordStatus)Status == IdempotencyRecordStatus.Failed)
        {
            record.MarkFailed(Error, UpdatedAt);
        }

        return record;
    }
}
