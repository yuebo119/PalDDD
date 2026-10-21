using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

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
// ⚠️ 中途抛语义（V25 探针 a 实测，2026-09-09）：outbox 写入中途抛（如 store 侧失败）时，
//   异常从 SavingChanges(Async) 阶段原样透传——EF 的 SaveChangesFailed 回调「不触发」
//   （该回调只覆盖 provider 层失败/DbUpdateException 形态，控制组实证）；聚合实体以
//   Added 态滞留 ChangeTracker、领域事件未清除、本上下文暂存状态残留。调用方 catch 后应弃用
//   当前 scope（新 scope 重试），依赖滞留实体二次 SaveChanges 会重复收集事件。
//
// 💡 保留理由：DDD + EF Core + Outbox 关键桥梁 · 事务内领域事件持久化。
//    详见 docs/decisions/004-core-type-retention.md

/// <summary>EF Core 拦截器 — 在 SaveChanges 事务内将领域事件写入发件箱。</summary>
/// <remarks>
/// 📐 <b>生命周期（2026-10-21 修订）</b>：本类除诊断指针 <c>_lastContext</c> 外不持有跨请求
/// 可变字段。每轮 SaveChanges 的领域事件、命中实体与注入 ID 存放在按 <c>DbContext</c> 实例
/// 键控的 <c>ConditionalWeakTable</c> 状态中，SavingChanges 开始时清空、成功/失败收尾时重置。
/// <c>_lastContext</c> 只服务 <c>PendingEvents</c> 诊断，Singleton 共享时可能指向另一请求的
/// 上下文（该属性不做正确性保证）。
/// 因此拦截器注册为 Scoped 或 Singleton 均安全，<b>可与 <c>AddDbContextPool</c> 组合使用</b>；
/// 池化复用的 DbContext 实例各自持有独立状态，不会交叉写入。
/// 当前注册方式见 <see cref="ServiceCollectionExtensions.AddPalOutboxUnitOfWorkForContext{TContext}"/>。
/// <para>
/// 📐 <b>状态键控说明</b>：弱表以 DbContext 实例为键，状态随上下文回收；池化上下文重新
/// 租出时由 <c>BeginSave</c> 的 Reset 清空，不残留上一租约的事件。
/// </para>
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
/// 推荐配置，AddPalOutboxUnitOfWorkForContext 默认）下链上唯一异常源是本拦截器自身，已由 v34
/// 注入循环自清理域覆盖；多拦截器场景的清理挂钩方案 v4.0 收敛。
/// </para>
/// </remarks>
public sealed class OutboxDomainEventInterceptor : SaveChangesInterceptor
{
    private readonly Transactions.IPalOutboxStore? _outboxStore;
    private readonly IServiceProvider? _services;
    // F4（2026-10-21）：消息构造与 Dapper/PalORM 共用的栈无关桥（单一形态来源）。
    private readonly Transactions.DomainEventOutboxBridge _bridge;

    /// <summary>以显式 store 构造（手动装配/测试）。</summary>
    public OutboxDomainEventInterceptor(
        Transactions.IPalOutboxStore outboxStore,
        Serialization.IMessageSerializer serializer,
        Serialization.IMessageCatalog messageCatalog)
    {
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _bridge = new Transactions.DomainEventOutboxBridge(
            messageCatalog ?? throw new ArgumentNullException(nameof(messageCatalog)),
            serializer ?? throw new ArgumentNullException(nameof(serializer)));
    }

