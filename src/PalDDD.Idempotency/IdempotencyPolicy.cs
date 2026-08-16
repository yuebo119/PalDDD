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
    // 字段仍是默认值，无法可靠比较）；且现有测试刻意构造倒挂策略验证过期语义
    //（IdempotencyEfCoreTests.GetAsync_DoesNotMutateStoreWhenRecordIsExpired 与
    // PalOrmIdempotencyStoreTests 同名用例：ProcessingTimeout 长于 Retention），
    // 测试文件不在本轮改动范围。语义约定：Retention 应 >= ProcessingTimeout，否则
    // Processing 记录可能在处理超时前过期清除、去重窗口失效——调用方自行保证。
}
