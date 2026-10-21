using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PalDDD.Core.Repository;
using PalDDD.Serialization;
using PalDDD.Transactions;

namespace PalDDD.Repository.EFCore;

// ─────────────────────────────────────────────────────────────
// 仓储服务注册
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core 工作单元与领域事件拦截器的注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 EF Core 工作单元与 Outbox 领域事件拦截器。</summary>
    /// <remarks>
    /// <c>DispatchingDomainEventInterceptor</c> 已在 v0.2.0 移除——其 AT-MOST-ONCE 语义导致
    /// broker 不可达时事件永久丢失。此方法只注册 Outbox 路径，是唯一推荐的事件持久化方式。
    /// <para>
    /// ⚠️ <b>原子性前置条件（编译期 + 解析期双重约束）</b>：领域事件与业务数据的原子性依赖
    /// <see cref="Transactions.IPalOutboxStore"/> 的 EF Core 实现与业务 <c>DbContext</c>
    /// <b>处于同一实例</b>——<see cref="OutboxDomainEventInterceptor"/> 在
    /// <c>SavingChangesAsync</c> 中将事件以 ChangeTracker 跟踪添加写入 outbox 表，只有业务
    /// 上下文自己的 <c>SaveChanges</c> 才会持久化它们。泛型约束
    /// <c>TContext : IPalOutboxStore</c> 排除「独立 outbox 上下文」形态；解析
    /// <see cref="IUnitOfWork"/> 时另校验 <c>IPalOutboxStore</c> 解析到同一实例、且拦截器
    /// 已挂到 DbContext options——错误装配在首个作用域解析时报错，而非静默丢事件。
    /// 若接入异构存储（独立 Dapper outbox 或跨库），不要使用本方法，须自行保证同事务。
    /// </para>
    /// <para>
    /// ⚠️ <b>拦截器注册（P0-FIX-4）</b>：此方法仅注册服务到 DI 容器。
    /// <b>调用方必须在 DbContext 配置中显式启用拦截器</b>：
    /// <code>
    /// builder.Services.AddDbContext&lt;AppDbContext&gt;((sp, options) =&gt;
    ///     options.UseNpgsql(cs).UsePalOutboxInterceptor(sp));
    /// </code>
    /// 或在 <c>OnConfiguring</c> 中 <c>optionsBuilder.UseApplicationServiceProvider(serviceProvider)</c>。
    /// 未挂接时解析 <see cref="IUnitOfWork"/> 会抛 <see cref="InvalidOperationException"/>。
    /// </para>
    /// <para>
    /// ✅ <b>可与 <c>AddDbContextPool</c> 组合（2026-10-21 修订）</b>：拦截器状态按
    /// <c>DbContext</c> 实例存放在 <c>ConditionalWeakTable</c> 中，不依赖 Scoped 独占。
    /// （原 ITM-640 的「禁止池化」禁令基于已删除的实例字段 <c>_pending</c>/<c>_injectedOutboxIds</c>，
    /// 与当前实现相反，已撤销。）
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPalOutboxUnitOfWork<TContext>(this IServiceCollection services)
        where TContext : DbContext, IPalOutboxStore
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IUnitOfWork>(sp =>
        {
            var context = sp.GetRequiredService<TContext>();
            ValidateOutboxWiring(context, sp);
            return new UnitOfWork<TContext>(context);
        });
        // F2（2026-10-21）：用工厂 + 延迟 store 解析构造拦截器——同上下文形态下
        // 构造期解析 IPalOutboxStore 会形成 DbContext options ↔ 拦截器 ↔ store 的循环。
        services.TryAddScoped<OutboxDomainEventInterceptor>(sp => new OutboxDomainEventInterceptor(
            sp,
            sp.GetRequiredService<IMessageSerializer>(),
            sp.GetRequiredService<IMessageCatalog>()));
        return services;
    }

    /// <summary>
    /// 在 <see cref="DbContextOptionsBuilder"/> 上注册 Outbox 领域事件拦截器。
    /// <para>用法：<c>services.AddDbContext&lt;MyDb&gt;((sp, opts) =&gt; opts.UseSqlite(cs).UsePalOutboxInterceptor(sp));</c></para>
    /// <para>
    /// ✅ 与 <c>AddDbContextPool</c> 兼容（2026-10-21 修订）：拦截器以 DbContext 实例为键
    /// 保存暂存状态，池化复用不交叉污染。原文档的「禁止池化」基于已删除的实例字段，已撤销。
    /// </para>
    /// </summary>
    public static DbContextOptionsBuilder UsePalOutboxInterceptor(
        this DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        optionsBuilder.AddInterceptors(serviceProvider.GetRequiredService<OutboxDomainEventInterceptor>());
        return optionsBuilder;
    }

    /// <summary>
    /// 解析期装配校验（F2，2026-10-21）：
    /// ① <see cref="IPalOutboxStore"/> 必须解析到同一个业务上下文实例；
    /// ② <see cref="OutboxDomainEventInterceptor"/> 必须已挂到该上下文的 options。
    /// 两项任一不满足即抛错——否则 SaveChanges 静默不写 outbox。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "EF Core adapter package is not AOT-compatible (IsAotCompatible=false); options introspection is runtime-only.")]
    private static void ValidateOutboxWiring(DbContext context, IServiceProvider serviceProvider)
    {
        var store = serviceProvider.GetService<IPalOutboxStore>();
        if (store is not null && !ReferenceEquals(store, context))
        {
            throw new InvalidOperationException(
                $"Pal Outbox wiring is invalid: IPalOutboxStore resolves to '{store.GetType().FullName}', "
                + $"but the unit of work uses '{context.GetType().FullName}'. Domain events tracked by the "
                + "interceptor would never be saved by the business context. Register the business "
                + "context itself as IPalOutboxStore (e.g. class AppDbContext : PostgreSqlOutboxDbContext).");
        }

        var core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();
        // CoreOptionsExtension.Interceptors 是 AddInterceptors 的落点（scoped 列表）；
        // SingletonInterceptors 只装 ISingletonInterceptor，本拦截器不实现该接口。
        var attached = core?.Interceptors?.Any(static i => i is OutboxDomainEventInterceptor) == true;
        if (!attached)
        {
            throw new InvalidOperationException(
                $"Pal Outbox wiring is invalid: {nameof(OutboxDomainEventInterceptor)} is not attached to "
                + $"'{context.GetType().FullName}'. Configure it with "
                + "'AddDbContext<T>((sp, o) => o.UseNpgsql(cs).UsePalOutboxInterceptor(sp))' or add it via "
                + "OnConfiguring/AddInterceptors. Without it, domain events are silently dropped.");
        }
    }
}
