using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PalDDD.Core;
using PalDDD.Core.Logging;
using PalDDD.CQRS;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 📡 流式端点测试 — MapStreamQuery ×2 + SSE 帧 + 取消传播（ADR-030）
// ═══════════════════════════════════════════════════════════════
// 镜像 ExceptionMiddlewareTests/HostingExtensionsTests 的 DefaultHttpContext 形态
//（无 TestServer 依赖）：MapGet/MapPost 注册后直取 RouteEndpoint.RequestDelegate 调用。
// 覆盖 ADR-030 验证计划中 Hosting 侧六场景 + D4 零分配契约 + D6 生命周期日志。

public sealed partial class StreamQueryEndpointTests
{
    // ─── happy path（GET）：帧序 / 帧格式 / 响应头 ───

    [Test]
    public async Task MapStreamQuery_Get_HappyPath_WritesFramesInOrderAndSseHeaders()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(context.Response.ContentType).IsEqualTo("text/event-stream; charset=utf-8");
        await Assert.That(context.Response.Headers["Cache-Control"].ToString()).Contains("no-cache");
        await Assert.That(context.Response.Headers["X-Accel-Buffering"].ToString()).IsEqualTo("no");

        var body = ReadBody(context);
        await Assert.That(body).IsEqualTo(
            "data: {\"Index\":0,\"Text\":\"Hello\"}\n\n" +
            "data: {\"Index\":1,\"Text\":\" stream\"}\n\n" +
            "data: {\"Index\":2,\"Text\":\" world\"}\n\n");
    }

    [Test]
    public async Task MapStreamQuery_KeepAliveBeyondTaskDelayLimit_ThrowsAtMapTime()
    {
        // R11 F21 回归锁：超过 Task.Delay 上限的心跳间隔必须在映射期拒绝，而不是
        // 每个请求 200 + event: error。
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);

        await Assert.That(() => endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
                "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
                keepAliveInterval: TimeSpan.FromDays(60)))
            .Throws<ArgumentOutOfRangeException>();
    }

    // ─── happy path（POST）：JSON body 反序列化 + 帧序列 ───

    [Test]
    public async Task MapStreamQuery_Post_HappyPath_ReadsJsonBodyAndWritesFrames()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", StreamTestJsonContext.Default.TokenStreamQuery, StreamTestJsonContext.Default.TokenChunk);

        var context = CreatePostContext(provider, "{\"Prompt\":\"hi\"}");
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(ReadBody(context)).Contains("data: {\"Index\":0,\"Text\":\"Hello\"}");
    }

    // ─── 验证失败：400 且在 SSE 头写出前 ───

    [Test]
    public async Task MapStreamQuery_Get_ValidationFailure_Returns400BeforeSseHeaders()
    {
        var provider = BuildProvider(new TokenStreamHandler(),
            services => services.AddSingleton<IPalValidator<TokenStreamQuery>, EmptyPromptValidator>());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery(string.Empty), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
        // 头未写出证据：Content-Type 仍是 400 ProblemDetails 的 JSON（框架默认 charset 形态），
        // 非 text/event-stream——状态码可回退是「验证前置」的核心契约
        await Assert.That(context.Response.ContentType).IsNotEqualTo("text/event-stream; charset=utf-8");
        var body = ReadBody(context);
        await Assert.That(body).Contains("Validation Failed");
        await Assert.That(body).Contains("prompt");
    }

    // ─── 客户端断开：取消传播进 handler 的 finally + 静默收尾 ───

    [Test]
    public async Task MapStreamQuery_ClientDisconnect_CancellationPropagatesToHandlerFinally()
    {
        using var cts = new CancellationTokenSource();
        var handler = new SelfCancellingStreamHandler(cts);
        var provider = BuildProvider(handler);
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        context.RequestAborted = cts.Token; // 端点从此令牌取 ct（RequestAborted）
        await GetRequestDelegate(endpoints)(context);

        // 头已写出（200 + SSE），取消按流截断静默收尾——无 error 帧、无 500
        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        var body = ReadBody(context);
        await Assert.That(body).Contains("data: {\"Index\":0,\"Text\":\"first\"}");
        await Assert.That(body).DoesNotContain("event: error");
        await Assert.That(body).DoesNotContain("second");

        // D5 契约实证：handler 收到的是 RequestAborted 同一令牌，且 finally 清理已执行
        //（ReceivedToken 为 null 时下一行 .Value 即抛，无需冗余 IsNotNull 弱断言）
        await Assert.That(handler.ReceivedToken!.Value.Equals(cts.Token)).IsTrue();
        await Assert.That(handler.FinallyRan).IsTrue();
    }

    // ─── 中途异常：event: error 帧 + 流终止（头已写出无法回退 500） ───

    [Test]
    public async Task MapStreamQuery_MidStreamException_WritesErrorFrameAndEndsStream()
    {
        var provider = BuildProvider(new ThrowingStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        var body = ReadBody(context);
        // 首帧正常 + event: error 补偿帧 + 终止；异常不逃逸为 delegate 抛出
        await Assert.That(body).Contains("data: {\"Index\":0,\"Text\":\"before-throw\"}");
        await Assert.That(body).Contains("event: error");
        await Assert.That(body).Contains("The stream failed unexpectedly.");
        // 帧序：数据帧在前、error 帧在后
        await Assert.That(body.IndexOf("event: error", StringComparison.Ordinal))
            .IsGreaterThan(body.IndexOf("before-throw", StringComparison.Ordinal));
    }

    // ─── POST 错误形态：415 / 畸形 JSON（与 MapCommand 逐字一致） ───

    [Test]
    public async Task MapStreamQuery_Post_NonJsonContentType_Returns415()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", StreamTestJsonContext.Default.TokenStreamQuery, StreamTestJsonContext.Default.TokenChunk);

        var context = CreatePostContext(provider, string.Empty, "text/plain");
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status415UnsupportedMediaType);
        await Assert.That(ReadBody(context)).Contains("Unsupported Media Type");
    }

    [Test]
    public async Task MapStreamQuery_Post_MalformedJson_Returns400()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", StreamTestJsonContext.Default.TokenStreamQuery, StreamTestJsonContext.Default.TokenChunk);

        var context = CreatePostContext(provider, "{ not json");
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
        await Assert.That(ReadBody(context)).Contains("Request body is not valid JSON.");
    }

    // ─── formatFrame 定制帧（D4 分型事件流） ───

    [Test]
    public async Task MapStreamQuery_FormatFrame_WritesCustomFrames()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            formatFrame: item => $"event: chunk\ndata: {item.Index}:{item.Text}\n\n");

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(ReadBody(context)).IsEqualTo(
            "event: chunk\ndata: 0:Hello\n\n" +
            "event: chunk\ndata: 1: stream\n\n" +
            "event: chunk\ndata: 2: world\n\n");
    }

    // ─── 参数守卫（对齐 MapCommand 的 null 守卫口径） ───

    [Test]
    public async Task MapStreamQuery_NullArguments_Throws()
    {
        await Assert.That(() =>
            ((IEndpointRouteBuilder)null!).MapStreamQuery<TokenStreamQuery, TokenChunk>(
                "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk))
            .Throws<ArgumentNullException>();
        await Assert.That(() =>
            ((IEndpointRouteBuilder)null!).MapStreamQuery<TokenStreamQuery, TokenChunk>(
                "/tokens", StreamTestJsonContext.Default.TokenStreamQuery, StreamTestJsonContext.Default.TokenChunk))
            .Throws<ArgumentNullException>();
    }

    // ─── D6 生命周期日志（IsEnabled 门控 + TimeProvider 可注入） ───

    [Test]
    public async Task MapStreamQuery_LifecycleLogging_RecordsStartAndCompletion()
    {
        var logger = new RecordingPalLogger<StreamQueryEndpoint<TokenStreamQuery, TokenChunk>>();
        var provider = BuildProvider(new TokenStreamHandler(), services =>
        {
            services.AddSingleton<IPalLogger<StreamQueryEndpoint<TokenStreamQuery, TokenChunk>>>(logger);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        });
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(logger.Entries).Count().IsEqualTo(2);
        await Assert.That(logger.Entries[0].Message).IsEqualTo("Stream TokenStreamQuery: started");
        await Assert.That(logger.Entries[1].Message).Contains("completed 3 item(s)");
    }

    [Test]
    public async Task MapStreamQuery_LifecycleLogging_ErrorRecordsException()
    {
        var logger = new RecordingPalLogger<StreamQueryEndpoint<TokenStreamQuery, TokenChunk>>();
        var provider = BuildProvider(new ThrowingStreamHandler(),
            services => services.AddSingleton<IPalLogger<StreamQueryEndpoint<TokenStreamQuery, TokenChunk>>>(logger));
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        var errorEntry = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Error);
        await Assert.That(errorEntry.Exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(errorEntry.Message).Contains("failed after");
    }

    [Test]
    public async Task MapStreamQuery_LifecycleLogging_IsEnabledFalse_SkipsAll()
    {
        // IsEnabled 门控实证：关闭时零记录（插值字符串不构造——与 LoggingBehavior 同款语义）
        var logger = new RecordingPalLogger<StreamQueryEndpoint<TokenStreamQuery, TokenChunk>> { Enabled = false };
        var provider = BuildProvider(new TokenStreamHandler(),
            services => services.AddSingleton<IPalLogger<StreamQueryEndpoint<TokenStreamQuery, TokenChunk>>>(logger));
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk);

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(logger.Entries).IsEmpty();
    }

    // ─── D4 零分配契约（framing span 直写；变异红证明的锁定测试） ───

    [Test]
    public async Task WriteDataFrame_FramingPath_ZeroAllocationAfterWarmup()
    {
        // 锁定 StreamFrameWriter.WriteDataFrame 的 framing 段零分配（span GetSpan/Advance，
        // 无字符串中转）。JSON 载荷字节在循环外准备（序列化器输出不在本契约范围）。
        // 量具说明：用预分配固定缓冲的 PipeWriter 替身（GetSpan/Advance/FlushAsync 零分配）
        // 把 framing 路径与 BCL StreamPipeWriter 内部机制隔离——实测真实管道 FlushAsync
        // 每帧 ~131B 内部分配（缓冲区段管理），属管道实现而非本契约范围；替身下 framing
        // 循环预期 0 B。
        // 变异红证明：替换为字符串形态（Serialize → 插值帧串 → GetBytes）后本断言判红
        //（每帧新增 ~3× 载荷尺寸，1000 帧 ~150KB）。
        var writer = new PreallocatedPipeWriter();
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new TokenChunk(0, "allocation-probe-chunk"), StreamTestJsonContext.Default.TokenChunk);

        const int Iterations = 1000;
        for (var i = 0; i < Iterations; i++)
        {
            StreamFrameWriter.WriteDataFrame(writer, json);
            await writer.FlushAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
        {
            StreamFrameWriter.WriteDataFrame(writer, json);
            await writer.FlushAsync();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task WriteDataFrame_ProducesDataOnlyFrame()
    {
        using var stream = new MemoryStream();
        var writer = PipeWriter.Create(stream);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new TokenChunk(0, "Hello"), StreamTestJsonContext.Default.TokenChunk);

        StreamFrameWriter.WriteDataFrame(writer, json);
        await writer.FlushAsync();

        await Assert.That(Encoding.UTF8.GetString(stream.ToArray()))
            .IsEqualTo("data: {\"Index\":0,\"Text\":\"Hello\"}\n\n");
    }

    // ─── Phase 2：event/id 富化帧（ADR-030 分期节收窄实施） ───

    [Test]
    public async Task MapStreamQuery_EventSelector_WritesEventFrames()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            eventSelector: item => item.Index == 2 ? "done" : "token");

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(ReadBody(context)).IsEqualTo(
            "event: token\ndata: {\"Index\":0,\"Text\":\"Hello\"}\n\n" +
            "event: token\ndata: {\"Index\":1,\"Text\":\" stream\"}\n\n" +
            "event: done\ndata: {\"Index\":2,\"Text\":\" world\"}\n\n");
    }

    [Test]
    public async Task MapStreamQuery_IdSelector_WritesIdFrames()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            idSelector: item => item.Index.ToString());

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(ReadBody(context)).IsEqualTo(
            "id: 0\ndata: {\"Index\":0,\"Text\":\"Hello\"}\n\n" +
            "id: 1\ndata: {\"Index\":1,\"Text\":\" stream\"}\n\n" +
            "id: 2\ndata: {\"Index\":2,\"Text\":\" world\"}\n\n");
    }

    [Test]
    public async Task MapStreamQuery_EventAndIdSelectors_WritesEnrichedFramesInOrder()
    {
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            eventSelector: item => "chunk", idSelector: item => $"e{item.Index}");

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        // 行序契约：event 行 → id 行 → data 行（缺项行省略；顺序写入端固定）
        await Assert.That(ReadBody(context)).IsEqualTo(
            "event: chunk\nid: e0\ndata: {\"Index\":0,\"Text\":\"Hello\"}\n\n" +
            "event: chunk\nid: e1\ndata: {\"Index\":1,\"Text\":\" stream\"}\n\n" +
            "event: chunk\nid: e2\ndata: {\"Index\":2,\"Text\":\" world\"}\n\n");
    }

    [Test]
    public async Task MapStreamQuery_FormatFrame_TakesPrecedenceOverSelectors()
    {
        // 帧优先级契约：formatFrame（完整帧覆写）> event/id 选择器（框架富化帧）
        var provider = BuildProvider(new TokenStreamHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            formatFrame: item => $"event: custom\ndata: {item.Index}\n\n",
            eventSelector: item => "ignored",
            idSelector: item => "ignored");

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(ReadBody(context)).IsEqualTo(
            "event: custom\ndata: 0\n\n" +
            "event: custom\ndata: 1\n\n" +
            "event: custom\ndata: 2\n\n");
    }

    // ─── Phase 2：心跳 keepalive（agent 场景结构性静默间隙的中间设备保活） ───

    [Test]
    public async Task MapStreamQuery_KeepAlive_WritesHeartbeatDuringIdleGap()
    {
        // handler 首项同步产出、次项延迟 150ms；心跳间隔 30ms → 静默窗口内心跳先于次帧到达
        var provider = BuildProvider(new SlowSecondItemHandler());
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            keepAliveInterval: TimeSpan.FromMilliseconds(30));

        var context = CreateContext(provider);
        await GetRequestDelegate(endpoints)(context);

        var body = ReadBody(context);
        // 心跳帧存在且早于慢帧（注释帧 :keepalive 不被计入 item，慢帧仍完整收尾）
        await Assert.That(body).Contains(":keepalive");
        await Assert.That(body.IndexOf(":keepalive", StringComparison.Ordinal))
            .IsLessThan(body.IndexOf("\"slow\"", StringComparison.Ordinal));
        await Assert.That(body).EndsWith("data: {\"Index\":1,\"Text\":\"slow\"}\n\n");
    }

    [Test]
    public async Task MapStreamQuery_KeepAlive_ClientDisconnect_EndsSilentlyWithoutErrorFrame()
    {
        // 取消优先于心跳：静默窗口中取消 → 无心跳补救、无 error 帧、handler finally 执行
        using var cts = new CancellationTokenSource();
        var handler = new IndefiniteWaitHandler();
        var provider = BuildProvider(handler);
        var endpoints = new StreamTestEndpointRouteBuilder(provider);
        endpoints.MapStreamQuery<TokenStreamQuery, TokenChunk>(
            "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
            keepAliveInterval: TimeSpan.FromMilliseconds(30));

        var context = CreateContext(provider);
        context.RequestAborted = cts.Token;
        var delegateTask = GetRequestDelegate(endpoints)(context);

        await Task.Delay(100); // 覆盖 2-3 个心跳窗口后取消
        await cts.CancelAsync();
        await delegateTask;

        var body = ReadBody(context);
        await Assert.That(body).Contains("data: {\"Index\":0,\"Text\":\"only\"}");
        await Assert.That(body).DoesNotContain("event: error");
        await Assert.That(handler.FinallyRan).IsTrue();
    }

    [Test]
    public async Task MapStreamQuery_KeepAliveInterval_ZeroOrNegative_Throws()
    {
        // 映射期守卫：零/负间隔会让 Task.Delay 立即完成形成心跳风暴
        await Assert.That(() =>
            new StreamTestEndpointRouteBuilder(BuildProvider(new TokenStreamHandler()))
                .MapStreamQuery<TokenStreamQuery, TokenChunk>(
                    "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
                    keepAliveInterval: TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() =>
            new StreamTestEndpointRouteBuilder(BuildProvider(new TokenStreamHandler()))
                .MapStreamQuery<TokenStreamQuery, TokenChunk>(
                    "/tokens", ctx => new TokenStreamQuery("hi"), StreamTestJsonContext.Default.TokenChunk,
                    keepAliveInterval: TimeSpan.FromSeconds(-1)))
            .Throws<ArgumentOutOfRangeException>();
    }

    // ─── Phase 2：Last-Event-ID 重连入口 ───

    [Test]
    public async Task GetLastEventId_ReadsHeaderValue()
    {
        var context = CreateContext(BuildProvider(new TokenStreamHandler()));
        context.Request.Headers["Last-Event-ID"] = "evt-42";

        await Assert.That(context.GetLastEventId()).IsEqualTo("evt-42");
    }

    [Test]
    public async Task GetLastEventId_MissingOrEmptyHeader_ReturnsNull()
    {
        var missing = CreateContext(BuildProvider(new TokenStreamHandler()));
        await Assert.That(missing.GetLastEventId()).IsNull();

        var empty = CreateContext(BuildProvider(new TokenStreamHandler()));
        empty.Request.Headers["Last-Event-ID"] = string.Empty;
        await Assert.That(empty.GetLastEventId()).IsNull();

        await Assert.That(() =>
            ((HttpContext)null!).GetLastEventId()).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WriteDataFrame_EnrichedFrame_FramingZeroAllocationAfterWarmup()
    {
        // 富化帧 framing 零分配（event/id 字符串 UTF-8 直写 span，无中间 byte[]）——
        // 与 data-only 帧同款量具（预分配 PipeWriter 替身隔离 BCL 管道内部分配）。
        // 探针输入（id 字符串）循环外预构造，测量段只剩 framing 本身
        var writer = new PreallocatedPipeWriter();
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new TokenChunk(0, "allocation-probe-chunk"), StreamTestJsonContext.Default.TokenChunk);
        var ids = new string[1000];
        for (var i = 0; i < ids.Length; i++) ids[i] = $"e{i}";

        for (var i = 0; i < ids.Length; i++)
        {
            StreamFrameWriter.WriteDataFrame(writer, json, "token", ids[i]);
            await writer.FlushAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ids.Length; i++)
        {
            StreamFrameWriter.WriteDataFrame(writer, json, "token", ids[i]);
            await writer.FlushAsync();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }

    // ─── P1：帧载荷序列化池化（消除每项 SerializeToUtf8Bytes 的 56B byte[]） ───

    [Test]
    public async Task StreamFrameSerializer_PerItem_ZeroAllocationAfterWarmup()
    {
        // 锁定池化序列化稳态每项零堆分配（探针实测 0.0 B/项 vs SerializeToUtf8Bytes 56.0 B/项，
        // 见 docs/performance.md 流式读链路首测节）。预热先建池（首调构造 writer/buffer）
        var item = new TokenChunk(7, "token");
        const int Iterations = 1000;
        for (var i = 0; i < Iterations; i++)
            _ = StreamFrameSerializer.Serialize(item, StreamTestJsonContext.Default.TokenChunk);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            _ = StreamFrameSerializer.Serialize(item, StreamTestJsonContext.Default.TokenChunk);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task StreamFrameSerializer_OutputMatchesSerializeToUtf8Bytes()
    {
        // 输出等价锁定：池化路径与 SerializeToUtf8Bytes 逐位一致（防 writer 状态/Reset 缺陷）
        var items = new[]
        {
            new TokenChunk(0, "Hello"),
            new TokenChunk(1, " stream"),
            new TokenChunk(2, "world")
        };
        foreach (var item in items)
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(item, StreamTestJsonContext.Default.TokenChunk);
            var actual = StreamFrameSerializer.Serialize(item, StreamTestJsonContext.Default.TokenChunk);
            await Assert.That(actual.SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    public async Task StreamFrameSerializer_SpanValidUntilNextCall_ConsecutiveItemsDistinct()
    {
        // 池内缓冲生命周期契约：返回 span 仅同线程下次调用前有效；连续两项内容各自正确
        // （防 Clear/Reset 时序缺陷导致两项串内容）
        var first = StreamFrameSerializer.Serialize(new TokenChunk(0, "a"), StreamTestJsonContext.Default.TokenChunk);
        var firstCopy = first.ToArray();
        var second = StreamFrameSerializer.Serialize(new TokenChunk(1, "b"), StreamTestJsonContext.Default.TokenChunk);
        var secondCopy = second.ToArray();

        await Assert.That(firstCopy.SequenceEqual(secondCopy)).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(firstCopy)).Contains("\"a\"");
        await Assert.That(Encoding.UTF8.GetString(secondCopy)).Contains("\"b\"");
    }

    // ─── 测试基础设施 ───

    private static ServiceProvider BuildProvider(
        IStreamQueryHandler<TokenStreamQuery, TokenChunk> handler,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IStreamQueryHandler<TokenStreamQuery, TokenChunk>>(_ => handler);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.RequestServices = provider; // 直调 delegate 时无路由中间件——手工补齐请求作用域
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreatePostContext(IServiceProvider provider, string body, string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.RequestServices = provider;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static RequestDelegate GetRequestDelegate(IEndpointRouteBuilder endpoints)
        => endpoints.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().Single().RequestDelegate!;

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class StreamTestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    /// <summary>预分配固定缓冲的 PipeWriter 替身——GetSpan/Advance/FlushAsync 全程零分配，
    /// 隔离 BCL StreamPipeWriter 的每帧内部分配（实测 ~131B/帧，属管道缓冲段管理）。</summary>
    private sealed class PreallocatedPipeWriter : PipeWriter
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _written;

        public override void Advance(int bytes) => _written += bytes;

        public override void CancelPendingFlush() { }

        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            _written = 0; // 模拟消费方读走——缓冲复位，下帧从头覆写
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_written);

        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.AsSpan(_written);

        public override bool CanGetUnflushedBytes => false;

        public override long UnflushedBytes => _written;
    }

    // 源生成 JsonContext——仓库全局 JsonSerializerIsReflectionEnabledByDefault=false
    [JsonSerializable(typeof(TokenChunk))]
    [JsonSerializable(typeof(TokenStreamQuery))]
    private sealed partial class StreamTestJsonContext : JsonSerializerContext;
}

// ─── 测试模型 ───

internal sealed record TokenChunk(int Index, string Text);

internal sealed record TokenStreamQuery(string Prompt) : IStreamQuery<TokenChunk>;

internal sealed class TokenStreamHandler : IStreamQueryHandler<TokenStreamQuery, TokenChunk>
{
    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TokenChunk(0, "Hello");
        await Task.Yield();
        yield return new TokenChunk(1, " stream");
        await Task.Yield();
        yield return new TokenChunk(2, " world");
    }
}

/// <summary>产出首项后自行取消（模拟 RequestAborted 触发）——验证取消传播与 finally。</summary>
internal sealed class SelfCancellingStreamHandler(CancellationTokenSource cts)
    : IStreamQueryHandler<TokenStreamQuery, TokenChunk>
{
    public bool FinallyRan { get; private set; }

    public CancellationToken? ReceivedToken { get; private set; }

    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ReceivedToken = ct;
        try
        {
            yield return new TokenChunk(0, "first");
            cts.Cancel();
            await Task.Delay(50, ct).ConfigureAwait(false);
            yield return new TokenChunk(1, "second");
        }
        finally
        {
            FinallyRan = true;
        }
    }
}

internal sealed class ThrowingStreamHandler : IStreamQueryHandler<TokenStreamQuery, TokenChunk>
{
    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TokenChunk(0, "before-throw");
        await Task.Yield();
        throw new InvalidOperationException("handler exploded");
    }
}

