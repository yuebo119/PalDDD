using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.AgentSample;
using PalDDD.Core;
using PalDDD.CQRS;
using PalDDD.DependencyInjection;
using PalDDD.EventLog;
using PalDDD.Hosting.AspNetCore;
using PalDDD.Idempotency;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
// PalUlid 别名沿用 Core/DomainEvent.cs 先例（ByteAether.Ulid 不传递到消费方命名空间）
using PalUlid = ByteAether.Ulid.Ulid;

// ═══════════════════════════════════════════════════════════════
// 🤖 Pal.DDD Agent 参考样本 — 流式产出 + 工具幂等 + EventLog 审计 + 断点续看
// ═══════════════════════════════════════════════════════════════
// 回答「agent 场景怎么用 PalDDD」：四个能力的接线范式，均为既有组件组合，无新概念。
//
//   ① 流式产出（ADR-030）：IStreamQuery + MapStreamQuery，event/id 帧 + 心跳；
//      响应内容审计不由框架捕获，由 handler 在流结束后追加 EventLog（D6 分工）。
//   ② 工具调用幂等（IIdempotencyStore）：LLM 重试同一 tool call 不重复产生副作用。
//   ③ EventLog 审计：一轮 run 结束追加一条完整记录（不逐 token 写——写放大）。
//   ④ 断点续看：EventLog 流版本即天然事件 ID，Last-Event-ID 回带后续读。
//
// 运行：dotnet run --project samples/PalDDD.AgentSample
// 本样本自起 Kestrel、自发 HTTP 请求、断言帧序列，退出码非零即失败（对齐 AotSample 风格）。

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) Console.WriteLine($"OK: {name}");
    else { failures++; Console.WriteLine($"FAILED: {name}"); }
}

// ── 1. 服务注册 ──
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddPalLogging();
builder.Services.AddPalDDD();
builder.Services.AddPalStreamQueryHandler<AgentRunQuery, AgentChunk, AgentRunHandler>();
builder.Services.AddPalStreamQueryHandler<AgentReplayQuery, AgentChunk, AgentReplayHandler>();
builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<IdempotencyProcessor>();
builder.Services.AddSingleton<IEventLog, InMemoryEventLog>();
builder.Services.AddSingleton<ToolExecutor>();
builder.Services.AddSingleton<AgentRunAuditor>();
// ADR-031：断线续跑（run 注册表 + 有界缓冲 + 附着/取消端点）
builder.Services.AddDetachedStreamRuns(options =>
{
    options.MaxBufferedItems = 64;
    options.DetachGrace = TimeSpan.FromSeconds(30);   // 示例放大，生产按产品语义取
    options.MaxRunDuration = TimeSpan.FromMinutes(2);
});

var app = builder.Build();

// ① 流式端点：event/id 分型 + 30s 心跳（nginx 60s 空闲断连的安全余量）
app.MapStreamQuery<AgentRunQuery, AgentChunk>(
    "/agent/run",
    ctx => new AgentRunQuery(
        Prompt: ctx.Request.Query["prompt"].ToString(),
        LastEventId: ctx.GetLastEventId()),   // ④ 断点续看入口
    AgentJsonContext.Default.AgentChunk,
    eventSelector: chunk => chunk.Kind switch
    {
        AgentChunkKind.ToolCall => "tool",
        AgentChunkKind.Token => "token",
        _ => "done"
    },
    idSelector: chunk => chunk.Index.ToString(),
    keepAliveInterval: TimeSpan.FromSeconds(30));

// ④ 回看已完成的 run：EventLog 有序读 → 同一个帧形状（无生成，纯回放）
app.MapStreamQuery<AgentReplayQuery, AgentChunk>(
    "/agent/replay",
    ctx => new AgentReplayQuery(ctx.Request.Query["run"].ToString(), ctx.GetLastEventId()),
    AgentJsonContext.Default.AgentChunk,
    eventSelector: chunk => chunk.Kind == AgentChunkKind.Done ? "done" : "replay",
    idSelector: chunk => chunk.Index.ToString());

// ⑤ 断线续跑（ADR-031）：启动即附着（X-Run-Id 供重连）+ 重连附着 + 显式取消
app.MapDetachedStreamQuery<AgentRunQuery, AgentChunk>(
    "/agent/detached",
    ctx => new AgentRunQuery(ctx.Request.Query["prompt"].ToString(), ctx.GetLastEventId()),
    AgentJsonContext.Default.AgentChunk,
    eventSelector: chunk => chunk.Kind switch
    {
        AgentChunkKind.ToolCall => "tool",
        AgentChunkKind.Token => "token",
        _ => "done"
    },
    idSelector: chunk => chunk.Index.ToString());
