using PalDDD.Core.Logging;
using PalDDD.Messaging.RabbitMQ;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using RabbitMQ.Client;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PalDDD.Benchmarks;

/// <summary>
/// M3 量具（2026-10-19）：RabbitMQ 1/4/8 订阅、共享 channel 与每订阅独立 consumer channel
/// 的混合负载对照。每个订阅经 fanout 收全量消息；记录端到端延迟 p50/p99 与吞吐。
/// <para>
/// 两个场景：<c>baseline</c>（全部订阅 handler 为空）测拓扑原始成本；
/// <c>isolation</c>（订阅 0 每次延迟 2ms，其余为空）测慢订阅对快订阅的队头阻塞——
/// M3 的设计收益只在后者成立，前者只应体现小幅拓扑成本。
/// </para>
/// 环境变量：PALDDD_BENCH_RABBIT_HOST/PORT/USER/PASS（缺省 127.0.0.1:5672/palddd-test）。
/// </summary>
internal static class RabbitChannelProbe
{
    private const int MessagesPerRound = 2_000;
    private const int SlowHandlerMs = 2;

    public static async Task<int> RunM3Async(CancellationToken ct = default)
    {
        var host = Environment.GetEnvironmentVariable("PALDDD_BENCH_RABBIT_HOST") ?? "127.0.0.1";
        var portRaw = Environment.GetEnvironmentVariable("PALDDD_BENCH_RABBIT_PORT");
        var port = int.TryParse(portRaw, out var parsed) ? parsed : 5672;
        var user = Environment.GetEnvironmentVariable("PALDDD_BENCH_RABBIT_USER") ?? "palddd-test";
        var pass = Environment.GetEnvironmentVariable("PALDDD_BENCH_RABBIT_PASS") ?? "palddd-test";

        var builder = new MessageCatalogBuilder();
        builder.Add<SerializationBenchmarks.BenchOrderPlaced>(
            BenchJsonContext.Default.BenchOrderPlaced, "m3.probe.v1");
        var catalog = builder.Build();
        var serializer = new JsonMessageSerializer(catalog);

        var factory = new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = user,
            Password = pass,
            AutomaticRecoveryEnabled = false,
        };

        Console.WriteLine("scenario   mode      subscriptions  delivered  throughput(msg/s)  publish(ms)  all p50(ms)  all p99(ms)  fast p50(ms)  fast p99(ms)");
        // 首轮会承担 JIT/连接/序列化初始化成本（实测 n=1 shared 的 p99 因此翻倍），
        // 先跑一轮小负载丢弃，保证表内各行同温。
        await RunRoundAsync(shared: true, subscriptions: 1, slow: false, factory, serializer, catalog, ct, messages: 200);

        var rows = new List<Row>();
        foreach (var slow in new[] { false, true })
        {
            foreach (var shared in new[] { true, false })
            {
                foreach (var n in new[] { 1, 4, 8 })
                {
                    if (slow && n == 1)
                        continue;

                    var row = await RunRoundAsync(shared, n, slow, factory, serializer, catalog, ct);
                    rows.Add(row);
                    Console.WriteLine(
                        $"{row.Scenario,-10} {row.Mode,-9} {n,13}  {n * MessagesPerRound,9}  {row.Throughput,17:F0}  " +
                        $"{row.PublishMs,11:F0}  {row.AllP50,11:F2}  {row.AllP99,11:F2}  {row.FastP50,11:F2}  {row.FastP99,11:F2}");
                }
            }
        }

