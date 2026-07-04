using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics;

namespace PalDDD.Repository.EFCore;

// ─────────────────────────────────────────────────────────────
// EF Core 领域事件拦截器 — Outbox 模式（同事务保证）
// ─────────────────────────────────────────────────────────────
//
// 💡 工作流程：
//   1. SavingChanges 时扫描 ChangeTracker 中所有 Entity 实例
//   2. 收集 HasDomainEvents 的实体中的所有领域事件
//   3. 序列化后通过 IPalOutboxStore.AddMessagesAsync 批量写入 outbox_messages 表
//   4. SaveChanges 成功后清除实体的领域事件（ClearDomainEvents）
//   5. 所有操作在同一个 SaveChanges 事务中——保证事件与业务数据的原子性
//
// 💡 保留理由：DDD + EF Core + Outbox 关键桥梁 · 事务内领域事件持久化。
//    详见 docs/decisions/004-core-type-retention.md

/// <summary>EF Core 拦截器 — 在 SaveChanges 事务内将领域事件写入发件箱。</summary>
/// <remarks>
/// 📐 <b>生命周期约束 — 必须注册为 Scoped</b>：<br/>
/// 本类持有实例字段 <c>_pending</c>（当前 SaveChanges 操作收集的领域事件列表）。
/// EF Core 的 <c>DbContext</c> 本身是 Scoped，interceptor 与之同生命周期。
/// 如果注册为 Singleton，<c>_pending</c> 会被多个并发请求交叉写入，导致数据污染。<br/>
/// 当前注册方式见 <see cref="ServiceCollectionExtensions.AddPalOutboxUnitOfWork{TContext}"/>，
/// 使用 <c>TryAddScoped</c> 保证正确生命周期。
/// </remarks>
public sealed class OutboxDomainEventInterceptor(
    Transactions.IPalOutboxStore outboxStore,
    Serialization.IMessageSerializer serializer,
    Serialization.IMessageCatalog messageCatalog) : SaveChangesInterceptor
{
    private readonly Transactions.IPalOutboxStore _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
    private readonly Serialization.IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly Serialization.IMessageCatalog _messageCatalog = messageCatalog ?? throw new ArgumentNullException(nameof(messageCatalog));

    /// <summary>当前 SaveChanges 操作收集的领域事件列表 — 非线程安全，依赖 Scoped 生命周期保证单请求独占。</summary>
    private readonly List<Core.DomainEvent> _pending = [];

    /// <summary>当前 SaveChanges 操作期间收集的领域事件列表。</summary>
    public IReadOnlyList<Core.DomainEvent> PendingEvents => _pending;

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        _pending.Clear();
        DomainEventCollector.Collect(eventData.Context, _pending);
        WriteEventsToOutbox(_pending);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        DomainEventCollector.Clear(eventData.Context);
        _pending.Clear();
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        _pending.Clear();
        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void WriteEventsToOutbox(IReadOnlyList<Core.DomainEvent> events)
    {
        foreach (var evt in events)
        {
            var descriptor = _messageCatalog.Find(evt.GetType())
                ?? throw new InvalidOperationException(
                    $"Domain event '{evt.GetType().FullName}' is not registered in MessageCatalog.");
            var payload = _serializer.Serialize(evt, descriptor);
            var msg = new Transactions.OutboxMessage
            {
                Type = descriptor.Name,
                Payload = payload.ToArray(),
                ContentType = _serializer.ContentType,
                SchemaVersion = descriptor.SchemaVersion,
                CausationId = evt.EventId,
                TraceParent = Activity.Current?.Id,
                TraceState = Activity.Current?.TraceStateString,
                Status = Transactions.OutboxStatus.Pending
            };
            _outboxStore.AddMessage(msg);
        }
    }
}

/// <summary>
/// 遍历 EF Core ChangeTracker 中所有实体的领域事件并收集到列表中。<br/>
/// 内部静态类——仅被 OutboxDomainEventInterceptor 使用。
/// </summary>
internal static class DomainEventCollector
{
    public static void Collect(Microsoft.EntityFrameworkCore.DbContext? context, List<Core.DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is Core.Entity { HasDomainEvents: true } entity)
            {
                foreach (var evt in entity.DomainEvents())
                    events.Add(evt);
            }
        }
    }

    public static void Clear(Microsoft.EntityFrameworkCore.DbContext? context)
    {
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is Core.Entity { HasDomainEvents: true } entity)
                entity.ClearDomainEvents();
        }
    }
}
