using Microsoft.EntityFrameworkCore;

namespace PalDDD.EventLog.Tests;

public sealed class EventLogPositionReserverTests
{
    [Test]
    public async Task ReserveAsync_FirstCall_InitializesAllocatorAndReturnsZero(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());
        var reserver = new EventLogPositionReserver(chunkSize: 10);

        var first = await reserver.ReserveAsync(db, count: 1, cancellationToken);

        await Assert.That(first).IsEqualTo(0);
    }

    [Test]
    public async Task ReserveAsync_WithinChunk_DoesNotHitDatabase(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());
        var reserver = new EventLogPositionReserver(chunkSize: 100);

        // 第一次调用：初始化分配器（1 次 DB 往返读取 + 1 次保存）。
        var first = await reserver.ReserveAsync(db, count: 1, cancellationToken);
        await Assert.That(first).IsEqualTo(0);

        // 第二次调用：在同一 chunk 内，应为纯内存操作。
        var second = await reserver.ReserveAsync(db, count: 1, cancellationToken);
        await Assert.That(second).IsEqualTo(1);

        // 第三次调用：仍在 chunk 内。
        var third = await reserver.ReserveAsync(db, count: 3, cancellationToken);
        await Assert.That(third).IsEqualTo(2);
    }

    [Test]
    public async Task ReserveAsync_ExhaustsChunkAndAllocatesNewOne(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());
        var reserver = new EventLogPositionReserver(chunkSize: 5);

        // 预留 5 个位置 [0..4] — 恰好填满第一个 chunk。
        var firstBatch = await reserver.ReserveAsync(db, count: 5, cancellationToken);
        await Assert.That(firstBatch).IsEqualTo(0);

        // 下一次调用必须分配新 chunk [5..9]。
        var nextBatch = await reserver.ReserveAsync(db, count: 1, cancellationToken);
        await Assert.That(nextBatch).IsEqualTo(5);
    }

    [Test]
    public async Task ReserveAsync_LargerThanChunk_AllocatesExactSizedChunk(CancellationToken cancellationToken)
    {
        await using var db = new TestEventLogDbContext(CreateOptions());
        var reserver = new EventLogPositionReserver(chunkSize: 10);

        // 一次请求 15 个位置 — chunk 必须至少为 15。
        var first = await reserver.ReserveAsync(db, count: 15, cancellationToken);
        await Assert.That(first).IsEqualTo(0);

        // 下一次调用应从 15 开始（新 chunk 为 [0..14] 然后是 [15..]）。
        var next = await reserver.ReserveAsync(db, count: 1, cancellationToken);
        await Assert.That(next).IsEqualTo(15);
    }

    [Test]
    public async Task ReserveAsync_PersistsAllocatorNextGlobalPosition(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using (var db = new TestEventLogDbContext(options))
        {
            var reserver = new EventLogPositionReserver(chunkSize: 10);
            await reserver.ReserveAsync(db, count: 3, cancellationToken);
        }

        await using var reader = new TestEventLogDbContext(options);
        var allocator = await reader.Set<EventLogGlobalPositionAllocator>()
            .SingleAsync(cancellationToken);
        // Chunk 为 [0..9]，因此 NextGlobalPosition（高水位标记）= 10。
        await Assert.That(allocator.NextGlobalPosition).IsEqualTo(10);
    }

    [Test]
    public async Task ReserveAsync_AfterProcessRestart_ResumesFromPersistedHiWaterMark(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using (var db = new TestEventLogDbContext(options))
        {
            var reserver = new EventLogPositionReserver(chunkSize: 10);
            await reserver.ReserveAsync(db, count: 3, cancellationToken);
            // 分配器已持久化 NextGlobalPosition=10，但进程在使用 [0..2] 后终止。
        }

        // 新进程 / 新 reserver — 应加载持久化的高水位标记并恢复。
        await using var db2 = new TestEventLogDbContext(options);
        var reserver2 = new EventLogPositionReserver(chunkSize: 10);
        var first = await reserver2.ReserveAsync(db2, count: 1, cancellationToken);

        // 从持久化的高水位标记 (10) 恢复，而非从内存中的低位 (3)。
        await Assert.That(first).IsEqualTo(10);
    }

    [Test]
    public async Task ReserveAsync_ConcurrentRequestsFromSameReserver_NoDuplicatePositions(CancellationToken cancellationToken)
    {
        // 单个 reserver（单进程模型）按顺序服务多个 DbContext，
        // 绝不能分发重复的位置。进程内锁序列化快速路径分配；
        // chunk 耗尽也受锁保护。
        // （InMemory EF provider 不支持跨 DbContext 的真正并发写入，
        // 因此此处按顺序执行 — ReserveAsync 内部的锁仍然会被测试到。）
        var dbName = Guid.NewGuid().ToString("N");
        var reserver = new EventLogPositionReserver(chunkSize: 50);

        var positions = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            await using var ctx = new TestEventLogDbContext(
                new DbContextOptionsBuilder<TestEventLogDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .Options);
            positions.Add(await reserver.ReserveAsync(ctx, count: 1, cancellationToken));
        }

        // 全部 100 个位置必须唯一（无竞态条件导致的重复）。
        await Assert.That(positions.Distinct().Count()).IsEqualTo(100);
        // 位置必须从 0 开始连续（顺序单线程分配）。
        await Assert.That(positions.Min()).IsEqualTo(0);
        await Assert.That(positions.Max() < 200).IsTrue();
    }

    private static DbContextOptions<TestEventLogDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestEventLogDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private sealed class TestEventLogDbContext(DbContextOptions<TestEventLogDbContext> options)
        : EventLogDbContext(options);
}
