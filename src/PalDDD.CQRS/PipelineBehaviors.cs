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
/// 注册方式：<c>services.AddSingleton(typeof(IPipelineBehavior&lt;,&gt;), typeof(LoggingBehavior&lt;,&gt;))</c>
/// </remarks>
[SuppressMessage("Design", "CA1031", Justification = "需记录任意 handler 失败后重新抛出，cancel 不记录。")]
internal sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IPalLogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(IPalLogger<LoggingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
    {
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.Debug($"Command {typeof(TRequest).Name}: dispatching");

        var start = TimeProvider.System.GetTimestamp();
        try
        {
            var result = await next();
            var elapsed = TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.Debug($"Command {typeof(TRequest).Name}: completed in {elapsed:F2}ms");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var elapsed = TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;
            _logger.Error(ex, $"Command {typeof(TRequest).Name}: failed after {elapsed:F2}ms");
            throw;
        }
    }
}
