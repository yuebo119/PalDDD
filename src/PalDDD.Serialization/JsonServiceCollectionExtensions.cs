// ─────────────────────────────────────────────────────────────
// 🔧 DI 注册 — AddPalSerialization 等
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Serialization.Json;

/// <summary>System.Text.Json 序列化 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 AOT-first JSON 消息序列化器。</summary>
    public static IServiceCollection AddPalJsonSerialization(
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
        services.TryAddSingleton<IMessageSerializer, JsonMessageSerializer>();
        return services;
    }
}
