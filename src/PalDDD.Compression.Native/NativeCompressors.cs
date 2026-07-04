using NativeCompressions;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// ⚙️ LZ4Compressor / ZStandardCompressor / OpenZLCompressor — 原生压缩器
// ─────────────────────────────────────────────────────────────

/// <summary>
/// LZ4 压缩器 — 基于 NativeCompressions (Cysharp) 的原生绑定。
/// </summary>
internal sealed class LZ4Compressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.LZ4;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        var maxSize = LZ4.GetMaxCompressedLength(data.Length);
        var destination = new byte[maxSize];

        var options = LZ4CompressionOptions.Default with
        {
            CompressionLevel = MapLevel(level),
        };

        var written = LZ4.Compress(data, destination, options);
        return new ReadOnlyMemory<byte>(destination, 0, written);
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return Array.Empty<byte>();
        return LZ4.Decompress(compressed);
    }

    private static int MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 0,
        CompressionLevel.Balanced => 6,
        CompressionLevel.SmallestSize => 12,
        _ => 6,
    };
}

/// <summary>
/// ZStandard 压缩器 — 基于 NativeCompressions (Cysharp) 的原生绑定。
/// </summary>
internal sealed class ZStandardCompressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.ZStandard;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        var maxSize = Zstandard.GetMaxCompressedLength(data.Length);
        var destination = new byte[maxSize];

        var options = ZstandardCompressionOptions.Default with
        {
            CompressionLevel = MapLevel(level),
        };

        var written = Zstandard.Compress(data, destination, options);
        return new ReadOnlyMemory<byte>(destination, 0, written);
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return Array.Empty<byte>();
        return Zstandard.Decompress(compressed);
    }

    private static int MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 1,
        CompressionLevel.Balanced => 3,
        CompressionLevel.SmallestSize => 19,
        _ => 3,
    };
}

/// <summary>
/// OpenZL 压缩器 — 实验性 ZStandard 占位实现。
/// </summary>
/// <remarks>
/// ⚠️ <b>实验性</b>：当前使用 Zstandard 作为底层实现（通过 NativeCompressions.Cysharp），
/// 未来将对接 OpenZL 原生 API（Facebook 2025 年发布的新一代压缩框架）。
/// 此实现仅供早期评估和 API 设计反馈，生产环境请使用 <see cref="ZStandardCompressor"/>。
/// </remarks>
internal sealed class OpenZLCompressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.OpenZL;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        // OpenZL 当前使用 Zstandard 作为底层实现，
        // 后续 NativeCompressions 版本会提供专用的 OpenZL API。
        var maxSize = Zstandard.GetMaxCompressedLength(data.Length);
        var destination = new byte[maxSize];

        var options = ZstandardCompressionOptions.Default with
        {
            CompressionLevel = MapLevel(level),
        };

        var written = Zstandard.Compress(data, destination, options);
        return new ReadOnlyMemory<byte>(destination, 0, written);
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return Array.Empty<byte>();
        return Zstandard.Decompress(compressed);
    }

    private static int MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 1,
        CompressionLevel.Balanced => 3,
        CompressionLevel.SmallestSize => 19,
        _ => 3,
    };
}
