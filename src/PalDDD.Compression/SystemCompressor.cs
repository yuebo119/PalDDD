using System.IO.Compression;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// ⚙️ BrotliCompressor / GZipCompressor / DeflateCompressor — 内置压缩器
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Brotli 压缩器 — 基于 System.IO.Compression.BrotliStream。
/// </summary>
internal sealed class BrotliCompressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.Brotli;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        using var output = new MemoryStream();
        var quality = MapLevelToQuality(level);

        using (var encoder = new BrotliStream(output, new BrotliCompressionOptions { Quality = quality }))
        {
            encoder.Write(data);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return [];

        using var input = new MemoryStream(compressed.ToArray());
        using var output = new MemoryStream();
        using var decoder = new BrotliStream(input, CompressionMode.Decompress);

        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static int MapLevelToQuality(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 1,
        CompressionLevel.Balanced => 4,
        CompressionLevel.SmallestSize => 11,
        _ => 4,
    };
}

/// <summary>
/// GZip 压缩器 — 基于 System.IO.Compression.GZipStream。
/// </summary>
internal sealed class GZipCompressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.GZip;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        using var output = new MemoryStream();
        var sysLevel = MapLevel(level);

        using (var gzip = new GZipStream(output, sysLevel))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return [];

        using var input = new MemoryStream(compressed.ToArray());
        using var output = new MemoryStream();
        using var gzip = new GZipStream(input, CompressionMode.Decompress);

        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static System.IO.Compression.CompressionLevel MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
        CompressionLevel.Balanced => System.IO.Compression.CompressionLevel.Optimal,
        CompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
        _ => System.IO.Compression.CompressionLevel.Optimal,
    };
}

/// <summary>
/// Deflate 压缩器 — 基于 System.IO.Compression.DeflateStream。
/// </summary>
internal sealed class DeflateCompressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.Deflate;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        using var output = new MemoryStream();
        var sysLevel = MapLevel(level);

        using (var deflate = new DeflateStream(output, sysLevel))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return [];

        using var input = new MemoryStream(compressed.ToArray());
        using var output = new MemoryStream();
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);

        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static System.IO.Compression.CompressionLevel MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
        CompressionLevel.Balanced => System.IO.Compression.CompressionLevel.Optimal,
        CompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
        _ => System.IO.Compression.CompressionLevel.Optimal,
    };
}
