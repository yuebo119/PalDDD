// ─────────────────────────────────────────────────────────────
// 🧹 RetentionProcessorService — 历史表清理后台服务
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PalDDD.Core.Logging;

namespace PalDDD.Transactions;

/// <summary>
/// 历史表保留清理后台服务（2026-10-20 性能轮）。仅在显式调用
/// <c>AddPalDapperRetention</c> 时注册；未注册时框架不启动任何删除任务。
/// </summary>
public sealed class RetentionProcessorService : PeriodicBackgroundProcessor
{
    private readonly IPalLogger<RetentionProcessorService> _logger;
    private readonly IOptionsMonitor<RetentionOptions> _options;

    /// <summary>构造清理后台服务。</summary>
    public RetentionProcessorService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> options,
        IPalLogger<RetentionProcessorService>? logger = null,
        TimeProvider? timeProvider = null)
        : base(
            scopeFactory,
            options is null ? throw new ArgumentNullException(nameof(options)) : () => options.CurrentValue.PollInterval,
            timeProvider)
    {
        _logger = logger ?? NullPalLogger<RetentionProcessorService>.Instance;
        _options = options;
    }

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<RetentionProcessor>();
        var result = await processor.RunOnceAsync(ct).ConfigureAwait(false);
        if (result.TotalDeleted > 0 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.Information(
                $"Retention: deleted {result.OutboxDeleted} outbox, {result.InboxDeleted} inbox, {result.IdempotencyDeleted} idempotency record(s) (interval {_options.CurrentValue.PollInterval})");
        }
    }

    protected override void OnTickFailed(Exception ex)
        => _logger.Error(ex, "Retention processing failed");
}