app.MapStreamRunAttach<AgentChunk>("/agent/runs", AgentJsonContext.Default.AgentChunk);
app.MapStreamRunCancel("/agent/runs");

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()!.Addresses.First();
using var http = new HttpClient();

// ── 2. 跑一轮 agent：流式 token + 两次工具调用（第二次故意重试同一幂等键）──
using (var runResponse = await http.GetAsync(
    new Uri($"{address}/agent/run?prompt=hello"), HttpCompletionOption.ResponseHeadersRead))
{
    Check("run status 200", runResponse.StatusCode == System.Net.HttpStatusCode.OK);
    Check("run content-type", runResponse.Content.Headers.ContentType?.MediaType == "text/event-stream");

    var frames = await ReadFramesAsync(runResponse);
    // 事件序列：tool(search) → token*3 → tool(calculate, 重试同键) → done
    Check("run frame events", string.Join(",", frames.Where(f => f.EventName is not null).Select(f => f.EventName))
        is "tool,token,token,token,tool,done");
    Check("run frame ids are monotonic",
        frames.Where(f => f.Id is not null).Select(f => int.Parse(f.Id!)).SequenceEqual([0, 1, 2, 3, 4, 5]));
    Check("run done payload carries usage",
        frames.Last().EventName == "done" && frames.Last().Data.Contains("usage", StringComparison.Ordinal));
}

// ── 3. 工具幂等：模拟 LLM 重试同一 tool call（同幂等键）——副作用只发生一次，第二次命中缓存 ──
var toolExecutor = app.Services.GetRequiredService<ToolExecutor>();
var firstCall = await toolExecutor.CalculateAsync("calc:retry-probe", 7, 8, CancellationToken.None);
var retriedCall = await toolExecutor.CalculateAsync("calc:retry-probe", 7, 8, CancellationToken.None);
Check("tool retry returns same result", firstCall == retriedCall && firstCall == 15);
Check("tool retry hit cached result", toolExecutor.CalculateCacheHits == 1);
// 副作用总数 2 = run 内 1 次（calc:hello:1）+ 重试探针 1 次（calc:retry-probe 两次调用只算一次）
Check("tool idempotent (side effect once per key)", toolExecutor.CalculateSideEffects == 2);

// ── 4. EventLog 审计：一轮 run 一条完整记录（不是逐 token）──
var eventLog = app.Services.GetRequiredService<IEventLog>();
var auditEvents = new List<RecordedEvent>();
await foreach (var recorded in eventLog.ReadStreamAsync(AgentRunAuditor.StreamNameFor("hello")))
    auditEvents.Add(recorded);
Check("audit single record per run", auditEvents.Count == 1);
Check("audit record name", auditEvents[0].EventName == AgentRunAuditor.CompletedEventName);
Check("audit payload has token count",
    Encoding.UTF8.GetString(auditEvents[0].Payload.Span).Contains("\"tokenCount\"", StringComparison.Ordinal));

// ── 5. 断点续看：从 Last-Event-ID 之后继续读（EventLog 版本即事件 ID）──
using (var replayRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"{address}/agent/replay?run=hello")))
{
    replayRequest.Headers.Add("Last-Event-ID", "2"); // 已看到索引 2，从 3 续
    using var replayResponse = await http.SendAsync(replayRequest, HttpCompletionOption.ResponseHeadersRead);
    var replayFrames = await ReadFramesAsync(replayResponse);
    Check("replay resumes after Last-Event-ID",
        replayFrames.Count > 0 && replayFrames.All(f => int.Parse(f.Id!) > 2));
    Check("replay ends with done", replayFrames.Last().EventName == "done");
}

// ── 6. 断线续跑（ADR-031）：启动 → 读到中途断开 → 重连按 Last-Event-ID 续看 ──
// 断线用「取消请求 CTS」实现：SSE 响应未读完时 Dispose HttpResponseMessage 会同步等
// 连接关闭，与仍在写的服务端互相等待（样本实测挂死）；取消 CTS 直接 abort 底层连接，
// 与真实切页/弱网断开同语义。
string detachedRunId;
using var firstRequestCts = new CancellationTokenSource();
var detachedFirst = await http.GetAsync(
    new Uri($"{address}/agent/detached?prompt=resume"),
    HttpCompletionOption.ResponseHeadersRead, firstRequestCts.Token);
detachedRunId = detachedFirst.Headers.GetValues("X-Run-Id").First();
Check("detached run exposes X-Run-Id", detachedRunId.Length > 0);

var partial = await ReadUntilFirstTokenAsync(detachedFirst);
Check("detached first frame is tool", partial.EventName == "tool");

