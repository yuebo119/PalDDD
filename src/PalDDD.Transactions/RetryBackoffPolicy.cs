// ─────────────────────────────────────────────────────────────
// 📈 IRetryBackoffPolicy — 退避策略抽象（可配置化、AOT 安全）
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个抽象？
//   ｜ OutboxProcessor 原本内联 Math.Pow(2, Math.Min(retryCount+1, 6)) 硬编码，
//   ｜ 上限 64 秒无法按业务调参，也无法注入抖动（jitter）避免惊群。
//   ｜
//   ｜ 提取为接口后：
//   ｜   - 运维可通过 OutboxOptions.RetryBackoffPolicy 注入自定义策略
//   ｜   - 默认 ExponentialBackoffPolicy 保持原语义（指数 2^n，上限 64s）
//   ｜   - 生产推荐 ExponentialBackoffPolicy(withJitter: true) 避免 thundering herd
//   ｜
//   ｜ DDD 位置：应用层 — 事务处理的横切关注点，不涉及领域逻辑。
//   ｜ AOT 安全：纯计算，零反射，零动态代码生成。
// ─────────────────────────────────────────────────────────────

using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

/// <summary>重试退避策略 — 计算第 N 次失败后的下次重试延迟。</summary>
/// <remarks>
/// 实现必须：<br/>
/// 1. 纯计算，无 I/O，无副作用 — AOT 安全<br/>
/// 2. 线程安全 — 可能被多个 OutboxBatchProcessor 实例并发调用<br/>
/// 3. <c>ComputeDelay</c> 的 attempt 参数从 1 开始（首次失败为 1，第二次为 2，依此类推）
/// </remarks>
public interface IRetryBackoffPolicy
{
    /// <summary>计算第 <paramref name="attempt"/> 次失败后的下次重试延迟。</summary>
    /// <param name="attempt">失败序号，从 1 开始（首次失败 = 1）</param>
    /// <returns>下次重试前的等待时长</returns>
    TimeSpan ComputeDelay(int attempt);
}

/// <summary>指数退避策略 — 2^attempt 秒，受 <c>MaxDelay</c> 上限约束，可选抖动。</summary>
/// <remarks>
/// 默认语义与原硬编码一致：<c>Min(MaxDelay, 2^Min(attempt, ExponentCap))</c>。<br/>
/// 构造时传入 <c>withJitter: true</c> 后，在计算值上叠加 ±20% 随机抖动，<br/>
/// 防止多实例同步重试导致的 thundering herd（惊群效应）。<br/>
/// 抖动使用 <see cref="Random.Shared"/>，线程安全且 AOT 友好（无反射）。
/// </remarks>
public sealed class ExponentialBackoffPolicy : IRetryBackoffPolicy
{
    private readonly TimeSpan _maxDelay;
    private readonly bool _withJitter;
    private readonly int _exponentCap;

    /// <summary>创建指数退避策略。</summary>
    /// <param name="maxDelay">延迟上限，默认 64 秒（与原硬编码一致）</param>
    /// <param name="withJitter">是否叠加 ±20% 随机抖动（生产推荐）</param>
    /// <param name="exponentCap">指数上限，默认 6（即 2^6=64 秒封顶）</param>
    public ExponentialBackoffPolicy(
        TimeSpan? maxDelay = null,
        bool withJitter = false,
        int exponentCap = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exponentCap, 1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(64);
        _withJitter = withJitter;
        _exponentCap = exponentCap;
    }

    /// <inheritdoc/>
    // CA5394 误报：Random.Shared 用于退避抖动（非安全场景），
    // 加密随机性在此场景无必要且会显著降低性能。
    [SuppressMessage("Security", "CA5394:Do not use insecure random number generators",
        Justification = "退避抖动不涉及安全上下文，密码学随机数无必要且损害性能，故使用非加密随机。")]
    public TimeSpan ComputeDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        // 指数增长 2^n，受 exponentCap 封顶（避免 attempt 过大时 2^attempt 溢出）
        var cappedExponent = Math.Min(attempt, _exponentCap);
        var delaySeconds = Math.Pow(2, cappedExponent);

        // 应用绝对上限
        var baseDelay = TimeSpan.FromSeconds(delaySeconds);
        if (baseDelay > _maxDelay)
            baseDelay = _maxDelay;

        if (!_withJitter)
            return baseDelay;

        // ±20% 抖动 — Random.Shared 线程安全，AOT 友好
        // 范围 [0.8, 1.2) × baseDelay
        var jitterFactor = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * jitterFactor));
    }
}

/// <summary>固定延迟策略 — 每次重试等待固定时长，适合可预测的重试场景。</summary>
public sealed class FixedBackoffPolicy(TimeSpan delay) : IRetryBackoffPolicy
{
    private readonly TimeSpan _delay = delay >= TimeSpan.Zero
        ? delay
        : throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be non-negative.");

    /// <inheritdoc/>
    public TimeSpan ComputeDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return _delay;
    }
}
