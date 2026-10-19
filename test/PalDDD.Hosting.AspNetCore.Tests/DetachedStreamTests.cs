using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using System.Text;
using System.Text.Json.Serialization;

namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧵 断线续跑测试（ADR-031）— 环形缓冲 / gap / 接管 / cancel / 宽限 / 404 / 作用域
// ═══════════════════════════════════════════════════════════════
// 形态：真 WebApplication 起 Kestrel（随机端口）+ 真 HttpClient——本组行为横跨多个
// HTTP 请求（启动→断线→重连→取消），DefaultHttpContext 直调无法模拟连接中断与重入。
//
// 并行纪律（类级串行）：每个测试自建 Kestrel 宿主并持有长连接，类内并行会让 N 个宿主
// 同进程争抢线程池与套接字回收，实测表现为挂起而非失败。串行执行代价可控（全部 ~10s 级）。

[TUnit.Core.NotInParallel]
public sealed partial class DetachedStreamTests
{
    [Test]
    public async Task DetachedRun_ReturnsRunIdAndStreamsFrames()
    {
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new SequencedHandler(3))
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        using var response = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var frames = await ReadSseAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(response.Headers.TryGetValues("X-Run-Id", out var ids)).IsTrue();
        await Assert.That(ids!.First()).IsNotEmpty();
        await Assert.That(frames.Count(f => f.EventName == "token")).IsEqualTo(3);
        await Assert.That(frames.Last().EventName).IsEqualTo("done");
    }

    [Test]
    public async Task DetachedRun_PostBody_StartsAndExposesRunId()
    {
        // POST 启动形态（2026-10-19 补强）：prompt 类请求体走 JSON body，避免 URL 长度上限。
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new SequencedHandler(2))
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{host.BaseAddress}/detached/post"))
        {
            Content = new StringContent("{\"prompt\":\"p\"}", Encoding.UTF8, "application/json")
        };
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var frames = await ReadSseAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(response.Headers.TryGetValues("X-Run-Id", out var ids)).IsTrue();
        await Assert.That(ids!.First()).IsNotEmpty();
        await Assert.That(frames.Count(f => f.EventName == "token")).IsEqualTo(2);
        await Assert.That(frames.Last().EventName).IsEqualTo("done");
    }

    [Test]
    public async Task DetachedRun_Post_NonJsonContentType_Returns415()
    {
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new SequencedHandler(1)));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{host.BaseAddress}/detached/post"))
        {
            Content = new StringContent("{\"prompt\":\"p\"}", Encoding.UTF8, "text/plain")
        };
        using var response = await host.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.UnsupportedMediaType);
    }

    [Test]
    public async Task DetachedRun_Post_MalformedJson_Returns400()
    {
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new SequencedHandler(1)));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{host.BaseAddress}/detached/post"))
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        };
        using var response = await host.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Reattach_AfterDisconnect_ReplaysFromLastEventId()
    {
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new SequencedHandler(6, delayMs: 40))
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        // 首附着：读前 2 项后主动断开
        using var first = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var runId = first.Headers.GetValues("X-Run-Id").First();
        var firstFrames = await ReadSseAsync(first, stopAfter: 2);
        await Assert.That(firstFrames.Count).IsEqualTo(2);

        // 重连：带 Last-Event-ID=1 → 从索引 2 续（不重发 0/1）
        using var reattach = await SendReattachAsync(host, runId, lastEventId: "1");
        var replayFrames = await ReadSseAsync(reattach);

        await Assert.That(reattach.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        var replayedIds = replayFrames.Where(f => f.Id is not null && f.EventName == "token")
            .Select(f => int.Parse(f.Id!)).ToList();
        await Assert.That(replayedIds).IsNotEmpty();
        await Assert.That(replayedIds.All(id => id >= 2)).IsTrue();
        // 续读覆盖剩余项且以 done 收尾
        await Assert.That(replayFrames.Last().EventName).IsEqualTo("done");
    }

    [Test]
    public async Task Reattach_GapWhenPositionEvicted_SendsGapFrame()
    {
        // 小缓冲（3 项）+ 不停顿产出：首附着读到 1 项即断，缓冲继续滚动；重连时位置已滚出
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new BurstHandler(12))
            .WithOptions(o => o.MaxBufferedItems = 3));

        using var first = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var runId = first.Headers.GetValues("X-Run-Id").First();
        _ = await ReadSseAsync(first, stopAfter: 1);
        await Task.Delay(200); // 让缓冲滚过位置 0

        using var reattach = await SendReattachAsync(host, runId, lastEventId: "0",
            keepAlive: null, connectTimeout: 10);
        var body = await reattach.Content.ReadAsStringAsync();

        // gap 帧必在数据帧之前出现（位置 1 已被丢弃）
        await Assert.That(body).Contains("event: gap");
        await Assert.That(body.IndexOf("event: gap", StringComparison.Ordinal))
            .IsLessThan(body.IndexOf("event: token", StringComparison.Ordinal));
    }

    [Test]
    public async Task Attach_UnknownRun_Returns404()
    {
        await using var host = await DetachedHost.StartAsync(run => run.WithHandler(new SequencedHandler(1)));

        using var response = await host.Client.GetAsync(
            new Uri($"{host.BaseAddress}/runs/does-not-exist/stream"),
            HttpCompletionOption.ResponseHeadersRead);

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NotFound);
    }

    [Test]
    public async Task TerminalRun_ReplaysBufferedItemsThenTerminalFrame()
    {
        // 终态回放：run 完成后（自然 Completed）重连应回放缓冲并以 event: done 收尾。
        // 注：DetachGrace（无附着宽限取消）的语义由注册表级测试
        // StreamRunRegistryTests.DetachGrace_WithoutAttach_CancelsRun 锁定——HTTP 层的
        // 「客户端断开 → 服务端观测 RequestAborted」依赖 TCP 半关闭时序，测试环境不稳定，
        // 不在此重复（避免用不稳定的网络信号做语义断言）。
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new SequencedHandler(3))
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        using var first = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var runId = first.Headers.GetValues("X-Run-Id").First();
        // 读完整个流（run 自然完成）
        var firstFrames = await ReadSseAsync(first);
        await Assert.That(firstFrames.Last().EventName).IsEqualTo("done");

        // 完成态重连：回放全部缓冲 + done 收尾
        using var reattach = await SendReattachAsync(host, runId, lastEventId: null);
        var replayFrames = await ReadSseAsync(reattach);

        await Assert.That(reattach.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(replayFrames.Count(f => f.EventName == "token")).IsEqualTo(3);
        await Assert.That(replayFrames.Last().EventName).IsEqualTo("done");
    }

    [Test]
    public async Task CancelledRun_ReattachReceivesCancelledFrame()
    {
        // 取消后的终态回放：cancel 端点 → 重连读到 cancelled 帧（不依赖网络断线信号）
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new IndefiniteHandler())
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        using var first = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var runId = first.Headers.GetValues("X-Run-Id").First();
        await ReadUntilDataAsync(first);

        using var cancel1 = await host.Client.PostAsync(new Uri($"{host.BaseAddress}/runs/{runId}/cancel"), null);
        using var cancel2 = await host.Client.PostAsync(new Uri($"{host.BaseAddress}/runs/{runId}/cancel"), null);
        await Assert.That(cancel1.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NoContent);
        // 幂等：终态再次取消仍 204
        await Assert.That(cancel2.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NoContent);

        using var reattach = await SendReattachAsync(host, runId, lastEventId: null);
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = await ReadAllWithTimeoutAsync(reattach, readCts.Token);
        await Assert.That(body).Contains("event: cancelled");
    }

    /// <summary>带取消的读全文——SSE 流不收尾时按超时退出（返回已读部分）。</summary>
    private static async Task<string> ReadAllWithTimeoutAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    [Test]
    public async Task Takeover_NewAttach_DetachesOldConnection()
    {
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new IndefiniteHandler())
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        using var first = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var runId = first.Headers.GetValues("X-Run-Id").First();

        // 保留旧连接的流（不能像 ReadSseAsync 那样读完即关闭——接管帧要在同一连接上读到）
        await using var firstStream = await first.Content.ReadAsStreamAsync();
        using var firstReader = new StreamReader(firstStream);
        // 读到首帧（token 0）确认已附着
        var firstLines = new List<string>();
        while (await firstReader.ReadLineAsync() is { } line)
        {
            firstLines.Add(line);
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                break;
        }
        await Assert.That(firstLines.Any(l => l.StartsWith("event: token", StringComparison.Ordinal))).IsTrue();

        // 第二次附着（默认接管）→ 旧连接应收到 event: detached 后结束
        using var second = await SendReattachAsync(host, runId, lastEventId: null);
        await Assert.That(second.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);

        var firstRemaining = await firstReader.ReadToEndAsync();
        await Assert.That(firstRemaining).Contains("event: detached");
    }

    [Test]
    public async Task RejectNewAttach_WhenEnabled_Returns409()
    {
        await using var host = await DetachedHost.StartAsync(run => run
            .WithHandler(new IndefiniteHandler())
            .WithOptions(o =>
            {
                o.DetachGrace = TimeSpan.FromSeconds(30);
                o.RejectNewAttach = true;
            }));

        using var first = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        var runId = first.Headers.GetValues("X-Run-Id").First();
        _ = await ReadSseAsync(first, stopAfter: 1);

        using var second = await SendReattachAsync(host, runId, lastEventId: null);
        await Assert.That(second.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Conflict);
    }

    [Test]
    public async Task RunScope_IsDisposedAfterCompletion()
    {
        // 作用域所有权：run 自有 scope 在终态释放（scoped 探针的 Dispose 计数）
        ScopeProbe.Reset();
        await using var host = await DetachedHost.StartAsync(run => run
            .WithScopedProbeHandler()
            .WithOptions(o => o.DetachGrace = TimeSpan.FromSeconds(30)));

        using var response = await host.Client.GetAsync(new Uri($"{host.BaseAddress}/detached"),
            HttpCompletionOption.ResponseHeadersRead);
        _ = await ReadSseAsync(response);
        await Task.Delay(150); // 泵收尾异步

        await Assert.That(ScopeProbe.Created).IsGreaterThanOrEqualTo(1);
        await Assert.That(ScopeProbe.Disposed).IsGreaterThanOrEqualTo(1);
    }

    // ─── 夹具 ───

    private static async Task<HttpResponseMessage> SendReattachAsync(
        DetachedHost host, string runId, string? lastEventId,
        TimeSpan? keepAlive = null, int connectTimeout = 30)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"{host.BaseAddress}/runs/{runId}/stream"));
        if (lastEventId is not null)
            request.Headers.Add("Last-Event-ID", lastEventId);
        if (keepAlive is not null)
            request.Headers.Add("X-KeepAlive", keepAlive.Value.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(connectTimeout));
        return await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    /// <summary>
    /// 读到首个 data 行即返回（不等待空行边界、不关闭响应流）——随后调用方释放响应即可
    /// 模拟真实断线。注意不能用 <c>await using</c> 包装响应流：Dispose 会等待底层流排空，
    /// 而 SSE 流未结束，表现为挂起（本测试实测教训）。
    /// </summary>
    private static async Task ReadUntilDataAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return;
        }
    }

    private static async Task<List<(string? EventName, string? Id, string Data)>> ReadSseAsync(
        HttpResponseMessage response, int? stopAfter = null)
    {
        var frames = new List<(string?, string?, string)>();
        string? eventName = null, id = null;
        var data = new List<string>();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName is not null || id is not null || data.Count > 0)
                {
                    frames.Add((eventName, id, string.Join('\n', data)));
                    eventName = null;
                    id = null;
                    data.Clear();
                    if (stopAfter is { } limit && frames.Count >= limit)
                        return frames;
                }
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var value = line[(colon + 1)..].TrimStart(' ');
            switch (line[..colon])
            {
                case "event": eventName = value; break;
                case "id": id = value; break;
                case "data": data.Add(value); break;
            }
        }
        return frames;
    }
}

