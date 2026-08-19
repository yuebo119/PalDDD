// ─────────────────────────────────────────────────────────────
// 🔧 DI 注册 — AddPalSerialization 等
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;

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

        // P3 声明（十八轮验证轮 C）：替换语义的必然推论——用户自定义 IMessageCatalog 会被
        // 后调的 AddPalJsonSerialization/MemoryPack 覆盖；自定义 catalog 必须在框架扩展【之后】注册。
        // P1 修复（十七轮）：catalog 改 AddSingleton 兑现双向"后者覆盖前者"承诺——
        // 此前 TryAdd 使 MemoryPack→Json 注册顺序下 catalog 留在 MemoryPack 版而
        // 序列化器换成 Json，Json 侧 configureCatalog 被静默丢弃（运行时远端抛
        // "not registered"）。与 MemoryPack 侧目录对称（双侧均为替换语义）。
        services.AddSingleton<IMessageCatalog>(_ =>
        {
            var builder = new MessageCatalogBuilder();
            configureCatalog?.Invoke(builder);
            return builder.Build();
        });
        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        return services;
    }
}
