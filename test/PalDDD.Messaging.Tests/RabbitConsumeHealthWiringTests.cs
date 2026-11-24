// ─────────────────────────────────────────────────────────────
// 🧪 Rabbit 消费链路健康位接线（R18 审计发现缺陷 1）
// ─────────────────────────────────────────────────────────────
// 缺陷：_consumeFaultReason 只在五个结算/取消安全包装 catch 置位；channel 空闲断连
// （无在飞交付、无结算动作）时不触发任何包装 → 故障位保持 null → /health 恒绿，
// 实例已停止消费却不被摘流量。上游 AsyncEventingBasicConsumer.ShutdownAsync 提供
// 主动通知（"Fires on channel shutdown, both client and server initiated"），修前零订阅。
//
// 为什么用源码级不变式：行为探针需真 broker 空闲断连且不产生结算动作（Integration
// 车道），本仓 R16/R17 的同族健康位接线（KafkaConsumeHealthWiringTests）已确立
// 「源码级断言不可恢复故障面接线」形态。此处断言两条不变式：
//   ① 订阅存在且用 Initiator 区分正常关停；② 退订在订阅句柄闭包内成对。
// 逐行扫描并跳过 `//` 注释行：全文 Contains 会把「注释掉的订阅」也算命中（假绿），
// 变异验证实测过这个陷阱，故判据必须只认真实代码行。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Messaging.Tests;

/// <summary>
/// Rabbit 消费链路健康位接线的源码级不变式（R18 缺陷 1 锁定）。
/// </summary>
public sealed class RabbitConsumeHealthWiringTests
{
    /// <summary>
    /// consumer 必须订阅 ShutdownAsync，并用 Initiator 把正常关停（Application）与
    /// 外部断连（Library/Peer）区分开；只有后者才置 _consumeFaultReason。
    /// 修前零订阅，本测试红。
    /// </summary>
    [Test]
    public async Task SubscribeAsync_WiresShutdownEventWithInitiatorGuard()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(FindRepoRoot(), "src", "PalDDD.Messaging.RabbitMQ", "RabbitMqBroker.cs"));
        var code = string.Join('\n', lines.Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var subscribeStart = code.IndexOf("public override async ValueTask<IAsyncDisposable> SubscribeAsync", StringComparison.Ordinal);
        await Assert.That(subscribeStart).IsGreaterThan(-1).Because("未定位 SubscribeAsync");

        var body = code[subscribeStart..];
        var shutdown = body.IndexOf("consumer.ShutdownAsync +=", StringComparison.Ordinal);
        await Assert.That(shutdown).IsGreaterThan(-1)
            .Because("consumer 必须订阅 ShutdownAsync 以捕获空闲断连（修前零订阅，健康位不置位）");
        await Assert.That(body.Contains("ShutdownInitiator.Application", StringComparison.Ordinal)).IsTrue()
            .Because("shutdown 回调必须用 Initiator 区分正常关停与故障断连，避免 Dispose 误红");
        await Assert.That(body.Contains("_consumeFaultReason", StringComparison.Ordinal)).IsTrue()
            .Because("shutdown 回调必须置健康故障位");
    }

    /// <summary>
    /// ShutdownAsync 的退订必须在订阅句柄的释放闭包里成对，且置于 linkedCts.Dispose() 之前
    /// （否则回调访问已释放 CTS 抛 ODE）。修前无订阅，本测试红。
    /// </summary>
    [Test]
    public async Task SubscriptionDispose_UnsubscribesShutdownEventBeforeCtsDispose()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(FindRepoRoot(), "src", "PalDDD.Messaging.RabbitMQ", "RabbitMqBroker.cs"));
        var code = string.Join('\n', lines.Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var closureStart = code.IndexOf("var subscription = new AsyncSubscription", StringComparison.Ordinal);
        await Assert.That(closureStart).IsGreaterThan(-1).Because("未定位订阅句柄闭包");

        var closure = code[closureStart..];
        var unsubscribe = closure.IndexOf("consumer.ShutdownAsync -=", StringComparison.Ordinal);
        await Assert.That(unsubscribe).IsGreaterThan(-1)
            .Because("订阅句柄释放时必须退订 ShutdownAsync（成对 API）");

        var ctsDispose = closure.IndexOf("linkedCts.Dispose()", StringComparison.Ordinal);
        await Assert.That(ctsDispose).IsGreaterThan(unsubscribe)
            .Because("退订必须先于 linkedCts.Dispose()，避免回调访问已释放 CTS");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
