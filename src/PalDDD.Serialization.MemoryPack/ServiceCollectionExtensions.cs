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
    /// <b>替换语义</b>（P1-2 修复）：后注册者生效——直接 AddSingleton 覆盖先前注册的
    /// <see cref="IMessageSerializer"/>（MS DI 单解析取最后注册）。
    /// 💡 与 <c>AddPalJsonSerialization()</c> 按调用顺序决定最终生效者（后者覆盖前者）。
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
        // P1-2 修复：注释承诺"替换默认"，TryAdd 只在缺失时添加——先 Json 后 MemoryPack
        // 时静默不生效。改为 AddSingleton（后注册覆盖，兑现文档语义）。
        services.AddSingleton<IMessageSerializer, MemoryPackMessageSerializer>();
        return services;
    }
}
