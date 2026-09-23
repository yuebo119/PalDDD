// ─────────────────────────────────────────────────────────────
// 🧪 EFCore 适配器 DI 注册完整性测试 — 审计 2026-09-20 A7
// ─────────────────────────────────────────────────────────────
// 背景（audit-2026-09-20.md A7）：EventLog/Idempotency/Projections/Transactions 四个
// EFCore 适配器包此前均无 *ServiceCollectionExtensions.cs，调用方必须手动 AddDbContext
// 再手动把上下文映射到 Store 接口；而 Dapper 三方言、PalORM 三方言、Compression/
// Serialization 共 11 处有 AddPal* 入口。ArchitectureBoundaryTests 的 DI 守卫只扫描
// **已存在**的扩展文件，对缺失入口完全无感知。
//
// 本文件验证四个新扩展的注册面：接口可解析、实现类型正确、生命周期为 Scoped
// （DbContext 非线程安全，不得 Singleton）。
// 使用 EF InMemory provider —— 零外部依赖，不需要 Docker。

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Idempotency;
using PalDDD.Projections;
using PalDDD.Transactions;

namespace PalDDD.Integration.Tests;

/// <summary>EFCore 适配器 DI 注册面完整性测试（审计 A7 / M1-3）。</summary>
public sealed class EfCoreDiRegistrationTests
{
    [Test]
    public async Task AddPalIdempotencyEfCore_ResolvesStoreInterface()
    {
        var services = new ServiceCollection();
        services.AddPalIdempotencyEfCore<RegIdempotencyDbContext>();
        services.AddDbContext<RegIdempotencyDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        await Assert.That(store).IsTypeOf<RegIdempotencyDbContext>();
    }

    [Test]
    public async Task AddPalProjectionsEfCore_ResolvesStoreInterface()
    {
        var services = new ServiceCollection();
        services.AddPalProjectionsEfCore<RegProjectionDbContext>();
        services.AddDbContext<RegProjectionDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        await Assert.That(store).IsTypeOf<RegProjectionDbContext>();
    }

    [Test]
    public async Task AddPalOutboxEfCore_ResolvesStoreInterface()
    {
        var services = new ServiceCollection();
        services.AddPalOutboxEfCore<RegOutboxDbContext>();
        services.AddDbContext<RegOutboxDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<IPalOutboxStore>();
        await Assert.That(store).IsTypeOf<RegOutboxDbContext>();
    }

    [Test]
    public async Task AddPalInboxEfCore_ResolvesStoreInterface()
    {
        var services = new ServiceCollection();
        services.AddPalInboxEfCore<RegInboxDbContext>();
        services.AddDbContext<RegInboxDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        await Assert.That(store).IsTypeOf<RegInboxDbContext>();
    }

    [Test]
    public async Task AddPalSagaStateEfCore_ResolvesStoreInterface()
    {
        var services = new ServiceCollection();
        services.AddPalSagaStateEfCore<RegSagaStateDbContext, RegSagaState>();
        services.AddDbContext<RegSagaStateDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<ISagaStateStore<RegSagaState>>();
        await Assert.That(store).IsTypeOf<RegSagaStateDbContext>();
    }

    [Test]
    public async Task StoreRegistrations_AreScoped_NotSingleton()
    {
        // DbContext 非线程安全：Store 映射必须与 AddDbContext 默认一致为 Scoped。
        // 若误注册 Singleton，同一实例跨请求共享 → 并发写损坏 ChangeTracker。
        var services = new ServiceCollection();
        services.AddPalIdempotencyEfCore<RegIdempotencyDbContext>();
        services.AddPalOutboxEfCore<RegOutboxDbContext>();
        services.AddPalInboxEfCore<RegInboxDbContext>();
        services.AddPalProjectionsEfCore<RegProjectionDbContext>();
        services.AddPalSagaStateEfCore<RegSagaStateDbContext, RegSagaState>();
        services.AddDbContext<RegIdempotencyDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddDbContext<RegOutboxDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddDbContext<RegInboxDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddDbContext<RegProjectionDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddDbContext<RegSagaStateDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();
        await using var scopeA = provider.CreateAsyncScope();
        await using var scopeB = provider.CreateAsyncScope();

        var outboxA = scopeA.ServiceProvider.GetRequiredService<IPalOutboxStore>();
        var outboxB = scopeB.ServiceProvider.GetRequiredService<IPalOutboxStore>();
        await Assert.That(ReferenceEquals(outboxA, outboxB)).IsFalse();

        var inboxA = scopeA.ServiceProvider.GetRequiredService<IInboxStore>();
        var inboxB = scopeB.ServiceProvider.GetRequiredService<IInboxStore>();
        await Assert.That(ReferenceEquals(inboxA, inboxB)).IsFalse();
    }

    private sealed class RegIdempotencyDbContext(DbContextOptions<RegIdempotencyDbContext> options)
        : IdempotencyDbContext(options);

    private sealed class RegProjectionDbContext(DbContextOptions<RegProjectionDbContext> options)
        : ProjectionCheckpointDbContext(options);

    private sealed class RegOutboxDbContext(DbContextOptions<RegOutboxDbContext> options)
        : OutboxDbContext(options)
    {
        // ITM-252 模式：本文件只验证 DI 注册面，不测 Lease——InMemory 变体不提供
        // 原子租约（需 provider 级 FOR UPDATE SKIP LOCKED / ExecuteUpdate），显式 throw
        // 防止未来误用本上下文测 Lease 而不自知。
        public override ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize,
            string owner,
            TimeSpan leaseDuration,
            int maxRetryCount,
            CancellationToken ct)
            => throw new NotSupportedException(
                "RegOutboxDbContext（InMemory）不提供 Lease 实现——Lease 测试请用方言派生上下文（生产路径）。");
    }

    private sealed class RegInboxDbContext(DbContextOptions<RegInboxDbContext> options)
        : InboxDbContext(options);

    private sealed class RegSagaState : SagaState;

    private sealed class RegSagaStateDbContext(DbContextOptions<RegSagaStateDbContext> options)
        : SagaStateDbContext<RegSagaState>(options);
}
