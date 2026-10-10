// ─────────────────────────────────────────────────────────────
// 横向对比车道（2026-10-09 新维度轮）——MediatR 同进程同 harness 四点对照
// + 绝对 QPS 并行吞吐量具（--qps harness，非 BDN：BDN 单线程 ns/op 不可直接
// 外推多线程聚合吞吐，专门量具做 1/4/8 线程 × 2 场景的实测 ops/sec）。
// 公平性口径：同 handler 形态（同步完成、每次 new 结果对象含 Guid 生成）、
// 同管道形态（零行为 + 单直通行为）、同 MediumRun InProcess 工具链同轮执行。
// MediatR 版本锚定 12.5.0：13.0.0 起转商业许可（License 变更），
// 对齐本仓「所有引用的库必须是开源许可」裁决。
// 命名空间注：两框架各有 IRequest<>/IPipelineBehavior<,>——碰撞处以全限定引用。
// ─────────────────────────────────────────────────────────────
using System.Data.Common;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using DotNetCore.CAP;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;
using PalDDD.Dapper;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Transactions;
using MediatRCommand = MediatR.IRequest<PalDDD.Benchmarks.MediatRResult>;
using MediatRBehavior = MediatR.IPipelineBehavior<PalDDD.Benchmarks.MediatRCommand, PalDDD.Benchmarks.MediatRResult>;
using MediatRHandlerIface = MediatR.IRequestHandler<PalDDD.Benchmarks.MediatRCommand, PalDDD.Benchmarks.MediatRResult>;
using PalBehavior = PalDDD.CQRS.IPipelineBehavior<PalDDD.Benchmarks.CmpCommand, PalDDD.Benchmarks.CmpResult>;
using PalHandlerIface = PalDDD.CQRS.ICommandHandler<PalDDD.Benchmarks.CmpCommand, PalDDD.Benchmarks.CmpResult>;

namespace PalDDD.Benchmarks;

// 对比契约（命名空间层级：using 别名需要完整类型名，嵌套私有类不可达）
public sealed record CmpCommand(string Name, decimal Amount) : PalDDD.CQRS.ICommand<CmpResult>;
public sealed record CmpResult(Guid Id);

public sealed record MediatRCommand(string Name, decimal Amount) : MediatR.IRequest<MediatRResult>;
public sealed record MediatRResult(Guid Id);

[MemoryDiagnoser]
// ⚠️ 无 job attribute：--compare 入口模式级 ManualConfig（同 FrameworkBenchmarks.cs 头注）
public class MediatRComparisonBenchmarks
{
    private PalDDD.CQRS.Dispatcher _palNoBehavior = null!;
    private PalDDD.CQRS.Dispatcher _palOneBehavior = null!;
    private IMediator _mediatrNoBehavior = null!;
    private IMediator _mediatrOneBehavior = null!;
    private static readonly CmpCommand Command = new("cmp", 42m);
    private static readonly MediatRCommand MrCmd = new("cmp", 42m);

    [GlobalSetup]
    public void Setup()
    {
        // PalDDD 侧（形态对齐 AppBenchmarks.DispatcherBenchmarks）
        var plain = new ServiceCollection();
        plain.AddSingleton<PalHandler>();
        _palNoBehavior = new Dispatcher(plain.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        _palNoBehavior.Register<CmpCommand, CmpResult, PalHandler>();

        var withBehavior = new ServiceCollection();
        withBehavior.AddSingleton<PalHandler>();
        withBehavior.AddSingleton<PalBehavior, PalPassThrough>();
        _palOneBehavior = new Dispatcher(withBehavior.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        _palOneBehavior.Register<CmpCommand, CmpResult, PalHandler>();

        // MediatR 侧（12.x：RegisterServicesFromAssembly 扫描注册，一次性 setup 不计入测量）
        var mrPlain = new ServiceCollection();
        mrPlain.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(MediatRCommand).Assembly));
        _mediatrNoBehavior = mrPlain.BuildServiceProvider().GetRequiredService<IMediator>();

        var mrBehavior = new ServiceCollection();
        mrBehavior.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(MediatRCommand).Assembly));
        mrBehavior.AddTransient<MediatRBehavior, MrPassThrough>();
        _mediatrOneBehavior = mrBehavior.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<CmpResult> PalDDD_NoBehavior() => _palNoBehavior.SendAsync(Command);

    [Benchmark]
    public ValueTask<CmpResult> PalDDD_OneBehavior() => _palOneBehavior.SendAsync(Command);

    [Benchmark]
    public Task<MediatRResult> MediatR_NoBehavior() => _mediatrNoBehavior.Send(MrCmd, default);

    [Benchmark]
    public Task<MediatRResult> MediatR_OneBehavior() => _mediatrOneBehavior.Send(MrCmd, default);

    public sealed class MrHandler : MediatRHandlerIface
    {
        public Task<MediatRResult> Handle(MediatRCommand request, CancellationToken ct)
            => Task.FromResult(new MediatRResult(Guid.NewGuid()));
    }

    public sealed class MrPassThrough : MediatRBehavior
    {
        public async Task<MediatRResult> Handle(MediatRCommand request, MediatR.RequestHandlerDelegate<MediatRResult> next, CancellationToken ct)
            => await next();
    }

    public sealed class PalHandler : PalHandlerIface
    {
        public ValueTask<CmpResult> HandleAsync(CmpCommand cmd, CancellationToken ct)
            => ValueTask.FromResult(new CmpResult(Guid.NewGuid()));
    }

    public sealed class PalPassThrough : PalBehavior
    {
        public ValueTask<CmpResult> HandleAsync(CmpCommand request, CancellationToken ct, Func<ValueTask<CmpResult>> next)
            => next();
    }
}