        // 对照：每订阅独立 channel 相对共享 channel 的 p99 与吞吐比值（仅展示，不设硬阈值）。
        foreach (var scenario in new[] { "baseline", "isolation" })
        {
            foreach (var n in new[] { 1, 4, 8 })
            {
                var shared = rows.SingleOrDefault(r => r.Scenario == scenario && r.Mode == "shared" && r.N == n);
                var perSub = rows.SingleOrDefault(r => r.Scenario == scenario && r.Mode == "per-sub" && r.N == n);
                if (shared is null || perSub is null)
                    continue;

                if (scenario == "isolation")
                {
                    Console.WriteLine(
                        $"{scenario} n={n}: per-sub/shared throughput={perSub.Throughput / shared.Throughput:F2}x, " +
                        $"fast p99 shared={shared.FastP99:F2}ms vs per-sub={perSub.FastP99:F2}ms " +
                        $"({shared.FastP99 / Math.Max(perSub.FastP99, 0.0001):F0}x lower)");
                    continue;
                }

                Console.WriteLine(
                    $"{scenario} n={n}: per-sub/shared throughput={perSub.Throughput / shared.Throughput:F2}x, " +
                    $"p99(all)={perSub.AllP99 / shared.AllP99:F2}x, p99(fast)={perSub.FastP99 / shared.FastP99:F2}x");
            }
        }
        return 0;
    }

    private static async Task<Row> RunRoundAsync(
        bool shared,
        int subscriptions,
        bool slow,
        ConnectionFactory factory,
        JsonMessageSerializer serializer,
        IMessageCatalog catalog,
        CancellationToken ct,
        int messages = MessagesPerRound)
    {
        await using var connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var broker = await CreateBrokerAsync(connection, serializer, catalog, shared, ct).ConfigureAwait(false);

        var state = new RoundState(subscriptions * messages, subscriptions);
        var publishTicks = new ConcurrentDictionary<Guid, long>();
        var handles = new List<IAsyncDisposable>(subscriptions);
        try
        {
            for (var i = 0; i < subscriptions; i++)
            {
                var index = i;
                var handlerDelay = slow && index == 0 ? TimeSpan.FromMilliseconds(SlowHandlerMs) : TimeSpan.Zero;
                handles.Add(await broker.SubscribeAsync<SerializationBenchmarks.BenchOrderPlaced>(
                    async (message, _, token) =>
                    {
                        if (publishTicks.TryGetValue(message.OrderId, out var started))
                            state.Latencies[index].Add(Stopwatch.GetTimestamp() - started);
                        if (handlerDelay > TimeSpan.Zero)
                            await Task.Delay(handlerDelay, token).ConfigureAwait(false);
                        state.RecordDelivered();
                    }, ct).ConfigureAwait(false));
            }

            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < messages; i++)
            {
                var orderId = Guid.NewGuid();
                publishTicks[orderId] = Stopwatch.GetTimestamp();
                await broker.PublishAsync(
                    new SerializationBenchmarks.BenchOrderPlaced(orderId, "m3", 1), ct).ConfigureAwait(false);
            }
            var publishMs = stopwatch.Elapsed.TotalMilliseconds;

            await state.Completion.Task.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            stopwatch.Stop();

            var all = state.Latencies.SelectMany(x => x).ToArray();
            if (all.Length == 0)
                throw new InvalidOperationException("M3 probe received no messages");

            // 慢订阅自身的延迟含 2ms 处理时间，隔离效果只看其余快订阅。
            var fast = slow
                ? state.Latencies.Skip(1).SelectMany(x => x).ToArray()
                : all;
            Array.Sort(all);
            Array.Sort(fast);

            var delivered = subscriptions * messages;
            var throughput = delivered / stopwatch.Elapsed.TotalSeconds;
            return new Row(
                slow ? "isolation" : "baseline",
                shared ? "shared" : "per-sub",
                subscriptions,
                throughput,
                publishMs,
                Percentile(all, 0.50),
                Percentile(all, 0.99),
                Percentile(fast, 0.50),
                Percentile(fast, 0.99));
        }
        finally
        {
            foreach (var handle in handles)
                await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<RabbitMqBroker> CreateBrokerAsync(
        IConnection connection,
        IMessageSerializer serializer,
        IMessageCatalog catalog,
        bool shared,
        CancellationToken ct)
    {
        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true,
            outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(128),
            consumerDispatchConcurrency: null);
        var channel = await connection.CreateChannelAsync(options, ct).ConfigureAwait(false);
        return new RabbitMqBroker(
            connection, channel, NullPalLogger<RabbitMqBroker>.Instance, serializer, catalog,
            prefetchCount: 64)
        {
            UseSharedConsumerChannel = shared,
        };
    }

    private static double Percentile(long[] sortedTicks, double percentile)
    {
        var index = (int)Math.Ceiling((sortedTicks.Length - 1) * percentile);
        return sortedTicks[index] * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class RoundState(int expected, int subscriptions)
    {
        private int _delivered;
        public ConcurrentBag<long>[] Latencies { get; } =
            [.. Enumerable.Range(0, subscriptions).Select(_ => new ConcurrentBag<long>())];
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordDelivered()
        {
            if (Interlocked.Increment(ref _delivered) >= expected)
                Completion.TrySetResult();
        }
    }

    private sealed record Row(
        string Scenario,
        string Mode,
        int N,
        double Throughput,
        double PublishMs,
        double AllP50,
        double AllP99,
        double FastP50,
        double FastP99);
}
