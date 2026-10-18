// ═══════════════════════════════════════════════════════════════
// ⑦ 流式读链路（ADR-030）— SSE 帧路径 / 富化帧 / 心跳竞速 / 零拷贝对照 / 端点全周期
// ═══════════════════════════════════════════════════════════════
// 口径声明（框架开销层，同 HostingBenchmarks）：DefaultHttpContext 直调 RequestDelegate
// + 内存 PipeWriter/MemoryStream，无网络栈、无真实 DB I/O。引用须带此口径——生产数字
// 由网络 RTT 与 provider 延迟主导（ADR-030 Phase 2 收窄论证的延迟地板论）。
//
// 回答 ADR-030 Phase 2 收窄论证的三个待测项（论证全文在 ADR-030 分期节实施附记）：
//   ① 帧路径三分支（默认 data-only / event-id 富化 / formatFrame 委托）的每帧 CPU 与分配；
//   ② 心跳竞速路径相对 await foreach 快路径的每项开销（同步项是否仍零额外开销）；
//   ③ PipeWriter 零拷贝化（Utf8JsonWriter 直写 PipeWriter）相对 SerializeToUtf8Bytes
//      的每帧收益——ADR 复活触发器 3 的 bench 证据来源（结论回写该触发器标注）。
//
// lane 分组：
//   Micro（预分配 PipeWriter 替身，隔离 BCL 管道内部分配——StreamPipeWriter.FlushAsync
//         实测 ~131B/帧属管道实现，见 pitfalls/hosting 测试同款替身）：
//         Frame_Default / Frame_Enriched / Frame_FormatFrame / Serialize_PerItem /
//         SerializeAndFrame / ZeroCopyFrame（零拷贝对照）
//   IO（MemoryStream 背 PipeWriter + 每帧 FlushAsync——真实响应路径的每帧成本形状）：
//         PerFrame_Default_WithFlush / PerFrame_ZeroCopy_WithFlush
//   Endpoint（公共链路全周期：绑定→验证→SSE 头→枚举写帧，16 异步项/请求）：
//         Endpoint_Plain / Endpoint_Enriched / Endpoint_Heartbeat

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using PalDDD.Hosting.AspNetCore;
using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalDDD.Benchmarks;

[MemoryDiagnoser]
// ⚠️ 无类级 RunJob 特性：--stream 入口传显式 MediumRun InProcess ManualConfig（同 HostingBenchmarks 注记；
// 方括号形态写进注释会被 TestGateGuardTests 的 job 特性正则误判为特性行，故本注释刻意不带方括号）
public class StreamReadBenchmarks
{
    // ─── Micro / IO lanes 状态 ───

    private byte[] _chunkJson = null!;
    private StreamBenchChunk _chunk = null!;
    private string _formatFrameText = null!;
    private PreallocatedSink _sink = null!;          // Micro：预分配缓冲（framing 隔离）
    private PipeWriter _memoryWriter = null!;        // IO：丢弃流背（含 FlushAsync，无存储增长）
    private DiscardStream _discardStream = null!;

    // ─── Endpoint lanes 状态 ───

    private RequestDelegate _plainEndpoint = null!;
    private RequestDelegate _enrichedEndpoint = null!;
    private RequestDelegate _heartbeatEndpoint = null!;
    private IServiceProvider _requestServices = null!;
    private const int ItemsPerRequest = 16;

    [GlobalSetup]
    public void Setup()
    {
        _chunk = new StreamBenchChunk(7, "token");
        _chunkJson = JsonSerializer.SerializeToUtf8Bytes(_chunk, StreamBenchJsonContext.Default.StreamBenchChunk);
        // formatFrame 委托产出的典型完整帧文本（与默认帧同尺寸量级）
        _formatFrameText = "event: token\ndata: {\"Index\":7,\"Text\":\"token\"}\n\n";

        _sink = new PreallocatedSink();
        _discardStream = new DiscardStream();
        _memoryWriter = PipeWriter.Create(_discardStream);

        SetupEndpoints();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _memoryWriter?.Complete();
        _discardStream?.Dispose();
        (_requestServices as IDisposable)?.Dispose();
    }

