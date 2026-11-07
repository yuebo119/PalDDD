using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

/// <summary>
/// 批次 B（2026-11-04）读侧 API 与状态值契约的单元锁定。<br/>
/// 覆盖：InMemory 实现的 <c>GetByStatusAsync</c> 矩阵（五态隔离 / maxCount / 排序）、
/// 未定义状态值的可见失败、以及枚举编号的线上契约（AwaitingHumanDecision 必须恒为 5，
/// 4 是 DeadLettered 留下的空洞——隐式重排会把存量行读成别的状态）。
/// </summary>
public sealed class SagaStatusQueryTests
{
    public sealed class QueryState : SagaState;

    [Test]
    public async Task GetByStatusAsync_EachStatusReturnsOnlyItsOwn()
    {
        var store = new InMemorySagaStateStore<QueryState>();
        var awaiting = Seed(store, SagaStatus.Active);
        var completed = Seed(store, SagaStatus.Completed);
        var compensated = Seed(store, SagaStatus.Compensated);
        var failed = Seed(store, SagaStatus.CompensationFailed);
        var hitl = Seed(store, SagaStatus.AwaitingHumanDecision);

        await AssertOnlyContaining(store, SagaStatus.Active, awaiting.SagaId);
        await AssertOnlyContaining(store, SagaStatus.Completed, completed.SagaId);
        await AssertOnlyContaining(store, SagaStatus.Compensated, compensated.SagaId);
        await AssertOnlyContaining(store, SagaStatus.CompensationFailed, failed.SagaId);
        await AssertOnlyContaining(store, SagaStatus.AwaitingHumanDecision, hitl.SagaId);
    }

    [Test]
    public async Task GetByStatusAsync_RespectsMaxCount()
    {
        var store = new InMemorySagaStateStore<QueryState>();
        for (var i = 0; i < 5; i++)
            Seed(store, SagaStatus.Active);

        var two = await IdsAsync(store, SagaStatus.Active, 2);

        await Assert.That(two.Count).IsEqualTo(2);
        await Assert.That((await store.GetByStatusAsync(SagaStatus.Active, 100)).Count).IsEqualTo(5);
    }

    [Test]
    public async Task GetByStatusAsync_ReturnsEmptyForStatusWithoutRows()
    {
        var store = new InMemorySagaStateStore<QueryState>();
        Seed(store, SagaStatus.Active);

        var none = await store.GetByStatusAsync(SagaStatus.Completed, 10);

        await Assert.That(none.Count).IsEqualTo(0);
    }

