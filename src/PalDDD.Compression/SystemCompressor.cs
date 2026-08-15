using System.Buffers;
using System.IO.Compression;

namespace PalDDD.Compression;

/// <summary>解压炸弹防护（P2 修复）：压缩输入体积上限——超限拒绝解压，防 OOM。</summary>
internal static class DecompressionGuard
{
    /// <summary>压缩输入安全上限（8MB）——合法消息负载压缩后极少超过此量级。</summary>
    internal const int MaxCompressedInputBytes = 8 * 1024 * 1024;

    /// <summary>解压输出安全上限（64MB）——gzip 最大膨胀比约 1032:1，8MB 输入理论上可
    /// 膨胀至 8GB；64MB 覆盖合法消息负载解压后的量级，超限抛 IOException 防炸内存。</summary>
    internal const int MaxOutputBytes = 64 * 1024 * 1024;

    /// <summary>带上限的流拷贝——超限抛 IOException（防高膨胀率炸弹）。</summary>
    internal static void CopyWithLimit(Stream source, MemoryStream destination)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxOutputBytes)
                throw new IOException($"解压输出超过安全上限 {MaxOutputBytes:N0} 字节（疑似解压炸弹）。");
            destination.Write(buffer, 0, read);
        }
    }
}

// ─────────────────────────────────────────────────────────────
// ⚙️ BrotliCompressor / GZipCompressor / DeflateCompressor — 内置压缩器
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Brotli 压缩器 — 基于 BrotliEncoder/BrotliDecoder span 原语，无 Stream 包装分配。
/// </summary>
internal sealed class BrotliCompressor : ICompressor
{
    public CompressionAlgorithm Algorithm => CompressionAlgorithm.Brotli;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        // GetMaxCompressedLength 给出最坏上界，一次 TryCompress 完成，免 MemoryStream/BrotliStream 分配
        int maxLength = BrotliEncoder.GetMaxCompressedLength(data.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            if (!BrotliEncoder.TryCompress(data, rented, out int bytesWritten, MapLevelToQuality(level), 22))
                throw new InvalidOperationException("Brotli 压缩失败：目标缓冲区不足。");
            return rented.AsSpan(0, bytesWritten).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return [];
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");

        // span 直解：免输入 ToArray 拷贝，输出走增长缓冲，无 MemoryStream 分配
        using var decoder = new BrotliDecoder();
        var buffer = new ArrayBufferWriter<byte>();
        var source = compressed;
        long totalWritten = 0;

        while (true)
        {
            Span<byte> destination = buffer.GetSpan(Math.Max(4096, source.Length * 2));
            OperationStatus status = decoder.Decompress(source, destination, out int bytesConsumed, out int bytesWritten);
            buffer.Advance(bytesWritten);
            totalWritten += bytesWritten;
            source = source.Slice(bytesConsumed);

            // P1 修复（四轮评审）：Brotli 路径补输出上限——与 GZip/Deflate 的 CopyWithLimit
            // 对称（此前六轮修复遗漏此分支，PD17 命中）
            if (totalWritten > DecompressionGuard.MaxOutputBytes)
                throw new IOException($"Brotli 解压输出超过安全上限 {DecompressionGuard.MaxOutputBytes:N0} 字节（疑似解压炸弹）。");

            if (status == OperationStatus.Done) break;
            if (status == OperationStatus.DestinationTooSmall) continue;
            throw new InvalidDataException($"Brotli 解压失败：{status}");
        }

        return buffer.WrittenSpan.ToArray();
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
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");

        using var input = new MemoryStream(compressed.ToArray());
        using var output = new MemoryStream();
        using var gzip = new GZipStream(input, CompressionMode.Decompress);

        DecompressionGuard.CopyWithLimit(gzip, output); // P2 修复：输出上限防膨胀炸弹
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
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");

        using var input = new MemoryStream(compressed.ToArray());
        using var output = new MemoryStream();
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);

        DecompressionGuard.CopyWithLimit(deflate, output); // P2 修复：输出上限防膨胀炸弹
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
