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
        _compressors = compressors.ToFrozenDictionary(c => c.Algorithm);
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
