namespace PalDDD.Idempotency;

public sealed record IdempotencyPolicy
{
    public static IdempotencyPolicy Default { get; } = new();

    /// <summary>处理租约超时 — 必须为正（ITM-118 起 init 校验）。</summary>
    public TimeSpan ProcessingTimeout
    {
        get;
        init
        {
            // ITM-118 修复：负值/零校验——原 init 自动属性无校验，负超时使租约即刻过期
            //（僵尸抢占语义失效）；TimeSpan 不实现 INumberBase，用 LessThanOrEqual 显式比较
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            // 优化（二十四轮 OP-3）：C# 14 field 关键字——删私有后备字段，属性自包含
            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>幂等保留窗口 — 必须为正（ITM-118 起 init 校验）。</summary>
    public TimeSpan Retention
    {
        get;
        init
        {
            // ITM-118 修复：负值/零校验——负保留窗口使记录即刻过期，去重失效
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromHours(24);

    // ITM-118 声明（Retention >= ProcessingTimeout 倒挂校验降级为注释声明）：
    // 跨字段不变式未在属性 setter 强制——init 赋值顺序不定（任一 setter 先执行时另一
    // 字段仍是默认值，无法可靠比较）。
    //
    // ITM-216 修复（三十二轮）：提供 Validate() 供生产入口调用——倒挂策略在旧处理仍持
    // 租约时允许重入（ExpiresAt < LockedUntil），导致 handler 双执行。Store 层直接测试
    // 倒挂语义的用例不走 Processor，不受影响。

    /// <summary>
    /// 校验跨字段不变式：<see cref="Retention"/> 必须大于等于 <see cref="ProcessingTimeout"/>。
    /// 倒挂策略使记录在处理租约仍有效时过期（ExpiresAt &lt; LockedUntil），去重窗口失效。
    /// </summary>
    /// <exception cref="ArgumentException">Retention 小于 ProcessingTimeout。</exception>
    public void Validate()
    {
        if (Retention < ProcessingTimeout)
            throw new ArgumentException(
                $"IdempotencyPolicy Retention ({Retention}) must be >= ProcessingTimeout ({ProcessingTimeout}). " +
                "Retention < ProcessingTimeout allows duplicate execution while the original lease is still active (ITM-216).",
                nameof(Retention));
    }
}
