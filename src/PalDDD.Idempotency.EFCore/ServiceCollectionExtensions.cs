// ─────────────────────────────────────────────────────────────
// 🔧 Idempotency.EFCore DI 注册扩展
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个扩展？
//   ｜ IdempotencyDbContext 实现 IIdempotencyStore，但此前包内无注册入口——
//   ｜ 调用方必须手动 AddDbContext + 手动把上下文映射到 IIdempotencyStore。
//   ｜ 本扩展补上与 Dapper/PalORM 栈对称的 AddPal* 入口。
//
// 📐 DDD 位置：基础设施层 — DI 注册是组合根的一部分，不涉及领域逻辑。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Idempotency;

/// <summary>EF Core 幂等存储适配器的 DI 注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 把派生自 <see cref="IdempotencyDbContext"/> 的上下文映射为 <see cref="IIdempotencyStore"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <typeparam name="TContext">派生自 <see cref="IdempotencyDbContext"/> 的上下文类型。</typeparam>
    /// <remarks>
    /// 本方法<b>不</b>注册 <typeparamref name="TContext"/> 本身（provider 选择权在调用方），
    /// 需与 <c>AddDbContext</c> 配套：
    /// <code>
    /// services.AddPalIdempotencyEfCore&lt;AppIdempotencyDbContext&gt;();
    /// services.AddDbContext&lt;AppIdempotencyDbContext&gt;((sp, o) =&gt; o.UseNpgsql(cs));
    /// </code>
    /// 生命周期为 Scoped，与 <c>AddDbContext</c> 默认一致（DbContext 非线程安全）。
    /// </remarks>
    public static IServiceCollection AddPalIdempotencyEfCore<TContext>(
        this IServiceCollection services)
        where TContext : IdempotencyDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IIdempotencyStore>(static sp => sp.GetRequiredService<TContext>());
        return services;
    }
}