await firstRequestCts.CancelAsync();   // 断线：abort 连接，run 继续跑
await Task.Delay(150);
// 重连用独立 HttpClient：首连接的 abort 可能使共享连接池对该 host 的连接进入异常态，
// 复用实例的后续请求在部分平台会挂起（样本实测）。真实客户端重连通常也走新连接。
using var reattachClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
using var attachCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using (var reattach = await SendWithLastEventIdAsync(
    reattachClient, $"{address}/agent/runs/{detachedRunId}/stream", "0", attachCts.Token))
{
    // 带超时读：SSE 流不收尾时按超时退出并显式失败（不挂死样本）
    using var replayCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var replay = await ReadFramesAsync(reattach, replayCts.Token);
    // 只对有 id 的数据帧校验续读起点（终态收尾帧 done 无 id）
    var replayIds = replay.Where(f => f.Id is not null).Select(f => int.Parse(f.Id!)).ToList();
    Check("reattach continues after last seen id", replayIds.Count > 0 && replayIds.All(id => id > 0));
    Check("reattach ends with done", replay.Last().EventName == "done");
}

await app.StopAsync();

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("✅ Pal.DDD agent sample — all checks passed.");
    return;
}

Console.WriteLine($"❌ Pal.DDD agent sample — {failures} check(s) FAILED.");
Environment.Exit(1);

// ── 辅助：读到首个 token 帧即返回（不断流，调用方随后释放响应模拟断线）──
static async Task<(string? EventName, string? Id, string Data)> ReadUntilFirstTokenAsync(
    HttpResponseMessage response)
{
    string? eventName = null, id = null;
    var data = new List<string>();
    var stream = await response.Content.ReadAsStreamAsync();
    var reader = new StreamReader(stream);
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length == 0)
        {
            if (eventName is not null || id is not null || data.Count > 0)
                return (eventName, id, string.Join('\n', data));
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
    return (eventName, id, string.Join('\n', data));
}

// ── 辅助：带 Last-Event-ID 的重连请求（复用调用方 HttpClient——临时实例随方法返回
//    Dispose 会连带关闭响应流，样本此前挂起的根因）──
static async Task<HttpResponseMessage> SendWithLastEventIdAsync(
    HttpClient client, string url, string lastEventId, CancellationToken ct = default)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
    request.Headers.Add("Last-Event-ID", lastEventId);
    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
}

