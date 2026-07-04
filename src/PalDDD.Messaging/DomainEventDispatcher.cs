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
        _handlers = handlers
            .GroupBy(h => h.EventType)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
        _options = options ?? new DomainEventDispatcherOptions();
        _logger = logger;
    }

    public async ValueTask DispatchAsync(IReadOnlyList<Core.DomainEvent> events, CancellationToken ct = default)
    {
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

            await DispatchSingleAsync(@event, ct);
        }

        if (queue.Count > 0)
            throw new InvalidOperationException(
                $"Domain event dispatch exceeded max iterations ({maxIterations}). Possible infinite event loop.");
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
                    await h.HandleAsync(@event, ct);
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
