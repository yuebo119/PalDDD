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
        var truncated = message is { Length: > MaxLength }
            ? message[..MaxLength]
            : message ?? string.Empty;
        return string.IsNullOrWhiteSpace(truncated) ? "(no message)" : truncated;
    }
}
