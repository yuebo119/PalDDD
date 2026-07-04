// ─────────────────────────────────────────────────────────────
// 📐 ICompressionProvider — 压缩提供器接口
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Compression;

/// <summary>
/// 压缩提供器接口 — 根据算法获取对应的 <see cref="ICompressor"/> 实例。
/// </summary>
public interface ICompressionProvider
{
    /// <summary>
    /// 获取指定算法的压缩器。
    /// </summary>
    /// <param name="algorithm">压缩算法。</param>
    /// <returns>对应的压缩器实例。</returns>
    /// <exception cref="NotSupportedException">未注册指定算法的压缩器。</exception>
    ICompressor GetCompressor(CompressionAlgorithm algorithm);
}
