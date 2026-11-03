using PalDDD.Core.Repository;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// UnitOfWorkExtensions.ExecuteInTransactionAsync 两重载契约
// 无返回值版本为既有实现（仅提取消息常量），泛型版本为本轮新增——
// 两侧事务语义、回滚失败聚合规则必须逐项一致。
// ═══════════════════════════════════════════════════════════════

/// <summary>记录式替身：只统计原语调用，不模拟数据库。</summary>
internal sealed class RecordingUnitOfWork : IUnitOfWork
{
    public int BeginCount { get; private set; }
    public int SaveCount { get; private set; }
    public int CommitCount { get; private set; }
    public int RollbackCount { get; private set; }

    public Func<ValueTask>? OnBegin { get; set; }
    public Func<ValueTask>? OnRollback { get; set; }

    public ValueTask BeginTransactionAsync(CancellationToken ct = default)
    {
        BeginCount++;
        return OnBegin?.Invoke() ?? ValueTask.CompletedTask;
    }

    public ValueTask CommitAsync(CancellationToken ct = default)
    {
        CommitCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RollbackAsync(CancellationToken ct = default)
    {
        RollbackCount++;
        return OnRollback?.Invoke() ?? ValueTask.CompletedTask;
    }

    public ValueTask<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return ValueTask.FromResult(0);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class UnitOfWorkExtensionsTests
{
    [Test]
    public async Task Generic_WorkSucceeds_ReturnsResultAndCommits()
    {
        var uow = new RecordingUnitOfWork();

        var result = await uow.ExecuteInTransactionAsync(
            static ct => ValueTask.FromResult(42), CancellationToken.None);

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(uow.BeginCount).IsEqualTo(1);
        await Assert.That(uow.SaveCount).IsEqualTo(1);
        await Assert.That(uow.CommitCount).IsEqualTo(1);
        await Assert.That(uow.RollbackCount).IsEqualTo(0);
    }

    [Test]
    public async Task Generic_WorkThrows_RollsBackAndRethrowsOriginal()
    {
        var uow = new RecordingUnitOfWork();
        var failure = new InvalidOperationException("business failure");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await uow.ExecuteInTransactionAsync<Guid>(
                _ => throw failure, CancellationToken.None));

        await Assert.That(caught).IsSameReferenceAs(failure);
        await Assert.That(uow.CommitCount).IsEqualTo(0);
        await Assert.That(uow.RollbackCount).IsEqualTo(1);
    }

    [Test]
    public async Task Generic_BeginThrows_DoesNotAttemptRollback()
    {
        // ADR-023：活动事务内 Begin 由实现栈抛 InvalidOperationException（fail-fast）。
        // 此时没有本方法开启的事务可回滚——Rollback 不应被调用（与既有无返回值重载一致）。
        var uow = new RecordingUnitOfWork
        {
            OnBegin = static () => throw new InvalidOperationException("transaction already active"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await uow.ExecuteInTransactionAsync(
                static ct => ValueTask.FromResult(1), CancellationToken.None));

        await Assert.That(uow.RollbackCount).IsEqualTo(0);
        await Assert.That(uow.CommitCount).IsEqualTo(0);
    }

    [Test]
    public async Task Generic_RollbackThrows_WrapsBothInAggregateException()
    {
        var uow = new RecordingUnitOfWork
        {
            OnRollback = static () => throw new IOException("rollback failed"),
        };
        var business = new InvalidOperationException("business failure");

        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
            await uow.ExecuteInTransactionAsync<Guid>(
                _ => throw business, CancellationToken.None));

        var inner = aggregate?.InnerExceptions ?? [];
        await Assert.That(inner.Count).IsEqualTo(2);
        await Assert.That(inner[0]).IsSameReferenceAs(business);
        await Assert.That(inner[1]).IsTypeOf<IOException>();
    }

    [Test]
    public async Task NonGeneric_WorkSucceeds_CommitsWithoutResult()
    {
        // 回归：本轮的常量提取改动了无返回值重载的消息来源，契约不得变化。
        var uow = new RecordingUnitOfWork();

        await uow.ExecuteInTransactionAsync(static _ => ValueTask.CompletedTask, CancellationToken.None);

        await Assert.That(uow.BeginCount).IsEqualTo(1);
        await Assert.That(uow.SaveCount).IsEqualTo(1);
        await Assert.That(uow.CommitCount).IsEqualTo(1);
        await Assert.That(uow.RollbackCount).IsEqualTo(0);
    }
}
