// ─────────────────────────────────────────────────────────────
// 🧪 Saga EFCore 并发 + HITL 恢复测试（八轮评审 P2 修复验证）
//   1. SagaStateDbContext.Version 并发令牌真实递增——双 DbContext 实例
//      （共享 SQLite 内存库连接）并发 SaveChangesAsync，后写者抛
//      DbUpdateConcurrencyException（修复前 WHERE Version=orig 恒匹配，永不抛）。
//   2. DefaultSagaManager.ResumeAsync 完整闭环——以决策为事件重新派发到
//      ProcessEventAsync 管线，成功后移除中断条目（修复前仅静默暂存决策）。
// 注：本文件属集成测试——EFCore 适配器引用为架构边界测试允许的 Infra 依赖。
// ─────────────────────────────────────────────────────────────
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

public sealed class SagaEfCoreConcurrencyTests
{
    // ═══════════════════════════════════════════════════════════════
    // EFCore Version 乐观锁并发（修复 1）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task SaveChangesAsync_SecondConcurrentWriter_ThrowsDbUpdateConcurrencyException(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using var ctx1 = new TestSagaDbContext(CreateOptions(connection));
        await using var ctx2 = new TestSagaDbContext(CreateOptions(connection));
        await ctx1.Database.EnsureCreatedAsync(ct);

        var state = new EfConcurrencySagaState { CurrentState = "Initial" };
        ctx1.SagaStates.Add(state);
        await ctx1.SaveChangesAsync(ct); // INSERT：Version=0 落库

        // 两个实例各自加载同一行（独立跟踪，original value 均为 0）
        var copy1 = await ctx1.SagaStates.SingleAsync(s => s.SagaId == state.SagaId, ct);
        var copy2 = await ctx2.SagaStates.SingleAsync(s => s.SagaId == state.SagaId, ct);
        copy1.CurrentState = "Writer1";
        copy2.CurrentState = "Writer2";

        await ctx1.SaveChangesAsync(ct); // 第一写入者成功：DB Version 0→1

        // 第二写入者 WHERE Version=0 不再匹配 → 并发异常（修复前恒匹配、静默覆盖）
        await Assert.That(async () => await ctx2.SaveChangesAsync(ct))
            .Throws<DbUpdateConcurrencyException>();
    }

    [Test]
    public async Task SaveChangesAsync_ModifiedSaga_AdvancesVersionInMemoryAndDatabase(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new TestSagaDbContext(CreateOptions(connection));
        await db.Database.EnsureCreatedAsync(ct);

        var state = new EfConcurrencySagaState { CurrentState = "Initial" };
        db.SagaStates.Add(state);
        await db.SaveChangesAsync(ct);

        var loaded = await db.SagaStates.SingleAsync(s => s.SagaId == state.SagaId, ct);
        await Assert.That(loaded.Version).IsEqualTo(0);

        loaded.CurrentState = "Step1Done";
        await db.SaveChangesAsync(ct);
        await Assert.That(loaded.Version).IsEqualTo(1);

        // 连续二次保存不抛——若递增发生在提交后（错误实现），SET 不含 Version，
        // DB 停在旧值而内存前进，第二次保存的 WHERE 将永不匹配
        loaded.CurrentState = "Step2Done";
        await db.SaveChangesAsync(ct);
        await Assert.That(loaded.Version).IsEqualTo(2);

        // DB 侧与内存同步递增
        db.ChangeTracker.Clear();
        var fresh = await db.SagaStates.SingleAsync(s => s.SagaId == state.SagaId, ct);
        await Assert.That(fresh.Version).IsEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // HITL 恢复闭环（修复 2：完整重派发分支）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task ResumeAsync_AfterInterrupt_DispatchesDecisionAndRemovesEntry(CancellationToken ct)
    {
        var manager = new DefaultSagaManager();
        var saga = new HitlTestSaga { SagaManager = manager };
        var state = new HitlSagaState();

        var interrupted = await saga.ProcessEventAsync(state, new KickoffEvent(), ct);
        await Assert.That(interrupted.Status).IsEqualTo(SagaStatus.AwaitingHumanDecision);
        await Assert.That(interrupted.InterruptReason).IsEqualTo("amount-above-threshold");

        // 恢复：以决策为事件重新进入 ProcessEventAsync（修复前仅暂存决策、无派发）
        await manager.ResumeAsync(state.SagaId, new ApproveDecision(Approved: true), ct);

        await Assert.That(state.Status).IsEqualTo(SagaStatus.Completed);
        await Assert.That(state.CurrentState).IsEqualTo("Approved");

        // 恢复成功后条目已移除——二次恢复不再静默 no-op，而是可见失败
        await Assert.That(async () =>
            await manager.ResumeAsync(state.SagaId, new ApproveDecision(Approved: true), ct))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ResumeAsync_UnknownSaga_ThrowsInvalidOperationException(CancellationToken ct)
    {
        var manager = new DefaultSagaManager();

        await Assert.That(async () =>
            await manager.ResumeAsync(PalUlid.New(), new ApproveDecision(Approved: false), ct))
            .Throws<InvalidOperationException>();
    }

    // ═══════════════════════════════════════════════════════════════
    // 测试装置
    // ═══════════════════════════════════════════════════════════════

    private static DbContextOptions<TestSagaDbContext> CreateOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<TestSagaDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class EfConcurrencySagaState : SagaState;

    private sealed class TestSagaDbContext(DbContextOptions<TestSagaDbContext> options)
        : SagaStateDbContext<EfConcurrencySagaState>(options);

    // ── HITL 测试事件与 Saga ──

    private sealed record KickoffEvent;

    private sealed record ApproveDecision(bool Approved);

    private sealed class HitlSagaState : SagaState;

    private sealed class HitlTestSaga : Saga<HitlSagaState>
    {
        public HitlTestSaga()
        {
            When<KickoffEvent>("Initial",
                new InterruptStep("await-approval", "amount-above-threshold", typeof(ApproveDecision)));
            When<ApproveDecision>("Initial", new SagaStep("apply-decision",
                execute: static (s, e, ct) =>
                {
                    var decision = (ApproveDecision)e;
                    s.CurrentState = decision.Approved ? "Approved" : "Rejected";
                    s.Status = SagaStatus.Completed;
                    return new ValueTask<SagaState>(s);
                }));
        }
    }
}
