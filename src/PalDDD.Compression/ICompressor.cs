// ─────────────────────────────────────────────────────────────
// 📦 ICompressor — 统一压缩/解压抽象接口
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Compression;

/// <summary>
/// 压缩器接口 — 统一的压缩/解压抽象。
/// </summary>
public interface ICompressor
{
    /// <summary>此压缩器对应的算法。</summary>
    CompressionAlgorithm Algorithm { get; }

    /// <summary>
    /// 压缩数据。
    /// </summary>
    /// <param name="data">待压缩数据。</param>
    /// <param name="level">压缩级别，默认 Balanced。</param>
    /// <returns>压缩后的数据。</returns>
    ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced);

    /// <summary>
    /// 解压数据。
    /// </summary>
    /// <param name="compressed">待解压数据。</param>
    /// <returns>解压后的原始数据。</returns>
    byte[] Decompress(ReadOnlySpan<byte> compressed);
}
