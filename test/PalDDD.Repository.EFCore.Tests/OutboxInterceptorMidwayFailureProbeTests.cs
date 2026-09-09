using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Core;
using PalDDD.Serialization;
using PalDDD.Transactions;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Repository.EFCore.Tests;

// ══════════════════════════════════════════════════════════════
// V25 探针 a — OutboxDomainEventInterceptor 中途抛异常路径
// ══════════════════════════════════════════════════════════════
// 事实探针：WriteEventsToOutbox 注入循环中途（第 2 条 AddMessage）抛异常，
// 第 1 条消息按生产 OutboxDbContext.AddMessage 形态注入 ChangeTracker
// （OutboxMessages.Add）。Sqlite mode=memory 真连接。只记录事实行为：
// ① SaveChangesFailed(Async) 回调是否触发；② 聚合实体在 ChangeTracker
// 的去留。本文件不对"应该怎样"下结论——断言均为事实断言。

public sealed class OutboxProbeEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "probe.outbox.v1";
}

[JsonSerializable(typeof(OutboxProbeEvent))]
internal sealed partial class OutboxProbeJsonContext : JsonSerializerContext;

public sealed class OutboxInterceptorMidwayFailureProbeTests
{
    private static readonly MessageDescriptor ProbeDescriptor = MessageDescriptor.Create(
        OutboxProbeJsonContext.Default.OutboxProbeEvent, "probe.outbox.v1", schemaVersion: 1);

    [Test]
    public async Task SaveChangesAsync_OutboxStoreThrowsMidway_SaveChangesFailedNotTriggered(CancellationToken ct)
    {
        await using var setup = await CreateSetupAsync(storeThrowsOnCall: 1, ct);

        AddEntityWithTwoEvents(setup.Context);

        // 事实断言：store 异常原样传播（非 DbUpdateException 包装）——测试完成即"异常冒出"事实
        await Assert.That(async () => await setup.Context.SaveChangesAsync(ct))
            .Throws<InvalidOperationException>();

        // 探针自证：SavingChanges 阶段确实进入（拦截器链已执行，排除"装置未跑"假阴性）
        await Assert.That(setup.Spy.SavingChangesPhaseEntered).IsTrue();
        // 探针自证：第 1 条消息已注入、第 2 条才抛——"中途"事实成立
        await Assert.That(setup.Store.InjectedMessages).Count().IsEqualTo(1);

        // 核心事实 1：SaveChangesFailed(Async) 回调均未触发
        await Assert.That(setup.Spy.SaveChangesFailedSyncCalled).IsFalse();
        await Assert.That(setup.Spy.SaveChangesFailedAsyncCalled).IsFalse();

        // 附加事实：已注入的第 1 条 OutboxMessage 已被自清理 Detach（ChangeTracker 无残留）
        var trackedOutbox = setup.Context.ChangeTracker.Entries<OutboxMessage>().ToList();
        await Assert.That(trackedOutbox).IsEmpty();
    }

    [Test]
    public async Task SaveChangesAsync_ProviderLevelFailure_SaveChangesFailedTriggered(CancellationToken ct)
    {
        // 探针自证（控制组）：无 outbox 异常、让保存本身在 provider 层失败
        // （重复主键）——SaveChangesFailed 回调触发，证明 ① Spy 装置有效，
        // ② 与中途抛路径形成可区分对照。
        await using var setup = await CreateSetupAsync(storeThrowsOnCall: -1, ct);

        // 第一条先成功落库后 Detach（否则同 context 二次 Add 同主键仍被 identity map 拒）；
        // 随后同主键新实例在 provider 层触发 UNIQUE 冲突
        var first = new OutboxProbeEntity { Id = 1, Name = "first" };
        setup.Context.Entities.Add(first);
        await setup.Context.SaveChangesAsync(ct);
        setup.Context.Entry(first).State = EntityState.Detached;

        setup.Context.Entities.Add(new OutboxProbeEntity { Id = 1, Name = "dup" });

        await Assert.That(async () => await setup.Context.SaveChangesAsync(ct))
            .Throws<DbUpdateException>();

        // 事实断言：provider 层失败路径 SaveChangesFailed(Async) 触发
        await Assert.That(setup.Spy.SavingChangesPhaseEntered).IsTrue();
        await Assert.That(setup.Spy.SaveChangesFailedSyncCalled || setup.Spy.SaveChangesFailedAsyncCalled).IsTrue();
    }

    [Test]
    public async Task SaveChangesAsync_OutboxStoreThrowsMidway_AggregateEntityRemainsTrackedWithEvents(CancellationToken ct)
    {
        await using var setup = await CreateSetupAsync(storeThrowsOnCall: 1, ct);

        var entity = AddEntityWithTwoEvents(setup.Context);

        await Assert.That(async () => await setup.Context.SaveChangesAsync(ct))
            .Throws<InvalidOperationException>();

        // 核心事实 2：聚合实体滞留 ChangeTracker（Added 态），领域事件未被清除
        // （对照成功路径：SavedChangesAsync 会 ClearDomainEvents）
        await Assert.That(setup.Context.Entry(entity).State).IsEqualTo(EntityState.Added);
        await Assert.That(entity.HasDomainEvents).IsTrue();

        // 附加事实：拦截器 _pending 未清空（SaveChangesFailed 不可达 → 其 _pending.Clear() 未执行）
        await Assert.That(setup.Interceptor.PendingEvents).Count().IsEqualTo(2);
    }

    // ── 探针装置 ──

