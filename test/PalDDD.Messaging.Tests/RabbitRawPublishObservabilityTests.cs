// ─────────────────────────────────────────────────────────────
// 🧪 Rabbit raw 发布可观测接线（R17 审计发现 D9）
// ─────────────────────────────────────────────────────────────
// 缺陷：PublishRawAsync 的 EnsureExchangeAsync 位于 try 外（broker 不可达时 exchange
// 声明失败逃逸出 BrokerPublishFailed 计数），且整条 raw 路径无 publish span——
// 对象路径经 FIX-011/FIX-016 已把声明移入 try 并建 publishActivity，raw 路径未同步。
// 该路径是 Outbox 主发布口，影响放大。

namespace PalDDD.Messaging.Tests;

public sealed class RabbitRawPublishObservabilityTests
{
    /// <summary>
    /// raw 发布路径必须建 publish span，且 exchange 声明在 try 内（失败落计数与 span Error）。
    /// 行为探针需真 broker 关闭连接制造声明失败（Integration 车道覆盖），此处按本仓
    /// 已接受的源码级不变式形态断言两条接线（同 Kafka 健康位/StopAsync 测试的取舍）。
    /// </summary>
    [Test]
    public async Task PublishRawAsync_HasPublishSpanAndDeclarationInsideTry()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "PalDDD.Messaging.RabbitMQ", "RabbitMqBroker.cs");
        var text = await File.ReadAllTextAsync(path);

        var methodStart = text.IndexOf("public override async ValueTask PublishRawAsync", StringComparison.Ordinal);
        await Assert.That(methodStart).IsGreaterThan(-1).Because("未定位 PublishRawAsync");
        // 方法结束边界：下一个方法声明（EnsureExchangeAsync 的 summary 之前）
        var methodEnd = text.IndexOf("/// <summary>\n    /// 声明 exchange", methodStart, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = text.IndexOf("private async", methodStart, StringComparison.Ordinal);
        await Assert.That(methodEnd).IsGreaterThan(methodStart).Because("未定位 PublishRawAsync 结束边界");

        var body = text[methodStart..methodEnd];

        await Assert.That(body.Contains("StartBrokerPublish", StringComparison.Ordinal)).IsTrue()
            .Because("raw 发布路径必须建 publishActivity（修前整条路径无 span，链路追踪缺发布跳）");

        var tryIndex = body.IndexOf("try", StringComparison.Ordinal);
        var ensureIndex = body.IndexOf("EnsureExchangeAsync", StringComparison.Ordinal);
        await Assert.That(ensureIndex).IsGreaterThan(tryIndex)
            .Because("exchange 声明必须在 try 内（修前位于 try 外，声明失败逃逸出 BrokerPublishFailed 计数与 span Error）");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
