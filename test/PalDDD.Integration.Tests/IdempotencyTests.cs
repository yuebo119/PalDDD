namespace PalDDD.Integration.Tests;

using PalDDD.Idempotency;
using PalDDD.Testing;
using System.Diagnostics;
using System.Text;

// ITM-281（R44）：指标断言测试与同类 emit 同 instrument 的非指标测试互斥——
// RecordingMeterListener 接收进程级广播，类内并行会互染计数（TST-109 同款根治）
[TUnit.Core.NotInParallel("idempotency-metrics")]
public sealed class IdempotencyTests
{
    [Test]
    public async Task ExecuteAsync_EmitsIdempotencyActivityWhenExecuted(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Idempotency Execute").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a =>
            string.Equals(a.GetTagItem("pal.idempotency.operation") as string, "CreateOrder", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("pal.idempotency.result") as string, "executed", StringComparison.Ordinal));
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        // ITM-229: pal.idempotency.key removed (high cardinality) — assert operation instead
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        await Assert.That(activity.GetTagItem("pal.idempotency.result")).IsEqualTo("executed");
    }

    [Test]
    public async Task ExecuteAsync_EmitsIdempotencyActivityWhenCached(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());
        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-456"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var matches = listener.StoppedActivities.Where(a =>
            a.OperationName == "Idempotency Execute" &&
            string.Equals(a.GetTagItem("pal.idempotency.result") as string, "cached", StringComparison.Ordinal)).ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches[0];
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Cached);
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        // ITM-229: pal.idempotency.key removed (high cardinality) — assert operation instead
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
    }

    [Test]
    public async Task ExecuteAsync_ReturnsCachedResultForCompletedCommand(CancellationToken cancellationToken)
    {
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        var first = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-123");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var second = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-456");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(first.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        await Assert.That(second.Status).IsEqualTo(IdempotencyExecutionStatus.Cached);
        await Assert.That(second.Result).IsEqualTo("order-123");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_EmitsIdempotencyActivityWhenSkipped(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new SkippingIdempotencyStore());

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Idempotency Execute").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a =>
            string.Equals(a.GetTagItem("pal.idempotency.operation") as string, "CreateOrder", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("pal.idempotency.result") as string, "skipped", StringComparison.Ordinal));
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Skipped);
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        // ITM-229: pal.idempotency.key removed (high cardinality) — assert operation instead
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        await Assert.That(activity.GetTagItem("pal.idempotency.result")).IsEqualTo("skipped");
    }

    [Test]
    public async Task ExecuteAsync_RecordsExecutedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.executed");
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        // ITM-281（R44）：Contains(1) 弱断言 + 进程级 Meter 广播互染下恒过——改精确计数
        await Assert.That(listener.Measurements.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_RecordsCachedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.cached");
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());
        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-456"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        // ITM-281（R44）：Contains(1) 弱断言 + 进程级 Meter 广播互染下恒过——改精确计数
        await Assert.That(listener.Measurements.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_RecordsSkippedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.skipped");
        var processor = new IdempotencyProcessor(new SkippingIdempotencyStore());

        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        // ITM-281（R44）：Contains(1) 弱断言 + 进程级 Meter 广播互染下恒过——改精确计数
        await Assert.That(listener.Measurements.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_RecordsFailedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.failed");
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ => throw new InvalidOperationException("handler failed"),
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        // ITM-281（R44）：Contains(1) 弱断言 + 进程级 Meter 广播互染下恒过——改精确计数
        await Assert.That(listener.Measurements.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenHandlerFails_MarksActivityAsError(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        var exception = await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ => throw new InvalidOperationException("handler failed"),
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Idempotency Execute").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a => a.Status == ActivityStatusCode.Error);
        await Assert.That(exception!.Message).IsEqualTo("handler failed");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).Contains("handler failed");
        await Assert.That(activity.GetTagItem("pal.idempotency.result")).IsEqualTo("failed");
    }

    [Test]
    public async Task ExecuteAsync_WhenHandlerCancels_PreservesProcessingLease(CancellationToken cancellationToken)
    {
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ =>
                {
                    calls++;
                    throw new OperationCanceledException("handler canceled");
                },
                Serialize,
                Deserialize,
                policy: new IdempotencyPolicy
                {
                    ProcessingTimeout = TimeSpan.FromMinutes(5),
                    Retention = TimeSpan.FromHours(1)
                },
                cancellationToken: cancellationToken)).Throws<OperationCanceledException>();

        var retry = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-123");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(retry.Status).IsEqualTo(IdempotencyExecutionStatus.Skipped);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_RetriesFailedExecution(CancellationToken cancellationToken)
    {
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("handler failed");
                },
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        var retry = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-123");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(retry.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        await Assert.That(retry.Result).IsEqualTo("order-123");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_WhenSerializeResultFails_PropagatesAndKeepsRecordProcessing(CancellationToken cancellationToken)
    {
        // F1 回归（audit-probe 2026-08-23）：serializeResult 抛异常必须传播——原实现作为
        // MarkCompletedAsync 实参求值落入 ITM-191 catch 被静默按 Executed 吞掉：序列化失败
        // 是持久性缺陷（每次必然再抛），记录残留 Processing、租约过期后 handler 重放、
        // 副作用无限重复执行且调用方无感知。修复后：传播异常 + 不标记 Failed
        // （Failed 可立即重试 → handler 重放）+ 保持 Processing（租约窗口内挡重试）。
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ =>
                {
                    calls++;
                    return ValueTask.FromResult("order-123");
                },
                _ => throw new InvalidOperationException("serialize boom"),
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        // 副作用已发生：记录保持 Processing，不得被标记 Failed
        var record = await store.GetAsync("CreateOrder", "cmd-1", DateTimeOffset.UtcNow, cancellationToken);
        await Assert.That(record!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        // 租约窗口内同 key 重试被挡（at-least-once 状态待确认）——handler 不重放
        var retry = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("retried");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(retry.Status).IsEqualTo(IdempotencyExecutionStatus.Skipped);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenHandlerThrowsBlankMessage_NormalizesAndMarksFailed(CancellationToken cancellationToken)
    {
        // F2 回归（audit-probe 2026-08-23）：handler 抛空白 Message 时失败原因须归一化再
        // 入库——否则三个 Store 的 MarkFailedAsync 入口 ThrowIfNullOrWhiteSpace 抛
        // ArgumentException 被吞（挂 Data），记录残留 Processing、租约过期后 handler
        // 重放、副作用二次执行（ITM-175 只堵了长度没堵空白）。
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-2",
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException(" ");
                },
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        // 归一化后 MarkFailed 成功：记录标 Failed、失败原因落固定文案
        var record = await store.GetAsync("CreateOrder", "cmd-2", DateTimeOffset.UtcNow, cancellationToken);
        await Assert.That(record!.Status).IsEqualTo(IdempotencyRecordStatus.Failed);
        await Assert.That(record!.Error).IsEqualTo("(no message)");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task InMemoryStore_TryStartAsync_PreemptsZombieAndOldHolderMarkIgnored()
    {
        // P3 回归（二十一轮）：InMemory 幂等存储的僵尸/失败抢占路径现返回新实例（引用隔离对齐
        // InMemoryProjectionCheckpointStore 十七轮语义）——被抢占旧实例的 MarkCompletedAsync
        // 必须被 ReferenceEquals 守卫静默忽略（镜像 ProjectionTests 同名形态）
        var store = new InMemoryIdempotencyStore();
        var now = DateTimeOffset.Parse("2026-08-16T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var policy = new IdempotencyPolicy
        {
            ProcessingTimeout = TimeSpan.FromMinutes(5),
            Retention = TimeSpan.FromHours(1)
        };

        // 行为断言（弱断言棘轮约束：新测试禁 IsNotNull 守卫式）——状态即非空证明
        var first = await store.TryStartAsync("CreateOrder", "cmd-1", now, policy);
        await Assert.That(first!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        // 租约未过期——同键不可重入
        var stillAlive = await store.TryStartAsync("CreateOrder", "cmd-1", now.AddMinutes(1), policy);
        await Assert.That(stillAlive).IsNull();

        // 租约过期——僵尸被抢占，返回新实例（非旧引用复用）
        var preempted = await store.TryStartAsync("CreateOrder", "cmd-1", now.AddMinutes(6), policy);
        await Assert.That(preempted!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
        await Assert.That(preempted).IsNotSameReferenceAs(first);

        // 被抢占旧实例的 Mark 必须被守卫忽略——新持有者仍为 Processing
        await store.MarkCompletedAsync(first!, new byte[] { 1, 2, 3 }, now.AddMinutes(7));
        var current = await store.GetAsync("CreateOrder", "cmd-1", now.AddMinutes(7));
        await Assert.That(current!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    private static ReadOnlyMemory<byte> Serialize(string value)
        => Encoding.UTF8.GetBytes(value);

    private static string Deserialize(ReadOnlyMemory<byte> payload)
        => Encoding.UTF8.GetString(payload.Span);

    [Test]
    public async Task ExecuteAsync_MarkCompletedFails_ReturnsExecutedWithoutMarkingFailed(CancellationToken cancellationToken)
    {
        // ITM-191 回归（三十轮）：handler 成功但 MarkCompleted 失败（DB 故障）——
        // 不得降级为 Failed（Failed 可重入 → handler 重放 → 副作用二次执行）。
        // 对齐 InboxProcessor ITM-180（镜像修复）。修复前 MarkCompleted 异常落入
        // 通用 catch → MarkFailedAsync 把已成功记录标 Failed。
        // TST-203/306b：装置改实例字段（static 可变字段污染并行测试）——每测试 new，
        // 无需手动重置。
        using var listener = new RecordingActivityListener();
        var store = new ThrowingOnCompleteStore();
        var processor = new IdempotencyProcessor(store);

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-fail",
            _ => ValueTask.FromResult("order-ok"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        // 副作用已发生：按 Executed 返回——修复前落入 catch 被降级 Failed
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        // 关键：MarkFailedAsync 必须零调用（不得把已成功记录标 Failed）
        await Assert.That(store.MarkFailedCalls).IsEqualTo(0);
        // 记录仍维持 TryStart 的 Processing 状态（未被降级）——直接断言状态（空引用访问
        // 即失败，无需 IsNotNull 守卫式弱断言，满足断言棘轮约束）
        await Assert.That(store.LastRecord!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        // 定位活动 event 标记 pending-confirmation 语义（可观测性）——Any 行为断言
        await Assert.That(listener.StoppedActivities.Any(a =>
            a.Events.Any(e => e.Name == "idempotency.completed-pending-confirmation"))).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_MarkCompletedThrowsOce_ReturnsExecutedInsteadOfEscaping(CancellationToken cancellationToken)
    {
        // ITM-653 回归（v66 镜像 InboxProcessor）：MarkCompletedAsync 以 CancellationToken.None
        // 调用，其抛 OCE 属存储异常形态而非请求级取消传播——原 `when (not OCE)` 过滤让 OCE
        // 逃逸给调用方，而 handler 实际已成功（副作用已发生），调用方按取消处理会触发重试
        // 重放路径。移除过滤后按 Executed 返回（completed-pending-confirmation 语义，
        // 与 MarkCompletedFails 抛 InvalidOperationException 的既有路径同归宿）。
        var store = new ThrowingOnCompleteStore
        {
            // None token——存储侧抛 OCE 的异常形态（处理器调用点恒传 CancellationToken.None）
            CompleteException = new OperationCanceledException()
        };
        var processor = new IdempotencyProcessor(store);

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-oce",
            _ => ValueTask.FromResult("order-ok"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        // 副作用已发生：按 Executed 返回而非 OCE 逃逸（修复前该断言不可达——OCE 直接抛出）
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        // 关键：MarkFailedAsync 必须零调用（不得把已成功记录标 Failed）
        await Assert.That(store.MarkFailedCalls).IsEqualTo(0);
    }

    /// <summary>MarkCompleted 抛 DB 故障、记录 TryStart 对象与 MarkFailed 调用数的存储 —— ITM-191 测试装置。
    /// TST-203/306b：状态用实例字段（static 可变字段会跨测试串扰并行执行），每测试 new 即隔离。</summary>
    private sealed class ThrowingOnCompleteStore : IIdempotencyStore
    {
        public IdempotencyRecord? LastRecord;
        public int MarkFailedCalls;
        // ITM-653：MarkCompleted 的抛出形态可注入——默认 InvalidOperationException 保持
        // ITM-191 既有行为；姊妹回归测试注入 OCE（None token 语义）锁定 v66 过滤移除
        public Exception CompleteException { get; init; } =
            new InvalidOperationException("simulated DB failure on complete");

        public ValueTask<IdempotencyRecord?> GetAsync(string operationName, string key, DateTimeOffset now, CancellationToken ct = default)
            => ValueTask.FromResult<IdempotencyRecord?>(null);

        public ValueTask<IdempotencyRecord?> TryStartAsync(string operationName, string key, DateTimeOffset now,
            IdempotencyPolicy policy, CancellationToken ct = default)
        {
            var nowUtc = now.ToUniversalTime();
            LastRecord = new IdempotencyRecord(operationName, key, IdempotencyRecordStatus.Processing,
                nowUtc.AddMinutes(5), nowUtc.AddMinutes(30), nowUtc);
            return ValueTask.FromResult<IdempotencyRecord?>(LastRecord);
        }

        public ValueTask MarkCompletedAsync(IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload,
            DateTimeOffset completedAt, CancellationToken ct = default)
            => throw CompleteException;

        public ValueTask MarkFailedAsync(IdempotencyRecord record, string failureReason,
            DateTimeOffset failedAt, CancellationToken ct = default)
        {
            MarkFailedCalls++;
            record.MarkFailed(failureReason, failedAt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SkippingIdempotencyStore : IIdempotencyStore
    {
        public ValueTask<IdempotencyRecord?> GetAsync(
            string operationName,
            string key,
            DateTimeOffset now,
            CancellationToken ct = default)
            => ValueTask.FromResult<IdempotencyRecord?>(null);

        public ValueTask<IdempotencyRecord?> TryStartAsync(
            string operationName,
            string key,
            DateTimeOffset now,
            IdempotencyPolicy policy,
            CancellationToken ct = default)
            => ValueTask.FromResult<IdempotencyRecord?>(null);

        public ValueTask MarkCompletedAsync(
            IdempotencyRecord record,
            ReadOnlyMemory<byte> responsePayload,
            DateTimeOffset completedAt,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Skipped executions must not complete a record.");

        public ValueTask MarkFailedAsync(
            IdempotencyRecord record,
            string failureReason,
            DateTimeOffset failedAt,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Skipped executions must not fail a record.");
    }
}
