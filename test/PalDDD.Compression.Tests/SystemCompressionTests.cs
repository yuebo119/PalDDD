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

        await Assert.That(provider.GetCompressor(CompressionAlgorithm.Brotli).Algorithm).IsEqualTo(CompressionAlgorithm.Brotli);
        await Assert.That(provider.GetCompressor(CompressionAlgorithm.GZip).Algorithm).IsEqualTo(CompressionAlgorithm.GZip);
        await Assert.That(provider.GetCompressor(CompressionAlgorithm.Deflate).Algorithm).IsEqualTo(CompressionAlgorithm.Deflate);
    }
}