// ─── 测试宿主（真 Kestrel，独立端口） ───

internal sealed class DetachedHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private DetachedHost(WebApplication app, HttpClient client, string baseAddress)
    {
        _app = app;
        _client = client;
        BaseAddress = baseAddress;
    }

    public HttpClient Client => _client;

    public string BaseAddress { get; }

    /// <summary>
    /// 强制断开客户端连接：HttpClient 默认连接池会复用连接，单纯 Dispose 响应不保证
    /// 服务端观测到 <c>RequestAborted</c>。取消该请求的 CTS 会让 HttpClient abort 底层
    /// 连接（真实断线语义），随后等待服务端观测（宽限计时依赖该信号）。
    /// </summary>
    public static async Task ForceDisconnectAsync(CancellationTokenSource requestCts)
    {
        await requestCts.CancelAsync();
        await Task.Delay(100);
    }

    public static async Task<DetachedHost> StartAsync(
        Action<DetachedHostBuilder> configure)
    {
        var builder = new DetachedHostBuilder();
        configure(builder);

        var web = WebApplication.CreateSlimBuilder();
        web.WebHost.UseUrls("http://127.0.0.1:0");
        // handler 注册分两路：作用域探针走 Scoped（构造注入 scoped 依赖），其余单例实例
        if (builder.UseScopedProbeHandler)
        {
            web.Services.AddScoped<IStreamQueryHandler<DetachedQuery, DetachedChunk>, ScopeProbeHandler>();
        }
        else
        {
            web.Services.AddSingleton(builder.Handler);
        }
        web.Services.AddDetachedStreamRuns(builder.ConfigureOptions);
        // 作用域探针：run 自有 scope 解析该 scoped 服务（构造计数）并在 run 终态 Dispose（释放计数）
        web.Services.AddScoped<ScopeProbeService>();

        var app = web.Build();
        app.MapDetachedStreamQuery<DetachedQuery, DetachedChunk>(
            "/detached",
            ctx => new DetachedQuery("p"),
            DetachedStreamJsonContext.Default.DetachedChunk,
            eventSelector: chunk => chunk.Kind,
            idSelector: chunk => chunk.Index.ToString());
        // POST 启动形态（2026-10-19 补强）：JSON body 承载 prompt 类请求体
        app.MapDetachedStreamQuery<DetachedQuery, DetachedChunk>(
            "/detached/post",
            DetachedStreamJsonContext.Default.DetachedQuery,
            DetachedStreamJsonContext.Default.DetachedChunk,
            eventSelector: chunk => chunk.Kind,
            idSelector: chunk => chunk.Index.ToString());
        app.MapStreamRunAttach<DetachedChunk>(
            "/runs", DetachedStreamJsonContext.Default.DetachedChunk);
        app.MapStreamRunCancel("/runs");

        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return new DetachedHost(app, client, address);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class DetachedHostBuilder
{
    private IStreamQueryHandler<DetachedQuery, DetachedChunk> _handler = new SequencedHandler(1);
    private Action<DetachedStreamOptions> _options = static _ => { };
    private bool _useScopedProbeHandler;

    public DetachedHostBuilder WithHandler(IStreamQueryHandler<DetachedQuery, DetachedChunk> handler)
    {
        _handler = handler;
        return this;
    }

    public DetachedHostBuilder WithOptions(Action<DetachedStreamOptions> configure)
    {
        _options = configure;
        return this;
    }

    public DetachedHostBuilder WithScopedProbeHandler()
    {
        _useScopedProbeHandler = true;
        return this;
    }

    public IStreamQueryHandler<DetachedQuery, DetachedChunk> Handler => _handler;

    public bool UseScopedProbeHandler => _useScopedProbeHandler;

    public Action<DetachedStreamOptions> ConfigureOptions => _options;
}

// 让 WebApplication 的 JSON 上下文可跨夹具复用（测试项目内单一类型）
[JsonSerializable(typeof(DetachedChunk))]
[JsonSerializable(typeof(DetachedQuery))]
internal sealed partial class DetachedStreamJsonContext : JsonSerializerContext
{
}

internal sealed record DetachedChunk(int Index, string Kind);

internal sealed record DetachedQuery(string Prompt) : IStreamQuery<DetachedChunk>;

/// <summary>产出 count 项 token + 一项 done（可加项间延迟）。</summary>
internal sealed class SequencedHandler(int count, int delayMs = 0) : IStreamQueryHandler<DetachedQuery, DetachedChunk>
{
    public async IAsyncEnumerable<DetachedChunk> HandleAsync(
        DetachedQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new DetachedChunk(i, "token");
            if (delayMs > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            else
                await Task.Yield();
        }
        yield return new DetachedChunk(count, "done");
    }
}

/// <summary>快速一次性产出多项（缓冲滚动探针）。</summary>
internal sealed class BurstHandler(int count) : IStreamQueryHandler<DetachedQuery, DetachedChunk>
{
    public async IAsyncEnumerable<DetachedChunk> HandleAsync(
        DetachedQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new DetachedChunk(i, "token");
            await Task.Yield();
        }
        yield return new DetachedChunk(count, "done");
    }
}

