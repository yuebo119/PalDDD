using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core.Repository;
using PalDDD.Serialization;
using PalDDD.Transactions;

namespace PalDDD.Repository.EFCore.Tests;

/// <summary>
/// F1/F2（2026-10-21 架构评审）：EF Outbox 装配链端到端锁定。
/// 原教程把 outbox 注册为独立上下文——拦截器只把消息跟踪到那个上下文，
/// 业务上下文的 SaveChanges 不会保存它，领域事件静默丢失；且没有任何测试覆盖
/// 「真实拦截器 + 真实 outbox 表」。本组用例用真实 SqliteOutboxDbContext 跑通。
/// </summary>
public sealed class OutboxWiringEndToEndTests
{
    private static readonly MessageDescriptor TestDescriptor = MessageDescriptor.Create(
        TestJsonContext.Default.TestDomainEvent, "test.event.v1", schemaVersion: 1);

    [Test]
    public async Task SameContextWithInterceptor_PersistsDomainEventIntoOutboxTable(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageCatalog>(new OutboxDomainEventInterceptorTests.SingleDescriptorCatalog(TestDescriptor));
        services.AddSingleton<IMessageSerializer>(new OutboxDomainEventInterceptorTests.PassthroughSerializer());
        services.AddDbContext<TestOutboxDbContext>((sp, options) => options
            .UseSqlite(connection)
            .UsePalOutboxInterceptor(sp));
        services.AddPalOutboxUnitOfWorkForContext<TestOutboxDbContext>();
        services.AddScoped<IPalOutboxStore>(sp => sp.GetRequiredService<TestOutboxDbContext>());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
        await context.Database.EnsureCreatedAsync(ct);

        var entity = new TestEntity { Id = 1, Name = "alpha" };
        entity.AppendEventDirectly(new TestDomainEvent());
        context.TestEntities.Add(entity);

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.SaveChangesAsync(ct);

        await using var verification = new TestOutboxDbContext(
            new DbContextOptionsBuilder<TestOutboxDbContext>().UseSqlite(connection).Options);
        var messages = await verification.OutboxMessages.AsNoTracking().ToListAsync(ct);

        await Assert.That(messages).Count().IsEqualTo(1);
        await Assert.That(messages[0].Type).IsEqualTo(TestDescriptor.Name);
        await Assert.That(entity.HasDomainEvents).IsFalse();
    }

    [Test]
    public async Task MissingInterceptor_FailsFastAtUnitOfWorkResolution(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageCatalog>(new OutboxDomainEventInterceptorTests.SingleDescriptorCatalog(TestDescriptor));
        services.AddSingleton<IMessageSerializer>(new OutboxDomainEventInterceptorTests.PassthroughSerializer());
        // 故意不调用 UsePalOutboxInterceptor
        services.AddDbContext<TestOutboxDbContext>(options => options.UseSqlite(connection));
        services.AddPalOutboxUnitOfWorkForContext<TestOutboxDbContext>();
        services.AddScoped<IPalOutboxStore>(sp => sp.GetRequiredService<TestOutboxDbContext>());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.That(() => { scope.ServiceProvider.GetRequiredService<IUnitOfWork>(); })
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SeparateOutboxStore_FailsFastAtUnitOfWorkResolution(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageCatalog>(new OutboxDomainEventInterceptorTests.SingleDescriptorCatalog(TestDescriptor));
        services.AddSingleton<IMessageSerializer>(new OutboxDomainEventInterceptorTests.PassthroughSerializer());
        services.AddDbContext<TestOutboxDbContext>((sp, options) => options
            .UseSqlite(connection)
            .UsePalOutboxInterceptor(sp));
        services.AddPalOutboxUnitOfWorkForContext<TestOutboxDbContext>();
        // 错误装配：IPalOutboxStore 指向另一个实例（教程旧形态）
        services.AddScoped<IPalOutboxStore>(_ => new OutboxDomainEventInterceptorTests.RecordingOutboxStore());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.That(() => { scope.ServiceProvider.GetRequiredService<IUnitOfWork>(); })
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task LegacyOverload_NonStoreContext_FailsFastAtUnitOfWorkResolution(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageCatalog>(new OutboxDomainEventInterceptorTests.SingleDescriptorCatalog(TestDescriptor));
        services.AddSingleton<IMessageSerializer>(new OutboxDomainEventInterceptorTests.PassthroughSerializer());
        services.AddDbContext<TestDbContext>((sp, options) => options
            .UseSqlite(connection)
            .UsePalOutboxInterceptor(sp));
#pragma warning disable CS0618 // 兼容入口的过渡行为锁定：约束在解析期校验
        services.AddPalOutboxUnitOfWork<TestDbContext>();
#pragma warning restore CS0618
        services.AddScoped<IPalOutboxStore>(_ => new OutboxDomainEventInterceptorTests.RecordingOutboxStore());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.That(() => { scope.ServiceProvider.GetRequiredService<IUnitOfWork>(); })
            .Throws<InvalidOperationException>();
    }
}

internal sealed class TestOutboxDbContext(DbContextOptions<TestOutboxDbContext> options)
    : SqliteOutboxDbContext(options)
{
    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
}
