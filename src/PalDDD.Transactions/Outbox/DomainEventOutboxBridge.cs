using PalDDD.Core;
using PalDDD.Serialization;
using System.Diagnostics;

namespace PalDDD.Transactions;

/// <summary>
/// 领域事件 → Outbox 的栈无关桥（2026-10-21 架构评审 F4）。
/// </summary>
/// <remarks>
/// <para>
/// EF Core 用户由 <c>OutboxDomainEventInterceptor</c> 自动完成收集、序列化、同事务写入；
/// Dapper/PalORM 没有 SaveChanges 钩子，此前需要自行查找 catalog、序列化、提取追踪字段
/// 并清理事件——漏一步就是静默丢事件。本类把这条链路收敛为一次调用：
/// </para>
/// <code>
/// await using var uow = ...;                 // 同一事务
/// await uow.BeginTransactionAsync(ct);
/// await db.ExecuteAsync(...);                // 业务写
/// await bridge.PersistAsync([order], outboxStore, ct); // 事件落 outbox + 清理
/// await uow.CommitAsync(ct);
/// </code>
/// <para>
/// <b>原子性</b>：本类只负责「构造消息 + 写 store + 清事件」，不开启事务。调用方必须让
/// 业务写入与 <see cref="PersistAsync"/> 处于同一 <c>IUnitOfWork</c> 事务，否则与
/// 「先写库后发消息」的双写问题同构。EF Core 路径不要用本类——拦截器已覆盖。
/// </para>
/// <para>
/// AOT：纯静态序列化调用（IMessageSerializer/IMessageCatalog 均为注入接口），零反射。
/// </para>
/// </remarks>
public sealed class DomainEventOutboxBridge
{
    private readonly IMessageCatalog _catalog;
    private readonly IMessageSerializer _serializer;

    /// <summary>创建桥。</summary>
    public DomainEventOutboxBridge(IMessageCatalog catalog, IMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(serializer);
        _catalog = catalog;
        _serializer = serializer;
    }

    /// <summary>把单个领域事件构造成 OutboxMessage（与 EF 拦截器同一形态）。</summary>
    /// <exception cref="InvalidOperationException">事件类型未在 MessageCatalog 注册。</exception>
    public OutboxMessage BuildMessage(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var descriptor = _catalog.Find(domainEvent.GetType())
            ?? throw new InvalidOperationException(
                $"Domain event '{domainEvent.GetType().FullName}' is not registered in MessageCatalog.");
        // 显式 (object) 强转走非泛型重载，用派生 JsonTypeInfo（同 EF 拦截器 P1 修复）。
        var payload = _serializer.SerializeToArray((object)domainEvent, descriptor);
        return new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = _serializer.ContentType,
            SchemaVersion = descriptor.SchemaVersion,
            CausationId = null,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString,
            Status = OutboxStatus.Pending,
        };
    }

    /// <summary>收集全部聚合的领域事件并构造消息（不写 store、不清事件）。</summary>
    public IReadOnlyList<OutboxMessage> BuildMessages(IEnumerable<Entity> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        List<OutboxMessage>? messages = null;
        foreach (var aggregate in aggregates)
        {
            ArgumentNullException.ThrowIfNull(aggregate, nameof(aggregates));
            if (!aggregate.HasDomainEvents)
                continue;
            foreach (var domainEvent in aggregate.DomainEvents())
                (messages ??= []).Add(BuildMessage(domainEvent));
        }

        return messages ?? (IReadOnlyList<OutboxMessage>)Array.Empty<OutboxMessage>();
    }

    /// <summary>
    /// 构造消息 → 写 outbox → 清理聚合事件（三步按序；写失败时事件保留，可重试）。
    /// </summary>
    /// <returns>写入的消息数。</returns>
    public async ValueTask<int> PersistAsync(
        IReadOnlyList<Entity> aggregates,
        IPalOutboxStore outboxStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(outboxStore);

        var messages = BuildMessages(aggregates);
        if (messages.Count == 0)
            return 0;

        if (outboxStore is IImmediateWriteOutboxStore immediate)
        {
            _ = await immediate.AddMessagesAsync(messages, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var message in messages)
                await outboxStore.AddMessageAsync(message, ct).ConfigureAwait(false);
        }

        // 只在写入成功后清理——写入抛异常时事件保留在聚合上，调用方可重试。
        foreach (var aggregate in aggregates)
            aggregate.ClearDomainEvents();
        return messages.Count;
    }
}
