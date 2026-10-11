using Microsoft.Extensions.DependencyInjection;

namespace PalDDD.Compression.Tests;

/// <summary>
/// 系统压缩器往返测试 — Brotli/GZip/Deflate（基于 System.IO.Compression，无 P/Invoke）。
/// 核心契约：Compress → Decompress 必须恢复原始数据。
/// </summary>
public sealed class SystemCompressionTests
{
    private static ICompressionProvider CreateProvider()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddPalCompression();
        return services.BuildServiceProvider().GetRequiredService<ICompressionProvider>();
    }

    private static byte[] DeterministicData(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)(i * 7 % 256);
        return data;
    }

    [Test]
    public async Task Brotli_RoundTrip_PreservesOriginalData()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.Brotli);
        var data = "Hello, Pal.DDD Compression!"u8.ToArray();

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task GZip_RoundTrip_PreservesOriginalData()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.GZip);
        var data = DeterministicData(1000);

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task Deflate_RoundTrip_PreservesOriginalData()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.Deflate);
        var data = new byte[500];
        System.Array.Fill(data, (byte)'A');

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task Brotli_RoundTrip_EmptyInput_PreservesEmpty()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.Brotli);
        var data = Array.Empty<byte>();

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task GZip_RoundTrip_EmptyInput_PreservesEmpty()
    {
        // .NET 11 起 DeflateStream/GZipStream 空载荷也会写格式头尾，
        // 压缩器入口的空载荷早退必须保持"空进空出"契约不被破坏。
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.GZip);
        var data = Array.Empty<byte>();

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(compressed.IsEmpty).IsTrue();
        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task Deflate_RoundTrip_EmptyInput_PreservesEmpty()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.Deflate);
        var data = Array.Empty<byte>();

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(compressed.IsEmpty).IsTrue();
        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task GZip_RoundTrip_LargeInput_PreservesData()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.GZip);
        var data = DeterministicData(1024 * 100); // 100KB

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task Brotli_AllLevels_ProduceValidRoundTrip()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.Brotli);
        var data = new byte[1000];
        System.Array.Fill(data, (byte)'X');

        foreach (CompressionLevel level in Enum.GetValues<CompressionLevel>())
        {
            var compressed = compressor.Compress(data, level);
            var decompressed = compressor.Decompress(compressed.Span);
            await Assert.That(decompressed).IsEquivalentTo(data);
        }
    }

    [Test]
    public async Task Provider_GetCompressor_ReturnsCorrectAlgorithm()
    {
        var provider = CreateProvider();

        await Assert.That(provider.GetCompressor(CompressionAlgorithm.Brotli).Algorithm).IsEquivalentTo(CompressionAlgorithm.Brotli);
        await Assert.That(provider.GetCompressor(CompressionAlgorithm.GZip).Algorithm).IsEquivalentTo(CompressionAlgorithm.GZip);
        await Assert.That(provider.GetCompressor(CompressionAlgorithm.Deflate).Algorithm).IsEquivalentTo(CompressionAlgorithm.Deflate);
    }

    // ═══════════════════════════════════════════════════════════════
    // ⚡ 分配契约（EPSL 地毯审计 2026-10-11 A-3）
    // Brotli 解压的增长缓冲须走 DecompressBufferPool 线程槽（对齐 LZ4/ZStd 的 C6 形态）。
    // 旧码每调用 new ArrayBufferWriter：16KB 负载解压 ~45KB/op（账本 Brotli_Decompress_16KB，
    // 2026-10-10 strict）；池化后仅剩返回值 ToArray 的 ~16.4KB/op。预算 18KB/op 区分两形态
    //（旧码红）。线程槽语义：预热与测量同线程，池容量在预热段到位。
    // ═══════════════════════════════════════════════════════════════

    private static long MeasurePerOpAllocation(Action action, int iterations)
    {
        action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            action();
        return (GC.GetAllocatedBytesForCurrentThread() - baseline) / iterations;
    }

    [Test]
    public async Task MeasurePerOpAllocation_PositiveControl_ReportsExpectedMagnitude()
    {
        // 仪器自证：裁判对必然分配（64KB/迭代）必须测出 > 60KB/op，防恒零读数
        var perOp = MeasurePerOpAllocation(static () => _ = new byte[64 * 1024], 2_000);
        await Assert.That(perOp > 60 * 1024).IsTrue();
    }

    [Test]
    public async Task Brotli_Decompress_GrowthBufferPooled_AllocationWithinBudget()
    {
        var compressor = CreateProvider().GetCompressor(CompressionAlgorithm.Brotli);
        var data = DeterministicData(16 * 1024);
        var compressed = compressor.Compress(data);
        byte[]? sink = null;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Decompress(compressed.Span), 2_000);
        _ = sink; // 消费返回值防整个调用被 JIT 消除

        await Assert.That(perOp <= 18 * 1024).IsTrue();
    }
}
