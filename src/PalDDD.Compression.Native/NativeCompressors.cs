using NativeCompressions;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// ⚙️ LZ4Compressor / ZStandardCompressor / OpenZLCompressor — 原生压缩器
// ─────────────────────────────────────────────────────────────
// 解压炸弹防护常量（八轮评审 P3 单一事实源）：本程序集直接引用 PalDDD.Compression 的
// public DecompressionGuard 常量（此前存在双副本，MaxOutputBytes/MaxDecompressedOutputBytes
// 命名与值需人工对齐，有漂移风险）。
// ⚠️ 已知限制（六轮评审声明）：LZ4/ZStandard/OpenZL 的原生库 API 为一次性
// 全量分配——输出上限检查在 Decompress 返回后执行，恶意载荷可能在检查生效前触发
// 超大分配（LZ4 膨胀比约 255:1、Zstd 更高）。输入 8MB 上限是最有效的防线；
// 需要流式输出检查的场景请使用 SystemCompressor（Brotli/GZip/Deflate 均为逐块检查）。
// 此限制受外部库 API 设计约束，无法在适配层修复。

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
        // ITM-219 修复：OOM → 受控 InvalidDataException——NativeCompressions 内部全量分配，
        // 恶意载荷在输出检查生效前可能触发 OOM。catch 转为可处理的受控异常而非进程崩溃。
        byte[] result;
        try
        {
            result = LZ4.Decompress(compressed);
        }
        catch (OutOfMemoryException)
        {
            throw new System.IO.InvalidDataException(
                $"LZ4 解压输出超出可用内存（疑似解压炸弹，输入 {compressed.Length:N0} 字节）。");
        }
        if (result.Length > DecompressionGuard.MaxOutputBytes)
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
        // ITM-219：OOM → 受控 InvalidDataException（同 LZ4 路径）
        byte[] result;
        try
        {
            result = Zstandard.Decompress(compressed);
        }
        catch (OutOfMemoryException)
        {
            throw new System.IO.InvalidDataException(
                $"ZStandard 解压输出超出可用内存（疑似解压炸弹，输入 {compressed.Length:N0} 字节）。");
        }
        if (result.Length > DecompressionGuard.MaxOutputBytes)
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
        // ITM-219：OOM → 受控 InvalidDataException（同 LZ4 路径）
        byte[] result;
        try
        {
            result = Zstandard.Decompress(compressed);
        }
        catch (OutOfMemoryException)
        {
            throw new System.IO.InvalidDataException(
                $"ZStandard 解压输出超出可用内存（疑似解压炸弹，输入 {compressed.Length:N0} 字节）。");
        }
        if (result.Length > DecompressionGuard.MaxOutputBytes)
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
