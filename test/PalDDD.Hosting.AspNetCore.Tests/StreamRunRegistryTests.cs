using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using System.Text.Json.Serialization;

namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧵 运行注册表单元测试（ADR-031 内部行为，不涉 HTTP）
// ═══════════════════════════════════════════════════════════════
// 经 IVT 直测 internal StreamRunRegistry / StreamRun<TItem>：缓冲、状态机、
// 宽限取消、附着代际。HTTP 层行为由 DetachedStreamTests 覆盖。

[TUnit.Core.NotInParallel]
public sealed partial class StreamRunRegistryTests
{
    [Test]
    public async Task Register_StartsPump_AndBuffersItems()
    {
        using var registry = CreateRegistry(out var options);
        options.DetachGrace = TimeSpan.FromSeconds(30);
        registry.Register(new RegQuery("p"), null, null, new FixedHandler(3));

        var run = await WaitForRunAsync(registry);
        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));

        await Assert.That(run.State).IsEqualTo(StreamRunState.Completed);
        await Assert.That(run.BufferedCount).IsEqualTo(3);
        await Assert.That(run.NextIndex).IsEqualTo(3);
    }

    [Test]
    public async Task Buffer_Overflow_DropsOldestAndAdvancesOldestAvailable()
    {
        using var registry = CreateRegistry(out var options, maxBufferedItems: 3);
        registry.Register(new RegQuery("p"), null, null, new FixedHandler(8));

        var run = await WaitForRunAsync(registry);
        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));

        // 8 项产出、缓冲 3 → 只保留最后 3 项，最旧可用位置前移 5
        await Assert.That(run.BufferedCount).IsEqualTo(3);
        await Assert.That(run.OldestAvailable).IsEqualTo(5);
        await Assert.That(run.NextIndex).IsEqualTo(8);
    }

    [Test]
    public async Task Cancel_TerminatesRunningRun_AsCancelled()
    {
        using var registry = CreateRegistry(out var options);
        options.DetachGrace = TimeSpan.FromSeconds(30);
        var runId = registry.Register(new RegQuery("p"), null, null, new BlockingHandler());
        _ = await WaitForRunAsync(registry);

        await Assert.That(registry.Cancel(runId)).IsTrue();
        var run = await WaitForRunAsync(registry, runId);
        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));

        await Assert.That(run.State).IsEqualTo(StreamRunState.Cancelled);
        // 幂等：终态再取消仍返回 true（204 语义由端点承载）
        await Assert.That(registry.Cancel(runId)).IsTrue();
    }

    [Test]
    public async Task Cancel_UnknownRun_ReturnsFalse()
    {
        using var registry = CreateRegistry(out _);
        await Assert.That(registry.Cancel("nope")).IsFalse();
    }

    [Test]
    public async Task MaxItems_StopsRun_AsCancelled()
    {
        using var registry = CreateRegistry(out var options, maxItems: 3);
        options.DetachGrace = TimeSpan.FromSeconds(30);
        var runId = registry.Register(new RegQuery("p"), null, null, new FixedHandler(50));
        var run = await WaitForRunAsync(registry, runId);
        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));

        await Assert.That(run.State).IsEqualTo(StreamRunState.Cancelled);
        await Assert.That(run.NextIndex).IsEqualTo(3);
    }

    [Test]
    public async Task CapacityExceeded_Throws()
    {
        using var registry = CreateRegistry(out var options, maxConcurrentRuns: 1, detachGraceSeconds: 30);
        _ = registry.Register(new RegQuery("p"), null, null, new BlockingHandler());
        _ = await WaitForRunAsync(registry);

        await Assert.That(() => registry.Register(new RegQuery("p2"), null, null, new BlockingHandler()))
            .Throws<StreamRunCapacityExceededException>();
    }

    [Test]
    public async Task DetachGrace_WithoutAttach_CancelsRun()
    {
        // 直接持有 run（不建附着）：宽限从「最后一次附着离开」起算——无附着场景由
        // TryBeginAttach/ReleaseAttachment 配对模拟：先附着再立即释放
        using var registry = CreateRegistry(out var options, detachGraceSeconds: 0.3);
        var runId = registry.Register(new RegQuery("p"), null, null, new BlockingHandler());
        var run = await WaitForRunAsync(registry, runId);

        await Assert.That(run.TryBeginAttach(null, out _, out _, out _)).IsTrue();
        await run.ReleaseAttachmentAsync();

        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));
        await Assert.That(run.State).IsEqualTo(StreamRunState.Cancelled);
    }

    [Test]
    public async Task Attach_ResolvesStartIndexById_IncludingCustomIds()
    {
        using var registry = CreateRegistry(out var options, maxBufferedItems: 8);
        options.DetachGrace = TimeSpan.FromSeconds(30);
        // 自定义 id（非数字）：验证按 Id 反查而非仅数字解析
        var runId = registry.Register(
            new RegQuery("p"),
            eventSelector: null,
            idSelector: static chunk => $"evt-{chunk.Index}",
            handlerOverride: new FixedHandler(5));
        var run = await WaitForRunAsync(registry, runId);
        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));

        await Assert.That(run.TryBeginAttach("evt-2", out var startIndex, out var gap, out _)).IsTrue();
        await Assert.That(startIndex).IsEqualTo(3); // evt-2 之后从 index 3 起
        await Assert.That(gap).IsFalse();
        await run.ReleaseAttachmentAsync();
    }

    [Test]
    public async Task Attach_EvictedPosition_ReportsGap()
    {
        using var registry = CreateRegistry(out var options, maxBufferedItems: 2);
        options.DetachGrace = TimeSpan.FromSeconds(30);
        var runId = registry.Register(new RegQuery("p"), null, null, new FixedHandler(6));
        var run = await WaitForRunAsync(registry, runId);
        await WaitUntilAsync(() => run.State != StreamRunState.Running, TimeSpan.FromSeconds(5));

        // 缓冲只剩 index 4/5；请求从 index 0 之后续 → 位置已滚出 → gap
        await Assert.That(run.TryBeginAttach("0", out var startIndex, out var gap, out _)).IsTrue();
        await Assert.That(gap).IsTrue();
        await Assert.That(startIndex).IsEqualTo(run.OldestAvailable);
        await run.ReleaseAttachmentAsync();
    }

    [Test]
    public async Task RejectNewAttach_WithActiveAttachment_ReturnsFalse()
    {
        using var registry = CreateRegistry(out var options, detachGraceSeconds: 30);
        options.RejectNewAttach = true;
        var runId = registry.Register(new RegQuery("p"), null, null, new BlockingHandler());
        var run = await WaitForRunAsync(registry, runId);

        await Assert.That(run.TryBeginAttach(null, out _, out _, out _)).IsTrue();
        await Assert.That(run.TryBeginAttach(null, out _, out _, out _)).IsFalse();
        await run.ReleaseAttachmentAsync();
    }

    [Test]
    public async Task Takeover_IncrementsGeneration_WithoutReject()
    {
        using var registry = CreateRegistry(out var options, detachGraceSeconds: 30);
        var runId = registry.Register(new RegQuery("p"), null, null, new BlockingHandler());
        var run = await WaitForRunAsync(registry, runId);

        await Assert.That(run.TryBeginAttach(null, out _, out _, out var gen1)).IsTrue();
        await Assert.That(run.TryBeginAttach(null, out _, out _, out var gen2)).IsTrue();
        await Assert.That(gen2).IsEqualTo(gen1 + 1);
        await Assert.That(run.AttachedCount).IsEqualTo(2); // 两连接并存直到旧者观测到代际变化
    }

    [Test]
    public async Task StreamToAsync_CancelledWhileIdle_CompletesWithoutWaitingHeartbeat()
    {
        using var registry = CreateRegistry(out var options, detachGraceSeconds: 30);
        options.DetachGrace = TimeSpan.FromSeconds(30);
        var runId = registry.Register(new RegQuery("p"), null, null, new BlockingHandler());
        var run = await WaitForRunAsync(registry, runId);
        await Assert.That(run.TryBeginAttach(null, out var startIndex, out var gap, out var generation)).IsTrue();

        using var cts = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.RequestAborted = cts.Token;
        context.Response.Body = new MemoryStream();

        var streamTask = run.StreamToAsync(
            context,
            RegChunkJsonContext.Default.RegChunk,
            generation,
            startIndex,
            gap,
            keepAliveInterval: TimeSpan.FromSeconds(30));

        await Task.Delay(100);
        cts.Cancel();

        // 旧实现 Monitor.Wait(30s) 不观察 RequestAborted，这里会超时；
        // 新实现 WaitAsync(ms, ct) 在取消时立即返回（超时抛 TimeoutException 即判红）。
        try
        {
            await streamTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            // 断线取消是正常收尾路径
        }
    }

    // ─── 夹具 ───

    private static StreamRunRegistry CreateRegistry(
        out DetachedStreamOptions options,
        int maxBufferedItems = 16,
        int maxItems = 100_000,
        int maxConcurrentRuns = 100,
        double detachGraceSeconds = 5)
    {
        options = new DetachedStreamOptions
        {
            MaxBufferedItems = maxBufferedItems,
            MaxItems = maxItems,
            MaxConcurrentRuns = maxConcurrentRuns,
            DetachGrace = TimeSpan.FromSeconds(detachGraceSeconds),
            MaxRunDuration = TimeSpan.FromMinutes(1)
        };
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new StreamRunRegistry(options, provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static Task<StreamRun<RegChunk>> WaitForRunAsync(StreamRunRegistry registry, string? runId = null)
    {
        return WaitCore();

        async Task<StreamRun<RegChunk>> WaitCore()
        {
            for (var i = 0; i < 200; i++)
            {
                if (runId is not null)
                {
                    if (registry.TryGet<RegChunk>(runId, out var run))
                        return run!;
                }
                else
                {
                    // 单 run 场景：取唯一已注册项
                    var only = registry.PeekSingle<RegChunk>();
                    if (only is not null)
                        return only;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("Run not found in registry.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not met within {timeout}.");
    }
}

internal sealed record RegChunk(int Index);

[JsonSerializable(typeof(RegChunk))]
internal sealed partial class RegChunkJsonContext : JsonSerializerContext;

internal sealed record RegQuery(string Prompt) : IStreamQuery<RegChunk>;

/// <summary>产出固定 count 项。</summary>
internal sealed class FixedHandler(int count) : IStreamQueryHandler<RegQuery, RegChunk>
{
    public async IAsyncEnumerable<RegChunk> HandleAsync(
        RegQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new RegChunk(i);
            await Task.Yield();
        }
    }
}

/// <summary>首项后无限阻塞（取消/宽限/容量探针）。</summary>
internal sealed class BlockingHandler : IStreamQueryHandler<RegQuery, RegChunk>
{
    public async IAsyncEnumerable<RegChunk> HandleAsync(
        RegQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new RegChunk(0);
        await Task.Delay(Timeout.Infinite, ct);
    }
}
