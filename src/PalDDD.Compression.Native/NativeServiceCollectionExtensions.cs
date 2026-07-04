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
    /// 注册原生压缩器 — LZ4 / ZStandard / OpenZL。
    /// 需要先调用 <see cref="CompressionServiceCollectionExtensions.AddPalCompression"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合（支持链式调用）。</returns>
    public static IServiceCollection AddPalCompressionNative(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, LZ4Compressor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, ZStandardCompressor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, OpenZLCompressor>());

        return services;
    }
}
