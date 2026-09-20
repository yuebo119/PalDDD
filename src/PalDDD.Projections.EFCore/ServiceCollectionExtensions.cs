// ─────────────────────────────────────────────────────────────
// 🔧 Projections.EFCore DI 注册扩展
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个扩展？
//   ｜ ProjectionCheckpointDbContext 实现 IProjectionCheckpointStore，但此前包内无
//   ｜ 注册入口——调用方必须手动 AddDbContext + 手动映射接口。本扩展补上与
//   ｜ Dapper/PalORM 栈对称的 AddPal* 入口。
//
// 📐 DDD 位置：基础设施层 — DI 注册是组合根的一部分，不涉及领域逻辑。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Projections;

/// <summary>EF Core 投影检查点存储适配器的 DI 注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 把派生自 <see cref="ProjectionCheckpointDbContext"/> 的上下文映射为 <see cref="IProjectionCheckpointStore"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <typeparam name="TContext">派生自 <see cref="ProjectionCheckpointDbContext"/> 的上下文类型。</typeparam>
    /// <remarks>
    /// 本方法<b>不</b>注册 <typeparamref name="TContext"/> 本身（provider 选择权在调用方），
    /// 需与 <c>AddDbContext</c> 配套：
    /// <code>
    /// services.AddPalProjectionsEfCore&lt;AppProjectionDbContext&gt;();
    /// services.AddDbContext&lt;AppProjectionDbContext&gt;((sp, o) =&gt; o.UseNpgsql(cs));
    /// </code>
    /// 生命周期为 Scoped，与 <c>AddDbContext</c> 默认一致（DbContext 非线程安全）。
    /// </remarks>
    public static IServiceCollection AddPalProjectionsEfCore<TContext>(
        this IServiceCollection services)
        where TContext : ProjectionCheckpointDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IProjectionCheckpointStore>(static sp => sp.GetRequiredService<TContext>());
        return services;
    }
}
