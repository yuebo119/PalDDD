namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ IRetryBackoffPolicy 单元测试 — 退避策略契约验证
// ═══════════════════════════════════════════════════════════════
// 覆盖维度：
// 1. ExponentialBackoffPolicy 指数增长 + 上限封顶
// 2. 抖动范围 [0.8, 1.2) × baseDelay
// 3. 边界：attempt < 1 抛异常
// 4. FixedBackoffPolicy 固定延迟
// 5. 线程安全（并发调用不崩溃）
// 6. OutboxOptions 默认策略 = ExponentialBackoffPolicy
// ═══════════════════════════════════════════════════════════════

public sealed class RetryBackoffPolicyTests
{
    // ── ExponentialBackoffPolicy ──────────────────────────────

    [Test]
    [Arguments(1, 2.0)]      // 2^1 = 2s
    [Arguments(2, 4.0)]      // 2^2 = 4s
    [Arguments(3, 8.0)]      // 2^3 = 8s
    [Arguments(4, 16.0)]     // 2^4 = 16s
    [Arguments(5, 32.0)]     // 2^5 = 32s
    [Arguments(6, 64.0)]     // 2^6 = 64s（封顶）
    public async Task Exponential_NoJitter_ReturnsExactPowersOfTwo(int attempt, double expectedSeconds)
    {
        var policy = new ExponentialBackoffPolicy();
        var delay = policy.ComputeDelay(attempt);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Test]
    [Arguments(7)]   // 超过 exponentCap=6，应封顶在 2^6=64s
    [Arguments(10)]
    [Arguments(100)]
    public async Task Exponential_BeyondExponentCap_CappedAtMax(int attempt)
    {
        var policy = new ExponentialBackoffPolicy(exponentCap: 6);
        var delay = policy.ComputeDelay(attempt);
        // 默认 maxDelay=64s，2^6=64s，两者一致
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(64));
    }

    [Test]
    public async Task Exponential_CustomMaxDelay_Respected()
    {
        var policy = new ExponentialBackoffPolicy(maxDelay: TimeSpan.FromSeconds(10));
        // 2^4 = 16s > 10s 上限 → 应返回 10s
        var delay = policy.ComputeDelay(4);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Exponential_CustomExponentCap_Respected()
    {
        var policy = new ExponentialBackoffPolicy(exponentCap: 3);
        // 2^3 = 8s（封顶）
        await Assert.That(policy.ComputeDelay(3)).IsEqualTo(TimeSpan.FromSeconds(8));
        // attempt=5 封顶在 3 → 2^3 = 8s
        await Assert.That(policy.ComputeDelay(5)).IsEqualTo(TimeSpan.FromSeconds(8));
    }

    [Test]
    public async Task Exponential_WithJitter_StaysWithinPlusMinus20Percent()
    {
        var policy = new ExponentialBackoffPolicy(
            maxDelay: TimeSpan.FromSeconds(64),
            withJitter: true);

        // 采样 100 次，验证全部落在 [0.8×base, 1.2×base) 区间
        // base = 2^3 = 8s → 范围 [6.4s, 9.6s)
        var baseDelay = TimeSpan.FromSeconds(8);
        var lowerBound = TimeSpan.FromTicks((long)(baseDelay.Ticks * 0.8));
        var upperBound = TimeSpan.FromTicks((long)(baseDelay.Ticks * 1.2));

        for (var i = 0; i < 100; i++)
        {
            var delay = policy.ComputeDelay(3);
            await Assert.That(delay).IsGreaterThanOrEqualTo(lowerBound).And.IsLessThanOrEqualTo(upperBound);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-100)]
    public async Task Exponential_InvalidAttempt_Throws(int invalidAttempt)
    {
        var policy = new ExponentialBackoffPolicy();
        await Assert.That(() => policy.ComputeDelay(invalidAttempt)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Exponential_InvalidExponentCap_Throws(int invalidCap)
    {
        await Assert.That(() => new ExponentialBackoffPolicy(exponentCap: invalidCap)).Throws<ArgumentOutOfRangeException>();
    }

    // ── FixedBackoffPolicy ────────────────────────────────────

    [Test]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(100)]
    public async Task Fixed_AlwaysReturnsSameDelay(int attempt)
    {
        var expected = TimeSpan.FromSeconds(30);
        var policy = new FixedBackoffPolicy(expected);
        await Assert.That(policy.ComputeDelay(attempt)).IsEqualTo(expected);
    }

    [Test]
    public async Task Fixed_ZeroDelay_Allowed()
    {
        var policy = new FixedBackoffPolicy(TimeSpan.Zero);
        await Assert.That(policy.ComputeDelay(1)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Fixed_NegativeDelay_Throws()
    {
        await Assert.That(() => new FixedBackoffPolicy(TimeSpan.FromSeconds(-1))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Fixed_InvalidAttempt_Throws(int invalidAttempt)
    {
        var policy = new FixedBackoffPolicy(TimeSpan.FromSeconds(5));
        await Assert.That(() => policy.ComputeDelay(invalidAttempt)).Throws<ArgumentOutOfRangeException>();
    }

    // ── OutboxOptions 默认配置 ─────────────────────────────────

    [Test]
    public async Task OutboxOptions_DefaultRetryBackoffPolicy_IsExponentialWithoutJitter()
    {
        var options = new OutboxOptions();
        await Assert.That(options.RetryBackoffPolicy).IsTypeOf<ExponentialBackoffPolicy>();

        // 默认无抖动 — 首次失败 attempt=1 → 2^1 = 2s
        var delay = options.RetryBackoffPolicy.ComputeDelay(1);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task OutboxOptions_CustomPolicy_Injectable()
    {
        var options = new OutboxOptions
        {
            RetryBackoffPolicy = new FixedBackoffPolicy(TimeSpan.FromSeconds(15))
        };
        await Assert.That(options.RetryBackoffPolicy.ComputeDelay(1)).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(options.RetryBackoffPolicy.ComputeDelay(99)).IsEqualTo(TimeSpan.FromSeconds(15));
    }

    // ── 线程安全（并发调用不崩溃）──────────────────────────────

    [Test]
    public async Task Exponential_WithJitter_ConcurrentCalls_AreThreadSafe()
    {
        var policy = new ExponentialBackoffPolicy(withJitter: true);
        var results = new System.Collections.Concurrent.ConcurrentBag<TimeSpan>();

        // 100 个并发任务各调用 100 次
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                results.Add(policy.ComputeDelay(3));
        }));

        await Task.WhenAll(tasks);

        // 全部应在 [6.4s, 9.6s) 范围内
        foreach (var delay in results)
            await Assert.That(delay).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(6.4)).And.IsLessThanOrEqualTo(TimeSpan.FromSeconds(9.6));
        await Assert.That(results.Count).IsEqualTo(10_000);
    }
}
