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

        // P3 补前置断言：原 msg!. 直接解引用——首次受理失败返回 null 时会在 .Status 处抛
        // NullReferenceException 而非指向性的断言失败（对齐下方 Duplicate 测试的 IsNotNull 形态）
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

        await Assert.That(c1).IsTypeOf<InboxMessage>();
        await Assert.That(c2).IsTypeOf<InboxMessage>();
        await Assert.That(c1!.ConsumerName).IsEqualTo("consumer-A");
        await Assert.That(c2!.ConsumerName).IsEqualTo("consumer-B");  // 不同 consumer 独立处理
    }

    /// <summary>ITM-274（R42）：null ProcessingStartedAt 的手工构造 message 直调 Mark* 属调用方
    /// 误用——显式 fail-fast（对齐 Dapper 姊妹 C# 层抛异常形态）而非 SQL =NULL 永假静默零变更。
    /// 修复前该场景 affected=0 无任何痕迹。</summary>
    [Test]
    public async Task Inbox_MarkProcessed_NullProcessingStartedAt_ThrowsFailFast()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var msg = new InboxMessage { MessageId = "m1", ConsumerName = "c1" }; // ProcessingStartedAt 默认 null

        var ex = await Assert.That(() =>
        {
            var m = msg;
            return store.MarkProcessedAsync(m, DateTimeOffset.UtcNow, default).AsTask();
        }).Throws<ArgumentNullException>();

        await Assert.That(ex!.ParamName).IsEqualTo("message");
        await Assert.That(ex.Message).Contains("ProcessingStartedAt");
    }

    /// <summary>
    /// 批次 C（2026-11-04）读侧补齐：Inbox 按状态列举——状态隔离、maxCount、自增 id 排序。
    /// </summary>
    [Test]
    public async Task Inbox_GetByStatusAsync_FiltersByStatus_AppliesMaxCount_AndOrdersById()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartProcessingAsync("orders", "message-1", now, TimeSpan.FromMinutes(5), default);
        var second = await store.TryStartProcessingAsync("orders", "message-2", now, TimeSpan.FromMinutes(5), default);
        var failed = await store.TryStartProcessingAsync("orders", "message-3", now, TimeSpan.FromMinutes(5), default);
        await store.MarkProcessedAsync(first!, now.AddSeconds(1), default);
        await store.MarkFailedAsync(failed!, "seeded failure", default);

        var failedRows = await store.GetByStatusAsync(InboxStatus.Failed, 10, default);
        await Assert.That(failedRows).Count().IsEqualTo(1);
        await Assert.That(failedRows[0].MessageId).IsEqualTo("message-3");

        var processedRows = await store.GetByStatusAsync(InboxStatus.Processed, 10, default);
        await Assert.That(processedRows).Count().IsEqualTo(1);
        await Assert.That(processedRows[0].MessageId).IsEqualTo("message-1");

        var processingRows = await store.GetByStatusAsync(InboxStatus.Processing, 10, default);
        await Assert.That(processingRows).Count().IsEqualTo(1);
        await Assert.That(processingRows[0].MessageId).IsEqualTo("message-2");

        var capped = await store.GetByStatusAsync(InboxStatus.Processing, 1, default);
        await Assert.That(capped).Count().IsEqualTo(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetByStatusAsync((InboxStatus)99, 10, default).AsTask());
    }
}
