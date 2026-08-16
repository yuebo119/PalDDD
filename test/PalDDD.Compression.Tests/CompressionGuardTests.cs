using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Compression;

namespace PalDDD.Compression.Tests;

// ═══════════════════════════════════════════════════════════════
// 解压炸弹防护测试（八轮评审 P2）— 三类防护的可触达性验证
// ═══════════════════════════════════════════════════════════════
// ① 压缩输入上限（8MB，DecompressionGuard.MaxCompressedInputBytes）
// ② 损坏输入拒绝（InvalidDataException）
// ③ 解压输出上限（64MB，System 版逐块检查 / Native 版返回后检查）
//
// P2 修复（十七轮）：上限常量直接引用 DecompressionGuard（已 public）——
// 消除本地数字副本的漂移风险（单一事实源）；本文件用真实量级数据端到端验证
// （组③峰值瞬时内存约 130MB）。若未来需要低成本回归，建议把上限做成可注入
// 选项（IOptions<DecompressionGuardOptions>），本文件即可改用小上限。
// Native 三算法（LZ4/ZStandard/OpenZL）的解压输出上限为"返回后检查"——
// 已知限制见 NativeCompressors.DecompressionGuard XML doc，此处仅验证检查会触发。
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 解压炸弹防护测试 — 输入上限 / 损坏输入 / 输出上限。
/// </summary>
public sealed class CompressionGuardTests
{
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
    public async Task Decompress_OutputAboveLimit_System_ThrowsIOException(CompressionAlgorithm algorithm)
    {
        var compressor = GetCompressor(algorithm, native: false);
        var bomb = new byte[DecompressionGuard.MaxOutputBytes + 1]; // 全 0——最大膨胀率的真实炸弹形态
        var compressed = compressor.Compress(bomb);

        // 前置：炸弹压缩体必须低于输入上限，确保走到的是输出上限检查
        await Assert.That(compressed.Length).IsLessThan(DecompressionGuard.MaxCompressedInputBytes);

        // System 三算法逐块检查（Brotli totalWritten / GZip+Deflate CopyWithLimit）→ IOException
        await Assert.That(() => compressor.Decompress(compressed.Span)).Throws<IOException>();
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

        // Native 版输出检查在 Decompress 返回后 → InvalidDataException（已知限制：先分配后检查）
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
    }

    private sealed class StubCompressor(CompressionAlgorithm algorithm) : ICompressor
    {
        public CompressionAlgorithm Algorithm => algorithm;
        public ReadOnlyMemory<byte> Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Balanced) => Array.Empty<byte>();
        public byte[] Decompress(ReadOnlySpan<byte> compressed) => [];
    }
}
