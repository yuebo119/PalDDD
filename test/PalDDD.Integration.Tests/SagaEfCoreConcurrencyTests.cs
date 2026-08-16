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
    public async Task SaveChangesAsync_Interface_OnConcurrencyConflict_ReturnsZero(CancellationToken ct)
    {
        // ITM-072 回归：经 ISagaStateStore 接口保存时，乐观锁冲突必须返回 0（契约语义），
        // 而非把 DbUpdateConcurrencyException 上抛给调用方（修复前直接透传 SaveChangesAsync(ct)）。
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using var ctx1 = new TestSagaDbContext(CreateOptions(connection));
        await using var ctx2 = new TestSagaDbContext(CreateOptions(connection));
        await ctx1.Database.EnsureCreatedAsync(ct);

        var state = new EfConcurrencySagaState { CurrentState = "Initial" };
        ctx1.SagaStates.Add(state);
        await ctx1.SaveChangesAsync(ct);

        var copy1 = await ctx1.SagaStates.SingleAsync(s => s.SagaId == state.SagaId, ct);
        var copy2 = await ctx2.SagaStates.SingleAsync(s => s.SagaId == state.SagaId, ct);
        copy1.CurrentState = "Writer1";
        copy2.CurrentState = "Writer2";

        await ctx1.SaveChangesAsync(ct);

        var affected = await ((ISagaStateStore<EfConcurrencySagaState>)ctx2).SaveChangesAsync(copy2, ct);

        await Assert.That(affected).IsEqualTo(0);
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

    [Test]
    public async Task ResumeAsync_DecisionTriggersSecondInterrupt_SucceedsAndKeepsNewEntry(CancellationToken ct)
    {
        // P3 回归（十轮修正）：多阶段 HITL——决策处理再次触发 InterruptStep 时返回的也是
        // AwaitingHumanDecision 状态（InterruptStep 不改 CurrentState，全程停留在 "Initial"）。
        // 路由缺失检查曾仅凭状态判别，会把合法二次中断误判为"未注册处理路由"。
        // 正确判别依据是条目身份：二次中断会注册新条目对象。
        var manager = new DefaultSagaManager();
        var saga = new TwoStageHitlSaga { SagaManager = manager };
        var state = new HitlSagaState();

        await saga.ProcessEventAsync(state, new KickoffEvent(), ct);
        await Assert.That(state.Status).IsEqualTo(SagaStatus.AwaitingHumanDecision);
        await Assert.That(state.InterruptReason).IsEqualTo("first-stage");

        // 第一决策触发第二阶段中断——ResumeAsync 正常返回（不抛路由缺失误报），
        // 新中断原因就位（证明新条目已注册而非路由缺失）
        await manager.ResumeAsync(state.SagaId, new ApproveDecision(Approved: true), ct);
        await Assert.That(state.Status).IsEqualTo(SagaStatus.AwaitingHumanDecision);
        await Assert.That(state.InterruptReason).IsEqualTo("second-stage");

        // 第二决策完成整个流程
        await manager.ResumeAsync(state.SagaId, new FinalDecision(), ct);
        await Assert.That(state.Status).IsEqualTo(SagaStatus.Completed);
    }

    [Test]
    public async Task ResumeAsync_DecisionRouteMissing_ThrowsAndKeepsEntry(CancellationToken ct)
    {
        // 路由缺失（未注册该决策类型的 When）——可见失败，条目保留可重试
        var manager = new DefaultSagaManager();
        var saga = new HitlTestSaga { SagaManager = manager };
        var state = new HitlSagaState();

        await saga.ProcessEventAsync(state, new KickoffEvent(), ct);

        await Assert.That(async () =>
            await manager.ResumeAsync(state.SagaId, new UnknownDecision(), ct))
            .Throws<InvalidOperationException>();

        // 条目保留——用正确决策类型仍可恢复
        await manager.ResumeAsync(state.SagaId, new ApproveDecision(Approved: true), ct);
        await Assert.That(state.Status).IsEqualTo(SagaStatus.Completed);
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

    // ── 多阶段 HITL 测试装置（十轮修正）──

    private sealed record FinalDecision;

    private sealed record UnknownDecision;

    private sealed class TwoStageHitlSaga : Saga<HitlSagaState>
    {
        public TwoStageHitlSaga()
        {
            // InterruptStep 不改 CurrentState——三步全部注册在 "Initial"（按事件类型区分路由）
            When<KickoffEvent>("Initial",
                new InterruptStep("first-stage-interrupt", "first-stage", typeof(ApproveDecision)));
            // 第一决策触发第二阶段中断
            When<ApproveDecision>("Initial", new InterruptStep(
                "second-stage-interrupt", "second-stage", typeof(FinalDecision)));
            // 第二决策完成流程
            When<FinalDecision>("Initial", new SagaStep("finish",
                execute: static (s, e, ct) =>
                {
                    s.CurrentState = "Done";
                    s.Status = SagaStatus.Completed;
                    return new ValueTask<SagaState>(s);
                }));
        }
    }
}
