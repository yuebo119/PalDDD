using System.Buffers;
using NativeCompressions;

namespace PalDDD.Compression;

// ─────────────────────────────────────────────────────────────
// ⚙️ LZ4Compressor / ZStandardCompressor / OpenZLCompressor — 原生压缩器
// ─────────────────────────────────────────────────────────────
// 解压炸弹防护常量（八轮评审 P3 单一事实源）：本程序集直接引用 PalDDD.Compression 的
// public DecompressionGuard 常量（此前存在双副本，MaxOutputBytes/MaxDecompressedOutputBytes
// 命名与值需人工对齐，有漂移风险）。
// 三个压缩器的 Decompress 均走流式 decoder（LZ4Decoder/ZstandardDecoder span 原语），
// 与 SystemCompressor 的 Brotli 路径同构：输出累计每轮即校验 MaxOutputBytes，
// 超限的下一轮立即中止——上限检查先于后续分配生效，而非全量解压完成之后。

// C6（2026-10-09 优化轮）：解压增长缓冲的线程槽池 DecompressBufferPool——三压缩器共用
// 一个 ThreadStatic 槽，免每调用 new ArrayBufferWriter 及其倍增分配。2026-10-11（EPSL
// 地毯审计 A-3）池上移至 PalDDD.Compression 程序集（Brotli 接入同一池，单一事实源），
// 本程序集经 InternalsVisibleTo 消费，调用点零变化。权衡与约束声明见池类头注。

/// <summary>
/// R1（C-F6，2026-10-14 性能实施轮）：压缩 options 三档静态缓存——record 的
/// <c>with</c> 每次调用分配新实例（~100-200B/次），级别枚举仅三档、Default 基准恒定，
/// 构造期定格三份后按档索引取用。record 不可变 ⇒ 跨线程/跨调用复用安全。
/// LZ4 与 ZStandard（含 OpenZL 底层同款）两组共六份。
/// </summary>
internal static class NativeCompressionOptionsCache
{
    public static readonly LZ4CompressionOptions Lz4Fastest = LZ4CompressionOptions.Default with { CompressionLevel = 0 };
    public static readonly LZ4CompressionOptions Lz4Balanced = LZ4CompressionOptions.Default with { CompressionLevel = 6 };
    public static readonly LZ4CompressionOptions Lz4SmallestSize = LZ4CompressionOptions.Default with { CompressionLevel = 12 };

