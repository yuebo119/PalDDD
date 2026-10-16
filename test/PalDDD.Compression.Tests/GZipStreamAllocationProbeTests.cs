using Microsoft.Extensions.DependencyInjection;
using PalDDD.Compression;

namespace PalDDD.Compression.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔬 B5 探针（2026-10-15 性能实施轮 S1）：GZip/Deflate Stream 管线现状分配实测
// 背景：Brotli 路径已全 span 原语化（含 DecompressBufferPool 线程槽），GZip/Deflate 仍
// 走 new MemoryStream + new GZipStream + output.ToArray 的 Stream 形态（BCL 无
// gzip/deflate 的 span 编码器，Stream 不可避免，但 Stream 对象可池化）。
// 台账表 2 登记：span decoder 改造（解码器重写形态）2026-09-25 否决回滚，基线
// GZip 33,704B / Deflate 33,648B 保持不变——本探针测的是「Stream 对象池化」这一
// 不同机制（TLS 复用 MemoryStream，消除倍增重分配），为 B5 是否实施提供判定依据。
// 量具同 SystemCompressionTests.MeasurePerOpAllocation 口径（预热 + 2000 次均值）。
// ═══════════════════════════════════════════════════════════════

public sealed class GZipStreamAllocationProbeTests
{
    private static PalDDD.Compression.ICompressor Create(CompressionAlgorithm algorithm)
    {
        // 同 SystemCompressionTests.CreateProvider 口径（DI 注册面）
        var services = new ServiceCollection();
        services.AddPalCompression();
        return services.BuildServiceProvider().GetRequiredService<ICompressionProvider>().GetCompressor(algorithm);
    }

