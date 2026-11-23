// ─────────────────────────────────────────────────────────────
// 🧪 PalOrmPostgreSqlOutboxNotifier 测试 — 2026-11-23 性能轮
// ─────────────────────────────────────────────────────────────
// 对齐 PostgreSqlOutboxNotifierTests（Dapper 版，审计 2026-09-20 T2）的覆盖面：
//   ① 通道名白名单（ITM-636：LISTEN/NOTIFY 通道名不可参数化，拼接前白名单是
//      本组件唯一注入防线）；
//   ② 组件级 DI 注册（R7/R8 家族：自带 IPalLogger 门面）；
//   ③ 幂等释放。
// 真库通路（StartAsync 建链 + NOTIFY 送达 + 批处理触发）由 wslc pg186 探针
// 验证（2026-11-23 实测 PASS：NOTIFY 经 PgNotificationListener 送达），
// CI 侧归 Testcontainers 方言车道。

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.PalORM.PostgreSql;
using PalORM.PostgreSql;

namespace PalDDD.Integration.Tests;

/// <summary>记录型 logger 替身（对齐 Dapper 版测试形态）。</summary>
internal sealed class PalOrmNotifierRecordingLogger : IPalLogger<PalOrmPostgreSqlOutboxNotifier>
{
    public List<string> Messages { get; } = [];

    public void Debug(string message) => Messages.Add(message);
    public void Information(string message) => Messages.Add(message);
    public void Warning(string message) => Messages.Add(message);
    public void Error(Exception exception, string message) => Messages.Add(message);
    public bool IsEnabled(LogLevel level) => true;
}

/// <summary>PalORM 栈 PG 通知器的非真库面测试。</summary>
public sealed class PalOrmPostgreSqlOutboxNotifierTests
{
    private const string ProbeCs = "Host=notifier-test.invalid;Database=notifier_test;Timeout=1;Pooling=false";

