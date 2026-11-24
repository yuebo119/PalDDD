using Microsoft.Extensions.DependencyInjection;

namespace PalDDD.Compression.Tests;

// ═══════════════════════════════════════════════════════════════
// 解压炸弹防护测试（八轮评审 P2）— 三类防护的可触达性验证
// ═══════════════════════════════════════════════════════════════
// ① 压缩输入上限（8MB，DecompressionGuard.MaxCompressedInputBytes）
// ② 损坏输入拒绝（InvalidDataException）
// ③ 解压输出上限（64MB，System 版与 Native 版均逐块检查）
//
// P2 修复（十七轮）：上限常量直接引用 DecompressionGuard（已 public）——
// 消除本地数字副本的漂移风险（单一事实源）；本文件用真实量级数据端到端验证
// （组③峰值瞬时内存约 130MB）。若未来需要低成本回归，建议把上限做成可注入
// 选项（IOptions<DecompressionGuardOptions>），本文件即可改用小上限。
// Native 三算法（LZ4/ZStandard/OpenZL）已改走流式 decoder，输出累计每轮即校验上限
//（形态对齐 System 版 Brotli）；本组断言"超限必抛"，并经变异验证（把上限检查
// 置为恒不触发 → 本组精确变红）。
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 解压炸弹防护测试 — 输入上限 / 损坏输入 / 输出上限。
/// </summary>
public sealed class CompressionGuardTests
{
    [Test]
    public async Task DecompressBufferPools_Release_DropsOversizedSlotsImmediately()
    {
        // R12 C-17 回归锁：解压炸弹路径留下的 64MB 级缓冲必须在操作结束时弃槽，
        // 而不是等同一线程下一次 Rent（长驻线程可能永久持有）。
        var writer = DecompressBufferPool.Rent();
        var span = writer.GetSpan(300 * 1024);
        span[..(300 * 1024)].Clear();
        writer.Advance(300 * 1024);
        var grownCapacity = writer.Capacity;

        DecompressBufferPool.Release(writer);
        var nextWriter = DecompressBufferPool.Rent();

        await Assert.That(grownCapacity).IsGreaterThan(256 * 1024);
        await Assert.That(nextWriter.Capacity).IsLessThan(300 * 1024);

        var stream = TlsStreamPool.RentDecompressOutput();
        stream.Capacity = 300 * 1024;
        TlsStreamPool.ReleaseDecompressOutput(stream);
        var nextStream = TlsStreamPool.RentDecompressOutput();

        await Assert.That(nextStream.Capacity).IsLessThan(300 * 1024);
    }

    /// <summary>
    /// R17 审计发现 D2 锁定：三个原生解压器在异常路径（解压炸弹）必须调用
    /// <c>DecompressBufferPool.Release</c> 弃用超大线程槽。
    /// <para>
    /// 修前 LZ4/ZStandard/OpenZL 的 Decompress 只 Rent 不 Release——bomb 抛异常后
    /// 64MB 级缓冲留在 ThreadStatic 槽，直到同线程下次 Rent 才弃用（长驻线程永久持有）。
    /// 本测试在同一测试线程上：① 先建立干净小槽；② 跑 bomb 解压使其抛异常；
    /// ③ 再 Rent 断言槽容量已回落。修前槽被污染 → 断言失败。
    /// </para>
    /// </summary>
    [Test]
    [Arguments(CompressionAlgorithm.LZ4)]
    [Arguments(CompressionAlgorithm.ZStandard)]
    [Arguments(CompressionAlgorithm.OpenZL)]
    public async Task Decompress_OutputBomb_Native_DropsOversizedThreadSlot(CompressionAlgorithm algorithm)
    {
        // ① 建立干净基线：Rent 空 writer 后 Release（容量 0，不触发弃槽）；bomb 在同线程复用该槽
        var baseline = DecompressBufferPool.Rent();
        DecompressBufferPool.Release(baseline);

        var compressor = GetCompressor(algorithm, native: true);
        var bomb = new byte[DecompressionGuard.MaxOutputBytes + 1];
        var compressed = compressor.Compress(bomb);

        // ② bomb 解压必然抛输出上限异常（既有测试已锁此行为）
        await Assert.That(() => compressor.Decompress(compressed.Span)).Throws<InvalidDataException>();

        // ③ 同一线程再 Rent：修后异常路径 Release 弃用超大槽，新槽容量须远小于上限；
        //    修前槽保留 bomb 增长的 64MB 级容量 → 断言失败。
        var after = DecompressBufferPool.Rent();
        await Assert.That(after.Capacity).IsLessThan(300 * 1024)
            .Because($"{algorithm} 解压炸弹异常路径必须弃用超大线程槽（修前只 Rent 不 Release）");
    }

