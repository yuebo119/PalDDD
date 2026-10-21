using Microsoft.Extensions.DependencyInjection;

namespace PalDDD.Compression.Tests;

/// <summary>
/// T3（基础库优化清单）：CompressionLevel 穷尽性。三个内置压缩器对每个级别
/// 都必须可压缩并原样解压；新增级别而未登记映射时测试变红。
/// </summary>
public sealed class CompressionLevelExhaustivenessTests
{
    [Test]
    public async Task AllCompressors_AllLevels_RoundTrip()
    {
        var services = new ServiceCollection();
        services.AddPalCompression();
        await using var provider = services.BuildServiceProvider();

        var compressors = provider.GetServices<ICompressor>().ToArray();
        await Assert.That(compressors.Length).IsGreaterThanOrEqualTo(3);

        var payload = "Pal.DDD compression level exhaustiveness probe."u8.ToArray();
        foreach (var compressor in compressors)
        {
            var resolved = provider.GetRequiredService<ICompressionProvider>().GetCompressor(compressor.Algorithm);
            await Assert.That(resolved.Algorithm).IsEqualTo(compressor.Algorithm);

            foreach (var level in Enum.GetValues<CompressionLevel>())
            {
                var compressed = compressor.Compress(payload, level);
                var roundTripped = compressor.Decompress(compressed.Span);
                await Assert.That(roundTripped.SequenceEqual(payload)).IsTrue();
            }
        }
    }
}
