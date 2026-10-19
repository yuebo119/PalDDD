using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Core;
using PalDDD.CQRS;

#pragma warning disable CA1812 // DI 实例化的内部类（AddPalCommandHandler 泛型注册）
using PalDDD.DependencyInjection;
using PalDDD.Hosting.AspNetCore;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Transactions;
using System.Text;
using System.Text.Json.Serialization;

// ═══════════════════════════════════════════════════════════════
// Pal.DDD AOT 示例 — 展示消息序列化 + Outbox + Inbox + Saga
// ═══════════════════════════════════════════════════════════════

// ── 1. 消息目录 ──
var builder = new MessageCatalogBuilder();
builder.Add(SampleJsonContext.Default.SampleMessage, name: "sample-message");
var catalog = builder.Build();

var serializer = new JsonMessageSerializer(catalog);
var descriptor = catalog.Find("sample-message")
    ?? throw new InvalidOperationException("sample-message was not registered.");

// ── 校验辅助：累计失败，任一失败最终以非零退出码结束进程（ITM-150）──
var failures = 0;

void Check(string name, bool condition)
{
    if (condition)
        Console.WriteLine($"OK: {name}");
    else
    {
        failures++;
        Console.WriteLine($"FAILED: {name}");
    }
}

// ── 2. 序列化往返 ──
var payload = serializer.Serialize(new SampleMessage("aot", 10), descriptor);
var message = serializer.Deserialize(payload.Span, descriptor);
Check("serialize round-trip", message is SampleMessage { Name: "aot", Count: 10 });

// ── 3. InMemory Outbox 发布 ──
var outboxStore = new InMemoryOutboxStore();
var outboxMsg = new OutboxMessage
{
    Type = descriptor.Name,
    Payload = payload.ToArray(),
    ContentType = descriptor.ContentType,
    SchemaVersion = descriptor.SchemaVersion
};
await outboxStore.AddMessageAsync(outboxMsg);
var pending = await outboxStore.LeasePendingMessagesAsync(10, "aot-sample", TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, CancellationToken.None).ConfigureAwait(false);
Check("outbox lease + process", pending.Count == 1);
// ITM-265（R40）：MarkProcessed 必须传租约返回的实例——InMemory 租约会创建 successor 对象，
// 传原始 outboxMsg 会被 IsCurrentLeaseHolder 的引用守卫拒绝（静默 no-op）。写法先例见
// ECommerce/PalOrmSample 样本（均传 Lease 返回值 msgs[0]/leased[0]——ITM-269 后两者同形态）；
// owner-null 直标分支仅 PalORM/Dapper 放行（ITM-272 勘正），样本不走该分支。
// ITM-268（R41）：先 Check 后索引——空租约时 pending[0] 直接抛 IndexOutOfRange 会绕过
// Check 的 failures 累计机制。
await outboxStore.MarkProcessedAsync(pending[0], DateTimeOffset.UtcNow);
// 终验（ITM-268 修正）：断言租约实例的终态而非查询侧不可见——R40 版的 GetPending==0 是恒真
//（标记被拒时 successor 持未过期租约，租约过滤本来就把租约中的消息排除出 GetPending，两态皆绿）。
// MarkProcessed 成功时原地改写入参状态为 Processed；被引用守卫拒绝时 no-op、状态仍 Pending。
Check("outbox marked processed (terminal state)", pending[0].Status == OutboxStatus.Processed);

