using NativeCompressions;

namespace PalDDD.Compression;

/// <summary>解压炸弹防护（P2 修复）：压缩输入体积上限——超限拒绝解压，防 OOM。</summary>
internal static class DecompressionGuard
{
    /// <summary>压缩输入安全上限（8MB）——合法消息负载压缩后极少超过此量级。</summary>
    internal const int MaxCompressedInputBytes = 8 * 1024 * 1024;

    /// <summary>解压输出安全上限（64MB，对齐 System 版）——超限抛 IOException。</summary>
    internal const int MaxDecompressedOutputBytes = 64 * 1024 * 1024;
}

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

        // 显式转 UIntPtr：NativeCompressions 0.6.1 的 LZ4.GetMaxCompressedLength(int)
        // 在 .NET 11 Preview 下因 int→UIntPtr 隐式转换触发重载解析 bug 导致栈溢出。
        // 直接用 UIntPtr 重载绕过包装层的递归。
        var maxSize = LZ4.GetMaxCompressedLength((nuint)data.Length);
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
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new System.IO.InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");
        var result = LZ4.Decompress(compressed);
        if (result.Length > DecompressionGuard.MaxDecompressedOutputBytes)
            throw new System.IO.InvalidDataException($"解压输出 {result.Length:N0} 字节超过安全上限（疑似解压炸弹）。");
        return result;
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

        var maxSize = Zstandard.GetMaxCompressedLength((nuint)data.Length);
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
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new System.IO.InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");
        var result = Zstandard.Decompress(compressed);
        if (result.Length > DecompressionGuard.MaxDecompressedOutputBytes)
            throw new System.IO.InvalidDataException($"解压输出 {result.Length:N0} 字节超过安全上限（疑似解压炸弹）。");
        return result;
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
    public CompressionAlgorithm // P2 定案（前向兼容声明）：本实现输出/输入为 Zstandard 字节格式，算法标识 OpenZL
        // 是历史命名。若未来接入"真 OpenZL"实现，旧数据的算法标识与字节格式不匹配
        // 将导致解压失败——持久化侧应同时记录格式版本。
        Algorithm => CompressionAlgorithm.OpenZL;

    public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced)
    {
        if (data.IsEmpty) return Array.Empty<byte>();

        // OpenZL 当前使用 Zstandard 作为底层实现，
        // 后续 NativeCompressions 版本会提供专用的 OpenZL API。
        var maxSize = Zstandard.GetMaxCompressedLength((nuint)data.Length);
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
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new System.IO.InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");
        var result = Zstandard.Decompress(compressed);
        if (result.Length > DecompressionGuard.MaxDecompressedOutputBytes)
            throw new System.IO.InvalidDataException($"解压输出 {result.Length:N0} 字节超过安全上限（疑似解压炸弹）。");
        return result;
    }

    private static int MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 1,
        CompressionLevel.Balanced => 3,
        CompressionLevel.SmallestSize => 19,
        _ => 3,
    };
}