/// <summary>首项同步产出、次项延迟 150ms——心跳窗口探针。</summary>
internal sealed class SlowSecondItemHandler : IStreamQueryHandler<TokenStreamQuery, TokenChunk>
{
    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TokenChunk(0, "fast");
        await Task.Delay(150, ct).ConfigureAwait(false);
        yield return new TokenChunk(1, "slow");
    }
}

/// <summary>首项后无限等待 ct——取消竞速探针（finally 清理记录）。</summary>
internal sealed class IndefiniteWaitHandler : IStreamQueryHandler<TokenStreamQuery, TokenChunk>
{
    public bool FinallyRan { get; private set; }

    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            yield return new TokenChunk(0, "only");
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield return new TokenChunk(1, "never");
        }
        finally
        {
            FinallyRan = true;
        }
    }
}

internal sealed class EmptyPromptValidator : IPalValidator<TokenStreamQuery>
{
    public PalValidationResult Validate(TokenStreamQuery instance)
        => string.IsNullOrWhiteSpace(instance.Prompt)
            ? PalValidationResult.Failed("prompt", "Prompt is required.")
            : PalValidationResult.Success();
}

/// <summary>D6 生命周期日志记录器（IVT 可见 internal 标记类别作门面实参）。</summary>
internal sealed class RecordingPalLogger<T> : IPalLogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public bool Enabled { get; set; } = true;

    public void Debug(string message) => Entries.Add((LogLevel.Debug, message, null));

    public void Information(string message) => Entries.Add((LogLevel.Information, message, null));

    public void Warning(string message) => Entries.Add((LogLevel.Warning, message, null));

    public void Error(Exception ex, string message) => Entries.Add((LogLevel.Error, message, ex));

    public bool IsEnabled(LogLevel level) => Enabled;
}
