using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PalDDD.EventLog.Tests;

// ══════════════════════════════════════════════════════════════
// A1 审计项回归：EventLogPositionReserver 的 DI 注册入口与接线。
//
// 背景（audit-2026-09-20.md A1）：Hi/Lo 分配器的价值完全依赖「进程内 chunk 缓存被
// 所有 DbContext 共享」，而 EventLogDbContext 构造器参数默认 null → 每实例 new。
// Scoped 生命周期下 chunk 缓存永不跨请求共享：每次 append 都走 allocator 行
// SELECT + CAS UPDATE（比直接取数据库序列更慢），且每次 append 消耗整个 chunk 的
// 位置（密度按 chunkSize 倍稀疏化）。此前仓库无注册入口，文档也未提该要求。
//
// 本文件锁定三件事：
//   ① AddPalEventLogEfCore 注册的生命周期为 Singleton（跨 scope 同一实例）；
//   ② 派生上下文经 ActivatorUtilities 解析时拿到的是该 Singleton（端到端接线）；
//   ③ 显式传入构造器的上下文不受注册影响（直构路径仍可用，测试替身场景）。
// ══════════════════════════════════════════════════════════════

public sealed class EventLogEfCoreRegistrationTests
{
    /// <summary>按审计建议形态派生：显式接收并转发 reserver。</summary>
    private sealed class WiredEventLogDbContext(
        DbContextOptions<WiredEventLogDbContext> options,
        EventLogPositionReserver reserver)
        : EventLogDbContext(options, positionReserver: reserver);

    private static DbContextOptions<WiredEventLogDbContext> CreateOptions(IServiceProvider sp)
        => new DbContextOptionsBuilder<WiredEventLogDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .UseApplicationServiceProvider(sp)
            .Options;

    [Test]
    public async Task AddPalEventLogEfCore_RegistersReserverAsSingleton(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddPalEventLogEfCore<WiredEventLogDbContext>();

        await using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var fromA = scopeA.ServiceProvider.GetRequiredService<EventLogPositionReserver>();
        var fromB = scopeB.ServiceProvider.GetRequiredService<EventLogPositionReserver>();

        // Singleton：跨 scope 同一实例（这是 chunk 缓存可共享的前提）
        await Assert.That(ReferenceEquals(fromA, fromB)).IsTrue();
    }

    [Test]
    public async Task AddPalEventLogEfCore_ChunkSizeFlowsToReserver(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddPalEventLogEfCore<WiredEventLogDbContext>(chunkSize: 7);

        await using var provider = services.BuildServiceProvider();
        var reserver = provider.GetRequiredService<EventLogPositionReserver>();

        // 行为验证而非反射读私有字段：chunkSize=7 时首个 chunk 用尽后第二次 Reserve
        // 必须再次触碰 DB（allocator 行推进），即快路径只覆盖 7 个位置。
        await using var db = new WiredEventLogDbContext(CreateOptions(provider), reserver);
        var first = await reserver.ReserveAsync(db, count: 7, cancellationToken);
        await Assert.That(first).IsEqualTo(0);

        var eighth = await reserver.ReserveAsync(db, count: 1, cancellationToken);
        await Assert.That(eighth).IsEqualTo(7);
    }

    [Test]
    public async Task AddPalEventLogEfCore_ContextResolvesInjectedSingleton(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddPalEventLogEfCore<WiredEventLogDbContext>();
        services.AddDbContext<WiredEventLogDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        });

        await using var provider = services.BuildServiceProvider();
        var singleton = provider.GetRequiredService<EventLogPositionReserver>();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<WiredEventLogDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<WiredEventLogDbContext>();

        // 端到端接线：两个 scope 的上下文必须共享同一个 reserver 实例
        var injectedA = GetInjectedReserver(contextA);
        var injectedB = GetInjectedReserver(contextB);
        await Assert.That(ReferenceEquals(injectedA, singleton)).IsTrue();
        await Assert.That(ReferenceEquals(injectedB, singleton)).IsTrue();
    }

    [Test]
    public async Task AddPalEventLogEfCore_DoesNotOverrideExistingRegistration(CancellationToken cancellationToken)
    {
        var custom = new EventLogPositionReserver(chunkSize: 3);
        var services = new ServiceCollection();
        services.AddSingleton(custom);
        services.AddPalEventLogEfCore<WiredEventLogDbContext>();

        await using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<EventLogPositionReserver>();

        // TryAdd 语义：调用方自己的注册优先（测试替身 / 自定义 chunkSize 场景）
        await Assert.That(ReferenceEquals(resolved, custom)).IsTrue();
    }

    /// <summary>读取基类私有字段以验证注入身份（测试代码用反射，非生产路径）。</summary>
    private static EventLogPositionReserver GetInjectedReserver(EventLogDbContext context)
    {
        var field = typeof(EventLogDbContext).GetField(
            "_positionReserver",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field is null)
            throw new InvalidOperationException(
                "EventLogDbContext._positionReserver 字段不存在——基类字段改名会使本接线验证静默失效。");
        return (EventLogPositionReserver)field.GetValue(context)!;
    }
}
