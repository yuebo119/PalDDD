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

    // ADR-023：嵌套 Begin 统一 fail-fast（对齐 Dapper ITM-088）。修复前 EFCore 为静默 no-op，
    // 而 ExecuteInTransactionAsync 是无条件「Begin → Commit」→ 嵌套时内层 Commit 提交的是
    // **外层**事务（CommitAsync 提交"当前活动事务"），静默原子性破坏。
    // ⚠️ 本用例必须用关系型 provider：InMemory 忽略事务（CurrentTransaction 恒 null），
    // "事务已活动"在 InMemory 下不可达（上一用例正是该 provider 的现状锁）。
    [Test]
    public async Task BeginTransactionAsync_WhenAlreadyActive_Throws(CancellationToken cancellationToken)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options;
        await using var context = new TestDbContext(options);
        var uow = new UnitOfWork<TestDbContext>(context);

        await uow.BeginTransactionAsync(cancellationToken);

        await Assert.That(async () => await uow.BeginTransactionAsync(cancellationToken))
            .Throws<InvalidOperationException>();
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
    public async Task SaveChangesAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // v30 P3 守卫族回归（v29 P3 ITM-284 三栈守卫 4/4 的补测）：UnitOfWork 释放后
        // SaveChangesAsync 必须被守卫拦截抛 ObjectDisposedException——无守卫时落到
        // 已释放 DbContext 上抛 provider 级异常（语义误导、排障指向错误层）
        var context = new TestDbContext(CreateOptions());
        var uow = new UnitOfWork<TestDbContext>(context);
        await uow.DisposeAsync();

        await Assert.That(async () => await uow.SaveChangesAsync()).Throws<ObjectDisposedException>();

        await context.DisposeAsync();
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
    public async Task ExecuteInTransactionAsync_WhenWorkThrows_DoesNotCallSaveChanges(CancellationToken cancellationToken)
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

    // 轮四（2026-10-10）：立即写 store 的分派锁定——marker store 在 SavingChangesAsync
    // 走单次 AddMessagesAsync 批量（含全部事件、零逐条 AddMessage），同步钩子（无异步
    // 可用）维持逐条路径。改前两路径均逐条（批量断言红），构成红→绿锁定
    [Test]
    public async Task SavingChangesAsync_ImmediateStore_SingleBatchCallWithAllEvents(CancellationToken cancellationToken)
    {
        var store = new RecordingImmediateOutboxStore();
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
        entity.AppendEventDirectly(new TestDomainEvent());
        entity.AppendEventDirectly(new TestDomainEvent());
        context.TestEntities.Add(entity);

        await context.SaveChangesAsync(cancellationToken);

        await Assert.That(store.Batches).Count().IsEqualTo(1);
        await Assert.That(store.Batches[0]).Count().IsEqualTo(3);
        await Assert.That(store.AddedViaSingle).Count().IsEqualTo(0);
        await Assert.That(store.Batches[0][0].Type).IsEqualTo(TestDescriptor.Name);
        await Assert.That(store.Batches[0][2].SchemaVersion).IsEqualTo(TestDescriptor.SchemaVersion);
    }

    [Test]
    public async Task SavingChanges_SyncHook_OnImmediateStore_BatchesOnce(CancellationToken cancellationToken)
    {
        var store = new RecordingImmediateOutboxStore();
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
        entity.AppendEventDirectly(new TestDomainEvent());
        context.TestEntities.Add(entity);

        context.SaveChanges();

        // 2026-10-19：同步钩子对立即写 store 改走单次批量（原逐事件阻塞 k 次往返）
        await Assert.That(store.Batches).Count().IsEqualTo(1);
        await Assert.That(store.Batches[0]).Count().IsEqualTo(2);
        await Assert.That(store.AddedViaSingle).Count().IsEqualTo(0);
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

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default)
        {
            AddedMessages.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default)
        {
            AddedMessages.AddRange(messages);
            return ValueTask.FromResult(messages.Count);
        }

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    /// <summary>轮四（2026-10-10）：IImmediateWriteOutboxStore 标注的记录 store——分路径记账（单条 vs 批量）。</summary>
    internal sealed class RecordingImmediateOutboxStore : IPalOutboxStore, PalDDD.Transactions.IImmediateWriteOutboxStore
    {
        public List<OutboxMessage> AddedViaSingle { get; } = [];

        public List<IReadOnlyList<OutboxMessage>> Batches { get; } = [];

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default)
        {
            AddedViaSingle.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default)
        {
            Batches.Add(messages);
            return ValueTask.FromResult(messages.Count);
        }

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default) => ValueTask.CompletedTask;

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