    private void SetupEndpoints()
    {
        var handlerServices = new ServiceCollection();
        handlerServices.AddScoped<IStreamQueryHandler<StreamBenchQuery, StreamBenchChunk>, StreamBenchHandler>();
        _requestServices = handlerServices.BuildServiceProvider();

        var appBuilder = WebApplication.CreateBuilder();
        // 随机端口：BDN LaunchCount=2 的两次启动若绑同一固定端口，第二次必 AddressInUse
        // （Launch 1 套接字释放时序）→ GlobalSetup 失败 → 端点 lane 全 NA（本轮实证）。
        // 与 AotSample §7 同款 127.0.0.1:0 形态
        appBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = appBuilder.Build();
        app.MapStreamQuery<StreamBenchQuery, StreamBenchChunk>(
            "/bench-plain",
            ctx => new StreamBenchQuery(ctx.Request.Query["prompt"].ToString()),
            StreamBenchJsonContext.Default.StreamBenchChunk);
        app.MapStreamQuery<StreamBenchQuery, StreamBenchChunk>(
            "/bench-enriched",
            ctx => new StreamBenchQuery(ctx.Request.Query["prompt"].ToString()),
            StreamBenchJsonContext.Default.StreamBenchChunk,
            eventSelector: chunk => "token",
            idSelector: chunk => chunk.Index.ToString());
        app.MapStreamQuery<StreamBenchQuery, StreamBenchChunk>(
            "/bench-heartbeat",
            ctx => new StreamBenchQuery(ctx.Request.Query["prompt"].ToString()),
            StreamBenchJsonContext.Default.StreamBenchChunk,
            // 1s 间隔：单请求（16 异步项，μs 级完成）内心跳不写出——竞速开销本身被测量
            keepAliveInterval: TimeSpan.FromSeconds(1));
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            var endpoints = app.Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>().ToList();
            _plainEndpoint = endpoints.Single(e => e.RoutePattern.RawText == "/bench-plain").RequestDelegate!;
            _enrichedEndpoint = endpoints.Single(e => e.RoutePattern.RawText == "/bench-enriched").RequestDelegate!;
            _heartbeatEndpoint = endpoints.Single(e => e.RoutePattern.RawText == "/bench-heartbeat").RequestDelegate!;
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    // ─── Micro lanes：framing 分支（预分配替身，无管道内部分配噪声） ───

    [Benchmark(Baseline = true)]
    public void Frame_Default()
        => StreamFrameWriter.WriteDataFrame(_sink, _chunkJson);

    [Benchmark]
    public void Frame_Enriched()
        => StreamFrameWriter.WriteDataFrame(_sink, _chunkJson, "token", "e7");

    [Benchmark]
    public void Frame_FormatFrame()
        => StreamFrameWriter.WriteRawFrame(_sink, _formatFrameText);

    [Benchmark]
    public void Serialize_PerItem()
        => JsonSerializer.SerializeToUtf8Bytes(_chunk, StreamBenchJsonContext.Default.StreamBenchChunk);

    [Benchmark]
    public void SerializeAndFrame()
        => StreamFrameWriter.WriteDataFrame(
            _sink, JsonSerializer.SerializeToUtf8Bytes(_chunk, StreamBenchJsonContext.Default.StreamBenchChunk));

    [Benchmark]
    public void ZeroCopyFrame()
    {
        // PipeWriter 零拷贝对照（ADR-030 Phase 2 收窄项③的候选形态）：Utf8JsonWriter
        // 直写 PipeWriter，无中间 byte[]。ref struct 每次调用栈上构造零堆分配
        var span = _sink.GetSpan(6);
        "data: "u8.CopyTo(span);
        _sink.Advance(6);
        var writer = new Utf8JsonWriter(_sink);
        JsonSerializer.Serialize(writer, _chunk, StreamBenchJsonContext.Default.StreamBenchChunk);
        writer.Flush();
        writer.Dispose();
        var tail = _sink.GetSpan(2);
        "\n\n"u8.CopyTo(tail);
        _sink.Advance(2);
    }

    // ─── IO lanes：每帧含 FlushAsync（MemoryStream 背，真实响应路径成本形状） ───

    [Benchmark]
    public async Task PerFrame_Default_WithFlush()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_chunk, StreamBenchJsonContext.Default.StreamBenchChunk);
        StreamFrameWriter.WriteDataFrame(_memoryWriter, json);
        await _memoryWriter.FlushAsync();
    }

    [Benchmark]
    public async Task PerFrame_ZeroCopy_WithFlush()
    {
        var span = _memoryWriter.GetSpan(6);
        "data: "u8.CopyTo(span);
        _memoryWriter.Advance(6);
        var writer = new Utf8JsonWriter(_memoryWriter);
        JsonSerializer.Serialize(writer, _chunk, StreamBenchJsonContext.Default.StreamBenchChunk);
        writer.Flush();
        writer.Dispose();
        var tail = _memoryWriter.GetSpan(2);
        "\n\n"u8.CopyTo(tail);
        _memoryWriter.Advance(2);
        await _memoryWriter.FlushAsync();
    }

    // ─── Endpoint lanes：公共链路全周期（16 异步项/请求） ───

    [Benchmark]
    public async Task<int> Endpoint_Plain()
        => await InvokeEndpoint(_plainEndpoint);

    [Benchmark]
    public async Task<int> Endpoint_Enriched()
        => await InvokeEndpoint(_enrichedEndpoint);

    [Benchmark]
    public async Task<int> Endpoint_Heartbeat()
        => await InvokeEndpoint(_heartbeatEndpoint);

    private async Task<int> InvokeEndpoint(RequestDelegate endpoint)
    {
        // 每请求 CTS：端点从 RequestAborted 取 ct；请求结束取消使共享心跳延时与迭代器
        // 取消注册整体释放（生产同形态——Kestrel 请求上下文回收时取消）——同时避免
        // bench 高 op 率下在途延时的计时器堆积污染测量
        using var cts = new CancellationTokenSource();
        var ctx = new DefaultHttpContext { RequestServices = _requestServices };
        ctx.Request.Method = "GET";
        ctx.Request.QueryString = new QueryString("?prompt=bench");
        ctx.RequestAborted = cts.Token;
        ctx.Response.Body = new MemoryStream();
        await endpoint(ctx);
        await cts.CancelAsync();
        return ctx.Response.StatusCode;
    }

    // ─── 测试模型与量具替身 ───

    internal sealed record StreamBenchChunk(int Index, string Text);

    internal sealed record StreamBenchQuery(string Prompt) : CQRS.IStreamQuery<StreamBenchChunk>;

    /// <summary>16 异步项（Task.Yield 分隔）——模拟 LLM token 流的异步产出形状。</summary>
    internal sealed class StreamBenchHandler : IStreamQueryHandler<StreamBenchQuery, StreamBenchChunk>
    {
        public async IAsyncEnumerable<StreamBenchChunk> HandleAsync(
            StreamBenchQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < ItemsPerRequest; i++)
            {
                yield return new StreamBenchChunk(i, "token");
                await Task.Yield();
            }
        }
    }

    /// <summary>预分配固定缓冲 PipeWriter 替身——GetSpan/Advance 全程零分配，把 framing 微 lane
    /// 与 BCL StreamPipeWriter 的每帧 ~131B 内部分配隔离（实测口径见 Hosting 测试同款替身注释）。
    /// 微 lane 不调 FlushAsync（framing 隔离），故 Advance 在接近容量时自行复位模拟消费方读走；
    /// 复位只做游标管理，帧内容不做校验。</summary>
    private sealed class PreallocatedSink : PipeWriter
    {
        private const int Headroom = 16 * 1024;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _written;

        public override void Advance(int bytes)
        {
            _written += bytes;
            if (_written > _buffer.Length - Headroom)
                _written = 0;
        }

        public override void CancelPendingFlush() { }

        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            _written = 0;
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_written);

        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.AsSpan(_written);

        public override bool CanGetUnflushedBytes => false;

        public override long UnflushedBytes => _written;
    }

    /// <summary>只写丢弃流——记录写入量不存储，供 IO lane 的 PipeWriter flush 路径使用
    ///（避免 MemoryStream 在 BDN 高 op 率下无界增长）。</summary>
    private sealed class DiscardStream : Stream
    {
        private long _position;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _position;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _position += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _position += buffer.Length;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _position += buffer.Length;
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}

[JsonSerializable(typeof(StreamReadBenchmarks.StreamBenchChunk))]
[JsonSerializable(typeof(StreamReadBenchmarks.StreamBenchQuery))]
internal sealed partial class StreamBenchJsonContext : JsonSerializerContext;
