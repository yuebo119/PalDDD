namespace PalDDD.Transactions.Tests;

public sealed class InMemoryStoreTests
{
    [Test]
    public async Task InMemoryInboxStore_FirstAttempt_ReturnsRecordWithProcessingStatus(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var record = await store.TryStartProcessingAsync(
            "consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(record!.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(record.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task InMemoryInboxStore_DuplicateProcessing_ReturnsNull(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        var dup = await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(dup).IsNull();
    }

    [Test]
    public async Task InMemoryInboxStore_AfterProcessed_DedupReturnsNull(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        var record = await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await store.MarkProcessedAsync(record!, now.AddSeconds(1), cancellationToken);
        var dup = await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(dup).IsNull();
    }

    [Test]
    public async Task InMemoryInboxStore_DifferentConsumers_Independent(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        var a = await store.TryStartProcessingAsync("consumer-a", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        var b = await store.TryStartProcessingAsync("consumer-b", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(a).IsNotNull();
        await Assert.That(b).IsNotNull();
    }

    [Test]
    public async Task InMemoryOutboxStore_LeaseAndProcess(CancellationToken cancellationToken)
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1, 2, 3], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(leased).Count().IsEqualTo(1);
        // ITM-174 修复：使用 Lease 返回的 successor 标记（真实调用语义——OutboxBatchProcessor
        // 用 Lease 返回值；旧引用已被替换，其标记被守卫忽略）
        var leasedMsg = leased[0];
        store.MarkProcessed(leasedMsg, DateTimeOffset.UtcNow);
        await Assert.That(leasedMsg.Status).IsEqualTo(OutboxStatus.Processed);
    }

    [Test]
    public async Task InMemoryOutboxStore_ReleaseForRetry_IncrementsRetryCount(CancellationToken cancellationToken)
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        // ITM-174 修复：对齐 Inbox 测试形态——先 Lease 拿 successor 再 Mark（守卫要求租约持有者）
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        store.ReleaseForRetry(leased[0], "test failure", DateTimeOffset.UtcNow.AddSeconds(30));
        await Assert.That(leased[0].RetryCount).IsEqualTo(1);
        await Assert.That(leased[0].Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task InMemoryOutboxStore_GetPending_UsesConfiguredMaxRetryCount(CancellationToken cancellationToken)
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        // ITM-174 修复：先 Lease 拿到 successor 再 ReleaseForRetry（守卫要求租约持有者）
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        store.ReleaseForRetry(leased[0], "test failure", DateTimeOffset.UtcNow.AddSeconds(-1));

        var pending = await store.GetPendingMessagesAsync(10, 1, cancellationToken);

        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task InMemoryOutboxStore_MarkDead_SetsDeadStatus(CancellationToken cancellationToken)
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        // ITM-174 修复：对齐 Inbox 测试形态——先 Lease 拿 successor 再 Mark（守卫要求租约持有者）
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        store.MarkDead(leased[0], "max retries", DateTimeOffset.UtcNow);
        await Assert.That(leased[0].Status).IsEqualTo(OutboxStatus.Dead);
        await Assert.That(leased[0].Error).IsEqualTo("max retries");
    }

    [Test]
    public async Task InMemoryOutboxStore_StaleReferenceAfterReLease_MarkIgnored(CancellationToken cancellationToken)
    {
        // ITM-174 回归：worker A 租约到期后 B 重租同一消息（successor 替换），
        // A 的旧引用 MarkProcessed/MarkDead 被 IsCurrentLeaseHolder 守卫忽略——
        // 修复前 A 的僵尸标记会覆盖 B 的活跃租约（消息在 B 处理完成前被标 Processed）
        var fakeTime = new PalDDD.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-06-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemoryOutboxStore(fakeTime);
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);

        // worker A 租约（t=0 起 2 分钟）
        var leaseA = await store.LeasePendingMessagesAsync(10, "owner-A", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var heldByA = leaseA[0];

        // 租约过期后 worker B 重租（successor 替换，A 的引用脱离列表持有者）
        fakeTime.Set(DateTimeOffset.Parse("2026-06-25T00:03:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var leaseB = await store.LeasePendingMessagesAsync(10, "owner-B", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var heldByB = leaseB[0];
        await Assert.That(ReferenceEquals(heldByA, heldByB)).IsFalse();

        // A 的僵尸标记被忽略——B 的活跃租约不被覆盖
        store.MarkProcessed(heldByA, DateTimeOffset.UtcNow);
        await Assert.That(heldByB.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(heldByB.LockedBy).IsEqualTo("owner-B");

        // B 正常标记生效
        store.MarkProcessed(heldByB, DateTimeOffset.UtcNow);
        await Assert.That(heldByB.Status).IsEqualTo(OutboxStatus.Processed);
    }

    [Test]
    public async Task InMemoryOutboxStore_IsCurrentLeaseHolder_ReferenceSemantics_ValueEqualSnapshotIgnored(CancellationToken cancellationToken)
    {
        // v74 P3（P4-S2 收口）回归网：IsCurrentLeaseHolder 的 Contains 显式传
        // ReferenceEqualityComparer——字段全等的另一引用（外部手工拷贝快照）不被视为
        // 持有者，其 Mark 被守卫忽略。锁定意图：一旦 OutboxMessage 改 record / 加值相等，
        // 值相等快照将命中守卫覆盖真持有者（ITM-174 僵尸标记守卫击穿）——本测试变红即
        // 该形态的报警器（显式引用语义对齐 InMemoryInboxStore/Idempotency/Checkpoint 三姊妹）
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var holder = leased[0]; // successor（列表当前持有者）

        // 手工拷贝快照：全部字段相等但不同引用
        var snapshot = new OutboxMessage
        {
            Id = holder.Id,
            Type = holder.Type,
            Payload = holder.Payload,
            ContentType = holder.ContentType,
            SchemaVersion = holder.SchemaVersion,
            CorrelationId = holder.CorrelationId,
            CausationId = holder.CausationId,
            TraceParent = holder.TraceParent,
            TraceState = holder.TraceState,
            CreatedAt = holder.CreatedAt,
            RetryCount = holder.RetryCount,
            Status = holder.Status,
            LockedBy = holder.LockedBy,
            LockedUntil = holder.LockedUntil,
        };
        await Assert.That(ReferenceEquals(holder, snapshot)).IsFalse();

        // 快照的 Mark 被守卫忽略——真持有者状态不被触碰
        store.MarkProcessed(snapshot, DateTimeOffset.UtcNow);
        await Assert.That(holder.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(holder.LockedBy).IsEqualTo("owner-1");
    }

    [Test]
    public async Task InMemoryOutboxStore_WithInjectedTimeProvider_LeaseExpiryIsDeterministic(CancellationToken cancellationToken)
    {
        // 注入 FakeTimeProvider 验证租约过期时序可控——与 OutboxBatchProcessor 的时间抽象对齐
        // P3 修复（十轮）：改用共享库 FakeTimeProvider（此前文件内私有同名类遮蔽共享实现）
        var fakeTime = new PalDDD.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-06-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemoryOutboxStore(fakeTime);
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);

        // t=0：租约 2 分钟，锁定到 t=2min
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        await Assert.That(leased).Count().IsEqualTo(1);

        // t=1min：租约未过期，无法重新获取
        fakeTime.Set(DateTimeOffset.Parse("2026-06-25T00:01:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var beforeExpiry = await store.GetPendingMessagesAsync(10, 10, cancellationToken);
        await Assert.That(beforeExpiry).IsEmpty();

        // t=3min：租约已过期，可重新获取
        fakeTime.Set(DateTimeOffset.Parse("2026-06-25T00:03:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var afterExpiry = await store.GetPendingMessagesAsync(10, 10, cancellationToken);
        await Assert.That(afterExpiry).Count().IsEqualTo(1);
    }

    [Test]
    public async Task InMemorySagaStateStore_GetActiveSagas_ReturnsOnlyActiveStates(CancellationToken cancellationToken)
    {
        var store = new InMemorySagaStateStore<SampleSaga>();
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Started", Status = SagaStatus.Active });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Waiting", Status = SagaStatus.AwaitingHumanDecision });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Done", Status = SagaStatus.Completed });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Compensated", Status = SagaStatus.Compensated });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "CompensationFailed", Status = SagaStatus.CompensationFailed });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Dead", Status = SagaStatus.DeadLettered });

        var active = await store.GetActiveSagasAsync(10, cancellationToken);
        // 返回 Active + AwaitingHumanDecision（三十四轮中断态超时兜底：观测查询同步纳入
        // 中断态，HITL 扫描依赖此查询）；终态（Completed/Compensated/…）被过滤
        await Assert.That(active).Count().IsEqualTo(2);
    }

    [Test]
    public async Task InMemorySagaStateStore_CancelledToken_ThrowsBeforeWork()
    {
        // 三十三轮修复回归：4 个异步方法 ct 对齐——已取消令牌在同步完成路径也须响应取消
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var store = new InMemorySagaStateStore<SampleSaga>();
        await Assert.That(async () => { await store.GetActiveSagasAsync(10, cts.Token); }).Throws<OperationCanceledException>();
        await Assert.That(async () => { await store.SaveChangesAsync(new SampleSaga { SagaId = Guid.NewGuid() }, cts.Token); }).Throws<OperationCanceledException>();
        await Assert.That(async () => { await store.LeaseActiveSagasAsync("scanner", TimeSpan.FromMinutes(1), 10, cts.Token); }).Throws<OperationCanceledException>();
        await Assert.That(async () => { await store.GetByIdAsync(Guid.NewGuid(), cts.Token); }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InMemorySagaStateStore_LeaseActiveSagas_IncludesAwaitingHumanDecision()
    {
        // 三十四轮中断态超时兜底回归：扫描集扩 AwaitingHumanDecision（配步骤 Timeout 的
        // 中断态由 SagaTimeoutProcessor 补偿）；终态（Completed 等）仍被过滤
        var store = new InMemorySagaStateStore<SampleSaga>();
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Waiting", Status = SagaStatus.AwaitingHumanDecision });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Done", Status = SagaStatus.Completed });

        var leased = await store.LeaseActiveSagasAsync("scanner", TimeSpan.FromMinutes(1), 10, CancellationToken.None);

        await Assert.That(leased.Count).IsEqualTo(1);
        await Assert.That(leased[0].Status).IsEqualTo(SagaStatus.AwaitingHumanDecision);
    }

    [Test]
    public async Task InMemorySagaStateStore_GetById_ReturnsCorrectState(CancellationToken cancellationToken)
    {
        var store = new InMemorySagaStateStore<SampleSaga>();
        var id = Guid.NewGuid();
        store.Add(new SampleSaga { SagaId = id, CurrentState = "Started" });

        var found = await store.GetByIdAsync(id, cancellationToken);
        await Assert.That(found!.CurrentState).IsEqualTo("Started");
    }

    [Test]
    public async Task InMemorySagaStateStore_LeaseActiveSagas_OnlyOneOwnerGetsActiveState(CancellationToken cancellationToken)
    {
        var store = new InMemorySagaStateStore<SampleSaga>();
        var id = Guid.NewGuid();
        store.Add(new SampleSaga { SagaId = id, CurrentState = "Started", Status = SagaStatus.Active });

        var first = await store.LeaseActiveSagasAsync("owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var second = await store.LeaseActiveSagasAsync("owner-2", TimeSpan.FromMinutes(2), 10, cancellationToken);

        await Assert.That(first).Count().IsEqualTo(1);
        var leased = first[0];
        await Assert.That(leased.SagaId).IsEqualTo(id);
        await Assert.That(leased.LeasedBy).IsEqualTo("owner-1");
        await Assert.That(leased.LeasedUntil).IsNotNull();
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task InMemorySagaStateStore_StaleLeaseHolder_SaveChangesReturnsZero(CancellationToken cancellationToken)
    {
        // v25 P3 行为族 B1 回归：租约被后继实例抢占（successor 替换）后，旧持有者的
        // SaveChangesAsync 因 Version 乐观锁不匹配返回 0——修复前原地直写共享引用且
        // 恒返回 1，"0 行 = 乐观锁冲突"契约路径在内存模式下不可达（僵尸写回无从拦截）
        var fakeTime = new PalDDD.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-06-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemorySagaStateStore<SampleSaga>(fakeTime);
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Started", Status = SagaStatus.Active });

        // worker A 租约（t=0 起 2 分钟）
        var leaseA = await store.LeaseActiveSagasAsync("owner-A", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var heldByA = leaseA[0];

        // 租约过期后 worker B 重租——successor 替换：新实例脱离 A 的旧引用
        fakeTime.Set(DateTimeOffset.Parse("2026-06-25T00:03:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var leaseB = await store.LeaseActiveSagasAsync("owner-B", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var heldByB = leaseB[0];
        await Assert.That(ReferenceEquals(heldByA, heldByB)).IsFalse();

        // 旧持有者 A 保存——Version 不匹配，返回 0（冲突路径可达）
        await Assert.That(await store.SaveChangesAsync(heldByA, cancellationToken)).IsEqualTo(0);

        // 当前持有者 B 保存正常生效（返回 1 且 Version 递增）
        await Assert.That(await store.SaveChangesAsync(heldByB, cancellationToken)).IsEqualTo(1);
        await Assert.That(heldByB.Version).IsGreaterThanOrEqualTo(heldByA.Version + 1);
    }

    [Test]
    public async Task InMemorySagaStateStore_StaleLeaseHolderRelease_DoesNotClearNewLease(CancellationToken cancellationToken)
    {
        // v25 P3 行为族 B1 回归：B 重租后 A 的"释放"（清 LeasedBy/LeasedUntil 后保存）
        // 不得清掉 B 的租约——修复前原地直写共享引用，A 直接清了 B 的活跃租约
        var fakeTime = new PalDDD.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-06-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemorySagaStateStore<SampleSaga>(fakeTime);
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Started", Status = SagaStatus.Active });

        var leaseA = await store.LeaseActiveSagasAsync("owner-A", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var heldByA = leaseA[0];

        fakeTime.Set(DateTimeOffset.Parse("2026-06-25T00:03:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var leaseB = await store.LeaseActiveSagasAsync("owner-B", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var heldByB = leaseB[0];

        // A 释放：清自己的租约字段后保存——Version 冲突返回 0，B 的租约不受影响
        heldByA.LeasedBy = null;
        heldByA.LeasedUntil = null;
        await Assert.That(await store.SaveChangesAsync(heldByA, cancellationToken)).IsEqualTo(0);

        await Assert.That(heldByB.LeasedBy).IsEqualTo("owner-B");
        await Assert.That(heldByB.LeasedUntil).IsNotNull();
    }

    [Test]
    public async Task InMemoryOutboxStore_AddMessagesAsync_NullEntry_LeavesNoPartialWrite(CancellationToken cancellationToken)
    {
        // v25 P3 行为族 B2 回归：批量添加遇 null 条目抛出时不留部分写入——
        // 修复前单循环边校验边添加，null 前的消息已进列表（调用方重试会产生重复投递）
        var store = new InMemoryOutboxStore();
        var msg1 = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        var messages = new List<OutboxMessage> { msg1, null! };

        await Assert.That(async () => await store.AddMessagesAsync(messages)).Throws<ArgumentNullException>();

        var pending = await store.GetPendingMessagesAsync(10, 10, cancellationToken);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task InMemoryOutboxStore_AddMessagesAsync_DuplicateId_ThrowsAndLeavesNoWrite(CancellationToken cancellationToken)
    {
        // v30 P3 重复 Id 防护回归：同一 OutboxMessage 实例 Add 两次（同批次重复 Id）抛
        // ArgumentException 且零写入——修复前双条目经 LeasePending 引用索引表（TryAdd
        // 保留首索引）产生错位租约，successor 覆盖首位置后第二位置残留 Pending 原始引用，
        // 下轮 Lease 再次租出（重复发布）。对齐 DB 栈 PK 约束行为
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        var messages = new List<OutboxMessage> { msg, msg };

        await Assert.That(async () => await store.AddMessagesAsync(messages)).Throws<ArgumentException>();

        var pending = await store.GetPendingMessagesAsync(10, 10, cancellationToken);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task InMemoryInboxStore_NegativeProcessingTimeout_ThrowsArgumentOutOfRange(CancellationToken cancellationToken)
    {
        // v30 P3 守卫族回归：负 processingTimeout 使租约即刻过期（now - started < negative
        // 恒 false），与数据库三实现语义分叉——必须 fail-fast（镜像 PalOrmInboxStore
        // v29 S7 / DapperInboxStore ITM-107 姊妹守卫的 InMemory 侧对齐）
        var store = new InMemoryInboxStore();

        await Assert.That(async () => await store.TryStartProcessingAsync(
            "consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(-1), cancellationToken))
            .Throws<ArgumentOutOfRangeException>();
    }

    public sealed class SampleSaga : SagaState;
}
