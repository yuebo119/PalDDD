// ─────────────────────────────────────────────────────────────
// ✅🔍 内置管道行为 — ValidationBehavior + LoggingBehavior
// ─────────────────────────────────────────────────────────────
// Validation：调用 IPalValidator<T> 验证请求，失败抛 PalValidationException
// Logging：记录 Handler 名称和耗时（IPalLogger 门面 + ZLogger 实现）
//
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 内置管道行为 — 验证 + 日志
// 💡 保留理由：DDD 验证拦截 + IPalLogger 日志门面。
//    详见 docs/decisions/004-core-type-retention.md
// ─────────────────────────────────────────────────────────────
//
// 性能设计：
//    - ValidationBehavior 的 errors 列表延迟分配（errors ??= []），
//      无验证失败时零堆分配。这是比 FluentValidation 更轻量的设计。
//    - LoggingBehavior 使用 IPalLogger<T> 门面，底层由 ZLogger 提供
//      零分配 UTF8 结构化日志。仅在 _logger.IsEnabled(LogLevel.Debug) 时才记录。
//    - LoggingBehavior 的有意义日志采用字符串插值而非 LoggerMessage 源生成：
//      日志经 IPalLogger<T> 门面发出，门面刻意隐藏底层 ILogger（见 IPalLogger.cs 设计原则），
//      与 LoggerMessage.Define 所需的 ILogger 入参不兼容；且插值仅在 IsEnabled(Debug) 门控后执行，
//      生产路径零分配。改为 LoggerMessage 会泄漏门面抽象、增加复杂度，故保持现状（YAGNI）。
//
// 3. 可扩展性：用户可以通过实现 IPipelineBehavior<TRequest,TResponse>
//    添加自定义行为（如事务、缓存、限流），无需修改框架代码。
//    这两个内置行为是"开箱即用"的合理默认，不是强制的。

/// <summary>验证管道行为 — 调用所有 IPalValidator&lt;TRequest&gt; 进行验证</summary>
/// <remarks>
/// 使用 <see cref="Core.IPalValidator{T}"/> 抽象，不依赖任何特定验证库。<br/>
/// errors 列表延迟分配——无验证失败时零堆分配。<br/>
/// 注册方式：<c>services.AddScoped(typeof(IPipelineBehavior&lt;,&gt;), typeof(ValidationBehavior&lt;,&gt;))</c>
/// </remarks>
internal sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<Core.IPalValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<Core.IPalValidator<TRequest>> validators)
        => _validators = validators;

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
    {
        List<Core.PalValidationError>? errors = null;
        foreach (var validator in _validators)
        {
            var result = validator.Validate(request);
            if (!result.IsValid)
            {
                errors ??= [];
                errors.AddRange(result.Errors);
            }
        }

        if (errors is { Count: > 0 })
            throw new PalValidationException([.. errors]);

        return await next();
    }
}

/// <summary>日志管道行为 — IPalLogger 门面日志记录</summary>
/// <remarks>
/// 记录命令/查询的执行时间和结果。<br/>
/// 注册方式：<c>services.AddScoped(typeof(IPipelineBehavior&lt;,&gt;), typeof(LoggingBehavior&lt;,&gt;))</c>（P3 修复：与 ServiceRegistration 实际注册对齐）
/// </remarks>
[SuppressMessage("Design", "CA1031", Justification = "需记录任意 handler 失败后重新抛出，cancel 不记录。")]
internal sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IPalLogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly TimeProvider _timeProvider;

    // P3 修复（时钟双轨清零）：可选注入，默认 System——测试可传 FakeTimeProvider
    public LoggingBehavior(IPalLogger<LoggingBehavior<TRequest, TResponse>> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
    {
        // ITM-199 修复（三十轮）：统一 "Request" 中性前缀——QueryAsync 也走本行为，
        // 原固定 "Command" 前缀在日志中误导排障；按 IQuery 反射区分需 DAM 注解（IL2090，
        // AOT 反对），改用中性措辞零反射安全。

        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.Debug($"Request {typeof(TRequest).Name}: dispatching");

        var start = _timeProvider.GetTimestamp();
        try
        {
            var result = await next();
            var elapsed = _timeProvider.GetElapsedTime(start).TotalMilliseconds;

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.Debug($"Request {typeof(TRequest).Name}: completed in {elapsed:F2}ms");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var elapsed = _timeProvider.GetElapsedTime(start).TotalMilliseconds;
            _logger.Error(ex, $"Request {typeof(TRequest).Name}: failed after {elapsed:F2}ms");
            throw;
        }
    }
}
