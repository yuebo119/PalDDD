// ─────────────────────────────────────────────────────────────
// 🔁 RetryExecutor — 通用重试执行器（复用 IRetryBackoffPolicy）
// ─────────────────────────────────────────────────────────────
//
// 💡 定位：把既有退避策略套到任意异步委托上（HTTP 调用、LLM 调用、外部服务）。
//   ｜ 缺口的由来：IRetryBackoffPolicy 已 public，但只有 Outbox/Saga 内部消费，
//   ｜ 应用层要用同一套退避语义包外部调用时无执行器可用。
//   ｜
//   ｜ 与 Outbox/Saga 的持久化重试是两套语义：
//   ｜   - 本执行器在进程内重试，进程退出即丢，不跨重启恢复；
//   ｜   - Outbox/Saga 把 nextAttemptAt 落到存储，由处理器跨重启继续。
//   ｜   需要跨重启保证时用后者，不要用本执行器冒充。
//   ｜
//   ｜ 依赖纪律：零新依赖，无反射、无动态代码生成（AOT 安全）。
//   ｜ 取消语义：调用方 CancellationToken 取消后立即传播，不计入重试；
//   ｜ 委托自身超时抛出的 OCE（ct 未取消）按普通失败处理，可重试。
// ─────────────────────────────────────────────────────────────

using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

/// <summary>重试执行器选项。</summary>
/// <remarks>
/// 总尝试次数 = <see cref="MaxRetries"/> + 1（首次尝试加 N 次重试）。
/// <see cref="MaxRetries"/> 为 0 表示不重试。
/// </remarks>
public sealed class RetryOptions
{
    /// <summary>最大重试次数。默认 3（总尝试 4 次）。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>失败后退避策略。默认指数退避 + ±20% 抖动。</summary>
    public IRetryBackoffPolicy BackoffPolicy { get; set; } = new ExponentialBackoffPolicy(withJitter: true);

    /// <summary>
    /// 判定异常是否可重试；null 表示除「调用方取消」外全部重试。
    /// </summary>
    /// <remarks>
    /// 典型用法：只重试瞬时错误（超时、连接失败、429/5xx），业务异常返回 false。
    /// </remarks>
    public Func<Exception, bool>? ShouldRetry { get; set; }

    /// <summary>每次重试前的观测钩子；参数为（异常，已失败次数，从 1 开始）。</summary>
    public Action<Exception, int>? OnRetry { get; set; }

    internal void Validate()
    {
        // MA0015：Validate() 无参，两条 guard 校验的是选项属性值，paramName 无可映射形参
        //（同族先例 IdempotencyPolicy.Validate/ RetentionOptions.Validate 的定点抑制）。
#pragma warning disable MA0015
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries);
        ArgumentNullException.ThrowIfNull(BackoffPolicy);
#pragma warning restore MA0015
    }
}

/// <summary>通用重试执行器：按 <see cref="IRetryBackoffPolicy"/> 重试异步委托。</summary>
/// <remarks>
/// 重试耗尽后原样重抛最后一次异常（保留调用栈），不包装成新异常类型，
/// 调用方的 catch 形态无需调整。重试过程中 <see cref="RetryOptions.OnRetry"/>
/// 先于退避等待触发；退避策略自身抛异常时降级为 1 秒，与 Saga 步骤重试同口径。
/// </remarks>
public static class RetryExecutor
{
    /// <summary>执行无返回值的异步委托。</summary>
    /// <param name="operation">待执行操作，接收调用方取消令牌。</param>
    /// <param name="options">重试选项。</param>
    /// <param name="timeProvider">时间源；null 用 <see cref="TimeProvider.System"/>，测试可注入。</param>
    /// <param name="cancellationToken">取消令牌；取消后立即停止重试。</param>
    public static async ValueTask ExecuteAsync(
        Func<CancellationToken, ValueTask> operation,
        RetryOptions options,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var clock = timeProvider ?? TimeProvider.System;

        // S1994：原 for(;;) 的停止增量在条件里未被测试，分析器误报为可疑循环。
        // 改 while(true) 并把增量移到迭代末尾——终点保持不可达（每条路径 return/throw/异常逃逸），
        // 泛型重载不会触发 CS0161（v1 条件化 for 的致命错误已在方案轮实测排除）。
        var attempt = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsRetryCandidate(ex, cancellationToken))
            {
                var failures = attempt;
                // FIX-020 次级（2026-11-08）：用户回调异常隔离——避免回调异常中止重试链
                // 并掩盖原始 operation 异常。ShouldRetry 异常按「不重试」降级；OnRetry 异常吞掉。
                if (failures > options.MaxRetries || !TryEvaluateShouldRetry(options, ex))
                    throw;
                TryInvokeOnRetry(options, ex, failures);
                await Task.Delay(ComputeDelaySafely(options.BackoffPolicy, failures), clock, cancellationToken)
                    .ConfigureAwait(false);
            }
            attempt++;
        }
    }

    /// <summary>执行有返回值的异步委托。</summary>
    /// <inheritdoc cref="ExecuteAsync(Func{CancellationToken, ValueTask}, RetryOptions, TimeProvider?, CancellationToken)"/>
    public static async ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        RetryOptions options,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var clock = timeProvider ?? TimeProvider.System;

        var attempt = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryCandidate(ex, cancellationToken))
            {
                var failures = attempt;
                // FIX-020 次级（2026-11-08）：用户回调异常隔离（同无返回值版本）。
                if (failures > options.MaxRetries || !TryEvaluateShouldRetry(options, ex))
                    throw;
                TryInvokeOnRetry(options, ex, failures);
                await Task.Delay(ComputeDelaySafely(options.BackoffPolicy, failures), clock, cancellationToken)
                    .ConfigureAwait(false);
            }
            attempt++;
        }
    }

    /// <summary>
    /// 回调异常隔离（FIX-020 次级，2026-11-08）：用户回调异常不得中止重试链或掩盖原始异常。
    /// ShouldRetry 异常按「不重试」降级；OnRetry 异常吞掉。
    /// </summary>
    private static bool TryEvaluateShouldRetry(RetryOptions options, Exception ex)
    {
        try { return options.ShouldRetry?.Invoke(ex) ?? true; }
        catch (Exception callbackEx) when (callbackEx is not OperationCanceledException)
        {
            return false;
        }
    }

    private static void TryInvokeOnRetry(RetryOptions options, Exception ex, int failures)
    {
        try { options.OnRetry?.Invoke(ex, failures); }
        catch (Exception callbackEx) when (callbackEx is not OperationCanceledException)
        {
            // OnRetry 异常仅吞掉（不中断重试链）。
        }
    }

    /// <summary>
    /// 重试候选判定：调用方已取消的 OCE 直接传播；其余（含委托自身超时的 OCE）可重试。
    /// </summary>
    private static bool IsRetryCandidate(Exception ex, CancellationToken cancellationToken)
        => ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>退避策略异常降级 1 秒，保证重试路径不因策略缺陷中断（与 Saga 同口径）。</summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "退避策略缺陷不得中断重试路径；降级固定 1 秒并继续，与 Saga 步骤重试同口径。")]
    private static TimeSpan ComputeDelaySafely(IRetryBackoffPolicy policy, int attempt)
    {
        try
        {
            var delay = policy.ComputeDelay(attempt);
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
        catch (Exception)
        {
            return TimeSpan.FromSeconds(1);
        }
    }
}