// ── 4. InMemory Inbox 幂等消费 ──
var inboxStore = new InMemoryInboxStore();
var inboxResult = await inboxStore.TryStartProcessingAsync("aot-consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
Check("inbox first attempt", inboxResult is not null);
var inboxDup = await inboxStore.TryStartProcessingAsync("aot-consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
Check("inbox deduplication", inboxDup is null);

// ── 5. InMemory Saga 状态存储 ──
var sagaStore = new InMemorySagaStateStore<SampleSagaState>();
var sagaState = new SampleSagaState { CurrentState = "Started" };
sagaStore.Add(sagaState);
var active = await sagaStore.GetActiveSagasAsync(10, CancellationToken.None).ConfigureAwait(false);
Check("saga active scan", active.Count == 1);

// ── 6. CQRS 管道（AOT 值类型管道验证）──
// 背景：Native AOT 下 DI 开放泛型解析 + 值类型响应（Unit/int）抛 AotCannotCreateGenericValueType。
// 解法：AddPalCommandHandler/AddPalQueryHandler 内部闭合注册管道行为——闭合类型走 TryCreateExact 不经值类型校验。
Console.WriteLine("\n── 6. CQRS 管道（AOT 值类型管道验证）──");

var pipelineServices = new ServiceCollection();
pipelineServices.AddPalLogging();
pipelineServices.AddPalDDD();
pipelineServices.AddPalCommandHandler<PipelineCreateCmd, Unit, PipelineCreateHandler>();
pipelineServices.AddPalQueryHandler<PipelineCountQry, int, PipelineCountHandler>();
await using var pipelineProvider = pipelineServices.BuildServiceProvider();

// 无 Host 场景下手动启动 HostedService（HandlerRegistrar 将 HandlerMarker 注册进 Dispatcher）
foreach (var hostedService in pipelineProvider.GetServices<IHostedService>())
    await hostedService.StartAsync(CancellationToken.None);

var dispatcher = pipelineProvider.GetRequiredService<Dispatcher>();
await dispatcher.SendAsync(new PipelineCreateCmd("aot-pipeline"));
await dispatcher.SendAsync(new PipelineCreateCmd("aot-pipeline-2"));
var count = await dispatcher.QueryAsync(new PipelineCountQry());
Check("AOT pipeline (Unit command + int query)", count == 2);

// ── 7. 流式读端点（SSE，ADR-030）──
// 背景：agent 场景的核心形态是 LLM 推理的增量产出流式回传客户端（token 流）。
// 流式读形态 = IStreamQuery/IStreamQueryHandler（不经 Dispatcher——per-request scope
// 随管道销毁而 IAsyncEnumerable 枚举晚于销毁，D1）+ 端点即作用域边界（D2）+ SSE 帧
// （D4）+ 取消贯穿（D5）。本样本在 AOT 发布产物里真起 Kestrel、真发 HTTP 请求、
// 真读 SSE 帧序列——喂 CI aot-verify 的正是这条链路。
Console.WriteLine("\n── 7. 流式读端点（SSE）──");

var streamBuilder = WebApplication.CreateSlimBuilder();
// 127.0.0.1:0——随机端口避免 CI 端口争用；实际绑定地址启动后从 IServerAddressesFeature 取
streamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
streamBuilder.Services.AddPalLogging();
streamBuilder.Services.AddPalStreamQueryHandler<TokenStreamQry, TokenChunk, TokenStreamHandler>();
streamBuilder.Services.AddSingleton<IPalValidator<TokenStreamQry>, TokenStreamValidator>();
// ADR-031 断线续跑：注册表 + 有界缓冲 + 附着/取消端点（2026-10-19 AOT 烟测补强）
streamBuilder.Services.AddDetachedStreamRuns(options =>
{
    options.MaxBufferedItems = 16;
    options.DetachGrace = TimeSpan.FromSeconds(5);
    options.MaxRunDuration = TimeSpan.FromSeconds(30);
});

var streamApp = streamBuilder.Build();
// GET 形态：浏览器原生 EventSource 兼容
streamApp.MapStreamQuery<TokenStreamQry, TokenChunk>(
    "/tokens",
    ctx => new TokenStreamQry(ctx.Request.Query["prompt"].ToString()),
    StreamJsonContext.Default.TokenChunk);
// POST 形态：JSON body 承载 prompt 类请求体（浏览器侧需 fetch + ReadableStream 手工解析）
streamApp.MapStreamQuery<TokenStreamQry, TokenChunk>(
    "/tokens/post",
    StreamJsonContext.Default.TokenStreamQry,
    StreamJsonContext.Default.TokenChunk);
// Phase 2 富化帧形态：event 名 + id 行 + 心跳（agent 场景分型事件流与静默间隙保活）
streamApp.MapStreamQuery<TokenStreamQry, TokenChunk>(
    "/tokens/typed",
    ctx => new TokenStreamQry(ctx.Request.Query["prompt"].ToString()),
    StreamJsonContext.Default.TokenChunk,
    eventSelector: chunk => chunk.Index == 2 ? "done" : "token",
    idSelector: chunk => chunk.Index.ToString(),
    keepAliveInterval: TimeSpan.FromMilliseconds(40));
// ADR-031 断线续跑：POST 启动（JSON body）+ 重连附着 + 幂等取消
streamApp.MapDetachedStreamQuery<TokenStreamQry, TokenChunk>(
    "/tokens/detached",
    StreamJsonContext.Default.TokenStreamQry,
    StreamJsonContext.Default.TokenChunk,
    eventSelector: chunk => chunk.Index == 2 ? "done" : "token",
    idSelector: chunk => chunk.Index.ToString());
streamApp.MapStreamRunAttach<TokenChunk>("/tokens/runs", StreamJsonContext.Default.TokenChunk);
streamApp.MapStreamRunCancel("/tokens/runs");

await streamApp.StartAsync();
var serverAddress = streamApp.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()!.Addresses.First();

using var streamHttpClient = new HttpClient();

// GET 消费（EventSource 形态）：逐行解析 data: 帧
using var getResponse = await streamHttpClient.GetAsync(
    new Uri($"{serverAddress}/tokens?prompt=hello"), HttpCompletionOption.ResponseHeadersRead);
Check("stream GET status", getResponse.StatusCode == System.Net.HttpStatusCode.OK);
Check("stream GET content-type", getResponse.Content.Headers.ContentType?.MediaType == "text/event-stream");
var frames = new List<string>();
await using (var responseStream = await getResponse.Content.ReadAsStreamAsync())
using (var lineReader = new StreamReader(responseStream))
{
    while (await lineReader.ReadLineAsync() is { } line)
    {
        if (line.StartsWith("data: ", StringComparison.Ordinal))
            frames.Add(line["data: ".Length..]);
    }
}
Check("stream GET frames", frames is
[
    "{\"index\":0,\"text\":\"Hello\"}",
    "{\"index\":1,\"text\":\" stream\"}",
    "{\"index\":2,\"text\":\" world\"}"
]);

// POST 消费（JSON body 形态）
using var postResponse = await streamHttpClient.PostAsync(
    new Uri($"{serverAddress}/tokens/post"),
    new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json"));
Check("stream POST status", postResponse.StatusCode == System.Net.HttpStatusCode.OK);
var postBody = await postResponse.Content.ReadAsStringAsync();
Check("stream POST frames", postBody.Contains("{\"index\":0,\"text\":\"Hello\"}", StringComparison.Ordinal));

// 验证失败：缺 prompt → 400 ProblemDetails（SSE 头未写出、状态码可回退——D3 验证前置）
using var invalidResponse = await streamHttpClient.GetAsync(new Uri($"{serverAddress}/tokens"));
Check("stream validation 400", invalidResponse.StatusCode == System.Net.HttpStatusCode.BadRequest);

// Phase 2 富化帧消费：event 名 + id 行 + 心跳（静默间隙内注释帧先于末项到达）
using var typedResponse = await streamHttpClient.GetAsync(
    new Uri($"{serverAddress}/tokens/typed?prompt=hello"), HttpCompletionOption.ResponseHeadersRead);
var typedLines = new List<string>();
await using (var typedStream = await typedResponse.Content.ReadAsStreamAsync())
using (var typedReader = new StreamReader(typedStream))
{
    while (await typedReader.ReadLineAsync() is { } line)
        typedLines.Add(line);
}
// 心跳条数随时序浮动（120ms 静默窗口 / 40ms 间隔）——断言存在性与位置，条数不锁死
var heartbeatCount = typedLines.Count(static l => string.Equals(l, ":keepalive", StringComparison.Ordinal));
var firstHeartbeat = typedLines.FindIndex(static l => string.Equals(l, ":keepalive", StringComparison.Ordinal));
var doneLine = typedLines.FindIndex(static l => string.Equals(l, "event: done", StringComparison.Ordinal));
Check("stream typed heartbeat during gap", heartbeatCount >= 1 && firstHeartbeat < doneLine);
// 剔除注释帧与帧终止空行后，event/id/data 行序精确锁定
Check("stream typed event frames", typedLines
    .Where(static l => l.Length > 0 && !string.Equals(l, ":keepalive", StringComparison.Ordinal))
    .ToArray() is
[
    "event: token", "id: 0", "data: {\"index\":0,\"text\":\"Hello\"}",
    "event: token", "id: 1", "data: {\"index\":1,\"text\":\" stream\"}",
    "event: done", "id: 2", "data: {\"index\":2,\"text\":\" world\"}"
]);

// ── 8. 断线续跑端点（ADR-031，2026-10-19 补强）──
// 覆盖新 POST 启动重载 → 注册表/环形缓冲 → 终态回放附着 → 幂等取消。
// 该段与 §7 同在 PublishAot 产物里执行，喂 CI aot-verify 的 AOT 覆盖。
Console.WriteLine("\n── 8. 断线续跑端点（ADR-031）──");

using (var detachedRequest = new HttpRequestMessage(
    HttpMethod.Post, new Uri($"{serverAddress}/tokens/detached"))
{
    Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json")
})
using (var detachedResponse = await streamHttpClient.SendAsync(
    detachedRequest, HttpCompletionOption.ResponseHeadersRead))
{
    Check("detached POST status", detachedResponse.StatusCode == System.Net.HttpStatusCode.OK);
    Check("detached POST X-Run-Id",
        detachedResponse.Headers.TryGetValues("X-Run-Id", out var runIds)
        && runIds!.First().Length == 32);
    var detachedRunId = detachedResponse.Headers.GetValues("X-Run-Id").First();
    var detachedBody = await detachedResponse.Content.ReadAsStringAsync();
    Check("detached POST done frame", detachedBody.Contains("event: done", StringComparison.Ordinal));

    // 终态回放：重连附着先回放环形缓冲，再以同一收尾帧结束
    using var attachResponse = await streamHttpClient.GetAsync(
        new Uri($"{serverAddress}/tokens/runs/{detachedRunId}/stream"),
        HttpCompletionOption.ResponseHeadersRead);
    var attachBody = await attachResponse.Content.ReadAsStringAsync();
    Check("detached attach replay",
        attachResponse.StatusCode == System.Net.HttpStatusCode.OK
        && attachBody.Contains("event: done", StringComparison.Ordinal));

    // 幂等取消：终态 run 再次取消仍返回 204
    using var cancelRequest = new HttpRequestMessage(
        HttpMethod.Post, new Uri($"{serverAddress}/tokens/runs/{detachedRunId}/cancel"));
    using var cancelResponse = await streamHttpClient.SendAsync(cancelRequest);
    Check("detached cancel terminal",
        cancelResponse.StatusCode == System.Net.HttpStatusCode.NoContent);
}

await streamApp.StopAsync();

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("✅ Pal.DDD AOT sample — all checks passed.");
    return;
}

Console.WriteLine($"❌ Pal.DDD AOT sample — {failures} check(s) FAILED.");
Environment.Exit(1);

// ── 类型定义 ──
internal sealed record SampleMessage(string Name, int Count);

[JsonSerializable(typeof(SampleMessage))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

internal sealed class SampleSagaState : SagaState;

// ── CQRS 管道类型（AOT 值类型管道验证）──
internal sealed record PipelineCreateCmd(string Name) : ICommand;
internal sealed record PipelineCountQry : IQuery<int>;

/// <summary>管道验证共享存储（handler 间共享，模拟应用状态）</summary>
internal static class PipelineStore
{
    public static readonly List<string> Names = [];
}

internal sealed class PipelineCreateHandler : ICommandHandler<PipelineCreateCmd, Unit>
{
    public ValueTask<Unit> HandleAsync(PipelineCreateCmd cmd, CancellationToken ct)
    {
        PipelineStore.Names.Add(cmd.Name);
        return ValueTask.FromResult(new Unit());
    }
}

internal sealed class PipelineCountHandler : IQueryHandler<PipelineCountQry, int>
{
    public ValueTask<int> HandleAsync(PipelineCountQry qry, CancellationToken ct)
        => ValueTask.FromResult(PipelineStore.Names.Count);
}

// ── 流式读类型（ADR-030 §7 样本）──
internal sealed record TokenChunk(int Index, string Text);

internal sealed record TokenStreamQry(string Prompt) : IStreamQuery<TokenChunk>;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TokenChunk))]
[JsonSerializable(typeof(TokenStreamQry))]
internal sealed partial class StreamJsonContext : JsonSerializerContext;

internal sealed class TokenStreamValidator : IPalValidator<TokenStreamQry>
{
    public PalValidationResult Validate(TokenStreamQry instance)
        => string.IsNullOrWhiteSpace(instance.Prompt)
            ? PalValidationResult.Failed("prompt", "Prompt is required.")
            : PalValidationResult.Success();
}

/// <summary>模拟 LLM token 流的 handler——ct 响应由 [EnumeratorCancellation] 语义承载（D5）。
/// 末项前 120ms 静默间隙模拟 agent 工具轮/首 token 等待（Phase 2 心跳样本的触发窗口）。</summary>
internal sealed class TokenStreamHandler : IStreamQueryHandler<TokenStreamQry, TokenChunk>
{
    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQry qry, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TokenChunk(0, "Hello");
        await Task.Yield();
        yield return new TokenChunk(1, " stream");
        await Task.Delay(120, ct).ConfigureAwait(false);
        yield return new TokenChunk(2, " world");
    }
}
