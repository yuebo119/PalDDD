// ─────────────────────────────────────────────────────────────
// 🔧 DI 注册 — AddPalSerialization 等
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace PalDDD.Serialization.Json;

/// <summary>System.Text.Json 序列化 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 AOT-first JSON 消息序列化器。</summary>
    /// <remarks>
    /// ⚠️ v36 P3 存疑挂账声明（DI 构造选择漂移）：<see cref="JsonMessageSerializer"/> 有两个
    /// 构造——单参 catalog 路径（<c>(JsonTypeInfo&lt;TMessage&gt;)descriptor.JsonTypeInfo</c>
    /// 强制转换获取 metadata）与双参 options 路径（<c>options.GetTypeInfo&lt;TMessage&gt;()</c>
    /// 强类型零装箱）。本方法仅注册 <see cref="IMessageCatalog"/> 与 <see cref="IMessageSerializer"/>，
    /// MS DI 默认激活按"可解析参数最多的构造"挑选——容器中已注册
    /// <see cref="JsonSerializerOptions"/> 时将静默选中 options 构造（形状分叉），且"后注册
    /// options"同样改变形状（构造选择在解析时点决策，非注册时点）。
    /// 需要 catalog 路径的用户：避免全局注册 JsonSerializerOptions，或显式固定构造——
    /// <c>services.AddSingleton&lt;IMessageSerializer&gt;(sp => new JsonMessageSerializer(
    /// sp.GetRequiredService&lt;IMessageCatalog&gt;()))</c>。
    /// 行为探针（锁定双分支选择语义）留后续验证轮。
    /// </remarks>
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
