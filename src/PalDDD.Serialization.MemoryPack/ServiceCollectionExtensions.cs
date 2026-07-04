// ─────────────────────────────────────────────────────────────
// 🔧 DI 注册 — AddPalMemoryPackSerialization
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Serialization.MemoryPack;

/// <summary>MemoryPack 二进制序列化 DI 注册。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MemoryPack 二进制序列化器。<br/>
    /// 替换默认的 <see cref="IMessageSerializer"/> 为 <see cref="MemoryPackMessageSerializer"/>。<br/>
    /// 💡 与 <c>AddPalJsonSerialization()</c> 互斥——全局只有一个 IMessageSerializer Singleton。
    /// </summary>
    /// <param name="configureCatalog">消息目录配置回调</param>
    public static IServiceCollection AddPalMemoryPackSerialization(
        this IServiceCollection services,
        Action<MessageCatalogBuilder>? configureCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMessageCatalog>(_ =>
        {
            var builder = new MessageCatalogBuilder();
            configureCatalog?.Invoke(builder);
            return builder.Build();
        });
        services.TryAddSingleton<IMessageSerializer, MemoryPackMessageSerializer>();
        return services;
    }
}
