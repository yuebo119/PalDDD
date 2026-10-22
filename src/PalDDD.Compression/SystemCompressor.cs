using System.Buffers;
using System.IO.Compression;

namespace PalDDD.Compression;

/// <summary>
/// 解压炸弹防护（P2 修复）：压缩输入体积上限——超限拒绝解压，防 OOM。
/// <para>📐 <b>跨程序集单一事实源（八轮评审 P3）</b>：上限常量为 public——
/// PalDDD.Compression.Native 的同名防护引用本类（此前两副本各自维护，存在漂移风险）。
/// <see cref="CopyWithLimit"/> 仍为 internal（仅 System 压缩器内部使用）。</para>
/// </summary>
public static class DecompressionGuard
{
    /// <summary>压缩输入安全上限（8MB）——合法消息负载压缩后极少超过此量级。</summary>
    public const int MaxCompressedInputBytes = 8 * 1024 * 1024;

    /// <summary>解压输出安全上限（64MB）——gzip 最大膨胀比约 1032:1，8MB 输入理论上可
    /// 膨胀至 8GB；64MB 覆盖合法消息负载解压后的量级，超限抛 InvalidDataException 防炸内存（v72 勘正——v70 统一后 doc 未同步）。</summary>
    public const int MaxOutputBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 解压增长缓冲的首轮 GetSpan 提交上限（R9/P2-2，2026-10-14 性能实施轮）——
    /// 原首轮提示 <c>max(4096, len*2)</c> 在超大压缩输入下按 2× 立即提交（8MB 输入首轮
    /// 16MB），而实际解压比可能远低于 2×，过量提交随 DecompressBufferPool 线程槽的
    /// 历史峰值驻留固化（长驻线程数 × 峰值）。改为首轮提交 ≤ 64KB、后续由 ArrayBufferWriter
    /// 几何增长按需扩容（DestinationTooSmall 循环自然承接）——峰值容量收敛为「≥ 实际输出
    /// 的最近倍增」，内存占用从 2×输入 上界改为 输出量级上界。
    /// </summary>
    public const int InitialBlockHintBytes = 64 * 1024;

    /// <summary>带上限的流拷贝——超限抛 InvalidDataException（防高膨胀率炸弹；v72 同步 v70 统一）。</summary>
    internal static void CopyWithLimit(Stream source, MemoryStream destination)
    {
        // 优化（二十四轮特性扫描 OP-1）：80KB 拷贝缓冲池化——GZip/Deflate 解压共用此路径，
        // 每次调用省 ~80KB 堆分配（Brotli 走 span 原语无需本方法）。模式对齐同文件 Compress 的 Rent/Return。
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxOutputBytes)
                    throw new System.IO.InvalidDataException($"解压输出超过安全上限 {MaxOutputBytes:N0} 字节（疑似解压炸弹）。");
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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

        // span 直解：免输入 ToArray 拷贝，输出走增长缓冲（DecompressBufferPool 线程槽，
        // EPSL 地毯审计 2026-10-11 A-3 接入——与 LZ4/ZStd 的 C6 形态对齐），无 MemoryStream 分配
        using var decoder = new BrotliDecoder();
        var buffer = DecompressBufferPool.Rent();
        try
        {
            var source = compressed;
            long totalWritten = 0;

            while (true)
            {
                // R9/P2-2：首轮提示上限 64KB（原 max(4096, len*2) 在超大输入下 2× 过量提交，
                // 驻留随线程槽峰值固化）——后续按需几何增长
                Span<byte> destination = buffer.GetSpan(Math.Min(DecompressionGuard.InitialBlockHintBytes, Math.Max(4096, source.Length * 2)));
                OperationStatus status = decoder.Decompress(source, destination, out int bytesConsumed, out int bytesWritten);
                buffer.Advance(bytesWritten);
                totalWritten += bytesWritten;
                source = source.Slice(bytesConsumed);

                // P1 修复（四轮评审）：Brotli 路径补输出上限——与 GZip/Deflate 的 CopyWithLimit
                // 对称（此前六轮修复遗漏此分支，PD17 命中）
                if (totalWritten > DecompressionGuard.MaxOutputBytes)
                    throw new System.IO.InvalidDataException($"Brotli 解压输出超过安全上限 {DecompressionGuard.MaxOutputBytes:N0} 字节（疑似解压炸弹）。");

                if (status == OperationStatus.Done) break;
                if (status == OperationStatus.DestinationTooSmall) continue;
                throw new InvalidDataException($"Brotli 解压失败：{status}");
            }

            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            // R12 C-17：成功/炸弹异常都立即归还大缓冲（超阈值弃槽），不等到下次 Rent。
            DecompressBufferPool.Release(buffer);
        }
    }

    private static int MapLevelToQuality(CompressionLevel level) => CompressionLevelMap.ToBrotliQuality(level);
}

