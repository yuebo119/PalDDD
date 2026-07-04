// ─────────────────────────────────────────────────────────────
// 🏷️ CompressionLevel — 压缩级别枚举
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Compression;

/// <summary>
/// 压缩级别 — 在速度与压缩比之间权衡。
/// </summary>
public enum CompressionLevel
{
    /// <summary>最快速度，压缩比最低。</summary>
    Fastest,

    /// <summary>速度与压缩比的平衡（默认）。</summary>
    Balanced,

    /// <summary>最小体积，速度最慢。</summary>
    SmallestSize,
}
