using Microsoft.Extensions.DependencyInjection;

namespace PalDDD.Compression.Tests;

/// <summary>
/// Native 压缩器往返测试 — LZ4/ZStandard（通过 NativeCompressions P/Invoke）。
/// 验证 .NET 11 Preview 下 GetMaxCompressedLength 栈溢出修复后的端到端往返。
/// </summary>
public sealed class NativeCompressionTests
{
    private static ICompressionProvider CreateNativeProvider()
    {
        var services = new ServiceCollection();
        services.AddPalCompression();
        services.AddPalCompressionNative();
        return services.BuildServiceProvider().GetRequiredService<ICompressionProvider>();
    }

    private static byte[] DeterministicData(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)(i * 7 % 256);
        return data;
    }

    [Test]
    public async Task Native_LZ4_RoundTrip_PreservesOriginalData()
    {
        var compressor = CreateNativeProvider().GetCompressor(CompressionAlgorithm.LZ4);
        var data = DeterministicData(500);

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }

    [Test]
    public async Task Native_ZStandard_RoundTrip_PreservesOriginalData()
    {
        var compressor = CreateNativeProvider().GetCompressor(CompressionAlgorithm.ZStandard);
        var data = DeterministicData(2048);

        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed.Span);

        await Assert.That(decompressed).IsEquivalentTo(data);
    }
}
