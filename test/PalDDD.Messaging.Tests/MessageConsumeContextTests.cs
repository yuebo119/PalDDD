using System.Text;

namespace PalDDD.Messaging.Tests;

// ─────────────────────────────────────────────────────────────
// MessageConsumeContext — 消费侧追踪头提取测试（八轮评审：追踪头消费端断链）
// 覆盖 FromHeaders 工厂：字节/字符串解码、键名映射、兜底 correlation、null 语义。
// ─────────────────────────────────────────────────────────────

public sealed class MessageConsumeContextTests
{
    [Test]
    public async Task FromHeaders_ByteValues_ExtractsAllTracingFields()
    {
        // 两个 Broker 写侧 AddHeader 均以 UTF-8 字节写入
        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = Encoding.UTF8.GetBytes("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"),
            ["tracestate"] = Encoding.UTF8.GetBytes("acme=orange,rig=honey"),
            ["x-correlation-id"] = Encoding.UTF8.GetBytes("01ARZ3NDEKTSV4RRFFQ69G5FAV"),
            ["x-causation-id"] = Encoding.UTF8.GetBytes("01ARZ3NDRQZ7QTNSK4DD3F9A5N"),
        };

        var context = MessageConsumeContext.FromHeaders(headers);

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.TraceParent).IsEqualTo("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        await Assert.That(context.TraceState).IsEqualTo("acme=orange,rig=honey");
        await Assert.That(context.CorrelationId).IsEqualTo("01ARZ3NDEKTSV4RRFFQ69G5FAV");
        await Assert.That(context.CausationId).IsEqualTo("01ARZ3NDRQZ7QTNSK4DD3F9A5N");
    }

    [Test]
    public async Task FromHeaders_StringValues_PassThrough()
    {
        // 宽容非本框架写入的字符串头（写侧为字节，但消费端不因表示不同而丢头）
        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = "00-abc-def-01",
            ["x-causation-id"] = "01ARZ3NDKK9Q3VQ7Z7V1F2K6FA",
        };

        var context = MessageConsumeContext.FromHeaders(headers);

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.TraceParent).IsEqualTo("00-abc-def-01");
        await Assert.That(context.CausationId).IsEqualTo("01ARZ3NDKK9Q3VQ7Z7V1F2K6FA");
        await Assert.That(context.TraceState).IsNull();
        await Assert.That(context.CorrelationId).IsNull();
    }

    [Test]
    public async Task FromHeaders_NullHeadersAndNoFallback_ReturnsNull()
    {
        var context = MessageConsumeContext.FromHeaders(headers: null);

        await Assert.That(context).IsNull();
    }

    [Test]
    public async Task FromHeaders_EmptyHeadersWithFallback_ReturnsContextWithFallbackCorrelation()
    {
        // RabbitMQ 路径：correlation 写入 BasicProperties 而非 header，兜底参数生效
        var context = MessageConsumeContext.FromHeaders(
            headers: new Dictionary<string, object?>(),
            correlationId: "01ARZ3NFF4G5H6J7K8L9M0N1O2");

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.CorrelationId).IsEqualTo("01ARZ3NFF4G5H6J7K8L9M0N1O2");
        await Assert.That(context.CausationId).IsNull();
        await Assert.That(context.Headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FromHeaders_HeaderCorrelationTakesPrecedenceOverFallback()
    {
        var headers = new Dictionary<string, object?>
        {
            ["x-correlation-id"] = Encoding.UTF8.GetBytes("from-header"),
        };

        var context = MessageConsumeContext.FromHeaders(headers, correlationId: "from-fallback");

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.CorrelationId).IsEqualTo("from-header");
    }

    [Test]
    public async Task FromHeaders_UnknownValueType_DecodesAsNullButKeyRetainedInHeadersView()
    {
        // 未知头值类型（如 AMQP 非字符串标量）不猜测编码：追踪字段按缺失，Headers 视图保留键
        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = 12345L,
        };

        var context = MessageConsumeContext.FromHeaders(headers);

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.TraceParent).IsNull();
        await Assert.That(context.Headers.ContainsKey("traceparent")).IsTrue();
        await Assert.That(context.Headers["traceparent"]).IsNull();
    }

    [Test]
    public async Task FromHeaders_EmptyByteArray_DecodesAsNull()
    {
        var headers = new Dictionary<string, object?>
        {
            ["tracestate"] = Array.Empty<byte>(),
        };

        var context = MessageConsumeContext.FromHeaders(headers);

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.TraceState).IsNull();
    }

    [Test]
    public async Task FromHeaders_UnknownHeaders_PreservedInHeadersView()
    {
        var headers = new Dictionary<string, object?>
        {
            ["x-custom"] = Encoding.UTF8.GetBytes("value"),
        };

        var context = MessageConsumeContext.FromHeaders(headers);

        await Assert.That(context).IsNotNull();
        await Assert.That(context!.CorrelationId).IsNull();
        await Assert.That(context.Headers["x-custom"]).IsEqualTo("value");
    }

    [Test]
    public async Task HeaderNames_MatchWriteSideHeaderKeys()
    {
        // 锁读写两侧键名一致——写侧 CreateHeaders 与消费端 FromHeaders 共用这些常量
        await Assert.That(MessageConsumeContext.HeaderNames.TraceParent).IsEqualTo("traceparent");
        await Assert.That(MessageConsumeContext.HeaderNames.TraceState).IsEqualTo("tracestate");
        await Assert.That(MessageConsumeContext.HeaderNames.CorrelationId).IsEqualTo("x-correlation-id");
        await Assert.That(MessageConsumeContext.HeaderNames.CausationId).IsEqualTo("x-causation-id");
    }
}
