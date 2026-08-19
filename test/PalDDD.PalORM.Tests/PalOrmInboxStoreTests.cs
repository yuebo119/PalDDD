using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Inbox Store 测试 —— 迁移自 DapperStoreTests.cs 的 Inbox 部分（8 测试，迁移核心 6 个）。
/// </summary>
public class PalOrmInboxStoreTests
{
    [Test]
    public async Task Inbox_TryStartProcessing_FirstAttempt()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var msg = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);

        await Assert.That(msg).IsNotNull();
        await Assert.That(msg!.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(msg.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Inbox_TryStartProcessing_Duplicate()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await Assert.That(first).IsNotNull();
        await store.MarkProcessedAsync(first!, now, default);

        // 二次处理同一消息 → null（幂等跳过）
        var second = await store.TryStartProcessingAsync("consumer-1", "msg-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Inbox_TryStartProcessing_StillProcessing()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await Assert.That(first).IsNotNull();

        // 仍在 Processing（租约未过期）→ null
        var second = await store.TryStartProcessingAsync("consumer-1", "msg-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Inbox_MarkProcessedAsync()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var msg = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await store.MarkProcessedAsync(msg!, now, default);

        // 已 Processed → 二次 TryStart 返回 null
        var again = await store.TryStartProcessingAsync("consumer-1", "msg-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(again).IsNull();
    }

    [Test]
    public async Task Inbox_MarkFailedAsync()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var msg = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await store.MarkFailedAsync(msg!, "transient error", default);

        // Failed 状态可重新 TryStart（attempts 自增）
        var retry = await store.TryStartProcessingAsync("consumer-1", "msg-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(retry).IsNotNull();
        await Assert.That(retry!.Attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Inbox_DifferentConsumers_Independent()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var c1 = await store.TryStartProcessingAsync("consumer-A", "msg-1", now, TimeSpan.FromMinutes(5), default);
        var c2 = await store.TryStartProcessingAsync("consumer-B", "msg-1", now, TimeSpan.FromMinutes(5), default);

        await Assert.That(c1).IsNotNull();
        await Assert.That(c2).IsNotNull();  // 不同 consumer 独立处理
    }
}