    /// <summary>
    /// DI 构造：store 延迟到首次 SavingChanges 时解析（2026-10-21 F2 修正）。
    /// 「业务上下文即 IPalOutboxStore」时，构造期解析 store 会回到同一 DbContext 的
    /// options 构造 → 拦截器 → store 的循环依赖。延迟解析让 options 构建不触碰 store。
    /// </summary>
    internal OutboxDomainEventInterceptor(
        IServiceProvider services,
        Serialization.IMessageSerializer serializer,
        Serialization.IMessageCatalog messageCatalog)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _bridge = new Transactions.DomainEventOutboxBridge(
            messageCatalog ?? throw new ArgumentNullException(nameof(messageCatalog)),
            serializer ?? throw new ArgumentNullException(nameof(serializer)));
    }

    /// <summary>目标 outbox store——显式注入优先，否则按作用域延迟解析。</summary>
    private Transactions.IPalOutboxStore OutboxStore
        => _outboxStore
           ?? _services?.GetRequiredService<Transactions.IPalOutboxStore>()
           ?? throw new InvalidOperationException("Outbox store is not configured for OutboxDomainEventInterceptor.");

    /// <summary>
    /// 每个 DbContext 实例独占的 SaveChanges 暂存状态（领域事件、命中实体、本拦截器注入的
    /// OutboxMessage ID）。2026-10-21：由拦截器实例字段移入按上下文键控的弱表，
    /// 拦截器因此可以随 <c>AddDbContextPool</c> 共享复用，不再有跨请求交叉写入。
    /// </summary>
    private sealed class PendingState
    {
        public List<Core.DomainEvent> Events { get; } = [];

        /// <summary>本轮收集到领域事件的实体引用——成功后只清理这些，避免二次全量扫描。</summary>
        public List<Core.Entity> Entities { get; } = [];

        /// <summary>ITM-227：本轮由拦截器注入的 OutboxMessage ID，失败时只 Detach 这些。</summary>
        public HashSet<ByteAether.Ulid.Ulid> InjectedOutboxIds { get; } = [];

        public void Reset()
        {
            Events.Clear();
            Entities.Clear();
            InjectedOutboxIds.Clear();
        }
    }

    private static readonly ConditionalWeakTable<Microsoft.EntityFrameworkCore.DbContext, PendingState> s_states = new();

    /// <summary>最近一次 SavingChanges 的上下文——仅供 <see cref="PendingEvents"/> 诊断读取。
    /// 并发共享拦截器时可能指向另一请求的上下文，诊断属性不做正确性保证。</summary>
    private Microsoft.EntityFrameworkCore.DbContext? _lastContext;

    /// <summary>当前 SaveChanges 操作期间收集的领域事件列表（诊断用，best-effort）。</summary>
    /// <remarks>
    /// 2026-10-21 双评审修复轮：生产代码零调用，计划在 v5.0.0 移除。为保持一个 Minor
    /// 窗口的二进制兼容，本属性先标 <see cref="ObsoleteAttribute"/>；内部测试改用
    /// <see cref="PendingEventsCore"/>。
    /// </remarks>
    [Obsolete("PendingEvents is diagnostic-only and will be removed in v5.0.0. Use PendingEventsCore internally or query the outbox store.")]
    public IReadOnlyList<Core.DomainEvent> PendingEvents => PendingEventsCore;

    /// <summary>内部诊断读取（公开属性待移除，测试经 InternalsVisibleTo 访问）。</summary>
    internal IReadOnlyList<Core.DomainEvent> PendingEventsCore
        => _lastContext is { } context
           && s_states.TryGetValue(context, out var state)
            ? state.Events
            : Array.Empty<Core.DomainEvent>();

    private static PendingState StateFor(Microsoft.EntityFrameworkCore.DbContext? context)
        => context is null
            ? new PendingState()
            : s_states.GetValue(context, static _ => new PendingState());

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var state = BeginSave(eventData.Context);
        DomainEventCollector.Collect(eventData.Context, state.Events, state.Entities);
        // 轮四（2026-10-10）：异步路径分派——立即写 store（Dapper/PalORM/InMemory，
        // IImmediateWriteOutboxStore）走单次异步批量（多事件合并单往返 + 消除逐条同步写的
        // 往返级线程阻塞）；EF store（延迟写族）走逐条异步路径（v4.0 ③：跟踪添加同步完成）。
        // 同步钩子 SavingChanges 无异步可用，两类 store 均走 WriteEventsToOutbox 的
        // 单点阻塞桥（v4.0 ③ 文档化形态）
        if (state.Events.Count > 0 && OutboxStore is Transactions.IImmediateWriteOutboxStore immediate)
            await WriteEventsToOutboxImmediateAsync(eventData.Context, state, immediate, cancellationToken).ConfigureAwait(false);
        else if (state.Events.Count > 0)
            await WriteEventsToOutboxAsync(eventData.Context, state).ConfigureAwait(false);
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

        var state = BeginSave(eventData.Context);
        DomainEventCollector.Collect(eventData.Context, state.Events, state.Entities);
        WriteEventsToOutbox(eventData.Context, state);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        CompleteSave(eventData.Context);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <para>P1 修复：sync SaveChanges() 成功路径的事件清理（与 async 版对齐）。</para>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        CompleteSave(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    /// <summary>开始一次 SaveChanges：取得该上下文的暂存状态并清空上一轮残留。</summary>
    private PendingState BeginSave(Microsoft.EntityFrameworkCore.DbContext? context)
    {
        var state = StateFor(context);
        state.Reset();
        _lastContext = context;
        return state;
    }

    /// <summary>成功收尾：只清理本轮收集到事件的实体，再清空暂存状态。</summary>
    private static void CompleteSave(Microsoft.EntityFrameworkCore.DbContext? context)
    {
        var state = StateFor(context);
        DomainEventCollector.Clear(state.Entities);
        state.Reset();
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
        var state = StateFor(eventData.Context);
        try
        {
            RemoveInjectedOutboxMessages(eventData.Context, state.InjectedOutboxIds);
        }
        catch (Exception cleanupEx)
        {
            eventData.Exception?.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
        }
        state.Reset();
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
        var state = StateFor(eventData.Context);
        try
        {
            RemoveInjectedOutboxMessages(eventData.Context, state.InjectedOutboxIds);
        }
        catch (Exception cleanupEx)
        {
            eventData.Exception?.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
        }
        state.Reset();
        base.SaveChangesFailed(eventData);
    }

    /// <summary>
    /// ITM-227 修复：只 Detach 本拦截器本轮注入的 OutboxMessage（按注入 ID 精确匹配），
    /// 不影响调用方自行 Add 的消息。避免失败重试时调用方消息被误删。
    /// </summary>
    private static void RemoveInjectedOutboxMessages(
        Microsoft.EntityFrameworkCore.DbContext? context,
        HashSet<ByteAether.Ulid.Ulid> injectedOutboxIds)
    {
        if (context is null || injectedOutboxIds.Count == 0)
            return;

        foreach (var entry in context.ChangeTracker.Entries<Transactions.OutboxMessage>().ToList())
        {
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added
                && injectedOutboxIds.Contains(entry.Entity.Id))
            {
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
        injectedOutboxIds.Clear();
    }

    // v35 P3：CA1031 抑制仅针对内层清理 catch（吞掉挂 Data、原异常经 throw; 重抛）——
    // 外层主 catch (Exception) 本就 rethrow 不触发本规约
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "部分注入清理必须对任意异常免疫，清理失败挂原始异常 Data 供诊断而非替换之。")]
    private void WriteEventsToOutbox(Microsoft.EntityFrameworkCore.DbContext? context, PendingState state)
    {
        // v34 P2 修复：注入循环整体纳入自清理域——EF 源码实证 SavingChanges(Async) 的
        // 派发点位于 DbContext.SaveChanges 的 try 块之前，拦截器自身抛异常（catalog
        // miss/序列化失败）不触发 SaveChangesFailed，ITM-178 的 Detach 兜底整条失效：
        // 批内第 k 个事件失败时前 k-1 个 Added 行滞留 ChangeTracker，同 scope 重试
        // SaveChanges 即同事件双行（下游重复消费）。循环内 try/catch 自清理后 rethrow，
        // sync/async 两路共用本方法天然闭合
        // v38 P3 声明：本自清理域只覆盖"本拦截器自身抛异常"路径——多拦截器配置下其他
        // 拦截器在 SavingChanges 抛异常时 SaveChangesFailed 同样不可达且无自清理挂钩，
        // 已注入行滞留至同 scope 重试；详见类 remarks（多拦截器场景 v4.0 收敛）。
        try
        {
            // 2026-10-19 性能实施轮（ADR-026 同步钩子文档化阻塞桥）：同步 SaveChanges 对
            // 立即写 store 也走一次批量调用（原逐事件阻塞桥在远程库上 k 个事件 = k 次往返）。
            // EF store 的 AddMessageAsync 是跟踪添加（同步完成），走下方逐条路径。
            if (OutboxStore is Transactions.IImmediateWriteOutboxStore immediate)
            {
                var messages = new List<Transactions.OutboxMessage>(state.Events.Count);
                foreach (var evt in state.Events)
                    messages.Add(BuildOutboxMessage(evt));

                // ADR-026：同步钩子无异步可用，单点阻塞桥（批量形态）
                immediate.AddMessagesAsync(messages).AsTask().GetAwaiter().GetResult();
                foreach (var msg in messages)
                    state.InjectedOutboxIds.Add(msg.Id);
                return;
            }

            foreach (var evt in state.Events)
            {
                var msg = BuildOutboxMessage(evt);
                // v4.0 ③（ADR-026）：接口异步化后的同步钩子单点阻塞桥——无同步上下文宿主
                //（ASP.NET Core/控制台）安全；classic sync-context 宿主请改用 SaveChangesAsync
                //（迁移指引见 CHANGELOG）。EF store 的 AddMessageAsync 为跟踪添加（同步完成），
                // 桥实际零阻塞；立即写 store（Dapper/PalORM）真异步——桥为文档化阻塞点
                OutboxStore.AddMessageAsync(msg).AsTask().GetAwaiter().GetResult();
                state.InjectedOutboxIds.Add(msg.Id);
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
                RemoveInjectedOutboxMessages(context, state.InjectedOutboxIds);
            }
            catch (Exception cleanupEx)
            {
                original.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
            }
            throw;
        }
    }

    /// <summary>
    /// 轮四（2026-10-10）：立即写 store 的异步批量路径——序列化全部事件后单次
    /// <see cref="Transactions.IImmediateWriteOutboxStore.AddMessagesAsync"/>（Dapper 栈
    /// PG 单 COPY 往返，批化收益见 AddBatch5 基准 3.5×/3.8×；SQLite/MySQL 同语义单语句），
    /// 消除原逐条同步写在远程库上对异步调用线程的往返级阻塞。自清理域镜像
    /// <see cref="WriteEventsToOutbox"/>（v34 P2 同款：失败 Detach 半成品后重抛原始异常；
    /// 序列化阶段零写入、批量调用为单语句原子，失败时无半批驻留 ChangeTracker——立即写
    /// 族不经 ChangeTracker，清理仍对称执行以覆盖 EF store 被误标注等防御场景）。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "部分注入清理必须对任意异常免疫（同 WriteEventsToOutbox v35 P3 形态）。")]
    private async Task WriteEventsToOutboxImmediateAsync(
        Microsoft.EntityFrameworkCore.DbContext? context,
        PendingState state,
        Transactions.IImmediateWriteOutboxStore immediateStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<Transactions.OutboxMessage>(state.Events.Count);
            foreach (var evt in state.Events)
            {
                var msg = BuildOutboxMessage(evt);
                messages.Add(msg);
                state.InjectedOutboxIds.Add(msg.Id);
            }
            // v4.0 ④（ADR-026）：接口已带 CancellationToken——异步钩子传播真实 token
            _ = await immediateStore.AddMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try
            {
                RemoveInjectedOutboxMessages(context, state.InjectedOutboxIds);
            }
            catch (Exception cleanupEx)
            {
                original.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
            }
            throw;
        }
    }

    /// <summary>
    /// v4.0 ③（ADR-026）：异步钩子的逐条路径（EF store 等延迟写族）——每事件
    /// <see cref="Transactions.IPalOutboxStore.AddMessageAsync"/> 真异步（EF 实现为跟踪添加
    /// 同步完成），自清理域同 <see cref="WriteEventsToOutbox"/>（v34 P2 同款）。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "部分注入清理必须对任意异常免疫（同 WriteEventsToOutbox v35 P3 形态）。")]
    private async Task WriteEventsToOutboxAsync(
        Microsoft.EntityFrameworkCore.DbContext? context,
        PendingState state)
    {
        try
        {
            foreach (var evt in state.Events)
            {
                var msg = BuildOutboxMessage(evt);
                await OutboxStore.AddMessageAsync(msg).ConfigureAwait(false);
                state.InjectedOutboxIds.Add(msg.Id);
            }
        }
        catch (Exception original)
        {
            try
            {
                RemoveInjectedOutboxMessages(context, state.InjectedOutboxIds);
            }
            catch (Exception cleanupEx)
            {
                original.Data["PalOutboxCleanupFailure"] = cleanupEx.ToString();
            }
            throw;
        }
    }

    /// <summary>领域事件 → OutboxMessage 构造（同步逐条路径与异步批量路径共用）。
    /// 2026-10-21 F4：实现移至栈无关的 <see cref="Transactions.DomainEventOutboxBridge"/>，
    /// Dapper/PalORM 用户可复用同一构造（不再各自复制 catalog/序列化/追踪字段逻辑）。</summary>
    private Transactions.OutboxMessage BuildOutboxMessage(Core.DomainEvent evt)
        => _bridge.BuildMessage(evt);
}

