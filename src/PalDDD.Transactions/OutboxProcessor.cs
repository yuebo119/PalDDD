// ─────────────────────────────────────────────────────────────
// 📤 OutboxPublisher — 租约模式 + 重试 + 死信的 Outbox 发布
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 发件箱后台发布器
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 发件箱后台轮询服务。<br/>
/// 💡 <b>工作流程：</b><br/>
/// 1. 定期轮询 outbox_messages 表<br/>
/// 2. 原子租约获取一批待发送消息（多实例安全）<br/>
/// 3. 反序列化 → 发布到 Broker → 标记成功/失败<br/>
/// 4. 失败重试（最多10次）→ 超限标记为死信<br/>
/// ⚡ 使用 ConfigureAwait(false) 避免同步上下文捕获。
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "BackgroundService 需隔离轮询循环失败并继续处理后续批次，需捕获 Exception 基类。")]
public sealed class OutboxProcessor : PeriodicBackgroundProcessor
{
    private readonly IPalLogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<OutboxOptions> options,
        IPalLogger<OutboxProcessor> logger,
        TimeSpan? pollInterval = null)
        : base(scopeFactory, pollInterval ?? options.CurrentValue.PollInterval)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        await processor.ProcessBatchAsync(ct);
    }

    protected override void OnTickFailed(Exception ex)
        => _logger.Error(ex, "Outbox processing failed");
}
