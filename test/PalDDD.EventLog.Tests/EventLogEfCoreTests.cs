namespace PalDDD.EventLog.Tests;

using Microsoft.EntityFrameworkCore;
using PalDDD.Testing;
using System.Text;
using PalUlid = ByteAether.Ulid.Ulid;

public sealed class EventLogEfCoreTests
{
    private static readonly string[] DefaultStreamNames = ["ordering-order-1", "billing-payment-1"];
    private static readonly long[] DefaultPositions = [0, 1];

    [Test]
    public async Task AppendAsync_PersistsEventsAcrossDbContexts(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using (var writer = new TestEventLogDbContext(options))
        {
            await writer.AppendAsync(
                "ordering-order-1",
                ExpectedStreamVersion.NoStream,
                [
                    CreateEvent(
                        PalUlid.New(Guid.Parse("1513375b-e2f0-48d2-96f4-07ea47310eed")),
                        "orders.order-submitted.v1",
                        "one")
                ],
                cancellationToken);
        }

        await using var reader = new TestEventLogDbContext(options);
        var events = await ReadAllAsync(reader.ReadStreamAsync(
            "ordering-order-1",
            cancellationToken: cancellationToken));

        await Assert.That(events).HasSingleItem();
        var recorded = events[0];
        await Assert.That(recorded.StreamName).IsEqualTo("ordering-order-1");
        await Assert.That(recorded.StreamVersion).IsEqualTo(0);
        await Assert.That(recorded.GlobalPosition).IsEqualTo(0);
        await Assert.That(recorded.EventId).IsEqualTo(PalUlid.New(Guid.Parse("1513375b-e2f0-48d2-96f4-07ea47310eed")));
        await Assert.That(recorded.EventName).IsEqualTo("orders.order-submitted.v1");
        await Assert.That(Encoding.UTF8.GetString(recorded.Payload.Span)).IsEqualTo("one");
        await Assert.That(recorded.Audit.ActorId).IsEqualTo("user-123");
        await Assert.That(recorded.Audit.Reason).IsEqualTo("submit order");
    }

