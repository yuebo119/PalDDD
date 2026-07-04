// ─────────────────────────────────────────────────────────────
// 🏷️ CompressionAlgorithm — 支持的压缩算法枚举
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Compression;

/// <summary>
/// 支持的压缩算法。
/// </summary>
public enum CompressionAlgorithm
{
    /// <summary>LZ4 — 极速压缩/解压，适合热路径。</summary>
    LZ4,

    /// <summary>ZStandard — 高压缩比与速度的平衡。</summary>
    ZStandard,

    /// <summary>OpenZL — Facebook 开源的新一代压缩框架（实验性）。</summary>
    OpenZL,

    /// <summary>Brotli — 高压缩比，适合静态资源和网络传输。</summary>
    Brotli,

    /// <summary>GZip — 通用兼容格式。</summary>
    GZip,

    /// <summary>Deflate — 原始 DEFLATE 压缩（无 gzip 头部）。</summary>
    Deflate,
}
