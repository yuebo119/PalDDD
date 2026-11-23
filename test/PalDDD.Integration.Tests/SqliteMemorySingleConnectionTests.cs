// ─────────────────────────────────────────────────────────────
// 🧪 SQLite 内存单连接守卫 — R16 修复（2026-11-24，F6）
// ─────────────────────────────────────────────────────────────
// 缺陷：:memory: 注册 Singleton 单连接（连接关闭数据即销毁），而 SqliteConnection
// 非线程安全。契约写「调用方必须串行访问」，但本仓自己的后台服务
// （OutboxProcessor / SagaProcessor / RetentionProcessorService）会在独立线程上访问
// 同一个连接——第二个写入者由框架自己注册。
//
// 探针实证（4 线程 × 500 次并发 SELECT 同一 :memory: 连接）：2000 次中 18 次抛
// ArgumentOutOfRangeException / IndexOutOfRangeException（驱动内部状态被踩坏的越界异常，
// 不是 "database is locked"）。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Dapper.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>
/// <c>:memory:</c> 单连接与后台服务共存的启动期守卫（行为测试）。
/// 注：类名不带 <c>*GuardTests</c> 后缀——该后缀是 guard.cs 的「门禁守卫」命名约定，
/// 会被要求登记进 guards 清单；本类是普通行为测试，不属于 pre-commit 门禁序列。
/// </summary>
public sealed class SqliteMemorySingleConnectionTests
{
    /// <summary>
    /// 内存库 + 本框架后台服务 → 启动即抛，错误信息给出两条出路。
    /// 修前无守卫：宿主正常启动，并发损坏只在运行时以无关异常形态暴露。
    /// </summary>
    [Test]
    public async Task InMemoryWithPalBackgroundService_ThrowsOnStart()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalSqliteInMemory();
        // 模拟本框架的后台服务（命名空间 PalDDD.* 是守卫的判定面）
        services.AddSingleton<IHostedService, FakePalBackgroundService>();

        await using var provider = services.BuildServiceProvider();
        var guards = provider.GetServices<IHostedService>().ToArray();

        InvalidOperationException? thrown = null;
        foreach (var hosted in guards)
        {
            try
            {
                await hosted.StartAsync(CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
                break;
            }
        }

        await Assert.That(thrown).IsNotEqualTo(null)
            .Because("内存单连接与后台服务共存必须在启动期 fail-fast（修前无守卫，恒为 null）");
        await Assert.That(thrown!.Message.Contains("Singleton 单连接", StringComparison.Ordinal)).IsTrue()
            .Because($"错误信息须点明单连接前提（实际：{thrown.Message}）");
        await Assert.That(thrown.Message.Contains("FakePalBackgroundService", StringComparison.Ordinal)).IsTrue()
            .Because("错误信息须列出检测到的后台服务名，便于排障");
        await Assert.That(thrown.Message.Contains("Data Source=pal.db", StringComparison.Ordinal)).IsTrue()
            .Because("错误信息须给出文件模式这条出路");
    }

    /// <summary>
    /// 内存库单独使用（无后台服务）→ 不抛。这是测试场景的主流形态，守卫不得误伤。
    /// </summary>
    [Test]
    public async Task InMemoryWithoutBackgroundService_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalSqliteInMemory();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        // 守卫自身必须已注册（否则本测试与「有后台服务」用例都只是空转）
        await Assert.That(hosted.Any(h => h.GetType().Name == "PalSqliteMemorySingleConnectionGuard")).IsTrue()
            .Because("内存分支必须注册守卫，否则无后台服务的场景永远绿而真问题漏检");

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// 文件模式 + 后台服务 → 不抛（Scoped 连接隔离，无共享单连接问题）。
    /// </summary>
    [Test]
    public async Task FileModeWithBackgroundService_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"pal-guard-{Guid.NewGuid():N}.db")}");
        services.AddSingleton<IHostedService, FakePalBackgroundService>();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        // 文件模式不注册守卫（Scoped 连接无共享单连接问题）
        await Assert.That(hosted.Any(h => h.GetType().Name == "PalSqliteMemorySingleConnectionGuard")).IsFalse()
            .Because("文件模式不得注册内存守卫（否则会误拦合法的 Scoped 连接组合）");

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
    }
}

/// <summary>本框架后台服务的替身（命名空间前缀 PalDDD.* 命中守卫判定面）。</summary>
internal sealed class FakePalBackgroundService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
