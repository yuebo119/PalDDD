// ─────────────────────────────────────────────────────────────
// 🔧 Transactions.EFCore DI 注册扩展
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个扩展？
//   ｜ OutboxDbContext / SagaStateDbContext<TState> / InboxDbContext 分别实现
//   ｜ IPalOutboxStore / ISagaStateStore<TState> / IInboxStore，但此前包内无注册
//   ｜ 入口——调用方必须手动 AddDbContext + 手动映射三个接口。本扩展补上与
//   ｜ Dapper/PalORM 栈对称的 AddPal* 入口。
//
// 📐 DDD 位置：基础设施层 — DI 注册是组合根的一部分，不涉及领域逻辑。
// ─────────────────────────────────────────────────────────────

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Transactions;

/// <summary>EF Core 事务构件（Outbox/Saga/Inbox）适配器的 DI 注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 把派生自 <see cref="OutboxDbContext"/> 的上下文映射为 <see cref="IPalOutboxStore"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <typeparam name="TContext">派生自 <see cref="OutboxDbContext"/> 的上下文类型。</typeparam>
    /// <remarks>
    /// 本方法<b>不</b>注册 <typeparamref name="TContext"/> 本身（provider 选择权在调用方），
    /// 需与 <c>AddDbContext</c> 配套：
    /// <code>
    /// services.AddPalOutboxEfCore&lt;AppOutboxDbContext&gt;();
    /// services.AddDbContext&lt;AppOutboxDbContext&gt;((sp, o) =&gt; o.UseNpgsql(cs));
    /// </code>
    /// 生命周期为 Scoped，与 <c>AddDbContext</c> 默认一致（DbContext 非线程安全）。
    /// </remarks>
    public static IServiceCollection AddPalOutboxEfCore<TContext>(
        this IServiceCollection services)
        where TContext : OutboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPalOutboxStore>(static sp => sp.GetRequiredService<TContext>());
        return services;
    }

    /// <summary>
    /// 把派生自 <see cref="SagaStateDbContext{TState}"/> 的上下文映射为 <see cref="ISagaStateStore{TState}"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <typeparam name="TContext">派生自 <see cref="SagaStateDbContext{TState}"/> 的上下文类型。</typeparam>
    /// <typeparam name="TState">Saga 状态类型。</typeparam>
    /// <remarks>
    /// 与 Outbox 变体同构：本方法不注册上下文本身，需与 <c>AddDbContext</c> 配套；
    /// 生命周期为 Scoped。
    /// </remarks>
    public static IServiceCollection AddPalSagaStateEfCore<TContext, TState>(
        this IServiceCollection services)
        where TContext : SagaStateDbContext<TState>
        where TState : SagaState
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISagaStateStore<TState>>(static sp => sp.GetRequiredService<TContext>());
        return services;
    }

    /// <summary>
    /// 把派生自 <see cref="InboxDbContext"/> 的上下文映射为 <see cref="IInboxStore"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <typeparam name="TContext">派生自 <see cref="InboxDbContext"/> 的上下文类型。</typeparam>
    /// <remarks>
    /// 与 Outbox 变体同构：本方法不注册上下文本身，需与 <c>AddDbContext</c> 配套；
    /// 生命周期为 Scoped。<see cref="InboxDbContext"/> 构造器接受可选的
    /// <c>IPalLogger&lt;InboxDbContext&gt;</c>，EF 经 <c>ActivatorUtilities</c> 自动注入。
    /// </remarks>
    public static IServiceCollection AddPalInboxEfCore<TContext>(
        this IServiceCollection services)
        where TContext : InboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IInboxStore>(static sp => sp.GetRequiredService<TContext>());
        return services;
    }

    /// <summary>
    /// 注册历史表保留清理（2026-10-20 性能轮，可选）。本方法要求
    /// <typeparamref name="TContext"/> 已通过 <c>AddDbContext</c> 注册；清理 store 复用该
    /// 上下文的作用域连接，不额外创建连接或释放调用方连接。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="dialect">数据库方言。</param>
    /// <param name="configure">可选保留策略配置。</param>
    /// <typeparam name="TContext">已注册的 DbContext 类型（其数据库须含 outbox/inbox/idempotency 表）。</typeparam>
    /// <remarks>
    /// 表名从 <typeparamref name="TContext"/> 的 EF 模型解析（<c>OutboxMessage</c> /
    /// <c>InboxMessage</c> 实体）；模型未映射对应实体时回退 EF 默认名
    ///（<c>OutboxMessages</c> / <c>InboxMessages</c> / <c>IdempotencyRecords</c>）。
    /// 自定义映射（<c>ToTable</c>）请直接构造 <see cref="EfCoreRetentionStore"/> 三表重载。
    /// </remarks>
    public static IServiceCollection AddPalEfCoreRetention<TContext>(
        this IServiceCollection services,
        RetentionDialect dialect,
        Action<RetentionOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPalRetention(configure);
        services.TryAddScoped<IRetentionStore>(sp =>
            new EfCoreRetentionStore(sp.GetRequiredService<TContext>(), dialect));
        return services;
    }
}
