using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using PalDDD.Testing;
using System.Text;
using System.Text.Json.Serialization;

namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 📊 流式读遥测测试（ADR-030 遥测接入，2026-10-18）
// ═══════════════════════════════════════════════════════════════
// 覆盖：活动存在与 tag、TTFT first-item 事件、四个终态各自计数、取消与失败分离、
// 项吞吐计数、SseFrameReader 助手解析正确性。
//
// 并行纪律（对齐 EventLogTests 类级 [NotInParallel] 无 key 范式）：测试用进程级
// RecordingActivityListener/RecordingMeterListener——Meter 计数器测量是静态累计，
// 其他测试类同时发流会把测量值混进来。类级不与其他测试并行，断言才能用「本测试测量增量」。

[TUnit.Core.NotInParallel]
public sealed partial class StreamTelemetryTests
{
    [Test]
    public async Task Stream_Completed_EmitsActivityWithFirstItemAndCounters()
    {
        using var activityListener = new RecordingActivityListener();
        using var startedCounter = new RecordingMeterListener("paldd.stream.started");
        using var completedCounter = new RecordingMeterListener("paldd.stream.completed");
        using var itemsCounter = new RecordingMeterListener("paldd.stream.items");

        var provider = BuildProvider(new TelemetryStreamHandler());
        var endpoint = MapEndpoint(provider, keepAlive: null);
        var context = CreateContext(provider);

        await endpoint(context);

        var streamActivity = activityListener.StoppedActivities.Single(a => a.OperationName == "Stream Query");
        await Assert.That(streamActivity.GetTagItem("pal.stream.query")).IsEqualTo(nameof(TelemetryStreamQuery));
        await Assert.That(streamActivity.GetTagItem("pal.stream.item")).IsEqualTo(nameof(TelemetryChunk));
        // TTFT 标记：首项写出时记一次 first-item 事件
        await Assert.That(streamActivity.Events.Count(e => e.Name == "first-item")).IsEqualTo(1);
        await Assert.That(streamActivity.Status).IsNotEqualTo(System.Diagnostics.ActivityStatusCode.Error);

        await Assert.That(startedCounter.Measurements.Sum()).IsGreaterThanOrEqualTo(1);
        await Assert.That(completedCounter.Measurements.Sum()).IsGreaterThanOrEqualTo(1);
        // 3 项产出（CompletionMode 完成路径）
        await Assert.That(itemsCounter.Measurements.Sum()).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Stream_Failed_MarksActivityErrorAndCountsFailed()
    {
        using var activityListener = new RecordingActivityListener();
        using var failedCounter = new RecordingMeterListener("paldd.stream.failed");
        using var completedCounter = new RecordingMeterListener("paldd.stream.completed");

        var provider = BuildProvider(new ThrowingTelemetryHandler());
        var endpoint = MapEndpoint(provider, keepAlive: null);
        var context = CreateContext(provider);

        await endpoint(context);

        var streamActivity = activityListener.StoppedActivities.Single(a => a.OperationName == "Stream Query");
        await Assert.That(streamActivity.Status).IsEqualTo(System.Diagnostics.ActivityStatusCode.Error);
        // 状态描述是异常类型名（无消息/业务数据）
        await Assert.That(streamActivity.StatusDescription).IsEqualTo(nameof(InvalidOperationException));
        await Assert.That(failedCounter.Measurements.Sum()).IsGreaterThanOrEqualTo(1);
        await Assert.That(completedCounter.Measurements.Sum()).IsEqualTo(0);
    }

    [Test]
    public async Task Stream_Cancelled_CountsCancelledNotFailed()
    {
        using var cancelledCounter = new RecordingMeterListener("paldd.stream.cancelled");
        using var failedCounter = new RecordingMeterListener("paldd.stream.failed");

        using var cts = new CancellationTokenSource();
        var provider = BuildProvider(new CancellingTelemetryHandler(cts));
        var endpoint = MapEndpoint(provider, keepAlive: null);
        var context = CreateContext(provider);
        context.RequestAborted = cts.Token;

        await endpoint(context);

        // 取消独立计数，且失败计数不受影响（错误率告警不被客户端断开污染）
        await Assert.That(cancelledCounter.Measurements.Sum()).IsGreaterThanOrEqualTo(1);
        await Assert.That(failedCounter.Measurements.Sum()).IsEqualTo(0);
    }

    [Test]
    public async Task Stream_ValidationFailure_DoesNotCountStarted()
    {
        // 验证失败在 SSE 头写出前返回 400 → 未进入枚举，started/items 均不应增加。
        // 口径：计数器是进程级静态累计（含其他测试类的流），断言用「本测试前后差值」而非绝对值
        using var startedCounter = new RecordingMeterListener("paldd.stream.started");
        using var itemsCounter = new RecordingMeterListener("paldd.stream.items");

        var provider = BuildProvider(new TelemetryStreamHandler(),
            services => services.AddSingleton<Core.IPalValidator<TelemetryStreamQuery>, AlwaysInvalidTelemetryValidator>());
        var endpoint = MapEndpoint(provider, keepAlive: null);
        var context = CreateContext(provider);

        await endpoint(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
        await Assert.That(startedCounter.Measurements.Sum()).IsEqualTo(0);
        await Assert.That(itemsCounter.Measurements.Sum()).IsEqualTo(0);
    }

    [Test]
    public async Task SseFrameReader_ParsesEventIdDataAndComments()
    {
        const string body =
            "event: token\nid: e0\ndata: {\"i\":0}\n\n" +
            ":keepalive\n\n" +
            "event: done\ndata: {\"i\":1}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var frames = await SseFrameReader.ReadAllAsync(stream);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames[0].EventName).IsEqualTo("token");
        await Assert.That(frames[0].Id).IsEqualTo("e0");
        await Assert.That(frames[0].Data).IsEqualTo("{\"i\":0}");
        await Assert.That(frames[0].IsComment).IsFalse();
        await Assert.That(frames[1].IsComment).IsTrue();
        await Assert.That(frames[2].EventName).IsEqualTo("done");
    }

    [Test]
    public async Task SseFrameReader_JoinsMultiLineDataAndToleratesTruncation()
    {
        // 多行 data 按规范以 \n 拼接；末帧无终止空行（断线截断）也应产出
        const string body = "data: line1\ndata: line2\n\ndata: truncated";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var frames = await SseFrameReader.ReadAllAsync(stream);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Data).IsEqualTo("line1\nline2");
        await Assert.That(frames[1].Data).IsEqualTo("truncated");
    }

    [Test]
    public async Task SseFrameReader_NullStream_Throws()
    {
        await Assert.That(async () => await SseFrameReader.ReadAllAsync(null!)).Throws<ArgumentNullException>();
    }

    // ─── 夹具 ───

    private static ServiceProvider BuildProvider(
        IStreamQueryHandler<TelemetryStreamQuery, TelemetryChunk> handler,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IStreamQueryHandler<TelemetryStreamQuery, TelemetryChunk>>(_ => handler);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static RequestDelegate MapEndpoint(IServiceProvider provider, TimeSpan? keepAlive)
    {
        var endpoints = new TelemetryEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TelemetryStreamQuery, TelemetryChunk>(
            "/telemetry", ctx => new TelemetryStreamQuery("p"), TelemetryJsonContext.Default.TelemetryChunk,
            keepAliveInterval: keepAlive);
        return endpoints.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().Single().RequestDelegate!;
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TelemetryEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    [JsonSerializable(typeof(TelemetryChunk))]
    private sealed partial class TelemetryJsonContext : JsonSerializerContext;
}

internal sealed record TelemetryChunk(int Index);

internal sealed record TelemetryStreamQuery(string Prompt) : IStreamQuery<TelemetryChunk>;

internal sealed class TelemetryStreamHandler : IStreamQueryHandler<TelemetryStreamQuery, TelemetryChunk>
{
    public async IAsyncEnumerable<TelemetryChunk> HandleAsync(
        TelemetryStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TelemetryChunk(0);
        await Task.Yield();
        yield return new TelemetryChunk(1);
        await Task.Yield();
        yield return new TelemetryChunk(2);
    }
}

internal sealed class ThrowingTelemetryHandler : IStreamQueryHandler<TelemetryStreamQuery, TelemetryChunk>
{
    public async IAsyncEnumerable<TelemetryChunk> HandleAsync(
        TelemetryStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TelemetryChunk(0);
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }
}

internal sealed class CancellingTelemetryHandler(CancellationTokenSource cts)
    : IStreamQueryHandler<TelemetryStreamQuery, TelemetryChunk>
{
    public async IAsyncEnumerable<TelemetryChunk> HandleAsync(
        TelemetryStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TelemetryChunk(0);
        cts.Cancel();
        await Task.Delay(50, ct);
        yield return new TelemetryChunk(1);
    }
}

internal sealed class AlwaysInvalidTelemetryValidator : Core.IPalValidator<TelemetryStreamQuery>
{
    public Core.PalValidationResult Validate(TelemetryStreamQuery instance)
        => Core.PalValidationResult.Failed("prompt", "required");
}
