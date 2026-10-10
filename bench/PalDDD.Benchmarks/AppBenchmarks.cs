// ─────────────────────────────────────────────────────────────
// 应用层基准 — CQRS 分发链 + 消息序列化（2026-10-08 缺口补齐轮）
// 评估结论：Dispatcher 分发与序列化是消息发布路径的两大缺位车道（框架开销 +
// 序列化 + DB I/O 三段中，序列化此前零数据，分发仅有 <500B 分配契约无计时）。
// 入口 --app，口径同 --framework（模式级 MediumRun InProcess，见 Program.cs）。
// ─────────────────────────────────────────────────────────────
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;

namespace PalDDD.Benchmarks;

[MemoryDiagnoser]
// ⚠️ 无 job attribute：--app 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker 上触发
// DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
public class DispatcherBenchmarks
{
    private Dispatcher _noBehavior = null!;
    private Dispatcher _oneBehavior = null!;
    private Dispatcher _instanceNoBehavior = null!;
    private static readonly BenchCommand Command = new("bench-order", 99.9m);

    [GlobalSetup]
    public void Setup()
    {
        // 零行为基线：handler 同步完成（ValueTask.FromResult），测 IsCompletedSuccessfully 快路径
        // 与每请求 scope 创建 + FrozenDictionary 路由查找的真实分发成本（命令实例复用，只测分发不测命令构造）
        var plain = new ServiceCollection();
        plain.AddSingleton<BenchCommandHandler>();
        _noBehavior = new Dispatcher(plain.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        _noBehavior.Register<BenchCommand, BenchResult, BenchCommandHandler>();

        // 单直通行为（仅调 next()）：增量即 PipelineStateMachine 编排开销——每行为一次
        // 委托分配 + 状态机 ~40B/请求 + GetServices 枚举链，口径见 Dispatcher.ExecutePipelineAsync remarks
        var withBehavior = new ServiceCollection();
        withBehavior.AddSingleton<BenchCommandHandler>();
        withBehavior.AddSingleton<IPipelineBehavior<BenchCommand, BenchResult>, PassThroughBehavior>();
        _oneBehavior = new Dispatcher(withBehavior.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        _oneBehavior.Register<BenchCommand, BenchResult, BenchCommandHandler>();

        // v4.0 ①（ADR-026）：实例注册路径——根容器构造 + 实例绑定，免 per-request scope
        //（DI 注册形态 160B 中的最大剩余项即 scope 创建；行为从根容器解析，本例零行为）
        var instanceProvider = plain.BuildServiceProvider();
        _instanceNoBehavior = new Dispatcher(instanceProvider);
        _instanceNoBehavior.Register<BenchCommand, BenchResult, BenchCommandHandler>(
            instanceProvider.GetRequiredService<BenchCommandHandler>());
    }

    [Benchmark(Baseline = true)]
    public ValueTask<BenchResult> SendAsync_NoBehavior() => _noBehavior.SendAsync(Command);

    [Benchmark]
    public ValueTask<BenchResult> SendAsync_OneBehavior() => _oneBehavior.SendAsync(Command);

    // v4.0 ①：实例注册免 scope 分发——与 NoBehavior 同 handler 同命令，唯一差异是派发路径
    [Benchmark]
    public ValueTask<BenchResult> SendAsync_InstanceNoBehavior() => _instanceNoBehavior.SendAsync(Command);

    public sealed record BenchCommand(string Name, decimal Amount) : ICommand<BenchResult>;

    public sealed record BenchResult(Guid Id);

    public sealed class BenchCommandHandler : ICommandHandler<BenchCommand, BenchResult>
    {
        public ValueTask<BenchResult> HandleAsync(BenchCommand cmd, CancellationToken ct)
            => ValueTask.FromResult(new BenchResult(Guid.NewGuid()));
    }

    public sealed class PassThroughBehavior : IPipelineBehavior<BenchCommand, BenchResult>
    {
        public ValueTask<BenchResult> HandleAsync(BenchCommand request, CancellationToken ct, Func<ValueTask<BenchResult>> next)
            => next();

        public Type RequestType => typeof(BenchCommand);
    }
}

[MemoryDiagnoser]
// ⚠️ 无 job attribute：同上注（--app 入口模式级 ManualConfig）
public class SerializationBenchmarks
{
    private JsonMessageSerializer _serializer = null!;
    private MessageCatalog _catalog = null!;
    private MessageDescriptor _descriptor = null!;
    private static readonly BenchOrderPlaced Message = new(Guid.NewGuid(), "bench-order", 3);
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 源生成 JsonTypeInfo 经 CatalogBuilder 注册——生产 AOT 路径的同一形态
        //（根 props 全局 JsonSerializerIsReflectionEnabledByDefault=false，反射路径非代表形态）
        var builder = new MessageCatalogBuilder();
        _descriptor = builder.Add<BenchOrderPlaced>(BenchJsonContext.Default.BenchOrderPlaced, "bench.order-placed.v1");
        _catalog = builder.Build();
        _serializer = new JsonMessageSerializer(_catalog);
        _payload = _serializer.Serialize(Message).ToArray();
    }

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> Serialize_TypedMessage() => _serializer.Serialize(Message);

    [Benchmark]
    public object? Deserialize_TypedMessage() => _serializer.Deserialize<BenchOrderPlaced>(_payload, _descriptor);

    [Benchmark]
    public MessageDescriptor? Catalog_FindByName() => _catalog.Find("bench.order-placed.v1");

    [Benchmark]
    public MessageDescriptor? Catalog_FindByType() => _catalog.Find(typeof(BenchOrderPlaced));

    public sealed record BenchOrderPlaced(Guid OrderId, string Name, int Quantity);
}

// 命名空间层级而非嵌套：JsonSerializerContext 源生成要求包含类型链全 partial（SYSLIB1032），
// 基准类无须为此改 partial
[JsonSerializable(typeof(SerializationBenchmarks.BenchOrderPlaced))]
public sealed partial class BenchJsonContext : JsonSerializerContext
{
}
