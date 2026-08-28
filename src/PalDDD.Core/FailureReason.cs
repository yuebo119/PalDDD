namespace PalDDD.Core;

/// <summary>
/// 入库失败原因归一化（二轮评审 T6 提取）——Inbox/Outbox/Idempotency/Projection/Saga
/// 五处处理器的同型逻辑收敛（ITM-175 截断族 + F2 空白归一族的姊妹同步根源）。
/// </summary>
/// <remarks>
/// 两条规则（任一缺失都会让终态保存本身失败、掩盖原始异常）：
/// <list type="bullet">
/// <item><b>截断到 <see cref="MaxLength"/></b>：五表 error/last_error 列上限 2048，超长
/// ex.Message（含大 payload 的序列化错误）会让 MarkFailed/MarkDead/ReleaseForRetry 的
/// 持久化抛截断异常（ITM-167/175/二十一轮 P1）。</item>
/// <item><b>空白归一为 "(no message)"</b>：空/空白 ex.Message（含自定义异常 override
/// Message 返回 null）会让 Store 入口 ThrowIfNullOrWhiteSpace 抛 ArgumentException 被
/// 外层 catch 吞掉——记录残留 Processing，租约过期后同一消息被双重执行（F2，
/// audit-probe 2026-08-23）。</item>
/// </list>
/// </remarks>
public static class FailureReason
{
    /// <summary>入库失败原因截断上限——五表 error 列上限 2048 的安全余量。</summary>
    public const int MaxLength = 2000;

    /// <summary>归一化异常消息用于持久化：截断到 <see cref="MaxLength"/>，空白归一为 "(no message)"。</summary>
    public static string Normalize(string? message)
    {
        // v29 P3：截断 + 代理对守卫收敛到 Truncate（单一来源，七处存储层兜底共用）
        var truncated = Truncate(message, MaxLength) ?? string.Empty;
        return string.IsNullOrWhiteSpace(truncated) ? "(no message)" : truncated;
    }

    /// <summary>
    /// v29 P3：仅截断不归一（存储层兜底族共享收口）——<see cref="Normalize"/> 会把 null/空白
    /// 归一为 "(no message)"，而 Error=null 是"未出错"语义（SagaState.Error 等），归一会破坏
    /// 该语义。Dapper 五处（Outbox MarkDead/ReleaseForRetry、Inbox MarkFailed、Checkpoint
    /// MarkFailed、Saga SaveChanges）+ PalORM/EFCore Saga 的 2040 兜底截断共用本方法
    ///（error 列上限 2048 的安全余量）；代理对守卫同 <see cref="Normalize"/>
    ///（末位高代理回退一位，防孤立高代理入库——UTF-16 代理对完整性）。
    /// <para>
    /// v30 P3 收口扩容：Outbox MarkDead/ReleaseForRetry 的 2040 兜底（PalORM
    /// PalOrmOutboxStore + EFCore OutboxDbContext 各两处）与 RequeueDeadAsync 的
    /// retriedBy 256 截断（Dapper/PalORM/EFCore/InMemory 四栈）全部改经本方法——
    /// 此前九处裸 [..N] 切片无代理对守卫。
    /// </para>
    /// </summary>
    /// <param name="value">原始值；null 原样返回（null 语义保持）。</param>
    /// <param name="maxLength">截断上限（超过才截断）；v30 P3 契约：<c>0</c> 返回空串（非 null 输入
    /// 截到零长），负值抛 <see cref="ArgumentOutOfRangeException"/>。</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    public static string? Truncate(string? value, int maxLength)
    {
        // v30 P3：maxLength 负值守卫——无守卫时负值落到 value[..maxLength] 抛
        // ArgumentOutOfRangeException，但错误信息指向切片表达式而非参数契约（可诊断性差，
        // 调用方难以定位误传负值的源头）。守卫前置于 null 检查：参数校验优先于
        // "null 原样返回"短路（null + 负值组合以契约违规报错，不静默放行）
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        // 关系模式（> maxLength）右侧要求编译期常量，参数版须显式比较
        if (value is null || value.Length <= maxLength) return value;
        var truncated = value[..maxLength];
        // v8 评审：char 截断可能在代理对中间切断（超长含 emoji 的消息）——末位高代理回退一位，
        // 防孤立高代理入库（UTF-16 代理对完整性）
        if (truncated.Length > 0 && char.IsHighSurrogate(truncated[^1]))
            truncated = truncated[..(truncated.Length - 1)];
        return truncated;
    }
}