    private static byte[] DeterministicData(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251); // 中等熵：压缩比真实，非全零病态
        return data;
    }

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
    public async Task Measure_PositiveControl_ReportsExpectedMagnitude()
    {
        // 仪器自证（对齐 SystemCompressionTests 同款）：必然分配须测出预期量级
        var perOp = MeasurePerOpAllocation(static () => _ = new byte[64 * 1024], 2_000);
        await Assert.That(perOp > 60 * 1024).IsTrue();
    }

    [Test]
    public async Task GZip_Compress16KB_CurrentAllocationMagnitude_Recorded()
    {
        var compressor = Create(CompressionAlgorithm.GZip);
        var data = DeterministicData(16 * 1024);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Compress(data), 2_000);

        // 现状记录口径：Stream 形态（new MemoryStream 倍增 + GZipStream + ToArray）。
        // 本载荷高度可压缩（输出近乎零）：实测 ~1.6KB/op（Stream 对象 + 短倍增链 +
        // 微输出）。断言带宽 [256B, 64KB] 记录该量级并防仪器恒零；不可压缩载荷的
        // 倍增 churn 由下方随机腿测量（那才是池化收益场景）。
        await Assert.That(perOp).IsGreaterThan(256);
        await Assert.That(perOp).IsLessThan(64 * 1024);
    }

    [Test]
    public async Task GZip_Decompress16KB_CurrentAllocationMagnitude_Recorded()
    {
        var compressor = Create(CompressionAlgorithm.GZip);
        var data = DeterministicData(16 * 1024);
        var compressed = compressor.Compress(data);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Decompress(compressed.Span), 2_000);

        // 解压路径输出侧同样 Stream 形态（输入侧 compressed 已定型）；CopyWithLimit 的
        // 80KB 池化租用不计入分配。带宽 [4KB, 64KB]。
        await Assert.That(perOp).IsGreaterThan(4 * 1024);
        await Assert.That(perOp).IsLessThan(64 * 1024);
    }

    [Test]
    public async Task Brotli_Compress16KB_ComparisonMagnitude_Recorded()
    {
        // 对照腿：Brotli 全 span 原语路径的同载荷分配量级——GZip 若显著高于本值即存在
        // 可池化空间（Stream 对象与倍增缓冲）；量级相当则池化收益不成立。
        var compressor = Create(CompressionAlgorithm.Brotli);
        var data = DeterministicData(16 * 1024);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Compress(data), 2_000);

        // Brotli span 路径：输出 ToArray 为主（~压缩后尺寸）+ 少量固定开销。本载荷
        // 高度可压缩，实测 ~288B/op；带宽 [128B, 64KB] 记录量级并防仪器恒零。
        await Assert.That(perOp).IsGreaterThan(128);
        await Assert.That(perOp).IsLessThan(64 * 1024);
    }

    [Test]
    public async Task GZip_CompressRandom16KB_StreamDoublingChurn_Recorded()
    {
        // 真实场景腿：不可压缩载荷——MemoryStream 从 0 倍增到输出尺寸（256+512+…+16K
        // ≈ 2× 输出的累计分配）+ ToArray。可压缩载荷下输出近乎零、倍增链短，会严重
        // 低估池化收益，故以随机数据测量（池化 TLS 复用消除的正是这段倍增 churn）。
        var compressor = Create(CompressionAlgorithm.GZip);
        var data = RandomData(16 * 1024);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Compress(data), 2_000);

        // 记录带宽 [16KB, 96KB]：输出 ~16KB（不可压缩）+ ToArray 16KB + 倍增累计 ~32KB
        // 的理论量级 ~48KB+；下限 16KB 防仪器失灵，上限 96KB 防离谱读数
        await Assert.That(perOp).IsGreaterThan(16 * 1024);
        await Assert.That(perOp).IsLessThan(96 * 1024);
    }

    [Test]
    public async Task Brotli_CompressRandom16KB_Comparison_Recorded()
    {
        // 对照：Brotli span 路径同随机载荷——理论量级 ~输出 ToArray（16KB+头）+ 少量
        // 固定开销（无倍增链）。与 GZip 随机腿的差即「可池化空间」的上界。
        var compressor = Create(CompressionAlgorithm.Brotli);
        var data = RandomData(16 * 1024);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Compress(data), 2_000);

        await Assert.That(perOp).IsGreaterThan(16 * 1024);
        await Assert.That(perOp).IsLessThan(64 * 1024);
    }

    /// <summary>确定性随机数据（xorshift，种子固定）——不可压缩，压缩比 ~1。</summary>
    private static byte[] RandomData(int size)
    {
        var data = new byte[size];
        uint state = 0x9E3779B9;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)state;
        }
        return data;
    }

    [Test]
    public async Task GZip_CompressRandom16KB_PooledStream_AllocationWithinBudget()
    {
        // B5 锁定（S1 轮）：TLS MemoryStream 池化后，随机 16KB 压缩的稳态分配应收敛到
        // 「输出 ToArray（~16.4KB）+ GZipStream 对象」量级；旧形态另有 MemoryStream
        // 倍增链累计约 2× 输出（~48KB+）。预算 20KB/op 区分两形态（旧码判红）。
        // 预热使线程槽容量到位（与 SystemCompressionTests 池化预算测试同口径）。
        var compressor = Create(CompressionAlgorithm.GZip);
        var data = RandomData(16 * 1024);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Compress(data), 2_000);

        await Assert.That(perOp).IsLessThan(20 * 1024);
    }

    [Test]
    public async Task GZip_DecompressRandom16KB_PooledStream_AllocationWithinBudget()
    {
        // B5 锁定（S1 轮）：解压路径输入/输出双槽池化后，稳态分配收敛到「输出 ToArray
        // 16KB + GZipStream 对象」；旧形态另有输出 MemoryStream 倍增链（~32KB 累计）。
        // 预算 20KB/op 区分两形态。
        var compressor = Create(CompressionAlgorithm.GZip);
        var data = RandomData(16 * 1024);
        var compressed = compressor.Compress(data);
        ReadOnlyMemory<byte> sink;

        var perOp = MeasurePerOpAllocation(() => sink = compressor.Decompress(compressed.Span), 2_000);

        await Assert.That(perOp).IsLessThan(20 * 1024);
    }
}