/// <summary>
/// 遍历 EF Core ChangeTracker 中所有实体的领域事件并收集到列表中。<br/>
/// 内部静态类——仅被 OutboxDomainEventInterceptor 使用。
/// </summary>
internal static class DomainEventCollector
{
    public static void Collect(
        Microsoft.EntityFrameworkCore.DbContext? context,
        List<Core.DomainEvent> events,
        List<Core.Entity> eventEntities)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(eventEntities);

        if (context is null)
            return;

        // 2026-10-19 性能实施轮：按 Core.Entity 类型过滤（原 Entries() 遍历全部被跟踪实体，
        // 含大量非实体/非领域模型条目）；同时记录命中实体，SavedChanges 只清理这些引用。
        foreach (var entry in context.ChangeTracker.Entries<Core.Entity>())
        {
            if (entry.Entity is { HasDomainEvents: true } entity)
            {
                eventEntities.Add(entity);
                foreach (var evt in entity.DomainEvents())
                    events.Add(evt);
            }
        }
    }

    public static void Clear(IReadOnlyList<Core.Entity> eventEntities)
    {
        ArgumentNullException.ThrowIfNull(eventEntities);

        // 只清理本轮收集到事件的实体（SavingChanges 已记录），无需二次全量扫描。
        foreach (var entity in eventEntities)
            entity.ClearDomainEvents();
    }
}