// ── SSE 帧读取（样本内自足；生产测试可用 PalDDD.Testing.SseFrameReader）──
static async Task<List<(string? EventName, string? Id, string Data)>> ReadFramesAsync(
    HttpResponseMessage response, CancellationToken ct = default)
{
    var frames = new List<(string?, string?, string)>();
    string? eventName = null, id = null;
    var data = new List<string>();
    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    using var reader = new StreamReader(stream);
    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
    {
        if (line.Length == 0)
        {
            if (eventName is not null || id is not null || data.Count > 0)
            {
                frames.Add((eventName, id, string.Join('\n', data)));
                eventName = null;
                id = null;
                data.Clear();
            }
            continue;
        }
        if (line[0] == ':') continue; // 心跳注释帧
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

namespace PalDDD.AgentSample
{
    // ─── 流式模型 ───

    public enum AgentChunkKind { ToolCall, Token, Done }

    public sealed record AgentChunk(int Index, AgentChunkKind Kind, string Text, int? Usage);

    public sealed record AgentRunQuery(string Prompt, string? LastEventId) : IStreamQuery<AgentChunk>;

    public sealed record AgentReplayQuery(string Run, string? LastEventId) : IStreamQuery<AgentChunk>;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AgentChunk))]
    [JsonSerializable(typeof(AgentAuditRecord))]
    [JsonSerializable(typeof(int))]
    public sealed partial class AgentJsonContext : JsonSerializerContext;

    // ─── 工具执行器：幂等封装（LLM 重试同键不重复副作用）──

    /// <summary>工具执行器——示范 IdempotencyProcessor 包住有副作用的工具调用。</summary>
    public sealed class ToolExecutor(IdempotencyProcessor processor)
    {
        public int CalculateSideEffects { get; private set; }

        public int CalculateCacheHits { get; private set; }

        /// <summary>有副作用的工具调用（这里用计数器模拟扣费/写库等不可重复操作）。</summary>
        public async ValueTask<int> CalculateAsync(string idempotencyKey, int a, int b, CancellationToken ct)
        {
            var execution = await processor.ExecuteAsync(
                operationName: "agent.tool.calculate",
                key: idempotencyKey,
                handler: _ =>
                {
                    CalculateSideEffects++;      // 副作用：只应发生一次
                    return ValueTask.FromResult(a + b);
                },
                serializeResult: value => JsonSerializer.SerializeToUtf8Bytes(value, AgentJsonContext.Default.Int32),
                deserializeResult: bytes => JsonSerializer.Deserialize(bytes.Span, AgentJsonContext.Default.Int32),
                cancellationToken: ct).ConfigureAwait(false);

            if (execution.Status == IdempotencyExecutionStatus.Cached)
                CalculateCacheHits++;
            return execution.Result;
        }
    }

    // ─── 流式 handler：token 流 + 工具调用 + 结束后 EventLog 审计 ───

    /// <summary>
    /// 模拟 LLM 的 token 流处理器。真实实现里 <c>llmClient.StreamAsync</c> 换成本地 SDK 调用；
    /// 关键接线：① ct 必须响应（客户端断开即停计费）；② 工具调用经 ToolExecutor 幂等；
    /// ③ 流结束后追加 EventLog（D6 分工：框架不捕获载荷，审计归应用层）。
    /// </summary>
    public sealed class AgentRunHandler(ToolExecutor tools, AgentRunAuditor auditor)
        : IStreamQueryHandler<AgentRunQuery, AgentChunk>
    {
        public async IAsyncEnumerable<AgentChunk> HandleAsync(
            AgentRunQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var index = 0;
            var tokenCount = 0;

            // 工具轮 1：搜索
            yield return new AgentChunk(index++, AgentChunkKind.ToolCall, "search: hello", null);
            await Task.Yield();

            // 文本轮：三个 token
            foreach (var token in new[] { "Hello", " from", " agent" })
            {
                ct.ThrowIfCancellationRequested();
                tokenCount++;
                yield return new AgentChunk(index++, AgentChunkKind.Token, token, null);
                await Task.Yield();
            }

            // 工具轮 2：计算（键固定 → 重试命中缓存，副作用只发生一次）
            var sum = await tools.CalculateAsync("calc:hello:1", 2, 3, ct);
            yield return new AgentChunk(index++, AgentChunkKind.ToolCall, $"calculate: 2+3={sum}", null);

            // 结束帧：usage 元数据（客户端据此计费/展示）
            yield return new AgentChunk(index, AgentChunkKind.Done, "completed", tokenCount);

            // ③ 流结束审计：一条完整记录（不逐 token 写）
            await auditor.AppendCompletedAsync(query.Prompt, tokenCount, ct).ConfigureAwait(false);
        }
    }

    /// <summary>回放 handler：EventLog 有序读 → 同帧形状（支持从 Last-Event-ID 续读）。</summary>
    public sealed class AgentReplayHandler(IEventLog eventLog)
        : IStreamQueryHandler<AgentReplayQuery, AgentChunk>
    {
        public async IAsyncEnumerable<AgentChunk> HandleAsync(
            AgentReplayQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // 已完成的 run 回放：EventLog 流版本即事件 ID，fromVersion 直接取 Last-Event-ID
            var fromVersion = long.TryParse(query.LastEventId, out var last) ? last + 1 : 0;
            await foreach (var recorded in eventLog.ReadStreamAsync(
                AgentRunAuditor.StreamNameFor(query.Run), fromVersion, cancellationToken: ct).ConfigureAwait(false))
            {
                var audit = JsonSerializer.Deserialize(recorded.Payload.Span, AgentJsonContext.Default.AgentAuditRecord)
                    ?? throw new InvalidOperationException("Audit payload could not be deserialized.");
                yield return new AgentChunk(
                    (int)recorded.StreamVersion, AgentChunkKind.Token, $"{audit.Caption}: {audit.TokenCount}", null);
            }

            yield return new AgentChunk(
                (int)(fromVersion + 1), AgentChunkKind.Done, "replay finished", null);
        }
    }

    // ─── EventLog 审计（D6：应用层负责，框架不捕获载荷）──

    /// <summary>把一轮 agent run 的完成事实写成一条 EventLog 记录。</summary>
    public sealed class AgentRunAuditor(IEventLog eventLog)
    {
        public const string CompletedEventName = "agent.run.completed.v1";

        /// <summary>流名按 run 提示词派生（真实实现用 runId）。</summary>
        public static string StreamNameFor(string prompt) => $"agent-run-{prompt}";

        public async ValueTask AppendCompletedAsync(string prompt, int tokenCount, CancellationToken ct)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new AgentAuditRecord(prompt, tokenCount, "mock-model"),
                AgentJsonContext.Default.AgentAuditRecord);
            var eventData = new EventData(
                PalUlid.New(),
                CompletedEventName,
                schemaVersion: 1,
                contentType: "application/json",
                payload: payload,
                metadata: ReadOnlyMemory<byte>.Empty,
                audit: EventAuditMetadata.Empty);
            await eventLog.AppendAsync(
                StreamNameFor(prompt), ExpectedStreamVersion.Any, [eventData], ct).ConfigureAwait(false);
        }
    }

    public sealed record AgentAuditRecord(string Caption, int TokenCount, string Model);
}