    public static readonly ZstandardCompressionOptions ZstdFastest = ZstandardCompressionOptions.Default with { CompressionLevel = 1 };
    public static readonly ZstandardCompressionOptions ZstdBalanced = ZstandardCompressionOptions.Default with { CompressionLevel = 3 };
    public static readonly ZstandardCompressionOptions ZstdSmallestSize = ZstandardCompressionOptions.Default with { CompressionLevel = 19 };
}

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
        // 直接用 UIntPtr 重载绕过包装层的递归。1.0.1 升位复核（2026-10-08）：int 重载仍在，
        // 显式 UIntPtr 限定维持直连原生重载路径；往返与守卫测试面板覆盖。
        var maxSize = (int)LZ4.GetMaxCompressedLength((nuint)data.Length);
        // C6（2026-10-09 优化轮）：scratch 改 ArrayPool 租借——原每调用 new byte[maxSize]
        // 为压缩分配大头（16KB 输入实测 16.8KB/op）；v13 裁剪拷贝决策保留（返回值仍为
        // 精确尺寸的独立数组），仅 scratch 不再每次分配
        var destination = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            // R1（C-F6）：options 三档静态缓存（原 with 每调用分配）
            var options = level switch
            {
                CompressionLevel.Fastest => NativeCompressionOptionsCache.Lz4Fastest,
                CompressionLevel.SmallestSize => NativeCompressionOptionsCache.Lz4SmallestSize,
                _ => NativeCompressionOptionsCache.Lz4Balanced,
            };

            var written = LZ4.Compress(data, destination, options);
            return destination.AsSpan(0, written).ToArray(); // v13：裁剪拷贝对齐 BrotliCompressor——ReadOnlyMemory 会持住整个 maxSize 超分配数组（8MB 输入 LZ4 上界 ~8MB+ 的 LoH 驻留）
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination);
        }
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return Array.Empty<byte>();
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new System.IO.InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");

        // 流式 span 直解（形态对齐同库 BrotliCompressor.Decompress）：免输入 ToArray 拷贝，
        // 输出走增长缓冲；每轮 Advance 后即校验上限，超限立即中止——不再等全量分配完成。
        // ITM-219 修复保留：单轮缓冲增长仍可能触发 OOM，catch 转为可处理的受控异常而非进程崩溃。
        // v38 P3：补 ex 内层异常保根因堆栈（ITM-219 只声明行为权衡未覆盖可诊断性）。
        try
        {
            using var decoder = new LZ4Decoder();
            var buffer = DecompressBufferPool.Rent(); // C6（2026-10-09 优化轮）：线程槽复用，见 DecompressBufferPool 头注
            var source = compressed;
            long totalWritten = 0;

            while (true)
            {
                Span<byte> destination = buffer.GetSpan(Math.Max(4096, source.Length * 2));
                OperationStatus status = decoder.Decompress(source, destination, out int bytesConsumed, out int bytesWritten);
                buffer.Advance(bytesWritten);
                totalWritten += bytesWritten;
                source = source.Slice(bytesConsumed);

                if (totalWritten > DecompressionGuard.MaxOutputBytes)
                    throw new System.IO.InvalidDataException($"LZ4 解压输出超过安全上限 {DecompressionGuard.MaxOutputBytes:N0} 字节（疑似解压炸弹）。");

                if (status == OperationStatus.Done) break;
                if (status == OperationStatus.DestinationTooSmall) continue;
                throw new System.IO.InvalidDataException($"LZ4 解压失败：{status}");
            }

            return buffer.WrittenSpan.ToArray();
        }
        catch (OutOfMemoryException ex)
        {
            throw new System.IO.InvalidDataException(
                $"LZ4 解压输出超出可用内存（疑似解压炸弹，输入 {compressed.Length:N0} 字节）。", ex);
        }
    }
    // R1（C-F6）：MapLevel 已删——级别映射并入 NativeCompressionOptionsCache 三档静态缓存
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

        var maxSize = (int)Zstandard.GetMaxCompressedLength((nuint)data.Length);
        // C6（2026-10-09 优化轮）：scratch 改 ArrayPool 租借（同 LZ4 路径，v13 裁剪决策保留）
        var destination = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            // R1（C-F6）：options 三档静态缓存（原 with 每调用分配）
            var options = level switch
            {
                CompressionLevel.Fastest => NativeCompressionOptionsCache.ZstdFastest,
                CompressionLevel.SmallestSize => NativeCompressionOptionsCache.ZstdSmallestSize,
                _ => NativeCompressionOptionsCache.ZstdBalanced,
            };

            var written = Zstandard.Compress(data, destination, options);
            return destination.AsSpan(0, written).ToArray(); // v13：裁剪拷贝对齐 BrotliCompressor——ReadOnlyMemory 会持住整个 maxSize 超分配数组（8MB 输入 ZStd 上界 ~8MB+ 的 LoH 驻留）
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination);
        }
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return Array.Empty<byte>();
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new System.IO.InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");

        // 流式 span 直解（形态对齐同库 BrotliCompressor.Decompress）：每轮 Advance 后即校验上限，
        // 超限立即中止——不再等全量分配完成。ITM-219：OOM → 受控异常（同 LZ4 路径）。
        // v38 P3：补 ex 内层异常保根因堆栈（ITM-219 只声明行为权衡未覆盖可诊断性）。
        // C6（2026-10-09 优化轮）：增长缓冲改 DecompressBufferPool 线程槽复用（原每调用
        // new ArrayBufferWriter 及倍增序列为解压分配大头）；异常残留由下一次 Rent 的 Clear 清除
        try
        {
            using var decoder = new ZstandardDecoder();
            var buffer = DecompressBufferPool.Rent();
            var source = compressed;
            long totalWritten = 0;

            while (true)
            {
                Span<byte> destination = buffer.GetSpan(Math.Max(4096, source.Length * 2));
                OperationStatus status = decoder.Decompress(source, destination, out int bytesConsumed, out int bytesWritten);
                buffer.Advance(bytesWritten);
                totalWritten += bytesWritten;
                source = source.Slice(bytesConsumed);

                if (totalWritten > DecompressionGuard.MaxOutputBytes)
                    throw new System.IO.InvalidDataException($"ZStandard 解压输出超过安全上限 {DecompressionGuard.MaxOutputBytes:N0} 字节（疑似解压炸弹）。");

                if (status == OperationStatus.Done) break;
                if (status == OperationStatus.DestinationTooSmall) continue;
                throw new System.IO.InvalidDataException($"ZStandard 解压失败：{status}");
            }

            return buffer.WrittenSpan.ToArray();
        }
        catch (OutOfMemoryException ex)
        {
            throw new System.IO.InvalidDataException(
                $"ZStandard 解压输出超出可用内存（疑似解压炸弹，输入 {compressed.Length:N0} 字节）。", ex);
        }
    }
    // R1（C-F6）：MapLevel 已删——级别映射并入 NativeCompressionOptionsCache 三档静态缓存
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
        var maxSize = (int)Zstandard.GetMaxCompressedLength((nuint)data.Length);
        // C6（2026-10-09 优化轮）：scratch 改 ArrayPool 租借（同 ZStandard 路径，v13 裁剪决策保留）
        var destination = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            // R1（C-F6）：options 三档静态缓存（原 with 每调用分配；OpenZL 底层同 Zstandard）
            var options = level switch
            {
                CompressionLevel.Fastest => NativeCompressionOptionsCache.ZstdFastest,
                CompressionLevel.SmallestSize => NativeCompressionOptionsCache.ZstdSmallestSize,
                _ => NativeCompressionOptionsCache.ZstdBalanced,
            };

            var written = Zstandard.Compress(data, destination, options);
            return destination.AsSpan(0, written).ToArray(); // v13：裁剪拷贝对齐 BrotliCompressor——ReadOnlyMemory 会持住整个 maxSize 超分配数组（ZStd/OpenZL 同为 Zstd 字节格式）
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination);
        }
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return Array.Empty<byte>();
        // P2 修复（解压炸弹防护）：超限输入拒绝解压——恶意/损坏数据可膨胀千倍致 OOM
        if (compressed.Length > DecompressionGuard.MaxCompressedInputBytes)
            throw new System.IO.InvalidDataException(
                $"压缩输入 {compressed.Length:N0} 字节超过安全上限 {DecompressionGuard.MaxCompressedInputBytes:N0} 字节（疑似解压炸弹）。");

        // 流式 span 直解（形态对齐同库 BrotliCompressor.Decompress）：每轮 Advance 后即校验上限，
        // 超限立即中止——不再等全量分配完成。ITM-219：OOM → 受控异常（同 LZ4 路径）。
        // v38 P3：补 ex 内层异常保根因堆栈（ITM-219 只声明行为权衡未覆盖可诊断性）。
        try
        {
            using var decoder = new ZstandardDecoder();
            var buffer = DecompressBufferPool.Rent(); // C6（2026-10-09 优化轮）：线程槽复用，见 DecompressBufferPool 头注
            var source = compressed;
            long totalWritten = 0;

            while (true)
            {
                Span<byte> destination = buffer.GetSpan(Math.Max(4096, source.Length * 2));
                OperationStatus status = decoder.Decompress(source, destination, out int bytesConsumed, out int bytesWritten);
                buffer.Advance(bytesWritten);
                totalWritten += bytesWritten;
                source = source.Slice(bytesConsumed);

                if (totalWritten > DecompressionGuard.MaxOutputBytes)
                    throw new System.IO.InvalidDataException($"OpenZL 解压输出超过安全上限 {DecompressionGuard.MaxOutputBytes:N0} 字节（疑似解压炸弹）。");

                if (status == OperationStatus.Done) break;
                if (status == OperationStatus.DestinationTooSmall) continue;
                throw new System.IO.InvalidDataException($"OpenZL 解压失败：{status}");
            }

            return buffer.WrittenSpan.ToArray();
        }
        catch (OutOfMemoryException ex)
        {
            // v39 P3 勘正：消息误写"ZStandard"（自 ZStandardCompressor 复制残留）——
            // 本类算法标识为 OpenZL（底层虽为 Zstd 实现，排障按算法标识检索），勘正为 OpenZL
            throw new System.IO.InvalidDataException(
                $"OpenZL 解压输出超出可用内存（疑似解压炸弹，输入 {compressed.Length:N0} 字节）。", ex);
        }
    }
    // R1（C-F6）：MapLevel 已删——级别映射并入 NativeCompressionOptionsCache 三档静态缓存
}
