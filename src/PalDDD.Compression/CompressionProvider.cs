using System.Collections.Frozen;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// 🏗️ CompressionProvider — 默认压缩提供器
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 默认压缩提供器 — 从 DI 容器收集所有已注册的 <see cref="ICompressor"/>。
/// </summary>
internal sealed class CompressionProvider : ICompressionProvider
{
    private readonly FrozenDictionary<CompressionAlgorithm, ICompressor> _compressors;

    public CompressionProvider(IEnumerable<ICompressor> compressors)
    {
        ArgumentNullException.ThrowIfNull(compressors);

        // P3 修复（十七轮）：构造期重复算法检测——ToFrozenDictionary 对重复键抛出的
        // ArgumentException 不含算法名，排障困难；单遍 TryAdd 同时完成检测与构建，
        // 重复注册抛 NotSupportedException 带算法名，配置错误在启动期即定位
        Dictionary<CompressionAlgorithm, ICompressor> map = [];
        foreach (var compressor in compressors)
        {
            if (!map.TryAdd(compressor.Algorithm, compressor))
                throw new NotSupportedException(
                    $"Multiple compressors registered for algorithm '{compressor.Algorithm}'; " +
                    $"ensure only one compressor per algorithm is registered via DI.");
        }

        _compressors = map.ToFrozenDictionary();
    }

    /// <inheritdoc />
    public ICompressor GetCompressor(CompressionAlgorithm algorithm)
    {
        if (_compressors.TryGetValue(algorithm, out var compressor))
            return compressor;

        throw new NotSupportedException(
            $"Compression algorithm '{algorithm}' is not registered. " +
            $"Ensure the corresponding compressor implementation is added via DI.");
    }
}
