// ─────────────────────────────────────────────────────────────
// 特性面缺口终轮车道 — Projection / 消息演化 / Specification / 端到端链路 / Hosting
//（2026-10-08 缺口清单 ③④⑤⑥⑧ 项；⑦ 残余由远程车道栈扩展承接）
// 入口 --coverage（MediumRun InProcess 同 --framework 口径）。
// 各类口径在其类头注明；端到端与 Hosting 为「框架开销层」测量（InMemory store /
// DefaultHttpContext），不含网络与真实 DB I/O。
// ─────────────────────────────────────────────────────────────
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using PalDDD.Core;
using PalDDD.Hosting.AspNetCore;
using PalDDD.Projections;
using PalDDD.Serialization;
using PalDDD.Serialization.Evolution;
using PalDDD.Serialization.Json;
using PalDDD.Transactions;

namespace PalDDD.Benchmarks;

// ═══════════════════════════════════════════════════════════
// ③ Projection 吞吐 — 处理器全周期 + 断点存储直测（InMemory 层）
// 全周期口径：TryStart(租约) → handler.ProjectAsync → MarkCompleted；
// position 逐 op 递增（真实投影流形态——断点语义按 position 单调推进）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 无 job attribute：--coverage 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job（同 FrameworkBenchmarks.cs 头注，见 Program.cs 注释）
public class ProjectionBenchmarks
{
    private ProjectionProcessor<BenchProjectedEvent> _processor = null!;
    private InMemoryProjectionCheckpointStore _checkpointStore = null!;
    private long _position;

    [GlobalSetup]
    public void Setup()
    {
        _checkpointStore = new InMemoryProjectionCheckpointStore();
        _processor = new ProjectionProcessor<BenchProjectedEvent>(
            new CountingProjectionHandler(), _checkpointStore);
    }

    [Benchmark(Baseline = true)]
    public async Task<bool> ProcessAsync_FullCycle()
        => await _processor.ProcessAsync(
            new BenchProjectedEvent(_position++),
            new ProjectionContext("bench-source", $"pos-{_position}", DateTimeOffset.UtcNow),
            default);

    // 断点存储直测（隔离处理器编排开销）：TryStart + MarkCompleted 一周期
    [Benchmark]
    public async Task<int> Checkpoint_TryStartComplete_Cycle()
    {
        var checkpoint = await _checkpointStore.TryStartAsync(
            "bench-projection", "bench-source", $"pos-{_position++}",
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), default);
        if (checkpoint is null)
            throw new InvalidOperationException("新 position 的 TryStart 不应返回 null");
        await _checkpointStore.MarkCompletedAsync(checkpoint, DateTimeOffset.UtcNow, default);
        return 0;
    }

    public sealed record BenchProjectedEvent(long Sequence);

    private sealed class CountingProjectionHandler : IProjectionHandler<BenchProjectedEvent>
    {
        private long _count;

        public string ProjectionName => "bench-projection";

        public ValueTask ProjectAsync(BenchProjectedEvent evt, ProjectionContext context, CancellationToken ct = default)
        {
            _count += evt.Sequence;   // 观测点防 DCE
            return ValueTask.CompletedTask;
        }
    }
}

// ═══════════════════════════════════════════════════════════
// ④ 消息演化管线 — V1→V2 单跳升级 vs V1→V2→V3 链式升级（消费侧 Upgrade 热路径）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 同上注
public class MessageEvolutionBenchmarks
{
    private MessageEvolutionPipeline _singleHop = null!;
    private MessageEvolutionPipeline _chain = null!;
    private JsonMessageSerializer _serializer = null!;
    private MessageDescriptor _v1 = null!;
    private MessageDescriptor _v2 = null!;
    private MessageDescriptor _v3 = null!;
    private byte[] _v1Payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 演化契约：同 wire name + 递增 schemaVersion（管线校验「stable wire name」——
        // 版本以 schemaVersion 承载而非名字后缀，README 演化段同款语义）
        var builder = new MessageCatalogBuilder();
        _v1 = builder.Add<EvolvedOrderV1>(EvolutionJsonContext.Default.EvolvedOrderV1, "bench.evolved", 1);
        _v2 = builder.Add<EvolvedOrderV2>(EvolutionJsonContext.Default.EvolvedOrderV2, "bench.evolved", 2);
        _v3 = builder.Add<EvolvedOrderV3>(EvolutionJsonContext.Default.EvolvedOrderV3, "bench.evolved", 3);
        _serializer = new JsonMessageSerializer(builder.Build());

        _singleHop = new MessageEvolutionBuilder()
            .Add<EvolvedOrderV1, EvolvedOrderV2>(_v1, _v2,
                old => new EvolvedOrderV2(old.OrderId, old.Amount, null))
            .Build();

        _chain = new MessageEvolutionBuilder()
            .Add<EvolvedOrderV1, EvolvedOrderV2>(_v1, _v2,
                old => new EvolvedOrderV2(old.OrderId, old.Amount, null))
            .Add<EvolvedOrderV2, EvolvedOrderV3>(_v2, _v3,
                old => new EvolvedOrderV3(old.OrderId, old.Amount, old.CouponCode, 0))
            .Build();

