// ─────────────────────────────────────────────────────────────
// 🔄 IDomainEventDispatcher — 防栈溢出 + 去重的事件派发
// ─────────────────────────────────────────────────────────────
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;

namespace PalDDD.Messaging;

// ─────────────────────────────────────────────────────────────
// 迭代式领域事件派发器
// ─────────────────────────────────────────────────────────────

/// <summary>领域事件派发器 — 迭代循环防栈溢出 + HashSet 去重防循环事件</summary>
public interface IDomainEventDispatcher
{
    ValueTask DispatchAsync(IReadOnlyList<Core.DomainEvent> events, CancellationToken ct = default);
}

/// <summary>迭代式领域事件派发器配置</summary>
public sealed class DomainEventDispatcherOptions
{
    /// <summary>最大迭代次数 — 防止无限事件循环（默认 1000）</summary>
    public int MaxIterations { get; set; } = 1000;
}

/// <summary>迭代式领域事件派发 — 用 while 循环替代递归，防深层事件链导致栈溢出</summary>
/// <remarks>
/// 通过 <see cref="IEventHandler.EventType"/> 属性（DIM 编译时常量）构建处理器映射，<br/>
/// 完全消除 <c>MakeGenericType</c> 运行时反射——100% Native AOT 兼容。<br/>
/// 循环事件检测时记录 Warning 日志以便诊断。
/// <para><b>派发语义</b>：本 dispatcher 仅处理传入的初始事件集合，不自动收集 handler 执行期间新产生的领域事件。
/// 链式事件（handler 产生新事件）应由外层应用层负责——典型模式是 aggregate.Apply → 收集事件 → SaveChanges → 调用 DispatchAsync。
/// 这样保证事务边界清晰，避免 dispatcher 内部无限递归。</para>
/// </remarks>
internal sealed class IterativeDomainEventDispatcher : IDomainEventDispatcher
{
    private readonly FrozenDictionary<Type, ImmutableArray<IEventHandler>> _handlers;
    private readonly DomainEventDispatcherOptions _options;
    private readonly IPalLogger<IterativeDomainEventDispatcher>? _logger;

    /// <summary>构造函数 — 注入所有 IEventHandler，按 EventType 分组索引</summary>
    public IterativeDomainEventDispatcher(
        IEnumerable<IEventHandler> handlers,
        DomainEventDispatcherOptions? options = null,
        IPalLogger<IterativeDomainEventDispatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = handlers
            .GroupBy(h => h.EventType)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
        _options = options ?? new DomainEventDispatcherOptions();
        // ITM-166 修复：MaxIterations 非正数会在下方循环中形成 0 次迭代或负循环
        // （for 条件恒 false/语义错乱），事件静默不派发——入口 fail-fast。
        if (_options.MaxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "DomainEventDispatcherOptions.MaxIterations must be greater than zero.");
        _logger = logger;
    }

    public async ValueTask DispatchAsync(IReadOnlyList<Core.DomainEvent> events, CancellationToken ct = default)
    {
        // ITM-166 修复：补 events null 守卫（空集合语义保留）。
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;

        var queue = new Queue<Core.DomainEvent>(events);
        var processed = new HashSet<Guid>(); // 防止循环事件
        var maxIterations = _options.MaxIterations;

        for (int i = 0; i < maxIterations && queue.Count > 0; i++)
        {
            var @event = queue.Dequeue();
            if (!processed.Add(@event.EventId))
            {
                // 循环事件检测 — 记录 Warning 以便诊断
                if (_logger is not null)
                    _logger.Warning($"Domain event cycle detected: {@event.GetType().Name} (EventId={@event.EventId}) already processed, skipping");
                continue;
            }

            await DispatchSingleAsync(@event, ct).ConfigureAwait(false);
        }

        if (queue.Count > 0)
            throw new InvalidOperationException(
                $"Domain event dispatch exceeded MaxIterations ({maxIterations}): initial batch size {events.Count} 超过上限"
                + "（事件循环防护由 EventId 去重承担——Handler 不产生新入队事件，此上限约束的是初始批量大小）。");
    }

    /// <summary>O(1) 字典查找 → DIM 桥接调用 —— 零反射，完全 AOT 安全</summary>
    private async ValueTask DispatchSingleAsync(Core.DomainEvent @event, CancellationToken ct)
    {
        using var activity = PalActivitySource.StartEventDispatch(@event.GetType().Name);

        try
        {
            if (_handlers.TryGetValue(@event.GetType(), out var handlers))
            {
                foreach (var h in handlers)
                {
                    await h.HandleAsync(@event, ct).ConfigureAwait(false);
                    PalMetrics.EventHandlersHandled.Add(1);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PalMetrics.EventHandlersFailed.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