/// <summary>
/// B5（S1 轮，2026-10-15）：GZip/Deflate Stream 路径的 TLS MemoryStream 池。BCL 无
/// gzip/deflate 的 span 编码器（Brotli 才有 BrotliEncoder），Stream 形态不可避免，但
/// Stream 对象可复用：原每次 new MemoryStream 从 0 倍增到输出尺寸（累计约 2× 输出的
/// 重分配拷贝，随机载荷实测腿见 GZipStreamAllocationProbeTests），复用后稳态零倍增。
/// 驻留按历史峰值、超 <see cref="MaxResidentStreamBytes"/> 弃槽重建（对齐 R9 的 TLS
/// 池化族与 JsonMessageSerializer 的 MaxResidentBufferBytes 取舍）。<br/>
/// 调用契约：同步方法内 Rent→使用→（不显式归还，槽即持有），同线程同步段内完成、
/// 无跨调用交错（压缩/解压均为同步方法）；压缩流须以 leaveOpen:true 构造，否则
/// Dispose 会连带销毁池化流。三个槽位（压缩输出/解压输入/解压输出）相互独立。
/// </summary>
internal static class TlsStreamPool
{
    private const int MaxResidentStreamBytes = 256 * 1024;

    [ThreadStatic]
    private static MemoryStream? _compressOutput;

    [ThreadStatic]
    private static MemoryStream? _decompressInput;

    [ThreadStatic]
    private static MemoryStream? _decompressOutput;

    /// <summary>压缩输出槽（容量按需增长；Rent 后长度 0、位置 0）。</summary>
    public static MemoryStream RentCompressOutput() => Rent(ref _compressOutput, 0);

    /// <summary>解压输入槽（容量保证 ≥ minCapacity——v53 的预设容量语义由单次扩容保留，
    /// 不从 0 倍增）。</summary>
    public static MemoryStream RentDecompressInput(int minCapacity) => Rent(ref _decompressInput, minCapacity);

    /// <summary>解压输出槽（容量按需增长）。</summary>
    public static MemoryStream RentDecompressOutput() => Rent(ref _decompressOutput, 0);

    /// <summary>R12 C-17：解压输入槽操作结束归还——超驻留上限弃槽，避免长驻线程持大缓冲。</summary>
    public static void ReleaseDecompressInput(MemoryStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (ReferenceEquals(_decompressInput, stream) && stream.Capacity > MaxResidentStreamBytes)
            _decompressInput = null;
    }

    /// <summary>R12 C-17：解压输出槽操作结束归还——超驻留上限弃槽。</summary>
    public static void ReleaseDecompressOutput(MemoryStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (ReferenceEquals(_decompressOutput, stream) && stream.Capacity > MaxResidentStreamBytes)
            _decompressOutput = null;
    }

