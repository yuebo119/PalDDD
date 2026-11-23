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
}
