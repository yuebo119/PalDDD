using FsCheck;
using FsCheck.Fluent;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🎲 退避策略属性测试 — 验证单调性、抖动区间、封顶
// ═══════════════════════════════════════════════════════════════
// 用 FsCheck 核心 API，在 TUnit [Test] 中驱动。
// ═══════════════════════════════════════════════════════════════

public sealed class BackoffPolicyPropertyTests
{
    [Test]
    public void Exponential_NeverDecreases_WithIncreasingAttempt()
    {
        var positiveInt = Arb.From(Gen.Choose(1, 100));
        Prop.ForAll(positiveInt, attempt =>
        {
            var policy = new ExponentialBackoffPolicy();
            var d1 = policy.ComputeDelay(attempt);
            var d2 = policy.ComputeDelay(attempt + 1);
            return d2 >= d1;
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void Exponential_WithJitter_AlwaysWithinPlusMinus20Percent()
    {
        var positiveInt = Arb.From(Gen.Choose(1, 100));
        Prop.ForAll(positiveInt, attempt =>
        {
            var policy = new ExponentialBackoffPolicy(withJitter: true);
            var delay = policy.ComputeDelay(attempt);
            var baseSeconds = Math.Min(Math.Pow(2, attempt), 64);
            return delay.TotalSeconds >= baseSeconds * 0.8
                && delay.TotalSeconds <= baseSeconds * 1.2;
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void Fixed_AlwaysReturnsSameDelay()
    {
        var attemptArb = Arb.From(Gen.Choose(1, 100));
        var secondsArb = Arb.From(Gen.Choose(0, 3600));
        Prop.ForAll(attemptArb, secondsArb, (attempt, seconds) =>
        {
            var expected = TimeSpan.FromSeconds(seconds);
            var policy = new FixedBackoffPolicy(expected);
            return policy.ComputeDelay(attempt) == expected;
        }).QuickCheckThrowOnFailure();
    }
}