    [Test]
    public async Task AppendAsync_RecordsGlobalOrderAcrossStreams(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());
        var first = await db.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "one")],
            cancellationToken);
        var second = await db.AppendAsync(
            "billing-payment-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "billing.payment-authorized.v1", "two")],
            cancellationToken);

        var events = await ReadAllAsync(db.ReadAllAsync(cancellationToken: cancellationToken));

        await Assert.That(first.FirstGlobalPosition).IsEqualTo(0);
        await Assert.That(second.FirstGlobalPosition).IsEqualTo(1);
        await Assert.That(events.Select(e => e.StreamName)).IsEquivalentTo(DefaultStreamNames);
        await Assert.That(events.Select(e => e.GlobalPosition)).IsEquivalentTo(DefaultPositions);
    }

    [Test]
    public async Task AppendAsync_AdvancesDurableGlobalPositionAllocator(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());

        var first = await db.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "one"),
                CreateEvent(PalUlid.New(), "orders.order-paid.v1", "two")
            ],
            cancellationToken);
        var second = await db.AppendAsync(
            "billing-payment-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "billing.payment-authorized.v1", "three")],
            cancellationToken);

        var allocator = await db.Set<EventLogGlobalPositionAllocator>()
            .SingleAsync(cancellationToken);

        // Hi/Lo 语义：NextGlobalPosition 是持久化的高水位标记（chunk 上界），
        // 而非已分配位置的精确计数。默认 chunk size 为 100 时，分配 3 个
        // 位置 [0..2] 会将高水位标记推进到 100（chunk 剩余部分缓存在进程内）。
        await Assert.That(first.FirstGlobalPosition).IsEqualTo(0);
        await Assert.That(first.LastGlobalPosition).IsEqualTo(1);
        await Assert.That(second.FirstGlobalPosition).IsEqualTo(2);
        await Assert.That(allocator.NextGlobalPosition >= 3).IsTrue();
    }

    [Test]
    public async Task AppendAsync_WithStaleExpectedVersion_ThrowsConcurrencyException(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());
        await db.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "one")],
            cancellationToken);

        var exception = await Assert.That(() =>
            db.AppendAsync(
                "ordering-order-1",
                ExpectedStreamVersion.NoStream,
                [CreateEvent(PalUlid.New(), "orders.order-paid.v1", "two")],
                cancellationToken).AsTask()).Throws<EventStreamConcurrencyException>();

        await Assert.That(exception!.StreamName).IsEqualTo("ordering-order-1");
        await Assert.That(exception!.ActualVersion).IsEqualTo(0);
    }

    [Test]
    public async Task AppendAsync_UsesInjectedTimeProviderForRecordedAt(CancellationToken cancellationToken)
    {
        // 注入固定时间 TimeProvider —— RecordedAt 应反映注入的时钟，而非系统时间。
        var fixedNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(fixedNow);
        await using var db = new TestEventLogDbContext(CreateOptions(), clock);

        await db.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "one")],
            cancellationToken);

        var events = await ReadAllAsync(db.ReadStreamAsync(
            "ordering-order-1",
            cancellationToken: cancellationToken));

        await Assert.That(events).HasSingleItem();
        var recorded = events[0];
        await Assert.That(recorded.RecordedAt).IsEqualTo(fixedNow);
    }

    [Test]
    public async Task AppendAsync_FirstAppendOnEmptyStream_ProducesStreamVersionZero(CancellationToken cancellationToken)
    {
        // GetActualStreamVersionAsync 边界条件：流为空时应视为 -1（NoStream），
        // 第一次 append 后版本为 0。改造为单次 MaxAsync 后此契约必须保持。
        await using var db = new TestEventLogDbContext(CreateOptions());

        var result = await db.AppendAsync(
            "fresh-stream",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "one")],
            cancellationToken);

        await Assert.That(result.FirstStreamVersion).IsEqualTo(0);
        await Assert.That(result.LastStreamVersion).IsEqualTo(0);
    }

    [Test]
    public async Task AppendAsync_SeparateStreams_EachStartFromZero(CancellationToken cancellationToken)
    {
        // 多流独立性：每个 stream 的 StreamVersion 计算独立，
        // GetActualStreamVersionAsync 必须仅查询 streamName 匹配的事件。
        await using var db = new TestEventLogDbContext(CreateOptions());

        var first = await db.AppendAsync(
            "stream-a",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "a-one")],
            cancellationToken);
        var second = await db.AppendAsync(
            "stream-b",
            ExpectedStreamVersion.NoStream,
            [CreateEvent(PalUlid.New(), "orders.order-submitted.v1", "b-one")],
            cancellationToken);

        await Assert.That(first.FirstStreamVersion).IsEqualTo(0);
        await Assert.That(second.FirstStreamVersion).IsEqualTo(0);
        // 全局位置依然单调递增。
        await Assert.That(first.FirstGlobalPosition).IsEqualTo(0);
        await Assert.That(second.FirstGlobalPosition).IsEqualTo(1);
    }

    [Test]
    public async Task AppendAsync_EmitsEventLogAppendActivityAndMetric(CancellationToken cancellationToken)
    {
        using var activityListener = new RecordingActivityListener();
        using var meterListener = new RecordingMeterListener("paldd.eventlog.appended");

        await using var db = new TestEventLogDbContext(CreateOptions());
        await db.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(PalUlid.New(Guid.Parse("a0000000-0000-0000-0000-000000000001")), "orders.order-submitted.v1", "one"),
                CreateEvent(PalUlid.New(Guid.Parse("a0000000-0000-0000-0000-000000000002")), "orders.order-paid.v1", "two")
            ],
            cancellationToken);

        await Assert.That(activityListener.StoppedActivities.Any(a => a.OperationName == "EventLog Append")).IsTrue();
        var activity = activityListener.StoppedActivities.First(a =>
            a.OperationName == "EventLog Append" &&
            string.Equals(a.GetTagItem("pal.eventlog.stream") as string, "ordering-order-1", StringComparison.Ordinal));
        await Assert.That(activity.GetTagItem("pal.eventlog.stream")).IsEqualTo("ordering-order-1");
        await Assert.That(activity.GetTagItem("pal.eventlog.event_count")).IsEqualTo(2);
        await Assert.That(activity.GetTagItem("pal.eventlog.first_global_position")).IsEqualTo(0L);
        await Assert.That(activity.GetTagItem("pal.eventlog.last_global_position")).IsEqualTo(1L);
        await Assert.That(meterListener.Measurements).Contains(2);
    }

    private static DbContextOptions<TestEventLogDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestEventLogDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static EventData CreateEvent(PalUlid eventId, string name, string payload)
        => new(
            eventId,
            name,
            schemaVersion: 1,
            "application/json",
            Encoding.UTF8.GetBytes(payload),
            metadata: "metadata"u8.ToArray(),
            new EventAuditMetadata(
                "user-123",
                "submit order",
                PalUlid.New(Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64")),
                PalUlid.New(Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7")),
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                "tenant=ordering"));

    private static async Task<List<RecordedEvent>> ReadAllAsync(IAsyncEnumerable<RecordedEvent> events)
    {
        var result = new List<RecordedEvent>();
        await foreach (var @event in events)
            result.Add(@event);

        return result;
    }

    private sealed class TestEventLogDbContext(
        DbContextOptions<TestEventLogDbContext> options,
        TimeProvider? timeProvider = null)
        : EventLogDbContext(options, timeProvider);

    /// <summary>固定时间 TimeProvider —— 验证 RecordedAt 来自注入的时钟而非系统时间。</summary>
    private sealed class FixedTimeProvider(DateTimeOffset fixedTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedTime;
    }
}

// ═══════════════════════════════════════════════════════════════
// P0 验收测试 — StoredEvent byte[] + 转换器消除
// ═══════════════════════════════════════════════════════════════

public sealed class EventLogOptimizedSerializationTests
{
    // P0-T1: 写入再读出，Payload/Metadata 字节一致
    [Test]
    public async Task PayloadAndMetadata_RoundTrip_MatchInputBytes(CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<TestOptimizedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using (var db = new TestOptimizedDbContext(options))
        {
            await db.AppendAsync(
                "test-stream",
                ExpectedStreamVersion.NoStream,
                [CreateTestEvent("hello-payload", "meta-data")],
                cancellationToken);
        }

        await using var reader = new TestOptimizedDbContext(options);
        var events = await ReadAllOptimizedAsync(reader.ReadStreamAsync(
            "test-stream", cancellationToken: cancellationToken));

        await Assert.That(events).HasSingleItem();
        var recorded = events[0];
        await Assert.That(Encoding.UTF8.GetString(recorded.Payload.Span)).IsEqualTo("hello-payload");
        await Assert.That(Encoding.UTF8.GetString(recorded.Metadata.Span)).IsEqualTo("meta-data");
    }

    // P0-T2: 多条事件写入，所有 payload/metadata 字节正确
    [Test]
    public async Task MultipleEvents_AllPayloadAndMetadata_Correct(CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<TestOptimizedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using (var db = new TestOptimizedDbContext(options))
        {
            await db.AppendAsync(
                "multi-stream",
                ExpectedStreamVersion.NoStream,
                [
                    CreateTestEvent("payload-0", "meta-0"),
                    CreateTestEvent("payload-1", "meta-1"),
                    CreateTestEvent("payload-2", "meta-2"),
                ],
                cancellationToken);
        }

        await using var reader = new TestOptimizedDbContext(options);
        var events = await ReadAllOptimizedAsync(reader.ReadStreamAsync(
            "multi-stream", cancellationToken: cancellationToken));

        await Assert.That(events.Count).IsEqualTo(3);
        for (var i = 0; i < 3; i++)
        {
            await Assert.That(Encoding.UTF8.GetString(events[i].Payload.Span)).IsEqualTo($"payload-{i}");
            await Assert.That(Encoding.UTF8.GetString(events[i].Metadata.Span)).IsEqualTo($"meta-{i}");
        }
    }

    // P0-T3: 分配验证 — 每次追加的分配量合理（验证转换器消除有效）
    [Test]
    public async Task Append_AllocationPerEvent_Reasonable(CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<TestOptimizedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        // 预热
        await using (var warmup = new TestOptimizedDbContext(options))
        {
            await warmup.AppendAsync(
                "warmup-stream",
                ExpectedStreamVersion.NoStream,
                [CreateTestEvent("warmup", "data")],
                cancellationToken);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetAllocatedBytesForCurrentThread();

        await using (var db = new TestOptimizedDbContext(options))
        {
            await db.AppendAsync(
                "test-alloc",
                ExpectedStreamVersion.NoStream,
                [
                    CreateTestEvent("payload-a", "meta-a"),
                    CreateTestEvent("payload-b", "meta-b"),
                ],
                cancellationToken);
        }

        var alloc = GC.GetAllocatedBytesForCurrentThread() - baseline;
        // 2 事件 × 约 2KB (EF Core 内部实体物化) = 合理上限
        await Assert.That(alloc < 100_000).IsTrue();
    }

    private static EventData CreateTestEvent(string payload, string metadata)
        => new(
            PalUlid.New(),
            "test-event.v1",
            schemaVersion: 1,
            "application/json",
            Encoding.UTF8.GetBytes(payload),
            Encoding.UTF8.GetBytes(metadata),
            new EventAuditMetadata(
                "test-actor", "test-reason",
                PalUlid.New(), PalUlid.New(),
                null, null));

    private static async Task<List<RecordedEvent>> ReadAllOptimizedAsync(
        IAsyncEnumerable<RecordedEvent> events)
    {
        var result = new List<RecordedEvent>();
        await foreach (var e in events) result.Add(e);
        return result;
    }

    private sealed class TestOptimizedDbContext(DbContextOptions<TestOptimizedDbContext> options)
        : EventLogDbContext(options)
    {
    }
}
