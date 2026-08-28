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
        // P3 修复（十七轮）：options 空守卫前移到 base 实参——原实参 pollInterval 为 null 时
        // 先在 options.CurrentValue 解引用 NRE，构造体内的 ThrowIfNull 永不可达
        // v29 P3：?? 短路勘正——pollInterval 非 null 时 (options ?? throw) 不求值，base 构造
        // 成功创建了 PeriodicTimer 后构造体 ThrowIfNull 才抛（构造中断 Dispose 不可达）→ timer
        // 泄漏。改为三元显式判 options：null 时在任何路径上都先于 base 调用求值抛出，
        // 不创建 timer
        // v30 P3（logger 轴扩展）：构造体 ThrowIfNull(logger) 同理在 base 成功后抛——timer
        // 已创建、Dispose 不可达 → 泄漏。logger 守卫合并进 base 第一实参（v29 options 轴
        // 三元形态扩展）：实参按出现顺序先于 base 体求值，logger/options 任一为 null 时
        // 均在 PeriodicTimer 创建前抛出；scopeFactory 轴无此问题（基类构造体在创建 timer
        // 之前先 ThrowIfNull(scopeFactory)）
        : base(
               logger is null
                   ? throw new ArgumentNullException(nameof(logger))
                   : scopeFactory,
               options is null
                   ? throw new ArgumentNullException(nameof(options))
                   : pollInterval ?? options.CurrentValue.PollInterval)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        await processor.ProcessBatchAsync(ct).ConfigureAwait(false);
    }

    protected override void OnTickFailed(Exception ex)
        => _logger.Error(ex, "Outbox processing failed");
}
