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
        // P2 修复（对称）：MemoryPack 已改 AddSingleton（替换语义），Json 也同步——
        // 双向"后者覆盖前者"承诺成立（此前先 MemoryPack 后 Json 时 Json 静默不生效）
        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        return services;
    }
}
