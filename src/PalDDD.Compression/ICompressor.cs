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
    /// <remarks>v53 P3 联动约束声明：本方法对输入/输出均无上限；而 <see cref="Decompress"/>
    /// 侧有 <c>DecompressionGuard.MaxCompressedInputBytes</c>（8MB）解压上限——压缩产出
    /// 超过该上限的字节（如 100MB JSON 压缩后仍 15MB）将被本框架的解压侧拒绝
    ///（InvalidDataException"疑似解压炸弹"）。消息负载量级有界的框架场景下为预期行为。</remarks>
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
