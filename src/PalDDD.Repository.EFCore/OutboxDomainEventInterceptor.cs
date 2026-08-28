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
// ⚠️ 已知窗口（三十八轮 P2 修复：声明不修）：原子性只在单次 SaveChanges 内成立。
//   SaveChanges 成功但外层 UnitOfWork Commit 失败时，领域事件已在 SavedChanges(Async)
//   中被清空——同一 scope 内重试 SaveChanges 不会重新产生 outbox 行（业务落库而
//   outbox 无行）。需要跨 Commit 重试保证的应用应在新的 UnitOfWork scope 中重试
//   整个业务操作。行为重构（事件清理移到 Commit 之后）有双行风险，本轮不做。
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
/// <para>
/// ⚠️ <b>已知窗口（三十八轮 P2 修复：声明不修）</b>：事件与业务数据的原子性只在单次
/// SaveChanges 内成立。SaveChanges 成功但外层 UnitOfWork Commit 失败时，领域事件已在
/// SavedChanges(Async) 中被清空——同一 scope 内重试不会重新产生 outbox 行（业务落库而
/// outbox 无行）。需要跨 Commit 重试保证的应用应在新的 UnitOfWork scope 中重试整个
/// 业务操作；行为重构（清理移到 Commit 后）有双行风险，本轮不做。
/// </para>
/// <para>
/// ⚠️ <b>多拦截器链中断窗口（v38 P3 声明不修）</b>：多拦截器配置下，链中其他拦截器在
/// SavingChanges(Async) 阶段抛异常时 SaveChangesFailed(Async) 不可达（EF 派发点位于
/// SaveChanges 的 try 块之前，见 <see cref="WriteEventsToOutbox"/> 注释）——本拦截器已
/// 注入的 outbox 行将滞留 ChangeTracker 至同 scope 重试（双行风险）。单拦截器（本框架
/// 推荐配置，AddPalOutboxUnitOfWork 默认）下链上唯一异常源是本拦截器自身，已由 v34
/// 注入循环自清理域覆盖；多拦截器场景的清理挂钩方案 v3.0 收敛。
/// </para>
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
        WriteEventsToOutbox(eventData.Context, _pending);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
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
        WriteEventsToOutbox(eventData.Context, _pending);
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
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
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
    // v35 P3：CA1031 抑制——清理路径捕获 Exception 是刻意设计（对齐 WriteEventsToOutbox
    // 的 v34 P2 catch(Exception)+rethrow 先例）：任何清理失败（ChangeTracker 状态异常等）
    // 都不得替换 EF 原始 SaveChanges 失败异常，清理异常已挂 Exception.Data 供诊断
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "失败事件派发路径的清理必须对任意异常免疫，否则清理次生异常替换真实 DbUpdateException。")]
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        // ITM-178 修复（二十九轮）：EF 失败不自动回滚 ChangeTracker——本轮 AddMessage
        // 注入的 OutboxMessage 仍处 Added 状态，若不 Detach，调用方修复后重试 SaveChanges
        // 会旧消息+新消息一起落库（同事件 outbox 双写，下游重复消费）。
        // v35 P3：清理包 try-catch——EF 在 SaveChanges 的 catch 内派发本失败事件，清理
        // 自身抛出会成为冒出异常替换真实 DbUpdateException；清理异常挂 Exception.Data
        // 供诊断，不替换原始异常。
        try
        {
            RemoveInjectedOutboxMessages(eventData.Context);
        }
        catch (Exception cleanupEx)
        {
            eventData.Exception?.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
        }
        _pending.Clear();
        await base.SaveChangesFailedAsync(eventData, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <para>P1 修复：sync SaveChanges() 失败路径（与 async 版对齐）。</para>
    // v35 P3：CA1031 同 SaveChangesFailedAsync——清理路径刻意捕获 Exception
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "失败事件派发路径的清理必须对任意异常免疫，否则清理次生异常替换真实 DbUpdateException。")]
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        // v35 P3：同 SaveChangesFailedAsync——清理异常不替换真实 SaveChanges 失败异常
        try
        {
            RemoveInjectedOutboxMessages(eventData.Context);
        }
        catch (Exception cleanupEx)
        {
            eventData.Exception?.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
        }
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

    // v35 P3：CA1031 抑制仅针对内层清理 catch（吞掉挂 Data、原异常经 throw; 重抛）——
    // 外层主 catch (Exception) 本就 rethrow 不触发本规约
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "部分注入清理必须对任意异常免疫，清理失败挂原始异常 Data 供诊断而非替换之。")]
    private void WriteEventsToOutbox(Microsoft.EntityFrameworkCore.DbContext? context, IReadOnlyList<Core.DomainEvent> events)
    {
        // v34 P2 修复：注入循环整体纳入自清理域——EF 源码实证 SavingChanges(Async) 的
        // 派发点位于 DbContext.SaveChanges 的 try 块之前，拦截器自身抛异常（catalog
        // miss/序列化失败）不触发 SaveChangesFailed，ITM-178 的 Detach 兜底整条失效：
        // 批内第 k 个事件失败时前 k-1 个 Added 行滞留 ChangeTracker，同 scope 重试
        // SaveChanges 即同事件双行（下游重复消费）。循环内 try/catch 自清理后 rethrow，
        // sync/async 两路共用本方法天然闭合
        // v38 P3 声明：本自清理域只覆盖"本拦截器自身抛异常"路径——多拦截器配置下其他
        // 拦截器在 SavingChanges 抛异常时 SaveChangesFailed 同样不可达且无自清理挂钩，
        // 已注入行滞留至同 scope 重试；详见类 remarks（多拦截器场景 v3.0 收敛）。
        try
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
                    // v36 P3（镜像 v35 EA4 在 EventAuditMetadata.TraceParent 的格式前提声明）：
                    // TraceParent 取自 Activity.Current?.Id，其格式跟随 Activity.IdFormat——
                    // 假定 W3C（.NET 5+ 默认 DefaultIdFormat 为 W3C）。宿主若全局改用 Hierarchical
                    //（Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical），写入值为
                    // hierarchical 格式（|... 形态）而非 W3C traceparent（00-... 四段），下游
                    // 按 W3C 解析将失真；跨格式环境需在宿主统一 IdFormat 或消费侧按前缀判别。
                    CausationId = null,
                    TraceParent = Activity.Current?.Id,
                    TraceState = Activity.Current?.TraceStateString,
                    Status = Transactions.OutboxStatus.Pending
                };
                _outboxStore.AddMessage(msg);
                _injectedOutboxIds.Add(msg.Id);
            }
        }
        catch (Exception original)
        {
            // 部分注入清理：对当前 context 的 Added 半成品 Detach（语义同 ITM-178 兜底，
            // 但覆盖"拦截器自身抛异常"这一 SaveChangesFailed 不可达路径）。
            // v35 P3：清理自身包 try-catch——清理失败（ChangeTracker 状态异常等）若不加
            // 保护会成为 catch 内新异常替换 rethrow 的原始异常（真实 catalog miss/序列化
            // 失败被吞），清理异常挂 original.Data 供诊断；throw; 重抛原始异常（堆栈保留）。
            try
            {
                RemoveInjectedOutboxMessages(context);
            }
            catch (Exception cleanupEx)
            {
                original.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
            }
            throw;
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