    /// <summary>
    /// R17 审计发现 D2 锁定：三个原生解压器的 each Decompress 必须成对调用
    /// <c>DecompressBufferPool.Rent</c> 与 <c>Release</c>。
    /// <para>
    /// 修前 LZ4/ZStandard/OpenZL 的 Decompress 只 Rent 不 Release——解压炸弹抛异常后
    /// 64MB 级缓冲留在 ThreadStatic 槽（R12 C-17 修前形态），长驻线程永久持有。
    /// 行为探针受 TLS 线程归属与 TUnit 调度影响不稳定，故用源码级不变式：
    /// Rent 与 Release 调用数必须相等（SystemCompressor 的 Brotli 路径已成对，是参照）。
    /// </para>
    /// </summary>
    [Test]
    public async Task NativeDecompressors_PairRentWithRelease()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "PalDDD.Compression.Native", "NativeCompressors.cs");
        var text = await File.ReadAllTextAsync(path);

        var rentCount = CountOccurrences(text, "DecompressBufferPool.Rent()");
        var releaseCount = CountOccurrences(text, "DecompressBufferPool.Release(");

        await Assert.That(rentCount).IsEqualTo(3)
            .Because("三个原生解压器各 Rent 一次（LZ4/ZStandard/OpenZL）");
        await Assert.That(releaseCount).IsEqualTo(rentCount)
            .Because($"修前 Release=0/Rent=3：解压炸弹后 64MB 槽不弃用（实际 Rent={rentCount}/Release={releaseCount}）");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }

    private static ICompressor GetCompressor(CompressionAlgorithm algorithm, bool native)
    {
        var services = new ServiceCollection();
        services.AddPalCompression();
        if (native)
            services.AddPalCompressionNative();
        return services.BuildServiceProvider()
            .GetRequiredService<ICompressionProvider>()
            .GetCompressor(algorithm);
    }

    // ── ① 压缩输入上限：>8MB 输入在格式解析前被拒绝 ───────────────

    [Test]
    [Arguments(CompressionAlgorithm.Brotli)]
    [Arguments(CompressionAlgorithm.GZip)]
    [Arguments(CompressionAlgorithm.Deflate)]
    public async Task Decompress_InputAboveLimit_System_ThrowsInvalidData(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: false);
        var oversize = new byte[DecompressionGuard.MaxCompressedInputBytes + 1]; // 内容无关——入口体积检查先于格式解析

        await Assert.That(() => compressor.Decompress(oversize)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(CompressionAlgorithm.LZ4)]
    [Arguments(CompressionAlgorithm.ZStandard)]
    [Arguments(CompressionAlgorithm.OpenZL)]
    public async Task Decompress_InputAboveLimit_Native_ThrowsInvalidData(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: true);
        var oversize = new byte[DecompressionGuard.MaxCompressedInputBytes + 1]; // 检查在 P/Invoke 之前，无需有效压缩格式

        await Assert.That(() => compressor.Decompress(oversize)).Throws<InvalidDataException>();
    }

    // ── ② 损坏输入：篡改首部（块头/magic）后拒绝 ──────────────────

    [Test]
    [Arguments(CompressionAlgorithm.GZip)]
    [Arguments(CompressionAlgorithm.Deflate)]
    public async Task Decompress_CorruptedHeader_System_ThrowsInvalidData(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: false);
        var compressed = compressor.Compress("Hello, Pal.DDD Compression Guard!"u8.ToArray());
        var corrupted = compressed.ToArray();

        corrupted[0] ^= 0xFF; // 破坏 GZip magic / Deflate 块头首字节
        corrupted[1] ^= 0xFF;

        // BCL 契约：无效 GZip/Deflate 流抛 InvalidDataException
        await Assert.That(() => compressor.Decompress(corrupted)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Decompress_CorruptedBrotli_ThrowsInvalidData()
    {
        var compressor = GetCompressor(CompressionAlgorithm.Brotli, native: false);
        var compressed = compressor.Compress("Hello, Pal.DDD Compression Guard!"u8.ToArray());
        var corrupted = compressed.ToArray();

        corrupted[0] ^= 0xFF;
        corrupted[1] ^= 0xFF;

        // Brotli span 路径：解码 status 非 Done/DestinationTooSmall → InvalidDataException
        await Assert.That(() => compressor.Decompress(corrupted)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(CompressionAlgorithm.LZ4)]
    [Arguments(CompressionAlgorithm.ZStandard)]
    [Arguments(CompressionAlgorithm.OpenZL)]
    public async Task Decompress_CorruptedInput_Native_Throws(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: true);
        var compressed = compressor.Compress("Hello, Pal.DDD Compression Guard!"u8.ToArray());
        var corrupted = compressed.ToArray();

        corrupted[0] ^= 0xFF; // 破坏帧 magic
        corrupted[1] ^= 0xFF;

        // NativeCompressions 内部异常类型非公开契约——防御性断言"必须抛异常而非返回垃圾/挂死"
        await Assert.That(() => compressor.Decompress(corrupted)).ThrowsException();
    }

    // ── ③ 解压输出上限：小压缩体 + 超限输出 → 拒绝 ────────────────
    // 全 0 数据 64MB+1 压缩后仅几 KB（<8MB 输入上限），解压必然超输出上限。

    [Test]
    [Arguments(CompressionAlgorithm.Brotli)]
    [Arguments(CompressionAlgorithm.GZip)]
    [Arguments(CompressionAlgorithm.Deflate)]
    public async Task Decompress_OutputAboveLimit_System_ThrowsInvalidData(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: false);
        var bomb = new byte[DecompressionGuard.MaxOutputBytes + 1]; // 全 0——最大膨胀率的真实炸弹形态
        var compressed = compressor.Compress(bomb);

        // 前置：炸弹压缩体必须低于输入上限，确保走到的是输出上限检查
        await Assert.That(compressed.Length).IsLessThan(DecompressionGuard.MaxCompressedInputBytes);

        // System 三算法逐块检查（Brotli totalWritten / GZip+Deflate span decoder 逐块）→
        // InvalidDataException（v2.1.0 发布验证勘正：v70 已统一解压异常为 InvalidDataException，
        // 本测试原断言 IOException 是 v62"面板漏列 Compression 项目"残留的 stale 期望；
        // 对齐 :135 Native 版同款异常口径）
        await Assert.That(() => compressor.Decompress(compressed.Span)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(CompressionAlgorithm.LZ4)]
    [Arguments(CompressionAlgorithm.ZStandard)]
    [Arguments(CompressionAlgorithm.OpenZL)]
    public async Task Decompress_OutputAboveLimit_Native_ThrowsInvalidData(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: true);
        var bomb = new byte[DecompressionGuard.MaxOutputBytes + 1];
        var compressed = compressor.Compress(bomb);

        await Assert.That(compressed.Length).IsLessThan(DecompressionGuard.MaxCompressedInputBytes);

        // Native 版流式解压每轮即校验上限 → 超限立即抛 InvalidDataException（不等全量分配）
        await Assert.That(() => compressor.Decompress(compressed.Span)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task CompressionProvider_DuplicateAlgorithm_ThrowsWithAlgorithmName()
    {
        // P4 回归（十八轮验证轮）：重复算法注册快速失败且异常含算法名
        NotSupportedException? thrown = null;
        try
        {
            _ = new CompressionProvider(
                [new StubCompressor(CompressionAlgorithm.GZip), new StubCompressor(CompressionAlgorithm.GZip)]);
        }
        catch (NotSupportedException ex) { thrown = ex; }
        // 行为断言（弱断言棘轮）：Message 为 null/空时 Contains 必败——非空证明异常确实抛出
        await Assert.That(thrown?.Message ?? "").Contains("GZip");
        // 变异补测（2026-10-18）：固定引导句——仅断言算法名无法杀死"删除后半句"的字符串变异体
        await Assert.That(thrown?.Message ?? "").Contains("ensure only one compressor");
    }

    [Test]
    public async Task CompressionProvider_NullCompressors_Throws()
    {
        await Assert.That(() => new CompressionProvider(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CompressionProvider_NullElement_Throws()
    {
        ICompressor[] compressors = [null!];
        var ex = await Assert.That(() => new CompressionProvider(compressors)).Throws<ArgumentNullException>();
        await Assert.That(ex!.Message).Contains("compressors collection contains a null element");
    }

    [Test]
    public async Task CompressionProvider_UnknownAlgorithm_ThrowsWithAlgorithmName()
    {
        var provider = new CompressionProvider([new StubCompressor(CompressionAlgorithm.GZip)]);
        NotSupportedException? thrown = null;
        try { _ = provider.GetCompressor(CompressionAlgorithm.Brotli); }
        catch (NotSupportedException ex) { thrown = ex; }
        // 变异补测（2026-10-18）：未注册算法的消息同时锁定算法名与引导句
        await Assert.That(thrown?.Message ?? "").Contains("Brotli");
        await Assert.That(thrown?.Message ?? "").Contains("Ensure the corresponding");
    }

    private sealed class StubCompressor(CompressionAlgorithm algorithm) : ICompressor
    {
        public CompressionAlgorithm Algorithm => algorithm;
        public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced) => Array.Empty<byte>();
        public byte[] Decompress(ReadOnlySpan<byte> compressed) => [];
    }
}