        _v1Payload = _serializer.Serialize(new EvolvedOrderV1(Guid.NewGuid(), 99.9m)).ToArray();
    }

    [Benchmark(Baseline = true)]
    public object? Upgrade_SingleHop_V1toV2()
        => _singleHop.Upgrade(_v1Payload, _v1, _v2, _serializer);

    [Benchmark]
    public object? ChainUpgrade_V1toV3()
        => _chain.Upgrade(_v1Payload, _v1, _v3, _serializer);
}

// ═══════════════════════════════════════════════════════════
// ⑤ Specification 内存路径 — Expression.Compile 求值（README「AOT 已知限制③」的量化）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 同上注
public class SpecificationBenchmarks
{
    private ISpecification<BenchSpecEntity> _spec = null!;
    private static readonly BenchSpecEntity Match = new(200);
    private static readonly BenchSpecEntity NoMatch = new(50);

    [GlobalSetup]
    public void Setup() => _spec = Spec<BenchSpecEntity>.Where(e => e.Amount > 100);

    [Benchmark(Baseline = true)]
    public bool IsSatisfiedBy_Match() => _spec.IsSatisfiedBy(Match);

    [Benchmark]
    public bool IsSatisfiedBy_NoMatch() => _spec.IsSatisfiedBy(NoMatch);

    [Benchmark]
    public Expression<Func<BenchSpecEntity, bool>> ToExpression_Access() => _spec.ToExpression();

    public sealed record BenchSpecEntity(decimal Amount);
}

// ═══════════════════════════════════════════════════════════
// ⑧ 端到端单命令链路（框架开销层）— 分发 → handler → 序列化 → Outbox 落入
//（InMemory store，无 DB I/O；命令实例复用只测链路，store 逐 op 重建保状态恒定）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 同上注
public class EndToEndChainBenchmarks
{
    private Dispatcher _noBehavior = null!;
    private Dispatcher _oneBehavior = null!;
    private ChainHandler _handler = null!;
    private static readonly ChainCommand Command = new("chain-order", 42m);

    [GlobalSetup]
    public void Setup()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add<ChainOrderPlaced>(ChainJsonContext.Default.ChainOrderPlaced, "bench.chain.v1");
        _handler = new ChainHandler(new JsonMessageSerializer(builder.Build()));

