// ─────────────────────────────────────────────────────────────
// 🔧 EventLog.EFCore DI 注册扩展
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个扩展？
//   ｜ EventLogPositionReserver 是 Hi/Lo 全局位置分配器，其价值完全依赖
//   ｜ 「进程内 chunk 缓存被所有 DbContext 共享」。EventLogDbContext 构造器的
//   ｜ positionReserver 参数默认为 null → 每实例 new 一个，Scoped 生命周期下
//   ｜ chunk 缓存永不跨请求共享，每次 append 都走 allocator 行 SELECT+CAS
//   ｜ UPDATE（比直接取数据库序列更慢），且每次 append 消耗整个 chunk 的位置
//   ｜ （位置密度按 chunkSize 倍稀疏化）。
//   ｜ 此前仓库没有提供注册入口，用户只能自行发明注册方式而文档从未提及——
//   ｜ 本扩展把「Singleton 是注入前提」变成可一键做到的事实。
//
// 📐 DDD 位置：基础设施层 — DI 注册是组合根的一部分，不涉及领域逻辑。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.EventLog;

/// <summary>EF Core 事件日志适配器的 DI 注册入口。</summary>
/// <remarks>
/// ⚠️ <b>派生上下文必须显式接收并转发 reserver</b>（否则每实例新建，Hi/Lo 优化归零）：
/// <code>
/// public sealed class AppEventLogDbContext(
///     DbContextOptions&lt;AppEventLogDbContext&gt; options,
///     EventLogPositionReserver reserver)
///     : EventLogDbContext(options, positionReserver: reserver);
/// </code>
/// EF Core 经 <c>ActivatorUtilities</c> 从 DI 解析构造参数，故注册为 Singleton 后
/// 所有请求上下文共享同一 chunk 缓存。
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 把 <see cref="EventLogPositionReserver"/> 注册为 <b>Singleton</b>（Hi/Lo chunk 缓存的注入前提）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="chunkSize">
    /// 每次持久化分配的位置数量（默认 100）。值越大数据库往返越少，但进程崩溃时的潜在间隙也越多
    /// （间隙不影响正确性，events 表唯一约束兜底）。
    /// </param>
    /// <typeparam name="TContext">派生自 <see cref="EventLogDbContext"/> 的上下文类型。</typeparam>
    /// <remarks>
    /// 本方法<b>不</b>注册 <typeparamref name="TContext"/> 本身（provider 选择权在调用方），
    /// 需与 <c>AddDbContext</c> 配套：
    /// <code>
    /// services.AddPalEventLogEfCore&lt;AppEventLogDbContext&gt;();
    /// services.AddDbContext&lt;AppEventLogDbContext&gt;((sp, o) =&gt; o.UseNpgsql(cs));
    /// </code>
    /// 已注册时 <c>TryAdd</c> 语义不覆盖调用方自己的注册（例如要调 chunkSize 或换测试替身）。
    /// </remarks>
    public static IServiceCollection AddPalEventLogEfCore<TContext>(
        this IServiceCollection services,
        int chunkSize = 100)
        where TContext : EventLogDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        services.TryAddSingleton(new EventLogPositionReserver(chunkSize));
        return services;
    }
}
