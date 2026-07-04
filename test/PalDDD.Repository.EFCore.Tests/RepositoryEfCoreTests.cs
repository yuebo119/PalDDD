using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Core;
using PalDDD.Core.Repository;
using PalDDD.Serialization;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;
using System.Text.Json.Serialization;

namespace PalDDD.Repository.EFCore.Tests;

public sealed class UnitOfWorkTests
{
    [Test]
    public async Task SaveChangesAsync_PersistsEntity(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using var context = new TestDbContext(options);
        var uow = new UnitOfWork<TestDbContext>(context);

        context.TestEntities.Add(new TestEntity { Id = 1, Name = "alpha" });
        await uow.SaveChangesAsync(cancellationToken);

        await using var reader = new TestDbContext(options);
        await Assert.That(reader.TestEntities).Count().IsEqualTo(1);
    }

    [Test]
    public async Task BeginTransactionAsync_OnInMemoryStore_IsNoOp(CancellationToken cancellationToken)
    {
        // InMemory provider 忽略事务（已在 CreateOptions 中抑制警告）。
        // UoW 封装器在底层 store 对 BeginTransaction 无操作时不得抛出异常。
        await using var context = new TestDbContext(CreateOptions());
        var uow = new UnitOfWork<TestDbContext>(context);

        await uow.BeginTransactionAsync(cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }

    [Test]
    public async Task CommitAsync_NoActiveTransaction_DoesNotThrow(CancellationToken cancellationToken)
    {
        await using var context = new TestDbContext(CreateOptions());
        var uow = new UnitOfWork<TestDbContext>(context);

        await uow.CommitAsync(cancellationToken);
        await uow.RollbackAsync(cancellationToken);
    }

    [Test]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var context = new TestDbContext(CreateOptions());
        var uow = new UnitOfWork<TestDbContext>(context);
        await uow.DisposeAsync();
        await uow.DisposeAsync();
    }

    [Test]
    public async Task ExecuteInTransactionAsync_CommitsWorkOnSuccess(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using var context = new TestDbContext(options);
        var uow = new UnitOfWork<TestDbContext>(context);

        await uow.ExecuteInTransactionAsync(async ct =>
        {
            context.TestEntities.Add(new TestEntity { Id = 1, Name = "tx-entity" });
            await uow.SaveChangesAsync(ct);
        }, cancellationToken);

        await using var reader = new TestDbContext(options);
        await Assert.That(reader.TestEntities).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteInTransactionAsync_RollsBackOnFailure(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using (var context = new TestDbContext(options))
        {
            var uow = new UnitOfWork<TestDbContext>(context);

            // 添加实体但在 SaveChangesAsync 之前失败 — InMemory provider 不支持
            // 真正的事务回滚，因此有意义的契约测试是：
            // 如果工作抛出异常，ExecuteInTransactionAsync 绝不会调用 SaveChangesAsync。
            context.TestEntities.Add(new TestEntity { Id = 1, Name = "rollback-entity" });

            await Assert.That(async () => await uow.ExecuteInTransactionAsync(async ct =>
                {
                    // 在 SaveChangesAsync 之前失败 — 不应有任何持久化。
                    await Task.Yield();
                    throw new InvalidOperationException("simulated failure");
                }, cancellationToken).AsTask()).Throws<InvalidOperationException>();
        }

        await using var reader = new TestDbContext(options);
        await Assert.That(reader.TestEntities).IsEmpty();
    }

    internal static DbContextOptions<TestDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
}

public sealed class OutboxDomainEventInterceptorTests
{
    private static readonly MessageDescriptor TestDescriptor = MessageDescriptor.Create(
        TestJsonContext.Default.TestDomainEvent, "test.event.v1", schemaVersion: 1);

    [Test]
    public async Task SavingChanges_WritesDomainEventsToOutbox(CancellationToken cancellationToken)
    {
        var store = new RecordingOutboxStore();
        var serializer = new PassthroughSerializer();
        var catalog = new SingleDescriptorCatalog(TestDescriptor);
        var interceptor = new OutboxDomainEventInterceptor(store, serializer, catalog);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new TestDbContext(options);
        var entity = new TestEntity { Id = 1, Name = "alpha" };
        entity.AppendEventDirectly(new TestDomainEvent());
        context.TestEntities.Add(entity);

        await context.SaveChangesAsync(cancellationToken);

        await Assert.That(store.AddedMessages).Count().IsEqualTo(1);
        await Assert.That(store.AddedMessages[0].Type).IsEqualTo(TestDescriptor.Name);
        await Assert.That(store.AddedMessages[0].SchemaVersion).IsEqualTo(TestDescriptor.SchemaVersion);
    }

    [Test]
    public async Task SavingChanges_WhenSaveFails_KeepsDomainEventsOnEntity(CancellationToken cancellationToken)
    {
        var store = new RecordingOutboxStore();
        var serializer = new PassthroughSerializer();
        var catalog = new SingleDescriptorCatalog(TestDescriptor);
        var interceptor = new OutboxDomainEventInterceptor(store, serializer, catalog);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor, new ThrowingSaveChangesInterceptor())
            .Options;

        await using var context = new TestDbContext(options);
        var entity = new TestEntity { Id = 1, Name = "alpha" };
        entity.AppendEventDirectly(new TestDomainEvent());
        context.TestEntities.Add(entity);

        await Assert.That(async () => await context.SaveChangesAsync(cancellationToken)).Throws<InvalidOperationException>();

        await Assert.That(entity.HasDomainEvents).IsTrue();
    }

    internal sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated save failure");
    }

    internal sealed class RecordingOutboxStore : IPalOutboxStore
    {
        public List<OutboxMessage> AddedMessages { get; } = [];

        public void AddMessage(OutboxMessage message) => AddedMessages.Add(message);

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
        {
            AddedMessages.AddRange(messages);
            return ValueTask.FromResult(messages.Count);
        }

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
        { }

        public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
        { }

        public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
        { }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    internal sealed class PassthroughSerializer : IMessageSerializer
    {
        public string ContentType => "application/json";

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
            => "payload"u8.ToArray();

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
            => "payload"u8.ToArray();

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;

        public T? Deserialize<T>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    internal sealed class SingleDescriptorCatalog(MessageDescriptor descriptor) : IMessageCatalog
    {
        public IReadOnlyList<MessageDescriptor> Descriptors => [descriptor];

        public MessageDescriptor? Find(string name) => descriptor;

        public MessageDescriptor? Find(string name, int schemaVersion) => descriptor;

        public MessageDescriptor? Find(Type type) => descriptor;
    }
}

internal sealed class TestEntity : Entity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public void AppendEventDirectly(DomainEvent @event) => RaiseEvent(@event);
}

internal sealed class TestDomainEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "test.event.v1";
}

internal sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
}

[JsonSerializable(typeof(TestDomainEvent))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