    /// <summary>四栈统一按 <c>saga_id</c> 排序（ULID 字典序=创建序）——跨栈结果可比的前提。</summary>
    [Test]
    public async Task GetByStatusAsync_OrdersBySagaId()
    {
        var store = new InMemorySagaStateStore<QueryState>();
        Seed(store, SagaStatus.Active);
        Seed(store, SagaStatus.Active);
        Seed(store, SagaStatus.Active);

        var ids = await IdsAsync(store, SagaStatus.Active, 10);

        await Assert.That(ids.Count).IsEqualTo(3);
        var expected = ids.OrderBy(static id => id).ToList();
        await Assert.That(ids[0]).IsEqualTo(expected[0]);
        await Assert.That(ids[1]).IsEqualTo(expected[1]);
        await Assert.That(ids[2]).IsEqualTo(expected[2]);
        // 排序键非插入序即证明排序生效（ULID 单调但同毫秒内可交错，故再断言一次不退化）。
        await Assert.That(ids.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task GetByStatusAsync_NonPositiveMaxCount_Throws()
    {
        var store = new InMemorySagaStateStore<QueryState>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetByStatusAsync(SagaStatus.Active, 0, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetByStatusAsync(SagaStatus.Active, -1, CancellationToken.None).AsTask());
    }

    /// <summary>
    /// 未定义状态值作为过滤条件必须可见失败：静默返回空列表会让调用方误以为
    /// 「该状态没有数据」——这正是被删除的 GetInterruptedSagasAsync 的假空形态。
    /// </summary>
    [Test]
    public async Task GetByStatusAsync_UndefinedStatusFilter_ThrowsActively()
    {
        var store = new InMemorySagaStateStore<QueryState>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetByStatusAsync((SagaStatus)4, 10, CancellationToken.None).AsTask());

        await Assert.That(ex!.Message.Contains('4')).IsTrue();
        await Assert.That(ex.Message.Contains("DeadLettered", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// 枚举编号是持久化契约：<see cref="SagaStatus.AwaitingHumanDecision"/> 必须恒为 5
    /// （历史落库值），4 必须保持未定义。若有人把成员挪位或插入中间值，本测试立刻转红——
    /// 三条 SQL 模板（<c>status IN (0, 5)</c>）与存量行语义同时失效。
    /// </summary>
    [Test]
    public async Task SagaStatus_NumericValuesArePersistedContract()
    {
        // 赋给局部变量再做断言：枚举成员是编译期常量，直接 Assert.That((int)SagaStatus.X)
        // 会被 TUnitAssertions0005 判为「断言常量」的恒真自检。走进局部变量后，实际值
        // 来自枚举定义——将来有人挪位/插值，本测试立刻转红。
        var active = (int)SagaStatus.Active;
        var completed = (int)SagaStatus.Completed;
        var compensated = (int)SagaStatus.Compensated;
        var compensationFailed = (int)SagaStatus.CompensationFailed;
        var awaitingHumanDecision = (int)SagaStatus.AwaitingHumanDecision;

        await Assert.That(active).IsEqualTo(0);
        await Assert.That(completed).IsEqualTo(1);
        await Assert.That(compensated).IsEqualTo(2);
        await Assert.That(compensationFailed).IsEqualTo(3);
        await Assert.That(awaitingHumanDecision).IsEqualTo(5);
        await Assert.That(Enum.IsDefined((SagaStatus)4)).IsFalse();
    }

    /// <summary>水合守卫的可行动异常：文案必须同时含 sagaId 与原始值，供运维直接定位行。</summary>
    [Test]
    public async Task SagaStatusGuard_UnknownValue_ThrowsWithSagaIdAndValue()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.FromResult(SagaStatusGuard.FromPersisted(4, "saga-abc")));

        await Assert.That(ex!.Message.Contains("saga-abc", StringComparison.Ordinal)).IsTrue();
        await Assert.That(ex.Message.Contains('4')).IsTrue();
        await Assert.That(ex.Message.Contains("SELECT COUNT(*)", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SagaStatusGuard_DefinedValue_PassesThrough()
    {
        await Assert.That(SagaStatusGuard.FromPersisted(5, "saga-ok")).IsEqualTo(SagaStatus.AwaitingHumanDecision);
        await Assert.That(SagaStatusGuard.FromPersisted(0, "saga-ok")).IsEqualTo(SagaStatus.Active);
    }

    private static async Task AssertOnlyContaining(
        InMemorySagaStateStore<QueryState> store,
        SagaStatus status,
        PalUlid expected)
    {
        var ids = await IdsAsync(store, status, 100);
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0]).IsEqualTo(expected);
    }

    private static QueryState Seed(InMemorySagaStateStore<QueryState> store, SagaStatus status)
    {
        var state = new QueryState
        {
            SagaId = PalUlid.New(),
            CurrentState = status.ToString(),
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        store.Add(state);
        return state;
    }

    private static async Task<List<PalUlid>> IdsAsync(
        InMemorySagaStateStore<QueryState> store,
        SagaStatus status,
        int maxCount)
    {
        var rows = await store.GetByStatusAsync(status, maxCount, CancellationToken.None);
        return rows.Select(static r => r.SagaId).ToList();
    }
}