/// <summary>首项后无限等待 ct（取消/宽限探针）。</summary>
internal sealed class IndefiniteHandler : IStreamQueryHandler<DetachedQuery, DetachedChunk>
{
    public async IAsyncEnumerable<DetachedChunk> HandleAsync(
        DetachedQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new DetachedChunk(0, "token");
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }
}

/// <summary>作用域探针 handler：构造注入的 scoped 服务记录创建；其 Dispose 验证 run scope 随终态释放。</summary>
internal sealed class ScopeProbeHandler(ScopeProbeService probe) : IStreamQueryHandler<DetachedQuery, DetachedChunk>
{
    public async IAsyncEnumerable<DetachedChunk> HandleAsync(
        DetachedQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        yield return new DetachedChunk(0, "token");
        await Task.Yield();
        yield return new DetachedChunk(1, "done");
    }
}

internal sealed class ScopeProbeService : IDisposable
{
    public ScopeProbeService() => ScopeProbe.OnCreated();

    public void Dispose() => ScopeProbe.OnDisposed();
}

internal static class ScopeProbe
{
    private static int s_created;
    private static int s_disposed;

    public static int Created => Volatile.Read(ref s_created);

    public static int Disposed => Volatile.Read(ref s_disposed);

    public static void OnCreated() => Interlocked.Increment(ref s_created);

    public static void OnDisposed() => Interlocked.Increment(ref s_disposed);

    public static void Reset()
    {
        s_created = 0;
        s_disposed = 0;
    }
}