    /// <summary>聚合实体 + 2 条领域事件</summary>
    private static OutboxProbeEntity AddEntityWithTwoEvents(OutboxProbeDbContext context)
    {
        var entity = new OutboxProbeEntity { Id = 1, Name = "agg" };
        entity.AppendEventDirectly(new OutboxProbeEvent());
        entity.AppendEventDirectly(new OutboxProbeEvent());
        context.Entities.Add(entity);
        return entity;
    }

    private static async Task<ProbeSetup> CreateSetupAsync(int storeThrowsOnCall, CancellationToken ct)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        var store = new ThrowingMidwayOutboxStore(storeThrowsOnCall);
        var interceptor = new OutboxDomainEventInterceptor(
            store, new PassthroughProbeSerializer(), new SingleProbeDescriptorCatalog(ProbeDescriptor));
        var spy = new FailureSpyInterceptor();

        var options = new DbContextOptionsBuilder<OutboxProbeDbContext>()
            .UseSqlite(connection)
            // Spy 注册在前——其 SavingChanges 阶段先于 outbox 拦截器抛出点执行，
            // 保证"链已执行"信号在失败场景下仍可观测
            .AddInterceptors(spy, interceptor)
            .Options;

        var context = new OutboxProbeDbContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        return new ProbeSetup(connection, context, store, spy, interceptor);
    }

    private sealed class ProbeSetup(
        SqliteConnection connection,
        OutboxProbeDbContext context,
        ThrowingMidwayOutboxStore store,
        FailureSpyInterceptor spy,
        OutboxDomainEventInterceptor interceptor) : IAsyncDisposable
    {
        public OutboxProbeDbContext Context => context;
        public ThrowingMidwayOutboxStore Store => store;
        public FailureSpyInterceptor Spy => spy;
        public OutboxDomainEventInterceptor Interceptor => interceptor;

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 中途抛异常的 outbox store——AddMessage 形态镜像生产 OutboxDbContext
    /// （将消息 Add 进同一 DbContext 的 ChangeTracker），第 N 次调用抛出。
    /// </summary>
    private sealed class ThrowingMidwayOutboxStore : IPalOutboxStore
    {
        private readonly int _throwOnCall;
        private int _calls;

        /// <summary>-1 表示不抛；否则第 N 次 AddMessage 抛</summary>
        public ThrowingMidwayOutboxStore(int throwOnCall) => _throwOnCall = throwOnCall;

        /// <summary>已成功注入（Add 进 ChangeTracker）的消息</summary>
        public List<OutboxMessage> InjectedMessages { get; } = [];

        public void AddMessage(OutboxMessage message)
        {
            var call = _calls++;
            if (call == _throwOnCall)
                throw new InvalidOperationException("outbox store failure (probe)");
            InjectedMessages.Add(message);
        }

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
        {
            foreach (var message in messages)
                AddMessage(message);
            return ValueTask.FromResult(messages.Count);
        }

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt) { }

        public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt) { }

        public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt) { }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
            => ValueTask.FromResult(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    /// <summary>失败回调 Spy——记录 SavingChanges 阶段与 SaveChangesFailed(sync/async) 触发</summary>
    private sealed class FailureSpyInterceptor : SaveChangesInterceptor
    {
        public bool SavingChangesPhaseEntered { get; private set; }
        public bool SaveChangesFailedSyncCalled { get; private set; }
        public bool SaveChangesFailedAsyncCalled { get; private set; }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            SavingChangesPhaseEntered = true;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SavingChangesPhaseEntered = true;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData)
            => SaveChangesFailedSyncCalled = true;

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            SaveChangesFailedAsyncCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughProbeSerializer : IMessageSerializer
    {
        public string ContentType => "application/json";

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
            => "payload"u8.ToArray();

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
            => "payload"u8.ToArray();

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;

        public T? Deserialize<T>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    private sealed class SingleProbeDescriptorCatalog(MessageDescriptor descriptor) : IMessageCatalog
    {
        public IReadOnlyList<MessageDescriptor> Descriptors => [descriptor];

        public MessageDescriptor? Find(string name) => descriptor;

        public MessageDescriptor? Find(string name, int schemaVersion) => descriptor;

        public MessageDescriptor? Find(Type type) => descriptor;
    }

    /// <summary>探针聚合实体（含 RaiseEvent 公开包装）</summary>
    private sealed class OutboxProbeEntity : Entity
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        public void AppendEventDirectly(DomainEvent @event) => RaiseEvent(@event);
    }

    /// <summary>
    /// 探针 DbContext——业务实体与 OutboxMessage 同模型（对齐生产同事务形态，
    /// 使拦截器自清理的 ChangeTracker.Entries&lt;OutboxMessage&gt;() 可遍历）。
    /// OutboxMessage 模型配置镜像生产 OutboxDbContext（Ulid 主键字符串转换）。
    /// </summary>
    private sealed class OutboxProbeDbContext(DbContextOptions<OutboxProbeDbContext> options) : DbContext(options)
    {
        public DbSet<OutboxProbeEntity> Entities => Set<OutboxProbeEntity>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OutboxMessage>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasConversion(v => v.ToString(), v => PalUlid.Parse(v));
                e.Property(x => x.CorrelationId).HasConversion(
                    v => v.HasValue ? v.Value.ToString() : default(string?),
                    v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
                e.Property(x => x.CausationId).HasConversion(
                    v => v.HasValue ? v.Value.ToString() : default(string?),
                    v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
                e.Property(x => x.Type).HasMaxLength(512);
                e.Property(x => x.ContentType).HasMaxLength(128);
                e.Property(x => x.Payload).IsRequired();
                e.Property(x => x.Error).HasMaxLength(2048);
            });
        }
    }
}