// ═══════════════════════════════════════════════════════════
// 绝对 QPS 量具（--qps harness）——1/4/8 线程 × 2 场景，每格 3 秒实测聚合 ops/sec。
// 非 BDN：多线程聚合吞吐不是单线程 ns/op 的线性外推（锁竞争/分配压力/GC 并发），
// 专门 harness 直接测量；口径与 --smoke 同级（趋势级，环境标签必带）。
// 场景：dispatch = 纯分发（零行为）；chain = 分发→序列化→Outbox 落入→SaveChanges
//（InMemory store 逐 op 重建——与 EndToEndChainBenchmarks 同口径，ThreadLocal 保线程安全）。
// ═══════════════════════════════════════════════════════════
internal static class QpsHarness
{
    internal static int Run()
    {
        const int durationSeconds = 3;
        int[] threadsSet = [1, 4, 8];

        Console.WriteLine($"QPS harness: {durationSeconds}s/格 · 场景=dispatch/chain · 线程={string.Join("/", threadsSet)}");
        Console.WriteLine("口径：趋势级（单轮实测；同日同机可比，跨环境须带标签复测）");

        // dispatch 场景（共享 Dispatcher——线程安全 Singleton）
        var plain = new ServiceCollection();
        plain.AddSingleton<MediatRComparisonBenchmarks.PalHandler>();
        var dispatcher = new Dispatcher(plain.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<CmpCommand, CmpResult, MediatRComparisonBenchmarks.PalHandler>();
        var command = new CmpCommand("qps", 1m);

        foreach (var threads in threadsSet)
            RunCell("dispatch", threads, durationSeconds, () => dispatcher.SendAsync(command).AsTask().GetAwaiter().GetResult());

        // chain 场景（ThreadLocal store——每线程独立，逐 op 重建与 EndToEnd 同口径）
        var chainServices = new ServiceCollection();
        chainServices.AddSingleton<ChainHandler>();
        var chainDispatcher = new Dispatcher(chainServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        chainDispatcher.Register<CmpCommand, CmpResult, ChainHandler>();
        var chainHandler = chainServices.BuildServiceProvider().GetRequiredService<ChainHandler>();

        foreach (var threads in threadsSet)
        {
            RunCell("chain", threads, durationSeconds, () =>
            {
                chainHandler.Store.Value = new InMemoryOutboxStore();
                _ = chainDispatcher.SendAsync(command).AsTask().GetAwaiter().GetResult();
            });
        }

        Console.WriteLine("✅ QPS harness 完成");
        return 0;
    }

    private static void RunCell(string scenario, int threads, int seconds, Action op)
    {
        // 预热 200ms（JIT/内联稳定；不计入）
        var warmupEnd = DateTime.UtcNow.AddMilliseconds(200);
        while (DateTime.UtcNow < warmupEnd) op();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 专项治理轮（2026-10-10）：单轮 3s 是趋势级口径，跨轮波动不可分离——改 3 轮取中位
        // 并报区间（min~max），单轮离群（后台负载窗口）不再主导读数；中位数与历史单轮值同量纲
        const int rounds = 3;
        var roundQps = new double[rounds];
        for (var r = 0; r < rounds; r++)
        {
            var counts = new long[threads];
            var threadsArr = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                var idx = t;
                threadsArr[t] = new Thread(() =>
                {
                    long c = 0;
                    var end = DateTime.UtcNow.AddSeconds(seconds);
                    while (DateTime.UtcNow < end)
                    {
                        op();
                        c++;
                    }
                    counts[idx] = c;
                });
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var th in threadsArr) th.Start();
            foreach (var th in threadsArr) th.Join();
            sw.Stop();

            var total = 0L;
            foreach (var c in counts) total += c;
            roundQps[r] = total / sw.Elapsed.TotalSeconds;
        }

        Array.Sort(roundQps);
        var median = roundQps[rounds / 2];
        Console.WriteLine(
            $"{scenario,-8} threads={threads} : 中位 {median:N0} ops/s（{rounds} 轮，区间 {roundQps[0]:N0}~{roundQps[^1]:N0}；{1_000_000_000 / (median / threads):F0} ns/op/线程）");
    }

    private sealed class ChainHandler : PalHandlerIface
    {
        private readonly JsonMessageSerializer _serializer;
        private object? _last;

        public ChainHandler()
        {
            var builder = new MessageCatalogBuilder();
            builder.Add<QpsChainPlaced>(QpsJsonContext.Default.QpsChainPlaced, "bench.chain.v1");
            _serializer = new JsonMessageSerializer(builder.Build());
        }

        // ThreadLocal：--qps chain 场景多线程下每线程独立 store（逐 op 重建，EndToEnd 同口径）
        public ThreadLocal<InMemoryOutboxStore> Store { get; } = new(() => new InMemoryOutboxStore());

        public async ValueTask<CmpResult> HandleAsync(CmpCommand cmd, CancellationToken ct)
        {
            var orderId = Guid.NewGuid();
            var payload = _serializer.Serialize(new QpsChainPlaced(orderId, cmd.Name, cmd.Amount));
            var store = Store.Value!;
            await store.AddMessageAsync(new OutboxMessage
            {
                Type = "bench.chain.v1",
                Payload = payload.ToArray(),
                ContentType = "application/json"
            });
            await store.SaveChangesAsync(ct);
            _last = store;   // 观测点防 DCE
            return new CmpResult(orderId);
        }
    }
}

[JsonSerializable(typeof(QpsChainPlaced))]
internal sealed partial class QpsJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

// QpsHarness 内部无法声明满足 JsonSerializerContext 的包含链（SYSLIB1032），
// 命名空间层级记录作载体
internal sealed record QpsChainPlaced(Guid OrderId, string Name, decimal Amount);

// ═══════════════════════════════════════════════════════════
// CAP 对比车道（DotNetCore.CAP 8.4.1，MIT）——「本地消息表写入 + 异步投递入队」口径。
// 公平性口径：两侧同为「同步写消息表 + 后台异步送 broker」——CAP.PublishAsync 内联
// 写 cap.published 行并入队后台 sender；PalDDD 侧为「序列化 + outbox_messages 写行」
//（broker 投递由 OutboxProcessor 后台负责，语义对位 CAP 的 sender）。broker：一次性
// rabbit 容器（不碰常驻 rabbit436）。
// 环境变量 opt-in（Program --compare 检查 PALDDD_BENCH_CAP_MYSQL + PALDDD_BENCH_CAP_RABBIT）：
//   PALDDD_BENCH_CAP_MYSQL  = Server=...;Database=palddd_bench_cap;...（CAP 建自有表，应用侧 DDL 另行注入）
//   PALDDD_BENCH_CAP_RABBIT = host:port:user:pass
// ═══════════════════════════════════════════════════════════
[MemoryDiagnoser]
// ⚠️ 无 job attribute：--compare 入口模式级 ManualConfig（同文件头注）
public class CapComparisonBenchmarks
{
    private ServiceProvider _provider = null!;
    private ICapPublisher _cap = null!;
    private DapperOutboxStore _palOutbox = null!;
    private DbConnection _keeper = null!;

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        var mysql = Environment.GetEnvironmentVariable("PALDDD_BENCH_CAP_MYSQL")
            ?? throw new InvalidOperationException("CAP 对比需 PALDDD_BENCH_CAP_MYSQL");
        var rabbit = Environment.GetEnvironmentVariable("PALDDD_BENCH_CAP_RABBIT")
            ?? throw new InvalidOperationException("CAP 对比需 PALDDD_BENCH_CAP_RABBIT");
        var rabbitParts = rabbit.Split(':');
        var (host, port, user, pass) = (rabbitParts[0], int.Parse(rabbitParts[1]), rabbitParts[2], rabbitParts[3]);

