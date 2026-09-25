using FsCheck;
using FsCheck.Fluent;
using TUnit.FsCheck;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🎲 退避策略属性测试 — 验证单调性、抖动区间、封顶
// ═══════════════════════════════════════════════════════════════
// TUnit.FsCheck 集成（[Test, FsCheckProperty]）：失败时 seed 直接进 TUnit 报告，
// Replay/MaxTest 声明式可配（原手写 Prop.ForAll + QuickCheckThrowOnFailure 的
// seed 只埋在异常文本里，无法一键复现）。生成域经自定义 Arbitrary 收敛到与
// 原手写 Gen.Choose 完全相同的有界区间（attempt 1-100 / seconds 0-3600），
// 迭代数沿用 FsCheck 默认 100 —— 语义与覆盖面不变，仅运行器与报告通道变化。
// ═══════════════════════════════════════════════════════════════

/// <summary>attempt 生成域 [1, 100]（域与原手写 <c>Arb.From(Gen.Choose(1, 100))</c> 一致）。</summary>
public static class AttemptArbitrary
{
    public static Arbitrary<int> Attempt() => Arb.From(Gen.Choose(1, 100));
}

/// <summary>秒数生成域 [0, 3600]（域与原手写 <c>Arb.From(Gen.Choose(0, 3600))</c> 一致）。</summary>
public static class SecondsArbitrary
{
    public static Arbitrary<int> Seconds() => Arb.From(Gen.Choose(0, 3600));
}

public sealed class BackoffPolicyPropertyTests
{
    [Test]
    [FsCheckProperty(Arbitrary = [typeof(AttemptArbitrary)])]
    public bool Exponential_NeverDecreases_WithIncreasingAttempt(int attempt)
    {
        var policy = new ExponentialBackoffPolicy();
        var d1 = policy.ComputeDelay(attempt);
        var d2 = policy.ComputeDelay(attempt + 1);
        return d2 >= d1;
    }

    [Test]
    [FsCheckProperty(Arbitrary = [typeof(AttemptArbitrary)])]
    public bool Exponential_WithJitter_AlwaysWithinPlusMinus20Percent(int attempt)
    {
        var policy = new ExponentialBackoffPolicy(withJitter: true);
        var delay = policy.ComputeDelay(attempt);
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 64);
        return delay.TotalSeconds >= baseSeconds * 0.8
            && delay.TotalSeconds <= baseSeconds * 1.2;
    }

    [Test]
    [FsCheckProperty(Arbitrary = [typeof(AttemptArbitrary), typeof(SecondsArbitrary)])]
    public bool Fixed_AlwaysReturnsSameDelay(int attempt, int seconds)
    {
        var expected = TimeSpan.FromSeconds(seconds);
        var policy = new FixedBackoffPolicy(expected);
        return policy.ComputeDelay(attempt) == expected;
    }
}