        var plain = new ServiceCollection();
        plain.AddSingleton(_handler);
        _noBehavior = new Dispatcher(plain.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        _noBehavior.Register<ChainCommand, ChainResult, ChainHandler>();

        var withBehavior = new ServiceCollection();
        withBehavior.AddSingleton(_handler);
        withBehavior.AddSingleton<IPipelineBehavior<ChainCommand, ChainResult>, PassThroughChainBehavior>();
        _oneBehavior = new Dispatcher(withBehavior.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        _oneBehavior.Register<ChainCommand, ChainResult, ChainHandler>();
    }

    [Benchmark(Baseline = true)]
    public async Task<Guid> FullChain_NoBehavior()
    {
        _handler.Store = new InMemoryOutboxStore();
        var result = await _noBehavior.SendAsync(Command);
        return result.Id;
    }

    [Benchmark]
    public async Task<Guid> FullChain_OneBehavior()
    {
        _handler.Store = new InMemoryOutboxStore();
        var result = await _oneBehavior.SendAsync(Command);
        return result.Id;
    }

    public sealed record ChainCommand(string Name, decimal Amount) : ICommand<ChainResult>;
    public sealed record ChainResult(Guid Id);
    public sealed record ChainOrderPlaced(Guid OrderId, string Name, decimal Amount);

    private sealed class ChainHandler(JsonMessageSerializer serializer) : ICommandHandler<ChainCommand, ChainResult>
    {
        private readonly JsonMessageSerializer _serializer = serializer;
        private object? _last;

        public InMemoryOutboxStore Store { get; set; } = null!;

        public async ValueTask<ChainResult> HandleAsync(ChainCommand cmd, CancellationToken ct)
        {
            var orderId = Guid.NewGuid();
            var payload = _serializer.Serialize(new ChainOrderPlaced(orderId, cmd.Name, cmd.Amount));
            await Store.AddMessageAsync(new OutboxMessage
            {
                Type = "bench.chain.v1",
                Payload = payload.ToArray(),
                ContentType = "application/json"
            });
            await Store.SaveChangesAsync(ct);
            _last = Store;   // 观测点防 DCE
            return new ChainResult(orderId);
        }
    }

    private sealed class PassThroughChainBehavior : IPipelineBehavior<ChainCommand, ChainResult>
    {
        public ValueTask<ChainResult> HandleAsync(ChainCommand request, CancellationToken ct, Func<ValueTask<ChainResult>> next)
            => next();

        public Type RequestType => typeof(ChainCommand);
    }
}

// ═══════════════════════════════════════════════════════════
// ⑥ Hosting 每请求开销 — 终端基线 / 异常中间件直通 / MapCommand 端点全周期
//（DefaultHttpContext 直接调 RequestDelegate，无网络栈；端点含 JSON 反序列化 + 分发 + 响应序列化）
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 同上注
public class HostingBenchmarks
{
    private RequestDelegate _terminalOnly = null!;
    private RequestDelegate _withExceptionMiddleware = null!;
    private RequestDelegate _commandEndpoint = null!;
    private IServiceProvider _requestServices = null!;
    private byte[] _commandBody = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 分发侧 provider：自建（端点运行时经 ctx.RequestServices 取 Dispatcher——
        // CqrsTests 同款构造形态；WebApplication 仅承载 routing 的端点定义）
        var handlerServices = new ServiceCollection();
        handlerServices.AddSingleton<HostingCommandHandler>();
        var handlerProvider = handlerServices.BuildServiceProvider();
        var dispatcher = new Dispatcher(handlerProvider.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<EndToEndChainBenchmarks.ChainCommand, EndToEndChainBenchmarks.ChainResult, HostingCommandHandler>();

        var endpointServices = new ServiceCollection();
        endpointServices.AddSingleton(dispatcher);
        // ExceptionMiddleware 构造依赖 IPalLogger<T>（UseMiddleware 按请求激活经 RequestServices
        // 解析）。NullPalLogger<T> 是私有构造器单例——开放泛型注册无法实例化，注册封闭类型实例
        endpointServices.AddSingleton<PalDDD.Core.Logging.IPalLogger<PalDDD.Hosting.AspNetCore.ExceptionMiddleware>>(
            PalDDD.Core.Logging.NullPalLogger<PalDDD.Hosting.AspNetCore.ExceptionMiddleware>.Instance);
        _requestServices = endpointServices.BuildServiceProvider();

        // MapCommand 端点走完整 WebApplication（routing 需 host 面）。端点在 StartAsync 前
        // 不填充（DataSourceDependentCache 惰性提交）——起停一次取 RequestDelegate，不起持续服务器
        var appBuilder = WebApplication.CreateBuilder();
        var app = appBuilder.Build();
        app.MapCommand<EndToEndChainBenchmarks.ChainCommand, EndToEndChainBenchmarks.ChainResult>(
            "/bench-cmd",
            HostingJsonContext.Default.ChainCommand,
            HostingJsonContext.Default.ChainResult);
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            _commandEndpoint = app.Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>().Single().RequestDelegate!;
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
        _commandBody = JsonSerializer.SerializeToUtf8Bytes(
            new EndToEndChainBenchmarks.ChainCommand("hosting", 1m), HostingJsonContext.Default.ChainCommand);

        // 终端基线 / 异常中间件直通：纯 ApplicationBuilder 组管（无 host 开销）
        var terminalBuilder = new ApplicationBuilder(_requestServices);
        _terminalOnly = terminalBuilder.Build();

        var middlewareBuilder = new ApplicationBuilder(_requestServices);
        middlewareBuilder.UsePalExceptionHandler();
        _withExceptionMiddleware = middlewareBuilder.Build();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Terminal_Only()
    {
        var ctx = NewContext();
        await _terminalOnly(ctx);
        return ctx.Response.StatusCode;
    }

    [Benchmark]
    public async Task<int> ExceptionMiddleware_Passthrough()
    {
        var ctx = NewContext();
        await _withExceptionMiddleware(ctx);
        return ctx.Response.StatusCode;
    }

    [Benchmark]
    public async Task<int> MapCommand_Endpoint_Invoke()
    {
        var ctx = new DefaultHttpContext { RequestServices = _requestServices };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/bench-cmd";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(_commandBody);
        ctx.Request.ContentLength = _commandBody.Length;
        ctx.Response.Body = new MemoryStream();
        await _commandEndpoint(ctx);
        return ctx.Response.StatusCode;
    }

    private DefaultHttpContext NewContext()
        => new() { RequestServices = _requestServices };

    private sealed class HostingCommandHandler : ICommandHandler<EndToEndChainBenchmarks.ChainCommand, EndToEndChainBenchmarks.ChainResult>
    {
        public ValueTask<EndToEndChainBenchmarks.ChainResult> HandleAsync(EndToEndChainBenchmarks.ChainCommand cmd, CancellationToken ct)
            => ValueTask.FromResult(new EndToEndChainBenchmarks.ChainResult(Guid.NewGuid()));
    }
}

[JsonSerializable(typeof(EvolvedOrderV1))]
[JsonSerializable(typeof(EvolvedOrderV2))]
[JsonSerializable(typeof(EvolvedOrderV3))]
public sealed partial class EvolutionJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(EndToEndChainBenchmarks.ChainOrderPlaced))]
public sealed partial class ChainJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(EndToEndChainBenchmarks.ChainCommand))]
[JsonSerializable(typeof(EndToEndChainBenchmarks.ChainResult))]
public sealed partial class HostingJsonContext : JsonSerializerContext
{
}

public sealed record EvolvedOrderV1(Guid OrderId, decimal Amount);
public sealed record EvolvedOrderV2(Guid OrderId, decimal Amount, string? CouponCode);
public sealed record EvolvedOrderV3(Guid OrderId, decimal Amount, string? CouponCode, int Priority);
