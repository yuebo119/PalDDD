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
//   3. 序列化后通过 IPalOutboxStore.AddMessage 逐条写入 outbox_messages 表
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

    /// <summary>ITM-227：本轮由拦截器注入的 OutboxMessage ID——SaveChanges 失败时只 Detach 这些，不影响调用方自己 Add 的消息。</summary>
    private readonly HashSet<ByteAether.Ulid.Ulid> _injectedOutboxIds = [];

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
    /// <para>P1 修复：sync SaveChanges() 路径——此前只覆写 async 版，应用调 sync 版时
    /// 领域事件静默不写 Outbox 且不清理。本覆写与 async 版逻辑一致。</para>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        _pending.Clear();
        DomainEventCollector.Collect(eventData.Context, _pending);
        WriteEventsToOutbox(_pending);
        return base.SavingChanges(eventData, result);
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
        _injectedOutboxIds.Clear();
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    /// <para>P1 修复：sync SaveChanges() 成功路径的事件清理（与 async 版对齐）。</para>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        DomainEventCollector.Clear(eventData.Context);
        _pending.Clear();
        _injectedOutboxIds.Clear();
        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc />
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        // ITM-178 修复（二十九轮）：EF 失败不自动回滚 ChangeTracker——本轮 AddMessage
        // 注入的 OutboxMessage 仍处 Added 状态，若不 Detach，调用方修复后重试 SaveChanges
        // 会旧消息+新消息一起落库（同事件 outbox 双写，下游重复消费）。
        RemoveInjectedOutboxMessages(eventData.Context);
        _pending.Clear();
        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <inheritdoc />
    /// <para>P1 修复：sync SaveChanges() 失败路径（与 async 版对齐）。</para>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        RemoveInjectedOutboxMessages(eventData.Context);
        _pending.Clear();
        base.SaveChangesFailed(eventData);
    }

    /// <summary>
    /// ITM-227 修复：只 Detach 本拦截器本轮注入的 OutboxMessage（按 _injectedOutboxIds 精确匹配），
    /// 不影响调用方自行 Add 的消息。避免失败重试时调用方消息被误删。
    /// </summary>
    private void RemoveInjectedOutboxMessages(Microsoft.EntityFrameworkCore.DbContext? context)
    {
        if (context is null || _injectedOutboxIds.Count == 0)
            return;

        foreach (var entry in context.ChangeTracker.Entries<Transactions.OutboxMessage>().ToList())
        {
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added
                && _injectedOutboxIds.Contains(entry.Entity.Id))
            {
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
        _injectedOutboxIds.Clear();
    }

    private void WriteEventsToOutbox(IReadOnlyList<Core.DomainEvent> events)
    {
        foreach (var evt in events)
        {
            var descriptor = _messageCatalog.Find(evt.GetType())
                ?? throw new InvalidOperationException(
                    $"Domain event '{evt.GetType().FullName}' is not registered in MessageCatalog.");
            var payload = _serializer.Serialize((object)evt, descriptor);
            // P1 修复（七轮评审）：evt 静态类型是 abstract DomainEvent——泛型重载
            // Serialize<DomainEvent>(evt, descriptor) 绑定基类 JsonTypeInfo 与派生 descriptor
            // 不匹配（InvalidCastException）。显式 (object) 强转走非泛型 Serialize(object, descriptor)
            // 用派生 JsonTypeInfo，与读侧（KafkaBroker/RabbitMqBroker 用 object 声明）对称。
            var msg = new Transactions.OutboxMessage
            {
                Type = descriptor.Name,
                Payload = payload.ToArray(),
                ContentType = _serializer.ContentType,
                SchemaVersion = descriptor.SchemaVersion,
                // ITM-103 修复：CausationId 不再自指——原 `CausationId = evt.EventId` 使 outbox 行
                // 的因果链自环（"事件由自身引起"），下游消费方按 causation 追踪时断链。
                // 本层无父事件追踪（DomainEvent 不含触发者 ID），诚实值为 null；
                // 有父链语义的调用方应在构造 OutboxMessage 时显式赋值。
                CausationId = null,
                TraceParent = Activity.Current?.Id,
                TraceState = Activity.Current?.TraceStateString,
                Status = Transactions.OutboxStatus.Pending
            };
            _outboxStore.AddMessage(msg);
            _injectedOutboxIds.Add(msg.Id);
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