        // 前置检查门（2026-10-10 全量复跑实录）：rabbit 凭据竞态（env 用户未生效/节点未就绪
        // 即 add_user 失败被吞）曾使 CAP 侧以「连接重试风暴」形态产出 3s/op 的静默垃圾数字
        // 且 exit 0——无效环境必须快速失败。此处用真实 AMQP 连接预检认证，失败抛指向
        // runbook 的异常（BDN 对 GlobalSetup 抛异常的基准报错非零退出）。
        // 既证 runbook（本机 wslc 形态）：①容器启动即带 RABBITMQ_DEFAULT_USER/PASSWORD env
        // 建用户；②等节点就绪（sleep 25 级）后 rabbitmqctl change_password 显式重置（env 凭据
        // 可能未生效，2026-10-09 实录）；③rabbitmqctl authenticate_user 验证通过才跑基准。
        // 反面形态（2026-10-10 首跑实录）：节点就绪前 add_user 静默失败 + 认证探针永不通过
        // → 本检查门将拦截（替代曾经的 3s/op 重试风暴）
        var preflight = new RabbitMQ.Client.ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = user,
            Password = pass,
            AutomaticRecoveryEnabled = false,
        };
        try
        {
            await using var probe = await preflight.CreateConnectionAsync("palddd-bench-cap-preflight").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CAP 对比前置检查失败：rabbit {host}:{port} 认证不可达（{ex.GetType().Name}: {ex.Message}）。"
                + "既证 runbook：容器 env 建用户 → 节点就绪后 rabbitmqctl change_password 重置 → authenticate_user 验证"
                + "（详见 CapComparisonBenchmarks 类头注）。本检查门使无效环境快速失败，不产出重试风暴形态的无效数字。");
        }

        // CAP 侧（8.4.1：MySQL 存储 + RabbitMQ 传输；显式启动 hosted services——无 Host 环境。
        // AddLogging 满足 CAP 内部 ILogger<T> 依赖——纯 ServiceProvider 不带默认日志设施）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCap(x =>
        {
            x.UseMySql(mysql);
            x.UseRabbitMQ(r => { r.HostName = host; r.Port = port; r.UserName = user; r.Password = pass; });
        });
        _provider = services.BuildServiceProvider();
        foreach (var hosted in _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            await hosted.StartAsync(default);

        _cap = _provider.GetRequiredService<ICapPublisher>();

        // PalDDD 侧（同库写 outbox_messages——应用侧 DDL 注入，镜像 RemoteDialectBenchSession）
        _keeper = new MySqlConnector.MySqlConnection(mysql);
        await _keeper.OpenAsync(default);
        // MySQL DDL 无 IF NOT EXISTS：表已存在即跳过（会话级幂等，镜像远程会话）
        await using (var check = _keeper.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'outbox_messages'";
            if (await check.ExecuteScalarAsync(default) is null)
            {
                var ddlPath = FindSchemaFile("docs/sql/mysql/000_schema.sql");
                await using var cmd = _keeper.CreateCommand();
                cmd.CommandText = await File.ReadAllTextAsync(ddlPath, default);
                await cmd.ExecuteNonQueryAsync(default);
            }
        }
        // Dapper store 复用 keeper 连接（单线程吞吐类，同 Dapper 远程吞吐类形态）
        _palOutbox = new DapperOutboxStore(_keeper, DapperDbType.MySql);
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        foreach (var hosted in _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Reverse())
            await hosted.StopAsync(default);
        await _provider.DisposeAsync();
        _keeper.Dispose();
    }

    [IterationSetup]
    public async Task Setup()
    {
        // CAP 首次发布前初始化存储表（幂等）；两表清场（cap 表在 CAP 启动后已存在）
        try
        {
            await using var cmd = _keeper.CreateCommand();
            cmd.CommandText = "DELETE FROM cap.published; DELETE FROM outbox_messages";
            await cmd.ExecuteNonQueryAsync(default);
        }
        catch (MySqlConnector.MySqlException)
        {
            // cap.published 未建（首次 IterationSetup 早于 CAP 惰性建表）——跳过清场，CAP 建表后自然恢复
        }
    }

    // CAP：同步写 cap.published + 入队后台 sender（口径见类头；callbackName 显式 null 消解重载二义）
    [Benchmark(Baseline = true)]
    public async Task Cap_PublishAsync()
        => await _cap.PublishAsync("bench.cap.v1", new CapPayload(Guid.NewGuid(), "cmp", 42m), callbackName: null, default);

    // PalDDD 对位：序列化 + outbox_messages 写行（broker 投递由 OutboxProcessor 后台负责）
    [Benchmark]
    public async Task PalDDD_SerializeAndOutboxAdd()
    {
        var payload = _serializer.Serialize(new QpsChainPlaced(Guid.NewGuid(), "cmp", 42m));
        await _palOutbox.AddMessageAsync(new OutboxMessage
        {
            Type = "bench.cap.v1",
            Payload = payload.ToArray(),
            ContentType = "application/json"
        });
    }

    private readonly JsonMessageSerializer _serializer = CreateSerializer();

    private static JsonMessageSerializer CreateSerializer()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add<QpsChainPlaced>(QpsJsonContext.Default.QpsChainPlaced, "bench.cap.v1");
        return new JsonMessageSerializer(builder.Build());
    }

    // CAP 消息契约（CAP 自带序列化器处理——其成本属 CAP 侧被测路径的一部分）
    public sealed record CapPayload(Guid OrderId, string Name, decimal Amount);

    private static string FindSchemaFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent!;
        if (dir is null)
            throw new InvalidOperationException($"未找到 {relative}——应从仓库内运行 bench");
        return Path.Combine(dir.FullName, relative);
    }
}
