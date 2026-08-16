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
    /// 💡 与 <c>AddPalJsonSerialization()</c> 按调用顺序决定最终生效者（后者覆盖前者）。<br/>
    /// 📐 <b>自定义 catalog 顺序契约（P3 修复·二十一轮，镜像 Json 侧十八轮声明）</b>：
    /// 替换语义的必然推论——用户自定义 <see cref="IMessageCatalog"/> 会被后调的
    /// <c>AddPalJsonSerialization()</c>/本扩展覆盖；自定义 catalog 必须在框架扩展【之后】注册。
    /// </summary>
    /// <param name="configureCatalog">消息目录配置回调</param>
    public static IServiceCollection AddPalMemoryPackSerialization(
        this IServiceCollection services,
        Action<MessageCatalogBuilder>? configureCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // P2 修复（目录对称）：修复前 serializer 用 AddSingleton（替换语义）而 catalog 用
        // TryAdd（仅缺失时添加）——不对称导致先 Json 后 MemoryPack 时序列化器换了但
        // catalog 还是 Json 扩展注册的旧目录，两个配置回调中后者被静默丢弃。
        // 十七轮起双侧均为 AddSingleton：目录与序列化器同为替换语义（后注册者生效），
        // 与 Json 侧目录对称（此为修复时点标记，非当前代码形态）。
        services.AddSingleton<IMessageCatalog>(_ =>
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