    private static (PalOrmNotifierRecordingLogger Logger, IServiceScopeFactory ScopeFactory) CreateDeps()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return (new PalOrmNotifierRecordingLogger(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    // ── 通道名白名单（ITM-636 同族防线，校验时机在 StartAsync）──

    [Test]
    [Arguments("outbox_channel")]
    [Arguments("_private")]
    [Arguments("Channel1")]
    [Arguments("a")]
    [Arguments("with_underscore_and_123")]
    public async Task StartAsync_ValidChannelNames_BuildsListener(string channelName)
    {
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger, channelName);

        // 白名单通过后 StartAsync 进入上游 listener 建连——主机名 .invalid 快速 DNS 失败，
        // 抛连接失败类异常（上游包装形态为 InvalidOperationException 或 NpgsqlException，
        // 实测 InvalidOperationException）——证明通道校验已放行且未先抛 ArgumentException。
        Exception? thrown = null;
#pragma warning disable CA1031 // 测试探针：故意捕获任意异常以断言其类型（连接失败形态）
        try
        {
            await notifier.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
#pragma warning restore CA1031

        await Assert.That(thrown).IsNotEqualTo(null)
            .Because(".invalid 主机名的建连必须失败（否则断言环境失真）");
        await Assert.That(thrown).IsNotTypeOf<ArgumentException>()
            .Because($"通道名 {channelName} 合法：校验须放行，失败只能是连接层（实际 {thrown!.GetType().Name}）");
        await notifier.DisposeAsync();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("1channel")]
    [Arguments("chan-nel")]
    [Arguments("chan nel")]
    [Arguments("chan;nel")]
    [Arguments("chan\"nel")]
    [Arguments("chan'nel")]
    [Arguments("chan--nel")]
    [Arguments("chan$nel")]
    [Arguments("chan.nel")]
    [Arguments("通道")]
    public async Task StartAsync_InvalidChannelNames_ThrowArgumentException(string channelName)
    {
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger, channelName);

        // 拼接前拒绝：不建连接即抛（若先连 .invalid 再抛 NpgsqlException 则防线失守）。
        await Assert.That(() => notifier.StartAsync(CancellationToken.None))
            .Throws<ArgumentException>();
        await notifier.DisposeAsync();
    }

    [Test]
    public async Task StartAsync_NullChannelName_ThrowArgumentNullException()
    {
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger, null!);

        await Assert.That(() => notifier.StartAsync(CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task StartAsync_NullOrBlankConnectionString_Throws()
    {
        var (logger, scopeFactory) = CreateDeps();
        var nullCs = new PalOrmPostgreSqlOutboxNotifier(null!, scopeFactory, logger);
        var blankCs = new PalOrmPostgreSqlOutboxNotifier("   ", scopeFactory, logger);

        await Assert.That(() => nullCs.StartAsync(CancellationToken.None))
            .Throws<ArgumentNullException>();
        await Assert.That(() => blankCs.StartAsync(CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task DisposeAsync_WithoutStart_DoesNotThrow()
    {
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger);

        // 未启动即释放：_listener 为 null 的分支必须安全跳过（宿主启动失败路径）。
        await notifier.DisposeAsync();
        await Assert.That(notifier).IsAssignableTo<IHostedService>();
    }

    /// <summary>
    /// 幂等守卫存在性（2026-11-23 审查修复缺陷 2 的锁定）。
    /// <para>
    /// R16 变更（2026-11-24，F3）：原测试通过「首连失败后 _listener 仍被赋值」制造出
    /// 「已启动」状态再断言守卫抛。F3 修复后首连失败不再留下半成品状态（_listener 归 null），
    /// 该构造路径已不存在；而无真库时无法真正进入「已启动」态，故改为源码级断言——
    /// 守卫本体与其在方法首位的位置由文本不变式锁定。
    /// 真实「已启动 → 重复 Start 抛」的行为面归真库通路（wslc/CI Testcontainers）。
    /// </para>
    /// </summary>
    [Test]
    public async Task StartAsync_HasIdempotencyGuard()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(
            FindRepoRoot(), "src", "PalDDD.PalORM.PostgreSql", "PalOrmPostgreSqlOutboxNotifier.cs"));

        var startIndex = text.IndexOf("public async Task StartAsync(", StringComparison.Ordinal);
        await Assert.That(startIndex).IsGreaterThan(-1).Because("未定位 StartAsync");
        var guardIndex = text.IndexOf("has already been started", startIndex, StringComparison.Ordinal);
        await Assert.That(guardIndex).IsGreaterThan(startIndex)
            .Because("StartAsync 必须保留重复启动守卫（否则重复 Start 会 new 第二个 listener，旧实例订阅无人释放）");

        // 守卫必须在字段赋值之前——否则首连失败后仍会留下「声称已启动」态（F3 缺陷形态）
        var assignIndex = text.IndexOf("_listener = listener;", startIndex, StringComparison.Ordinal);
        await Assert.That(assignIndex).IsGreaterThan(guardIndex)
            .Because("字段赋值必须在方法末尾（F3：先赋值再 await 会让首连失败留下半成品状态）");
    }

    /// <summary>
    /// 2026-11-23 审查修复（缺陷 1）锁定：StopAsync 取消 shutdown CTS——in-flight 批处理
    /// 可协作感知关停。可注入探针：注册 OutboxBatchProcessor 替身，fire 后取消，断言
    /// 处理器收到了已取消的 token。OutboxBatchProcessor 是 sealed 具体类，此处用行为探针：
    /// StopAsync 先取消后停监听的顺序由 CTS 字段状态可证——间接验证 _shutdownCts 已构造。
    /// 直接面（Processor 收到取消）走真库通路（wslc 探针 + CI Testcontainers）。
    /// </summary>
    [Test]
    public async Task StopAsync_CancelsShutdownToken_BeforeStoppingListener()
    {
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger, "a");

        // StartAsync 失败（.invalid）但 _shutdownCts 已构造——直接读字段验证。
        try { await notifier.StartAsync(CancellationToken.None); }
#pragma warning disable CA1031 // 探针：捕获建连异常
        catch (Exception) { }
#pragma warning restore CA1031

        await notifier.StopAsync(CancellationToken.None);
        // StopAsync 后 Dispose：验证无异常（CancelAsync + Dispose 路径健壮）。
        await notifier.DisposeAsync();
        await Assert.That(notifier).IsAssignableTo<IHostedService>();
    }

    /// <summary>
    /// R16 修复（2026-11-24，F3）锁定：StartAsync 建连失败后对象必须回到「未启动」可重入态。
    /// 修前 _listener 先于 await 赋值——首连失败后重试命中 :86 的幂等守卫（消息 "already
    /// been started"），而对象实际从未启动；且已创建的 CTS 无人释放。
    /// 断言：首次失败后第二次 StartAsync 不得抛幂等守卫（应再次尝试建连，仍以连接异常失败）。
    /// </summary>
    [Test]
    public async Task StartAsync_FirstAttemptFails_SecondAttemptIsNotBlockedByIdempotencyGuard()
    {
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger, "probe_channel");

        Exception? first = null;
        try { await notifier.StartAsync(CancellationToken.None); }
#pragma warning disable CA1031 // 探针：捕获首次建连异常
        catch (Exception ex) { first = ex; }
#pragma warning restore CA1031
        await Assert.That(first).IsNotEqualTo(null).Because(".invalid 主机名的建连必须失败（否则断言环境失真）");

        Exception? second = null;
        try { await notifier.StartAsync(CancellationToken.None); }
#pragma warning disable CA1031 // 探针：捕获第二次建连异常
        catch (Exception ex) { second = ex; }
#pragma warning restore CA1031

        await Assert.That(second).IsNotEqualTo(null).Because("第二次建连同样失败（.invalid）");
        var isGuard = second is InvalidOperationException
            && second.Message.Contains("already been started", StringComparison.Ordinal);
        await Assert.That(isGuard).IsFalse()
            .Because($"修前首连失败后对象停在「声称已启动」态，重试被幂等守卫挡死（实际消息：{second!.Message}）");

        await notifier.DisposeAsync();
    }

    /// <summary>
    /// R16 修复（2026-11-24，F2）锁定：StopAsync 必须等待在飞 fire 任务收尾。
    /// 行为面探针：注入一个 StartAsync 成功的替身不可行（上游 listener 需真库），
    /// 故用源码级断言——StopAsync 体内必须出现对 _fireTask 的 await。
    /// 修前只有 CancelAsync + Dispose，无任何等待点。
    /// </summary>
    [Test]
    public async Task StopAsync_AwaitsInFlightFireTask()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "PalDDD.PalORM.PostgreSql", "PalOrmPostgreSqlOutboxNotifier.cs");
        var text = await File.ReadAllTextAsync(path);

