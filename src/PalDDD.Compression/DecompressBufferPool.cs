using System.Buffers;

namespace PalDDD.Compression;

// C6（2026-10-09 优化轮，原落 PalDDD.Compression.Native）：解压增长缓冲的线程槽池——
// 各压缩器 Decompress 共用一个 ThreadStatic 槽，免每调用 new ArrayBufferWriter 及其倍增分配。
// 2026-10-11 上移自 Native 程序集（EPSL 地毯审计 A-3）：Brotli（System 压缩器）接入同一池，
// 单一事实源——对齐 DecompressionGuard 的跨程序集先例；池无公共 API 面，以
// InternalsVisibleTo 授 Native 程序集消费（较 public 常量的轻量形态）。
// 权衡镜像 JsonMessageSerializer 的 ThreadStatic 池先例（含峰值驻留声明）：缓冲按历史峰值
// 容量保留，线程退出才回收；输出上限受 DecompressionGuard.MaxOutputBytes 约束，单线程峰值
// 驻留有界。非重入：单线程内嵌套 Decompress（自定义流内回调）会清空外层缓冲——与序列化器
// 池同款约束，本 API 形态（同步直解）无嵌套路径。
// EPSL 地毯审计 2026-10-16 B 批 #8：驻留上限弃槽（R9/P2-1 同族五池漏格补齐）——历史峰值
// 超过 MaxResidentBytes（256KB）的线程槽在下次入口弃槽重建，把「按历史峰值无限驻留」收敛
// 为有界（长驻线程 × 峰值 的稳态 RSS 压到 阈值 × 线程数 内）。阈值与 JsonMessageSerializer /
// JsonLinesEventWriter / KafkaBroker / TlsStreamPool 四池对齐：覆盖框架消息级载荷常态（小载荷
// 零额外成本）；稳态输出超阈值的负载以 RSS 有界换池化收益（成本 = 倍增链，与无池同构），
// 如出现此类常态负载上调本单一常量即可。
internal static class DecompressBufferPool
{
    /// <summary>TLS 缓冲驻留上限——超阈值弃槽重建（与 R9 四池同族同值）。</summary>
    private const int MaxResidentBytes = 256 * 1024;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _writer;

    /// <summary>取本线程的解压缓冲（复用前 Clear；异常残留由下一次 Rent 的 Clear 清除；
    /// 超驻留上限弃槽重建）。</summary>
    public static ArrayBufferWriter<byte> Rent()
    {
        var writer = _writer;
        if (writer is null || writer.Capacity > MaxResidentBytes) // B 批 #8：超阈值弃槽重建
        {
            _writer = writer = new ArrayBufferWriter<byte>();
        }
        else
        {
            writer.Clear();
        }
        return writer;
    }

    /// <summary>
    /// R12 C-17：操作结束后释放本线程槽。仅当历史峰值超过驻留上限时弃槽——
    /// 原实现等到“下一次 Rent”才弃槽，长驻线程在解压炸弹异常后可能长期持有 64MB。
    /// 小缓冲保留复用收益，下一次 Rent 的 Clear 语义不变。
    /// </summary>
    public static void Release(ArrayBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (ReferenceEquals(_writer, writer) && writer.Capacity > MaxResidentBytes)
            _writer = null;
    }
}
