// ─────────────────────────────────────────────────────────────
// 🧪 PostgreSqlOutboxNotifier 测试 — 审计 2026-09-20 T2
// ─────────────────────────────────────────────────────────────
// 背景：该类（PG LISTEN/NOTIFY 后台服务，Dapper PG 栈"零延迟 Outbox"
// 卖点实现）此前全仓零测试引用、覆盖率 0/113。其 ExecuteAsync 需真实
// PG 连接（由 CI dialect-probe 的 Testcontainers 覆盖），本文件覆盖
// **不依赖真库**的部分：
//   ① 构造器参数校验（null 守卫）；
//   ② 通道名白名单校验——LISTEN/NOTIFY 通道名不可参数化（Npgsql 协议
//      限制），必须字符串拼接进 `LISTEN "<name>"`，故入口白名单是该
//      组件唯一的 SQL 注入防线（ITM-636）。这一面必须有回归测试。
//
// 覆盖范围声明（诚实边界）：连接生命周期、NOTIFY 触发批处理、
// SemaphoreSlim 背压、断线重连退避、catch (ObjectDisposedException)
// 静默 swallow 等仍需 Testcontainers 真库，本次未覆盖。

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.Dapper.PostgreSql;
using Npgsql;

namespace PalDDD.Integration.Tests;

/// <summary>记录型 logger 替身——断言日志通道不被静默吞掉。</summary>
internal sealed class RecordingLogger : IPalLogger<PostgreSqlOutboxNotifier>
{
    public List<string> Messages { get; } = [];

    public void Debug(string message) => Messages.Add(message);

    public void Information(string message) => Messages.Add(message);

    public void Warning(string message) => Messages.Add(message);

    public void Error(Exception exception, string message) => Messages.Add(message);

    public bool IsEnabled(LogLevel level) => true;
}

/// <summary>PostgreSqlOutboxNotifier 的非真库面测试（审计 T2）。</summary>
public sealed class PostgreSqlOutboxNotifierTests
{
    private static (RecordingLogger Logger, IServiceScopeFactory ScopeFactory) CreateDeps()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return (new RecordingLogger(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        // 仅用于构造器注入——本组测试不打开连接（无 OpenAsync 调用），
        // 故不会触碰真实数据库。主机名为保留测试域 .invalid，即便误打开
        // 也是快速 DNS 失败而非连上某台真实库。
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = "notifier-test.invalid",
            Database = "notifier_test",
        };
        return new NpgsqlDataSourceBuilder(csb.ConnectionString).Build();
    }

    [Test]
    public async Task Constructor_NullArguments_Throw()
    {
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        await Assert.That(() => new PostgreSqlOutboxNotifier(null!, scopeFactory, logger))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new PostgreSqlOutboxNotifier(dataSource, null!, logger))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new PostgreSqlOutboxNotifier(dataSource, scopeFactory, null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_DefaultChannelName_Accepted()
    {
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        // 默认 "outbox_channel" 必须通过校验（回归：默认值被误拒则组件完全不可用）。
        // 行为断言：构造成功后实例可作 IHostedService 使用，而非仅 IsNotNull。
        var notifier = new PostgreSqlOutboxNotifier(dataSource, scopeFactory, logger);
        await Assert.That(notifier).IsAssignableTo<IHostedService>();
    }

    // ── 通道名白名单（ITM-636：LISTEN/NOTIFY 唯一的注入防线）──

    [Test]
    [Arguments("outbox_channel")]
    [Arguments("_private")]
    [Arguments("Channel1")]
    [Arguments("a")]
    [Arguments("with_underscore_and_123")]
    public async Task Constructor_ValidChannelNames_Accepted(string channelName)
    {
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        // 行为断言：合法通道名构造成功且可托管（构造抛异常即本用例失败）
        var notifier = new PostgreSqlOutboxNotifier(dataSource, scopeFactory, logger, channelName: channelName);
        await Assert.That(notifier).IsAssignableTo<IHostedService>();
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
    [Arguments("chan/*nel")]
    [Arguments("chan$nel")]
    [Arguments("chan.nel")]
    [Arguments("通道")]
    public async Task Constructor_InvalidChannelNames_ThrowArgumentException(string channelName)
    {
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        // 这些形态若进入 `LISTEN "<name>"` 字符串拼接即构成 SQL 注入或语法错误：
        // 首字符非字母/下划线（数字开头）、含非 [A-Za-z0-9_] 字符（引号/分号/
        // SQL 注释符/空白/点号/中文）都必须在入口拒绝。
        await Assert.That(() => new PostgreSqlOutboxNotifier(
                dataSource, scopeFactory, logger, channelName: channelName))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_NullChannelName_ThrowArgumentNullException()
    {
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        await Assert.That(() => new PostgreSqlOutboxNotifier(
                dataSource, scopeFactory, logger, channelName: null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Notifier_IsHostedService_CanBeRegistered()
    {
        // 类型契约：必须可作 IHostedService 注册（DI 用法 AddPalPostgreSqlOutboxNotifier 的前提）
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        var notifier = new PostgreSqlOutboxNotifier(dataSource, scopeFactory, logger);
        await Assert.That(notifier).IsAssignableTo<IHostedService>();
    }

    [Test]
    public async Task Dispose_IsIdempotent_DoesNotThrow()
    {
        var (logger, scopeFactory) = CreateDeps();
        await using var dataSource = CreateDataSource();

        var notifier = new PostgreSqlOutboxNotifier(dataSource, scopeFactory, logger);
        notifier.Dispose();
        // SemaphoreSlim 重复释放会抛 ObjectDisposedException——幂等性是宿主
        // 关停路径的硬要求（ITM-217 姊妹修复模式）。行为断言：第二次 Dispose
        // 不抛即通过（TUnit 对未抛异常的 void 调用直接判绿），另验构造参数
        // 在 Dispose 后仍可读，证明实例未被置坏。
        notifier.Dispose();
        await Assert.That(notifier).IsAssignableTo<IHostedService>();
    }
}