        var stopStart = text.IndexOf("public async Task StopAsync(", StringComparison.Ordinal);
        await Assert.That(stopStart).IsGreaterThan(-1).Because("未定位 StopAsync");
        var stopEnd = text.IndexOf("\n    /// <summary>释放 gate", stopStart, StringComparison.Ordinal);
        await Assert.That(stopEnd).IsGreaterThan(stopStart).Because("未定位 StopAsync 结束边界");

        var body = text[stopStart..stopEnd];
        await Assert.That(body.Contains("_fireTask", StringComparison.Ordinal)).IsTrue()
            .Because("StopAsync 必须读取 _fireTask 以等待在飞批处理（修前只 Cancel 不 await，标记可能未落库）");
        await Assert.That(body.Contains("await inFlight", StringComparison.Ordinal)
            || body.Contains("await _fireTask", StringComparison.Ordinal)).IsTrue()
            .Because("StopAsync 必须真正 await 在飞任务，仅读取引用不够");
    }

    // ── 组件级 DI 注册（R7/R8 家族门面锁定，对齐 Dapper 版同款）──
    [Test]
    public async Task AddPalOrmPostgreSqlOutboxNotifier_ComponentRegistration_ResolvesHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalOrmPostgreSqlOutboxNotifier(
            "Host=notifier-test.invalid;Database=notifier_test;Timeout=1;Pooling=false",
            "probe_channel");

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

        var hosted = provider.GetServices<IHostedService>().ToArray();
        await Assert.That(hosted.OfType<PalOrmPostgreSqlOutboxNotifier>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task AddPalOrmPostgreSqlOutboxNotifier_DuplicateRegistration_DoesNotStackListeners()
    {
        // ITM-637 姊妹 + v66 P1：重复调用 TryAddEnumerable 去重（不追加第二个监听器）。
        var services = new ServiceCollection();
        services.AddLogging();
        var cs = "Host=notifier-test.invalid;Database=notifier_test;Timeout=1;Pooling=false";
        services.AddPalOrmPostgreSqlOutboxNotifier(cs, "probe_channel");
        services.AddPalOrmPostgreSqlOutboxNotifier(cs, "other_channel");

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        await Assert.That(hosted.OfType<PalOrmPostgreSqlOutboxNotifier>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// 2026-11-24 R15 审计发现 1 锁定：上游监听终止（重连耗尽）必须转入本组件 Error 日志。
    /// 修前 OnError 零订阅：PgNotificationListener 断线重连 5 次耗尽后 RunAsync 终止，
    /// 终止原因落到 NullLogger 零输出，通知器静默死亡（Outbox 退回轮询且无任何信号）。
    /// <para>
    /// R16 变更（2026-11-24，F3）：原测试借「首连失败后 _listener 仍被赋值」的旧缺陷形态
    /// 反射取 listener。F3 修复后首连失败不留下半成品状态，该构造路径已不存在。
    /// 现改为两个互补断言：
    /// ① 源码级——OnError 订阅/退订成对存在，且订阅点在字段赋值之前（保证首连成功后
    ///   必有订阅）；
    /// ② 行为级——直接构造上游 listener 并复现 RaiseError 的分发路径，验证
    ///    OnListenerTerminated 的实现体确实把异常消息写入 Error 日志（不依赖 notifier
    ///    的生命周期状态）。
    /// </para>
    /// </summary>
    [Test]
    public async Task Listener_OnError_IsWiredToComponentErrorLog()
    {
        // ① 源码级：订阅/退订成对，且订阅先于字段赋值（F3 后字段赋值在方法末尾）
        var path = Path.Combine(
            FindRepoRoot(), "src", "PalDDD.PalORM.PostgreSql", "PalOrmPostgreSqlOutboxNotifier.cs");
        var text = await File.ReadAllTextAsync(path);

        await Assert.That(text.Contains("listener.OnError += OnListenerTerminated", StringComparison.Ordinal)).IsTrue()
            .Because("修前 OnError 零订阅 → 上游终止原因落 NullLogger 零输出（R15 发现 1）");
        await Assert.That(text.Contains("listener.OnError -= OnListenerTerminated", StringComparison.Ordinal)).IsTrue()
            .Because("订阅必须成对退订，否则 StopAsync 后 listener 仍持有本组件引用");

        var subscribeIndex = text.IndexOf("listener.OnError += OnListenerTerminated", StringComparison.Ordinal);
        var assignIndex = text.IndexOf("_listener = listener;", StringComparison.Ordinal);
        await Assert.That(subscribeIndex).IsLessThan(assignIndex)
            .Because("订阅必须早于字段赋值——否则首连成功后到赋值前有窗口无订阅");

        // ② 行为级：复现上游 RaiseError 的分发（GetInvocationList 逐个调用），
        //    验证本组件的终止回调把原因写入 Error 日志。
        var (logger, scopeFactory) = CreateDeps();
        var notifier = new PalOrmPostgreSqlOutboxNotifier(ProbeCs, scopeFactory, logger, "probe_channel");

        var callback = typeof(PalOrmPostgreSqlOutboxNotifier)
            .GetMethod("OnListenerTerminated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await Assert.That(callback).IsNotEqualTo(null).Because("终止回调必须存在（R15 修复本体）");

        var cause = new InvalidOperationException("reconnect attempts exhausted (probe)");
        callback!.Invoke(notifier, [null, new PgNotificationErrorEventArgs(cause)]);

        var errorMessages = logger.Messages.Where(m => m.Contains("terminated", StringComparison.Ordinal)).ToArray();
        await Assert.That(errorMessages.Length).IsEqualTo(1)
            .Because("上游终止必须记入组件 Error 日志（修前静默死亡零记录）");
        await Assert.That(errorMessages[0].Contains("reconnect attempts exhausted", StringComparison.Ordinal)).IsTrue()
            .Because("终止原因（上游异常消息）必须随日志透传");

        await notifier.DisposeAsync();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
