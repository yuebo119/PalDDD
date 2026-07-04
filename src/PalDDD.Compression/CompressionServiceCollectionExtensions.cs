using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// 🏗️ CompressionServiceCollectionExtensions — 压缩服务 DI 注册
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Pal.DDD 压缩服务的 DI 注册扩展。
/// </summary>
public static class CompressionServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Pal.DDD 压缩抽象层，包含内置的 Brotli / GZip / Deflate 压缩器。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合（支持链式调用）。</returns>
    public static IServiceCollection AddPalCompression(this IServiceCollection services)
    {
        // 注册压缩提供器（单例）
        services.TryAddSingleton<ICompressionProvider, CompressionProvider>();

        // 注册内置压缩器（以可枚举方式注入 CompressionProvider）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, BrotliCompressor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, GZipCompressor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompressor, DeflateCompressor>());

        return services;
    }
}
