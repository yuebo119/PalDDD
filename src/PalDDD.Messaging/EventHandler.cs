// ─────────────────────────────────────────────────────────────
// 📨 IEventHandler<T> — 事件处理器 DIM 桥接
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Messaging;

// ─────────────────────────────────────────────────────────────
// 事件处理器 DIM 桥接
// ─────────────────────────────────────────────────────────────

/// <summary>非泛型事件处理器接口 — 派发器通过此接口零反射调用（AOT 安全）</summary>
/// <remarks>
/// <see cref="IEventHandler{TEvent}"/> 通过默认接口方法（DIM）自动实现此接口。<br/>
/// Handler 作者无需关心此接口的存在。<br/>
/// <see cref="EventType"/> 属性提供编译时类型标识，消除运行时 MakeGenericType。
/// </remarks>
public interface IEventHandler
{
    /// <summary>处理领域事件 — 泛型桥接到具体 Handler</summary>
    ValueTask HandleAsync(Core.DomainEvent @event, CancellationToken ct);

    /// <summary>此 Handler 处理的领域事件类型 — DIM 编译时常量，零反射</summary>
    Type EventType { get; }
}

/// <summary>泛型事件处理器接口 — 通过 DIM 桥接 <see cref="IEventHandler"/>（AOT 安全）</summary>
/// <typeparam name="TEvent">领域事件类型</typeparam>
public interface IEventHandler<in TEvent> : IEventHandler
    where TEvent : Core.DomainEvent
{
    /// <summary>处理领域事件</summary>
    ValueTask HandleAsync(TEvent @event, CancellationToken ct);

    /// <summary>非泛型桥接 — 默认实现，Handler 作者无需覆盖</summary>
    /// <remarks>同步完成路径零分配（跳过 async 状态机），异步路径按需分配</remarks>
    ValueTask IEventHandler.HandleAsync(Core.DomainEvent @event, CancellationToken ct)
    {
        var task = HandleAsync((TEvent)@event, ct);
        if (task.IsCompletedSuccessfully)
            return ValueTask.CompletedTask;
        return AwaitAndForget(task);

        static async ValueTask AwaitAndForget(ValueTask vt)
        {
            await vt.ConfigureAwait(false);
        }
    }

    /// <summary>AOT 安全事件类型标识 — typeof(TEvent) 是编译时常量</summary>
    Type IEventHandler.EventType => typeof(TEvent);
}
