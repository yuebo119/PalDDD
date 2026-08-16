using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// 🏗️ NativeCompressionServiceCollectionExtensions — 原生压缩 DI 注册
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Pal.DDD 原生压缩服务的 DI 注册扩展。
/// </summary>
public static class NativeCompressionServiceCollectionExtensions
{
    /// <summary>
    /// 注册原生压缩器 — LZ4 / ZStandard / OpenZL。<br/>
    /// P3 修复（二十一轮）起本扩展自足：内部先调用
    /// <see cref="CompressionServiceCollectionExtensions.AddPalCompression"/>（TryAdd 幂等，
    /// 已注册时无副作用），无需调用方先行注册基础压缩层。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合（支持链式调用）。</returns>
    public static IServiceCollection AddPalCompressionNative(this IServiceCollection services)
    {
        // P3 修复（八轮评审）：补 null 防护——对齐 AddPalCompression
        ArgumentNullException.ThrowIfNull(services);

        // P3 修复（二十一轮）：扩展自足——此前要求调用方先调 AddPalCompression 否则
        // CompressionProvider（ICompressionProvider）未注册，运行时解析失败；本压缩器
        // 依赖基础层提供器。AddPalCompression 内部全为 TryAdd/TryAddEnumerable（幂等），
        // 先行调用无重复注册副作用。
        services.AddPalCompression();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, LZ4Compressor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, ZStandardCompressor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, OpenZLCompressor>());

        return services;
    }
}