    private static MemoryStream Rent(ref MemoryStream? slot, int minCapacity)
    {
        var stream = slot;
        if (stream is null || stream.Capacity > MaxResidentStreamBytes)
        {
            stream = new MemoryStream(minCapacity);
            slot = stream;
            return stream;
        }

        // Capacity setter 单次扩容到目标尺寸（无倍增链）——v53 预设容量语义
        if (minCapacity > stream.Capacity)
            stream.Capacity = minCapacity;
        stream.SetLength(0);
        stream.Position = 0;
        return stream;
    }
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

        // B5（S1 轮）：TLS 输出流复用 + leaveOpen（池化流不随压缩流销毁）——原每次
        // new MemoryStream 倍增到输出尺寸；稳态零倍增，ToArray 仍为一次不可避免的
        // 精确尺寸分配（API 返回 byte[]/ReadOnlyMemory 契约）
        var output = TlsStreamPool.RentCompressOutput();
        var sysLevel = MapLevel(level);

        using (var gzip = new GZipStream(output, sysLevel, leaveOpen: true))
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

        // B5（S1 轮）：输入/输出双槽 TLS 复用 + leaveOpen。三十八轮 P3 的 Write 进
        // MemoryStream（免 ToArray 整体拷贝）与 v53 的预设容量（免倍增）语义保留——
        // 预设容量由 RentDecompressInput 的 Capacity setter 单次扩容兑现。
        var input = TlsStreamPool.RentDecompressInput(compressed.Length);
        input.Write(compressed);
        input.Position = 0;
        var output = TlsStreamPool.RentDecompressOutput();
        try
        {
            using (var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                DecompressionGuard.CopyWithLimit(gzip, output); // P2 修复：输出上限防膨胀炸弹
                return output.ToArray();
            }
        }
        finally
        {
            // R12 C-17：成功/超限异常都归还；超 256KB 的槽立即弃置。
            TlsStreamPool.ReleaseDecompressInput(input);
            TlsStreamPool.ReleaseDecompressOutput(output);
        }
    }

    private static System.IO.Compression.CompressionLevel MapLevel(CompressionLevel level) => CompressionLevelMap.ToFramework(level);
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

        // B5（S1 轮）：TLS 输出流复用 + leaveOpen（同 GZipCompressor 改造）
        var output = TlsStreamPool.RentCompressOutput();
        var sysLevel = MapLevel(level);

        using (var deflate = new DeflateStream(output, sysLevel, leaveOpen: true))
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

        // B5（S1 轮）：输入/输出双槽 TLS 复用 + leaveOpen（同 GZipCompressor 改造；
        // 三十八轮 P3 Write 免拷贝与 v53 预设容量语义由 RentDecompressInput 保留）
        var input = TlsStreamPool.RentDecompressInput(compressed.Length);
        input.Write(compressed);
        input.Position = 0;
        var output = TlsStreamPool.RentDecompressOutput();
        try
        {
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                DecompressionGuard.CopyWithLimit(deflate, output); // P2 修复：输出上限防膨胀炸弹
                return output.ToArray();
            }
        }
        finally
        {
            // R12 C-17：同 GZip——超限异常路径也归还/弃置大缓冲。
            TlsStreamPool.ReleaseDecompressInput(input);
            TlsStreamPool.ReleaseDecompressOutput(output);
        }
    }

    private static System.IO.Compression.CompressionLevel MapLevel(CompressionLevel level) => CompressionLevelMap.ToFramework(level);
}

/// <summary>
/// 压缩级别映射单一来源（2026-10-21 评审 F15）：Brotli 质量与 System.IO.Compression 级别
/// 同表维护；未知枚举值 fail-fast，不再静默映射为默认级别。
/// </summary>
internal static class CompressionLevelMap
{
    internal static int ToBrotliQuality(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 1,
        CompressionLevel.Balanced => 4,
        CompressionLevel.SmallestSize => 11,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown compression level."),
    };

    internal static System.IO.Compression.CompressionLevel ToFramework(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
        CompressionLevel.Balanced => System.IO.Compression.CompressionLevel.Optimal,
        CompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown compression level."),
    };
}
