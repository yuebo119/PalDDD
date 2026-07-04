// ─────────────────────────────────────────────────────────────
// 核心抽象契约测试 — 接口契约 + 基础类型验证
// （原 PalDDD.Abstractions.Tests — Abstractions 项目已拆分归还各层，此处保留低层类型测试）
// ─────────────────────────────────────────────────────────────
using PalDDD.Core.Repository;
using PalDDD.Serialization;
using PalDDD.Transactions;

namespace PalDDD.Core.Abstractions.Tests;

// ═══════════════════════════════════════════════════════════════
// ContentTypes — 常量正确性
// ═══════════════════════════════════════════════════════════════

public class ContentTypesTests
{
    [Test]
    public async Task Json_IsStandardMimeType()
        => await Assert.That(ContentTypes.Json).IsEqualTo("application/json");

    [Test]
    public async Task MemoryPack_IsCustomType()
        => await Assert.That(ContentTypes.MemoryPack).IsEqualTo("application/x-memorypack");
}

// ═══════════════════════════════════════════════════════════════
// IUnitOfWork 扩展方法
// ═══════════════════════════════════════════════════════════════

public class UnitOfWorkExtensionTests
{
    [Test]
    public async ValueTask ExecuteInTransaction_Success_Commits()
    {
        var uow = new TestUnitOfWork();
        var executed = false;

        await uow.ExecuteInTransactionAsync(_ =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        await Assert.That(executed).IsTrue();
        await Assert.That(uow.Committed).IsTrue();
        await Assert.That(uow.RolledBack).IsFalse();
    }

    [Test]
    public async ValueTask ExecuteInTransaction_Failure_Rollbacks()
    {
        var uow = new TestUnitOfWork();

        await Assert.That(() =>
            uow.ExecuteInTransactionAsync(_ =>
                throw new InvalidOperationException("fail")).AsTask()).Throws<InvalidOperationException>();

        await Assert.That(uow.RolledBack).IsTrue();
        await Assert.That(uow.Committed).IsFalse();
    }

    [Test]
    public async ValueTask ExecuteInTransaction_NullUow_Throws()
    {
        IUnitOfWork? uow = null;
        await Assert.That(() =>
            uow!.ExecuteInTransactionAsync(_ => ValueTask.CompletedTask).AsTask()).Throws<ArgumentNullException>();
    }

    [Test]
    public async ValueTask ExecuteInTransaction_NullWork_Throws()
    {
        var uow = new TestUnitOfWork();
        await Assert.That(() =>
            uow.ExecuteInTransactionAsync(null!).AsTask()).Throws<ArgumentNullException>();
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }

        public ValueTask BeginTransactionAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask CommitAsync(CancellationToken ct = default)
        { Committed = true; return ValueTask.CompletedTask; }

        public ValueTask RollbackAsync(CancellationToken ct = default)
        { RolledBack = true; return ValueTask.CompletedTask; }

        public ValueTask<int> SaveChangesAsync(CancellationToken ct = default) => ValueTask.FromResult(1);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════
// OutboxMessage / InboxMessage — 字段默认值验证
// ═══════════════════════════════════════════════════════════════

public class OutboxMessageTests
{
    [Test]
    public async Task DefaultConstructor_HasEmptyPayload()
    {
        var msg = new OutboxMessage { Type = "Test" };
        await Assert.That(msg.Payload).IsNotNull();
        await Assert.That(msg.Payload).IsEmpty();
        await Assert.That(msg.Type).IsEqualTo("Test");
        await Assert.That(msg.RetryCount).IsEqualTo(0);
    }
}

public class InboxMessageTests
{
    [Test]
    public async Task CompositeKey_ConsumerNameAndMessageId()
    {
        var messageId = Guid.NewGuid().ToString("N");
        var msg = new InboxMessage
        {
            ConsumerName = "consumer-1",
            MessageId = messageId
        };

        await Assert.That(msg.ConsumerName).IsEqualTo("consumer-1");
        await Assert.That(msg.MessageId).IsEqualTo(messageId);
    }
}
