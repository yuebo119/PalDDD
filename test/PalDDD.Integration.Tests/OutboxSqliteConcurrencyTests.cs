using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PalDDD.Transactions;
using System.Data.Common;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

public sealed class OutboxSqliteConcurrencyTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestSqliteOutboxDbContext> _options = null!;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(cancellationToken);

        _options = new DbContextOptionsBuilder<TestSqliteOutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = new TestSqliteOutboxDbContext(_options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task LeasePending_SequentialWorkers_SecondGetsNoMessage()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var firstCtx = new TestSqliteOutboxDbContext(_options);
        var firstLeased = await ((IPalOutboxStore)firstCtx).LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await using var secondCtx = new TestSqliteOutboxDbContext(_options);
        var secondLeased = await ((IPalOutboxStore)secondCtx).LeasePendingMessagesAsync(
            10, "worker-2", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

        await Assert.That(firstLeased).Count().IsEqualTo(1);
        await Assert.That(firstLeased[0].Id).IsEqualTo(messageId);
        await Assert.That(secondLeased).IsEmpty();
    }

    // TST-402 改名：原名 MarkProcessed_TransactionCommits_PersistsAcrossContexts 名不副实
    // （本测试不涉及显式事务提交语义，聚焦 MarkProcessed 跨 DbContext 持久化）
    [Test]
    public async Task MarkProcessed_PersistsAcrossContexts(CancellationToken cancellationToken)
    {
        // 历史（ITM-261 已修复，原"见 Skip 说明"勘正）——
        // 租约改直接种子建立；本测试聚焦 MarkProcessed 持久化语义（其 fencing 用 == 相等比较，可翻译）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var processorCtx = new TestSqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)processorCtx;
        var msg = await processorCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        store.MarkProcessed(msg, DateTimeOffset.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var loaded = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Processed);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
    }

    [Test]
    public async Task ReleaseForRetry_IncrementsRetryCountAndClearsLease(CancellationToken cancellationToken)
    {
        // 租约直接种子建立（ITM-261 已修复；保留此形态聚焦终态写语义——见类尾注释）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)ctx;
        var msg = await ctx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        store.ReleaseForRetry(msg, "broker timeout", DateTimeOffset.UtcNow.AddSeconds(30));
        await store.SaveChangesAsync(cancellationToken);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var loaded = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.RetryCount).IsEqualTo(1);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
        await Assert.That(loaded.Error).IsEqualTo("broker timeout");
    }

    [Test]
    public async Task MarkProcessed_LeaseTakenOverByReLease_StaleWriteRejected(CancellationToken cancellationToken)
    {
        // 三十四轮 ITM-210 租约 token 回归：worker-1 持租后租约被重租（同 owner 复用场景，
        // locked_until 更晚 = 新 token），旧 worker 的终态写必须影响 0 行——原 owner 守卫的
        // "LockedBy 相同即放行"分支会让旧写覆盖新租约状态
        // 租约直接种子建立（ITM-261 已修复；保留此形态聚焦终态写语义——见类尾注释）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var ctx1 = new TestSqliteOutboxDbContext(_options);
        var store1 = (IPalOutboxStore)ctx1;
        var staleMsg = await ctx1.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        // 模拟同 owner 重租（token 单调变化：locked_until 更晚）
        await using var ctx2 = new TestSqliteOutboxDbContext(_options);
        var row = await ctx2.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken);
        row.LockedUntil = row.LockedUntil!.Value.AddMinutes(5);
        await ctx2.SaveChangesAsync(cancellationToken);

        // 旧 worker 终态写——token 失配应影响 0 行
        store1.MarkProcessed(staleMsg, DateTimeOffset.UtcNow);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var final = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(final.Status).IsNotEqualTo(OutboxStatus.Processed);
        await Assert.That(final.LockedBy).IsEqualTo("worker-1"); // 新租约未被旧写清除
    }

    [Test]
    public async Task MarkProcessed_LeaseReleased_StaleWriteRejected(CancellationToken cancellationToken)
    {
        // 三十四轮 ITM-210 租约 token 回归：租约被释放（locked_by/until 置 NULL，
        // 如他路径 RequeueDead/ReleaseForRetry），旧 worker 的终态写必须被拒——
        // 原 "LockedBy IS NULL OR ..." 守卫的 NULL 放行分支正是 fencing 缺口
        // 租约直接种子建立（ITM-261 已修复；保留此形态聚焦终态写语义——见类尾注释）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var ctx1 = new TestSqliteOutboxDbContext(_options);
        var store1 = (IPalOutboxStore)ctx1;
        var staleMsg = await ctx1.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        // 模拟租约释放（行回到未租状态）
        await using var ctx2 = new TestSqliteOutboxDbContext(_options);
        var row = await ctx2.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken);
        row.LockedBy = null;
        row.LockedUntil = null;
        await ctx2.SaveChangesAsync(cancellationToken);

        // 旧 worker 终态写——持租 token 非空但行未租，必须影响 0 行
        store1.MarkProcessed(staleMsg, DateTimeOffset.UtcNow);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var final = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(final.Status).IsEqualTo(OutboxStatus.Pending);
    }

    // ── 2026-09-19 批量化（decision-2026-09-17 shape 2）回归三联 ──

    /// <summary>批量分割：4 条消息两 worker 各租 2——交集为空、合并覆盖全部
    /// （单语句资格谓词子查询重估的互斥语义，对齐 Dapper/PalORM 同款测试）。</summary>
    [Test]
    public async Task LeasePending_BatchSplitAcrossWorkers_NoIntersection()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => PalUlid.New()).ToList();
        foreach (var id in ids)
            await SeedMessageAsync(id, "orders.created.v1");

        await using var firstCtx = new TestSqliteOutboxDbContext(_options);
        var firstLeased = await ((IPalOutboxStore)firstCtx).LeasePendingMessagesAsync(
            2, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await using var secondCtx = new TestSqliteOutboxDbContext(_options);
        var secondLeased = await ((IPalOutboxStore)secondCtx).LeasePendingMessagesAsync(
            2, "worker-2", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

        await Assert.That(firstLeased).Count().IsEqualTo(2);
        await Assert.That(secondLeased).Count().IsEqualTo(2);
        var firstIds = firstLeased.Select(m => m.Id).ToHashSet();
        await Assert.That(secondLeased.All(m => !firstIds.Contains(m.Id))).IsTrue();
        var union = firstLeased.Select(m => m.Id).Concat(secondLeased.Select(m => m.Id)).ToHashSet();
        await Assert.That(union.SetEquals(ids)).IsTrue();
    }

    /// <summary>过期租约回收：LockedUntil 已过期的行对新一轮租约可见（谓词
    /// <c>LockedUntil IS NULL OR LockedUntil &lt;= now</c> 的回收路径）。</summary>
    [Test]
    public async Task LeasePending_ExpiredLease_RecoversMessage()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-dead", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var leased = await ((IPalOutboxStore)ctx).LeasePendingMessagesAsync(
            10, "worker-new", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

        await Assert.That(leased).Count().IsEqualTo(1);
        await Assert.That(leased[0].Id).IsEqualTo(messageId);
        await Assert.That(leased[0].LockedBy).IsEqualTo("worker-new"); // 守卫回读带出租约标识
    }

    /// <summary>UTC 前提锁定：lease 写入的 LockedUntil 存储文本偏移恒 +00:00——
    /// 时间列文本序比较等于时间序的前提（类头 remarks 的 UTC 前提，机器本地时区
    /// 为 +08:00 仍须成立，因时间源恒 GetUtcNow）。</summary>
    [Test]
    public async Task LeasePending_LockedUntilStoredAsUtc_TextOrderSound()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var leased = await ((IPalOutboxStore)ctx).LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await Assert.That(leased).Count().IsEqualTo(1);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT LockedUntil FROM OutboxMessages WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        var storedText = (string)cmd.ExecuteScalar()!;
        await Assert.That(storedText.EndsWith("+00:00", StringComparison.Ordinal)).IsTrue();
    }

    // ── 2026-09-19 GetPending 谓词下推（decision-2026-09-19）前置表征——
    // 反方发现③：QueryEligibleAsync 的翻页/时间过滤语义此前无 SQLite-EF 生产类
    // 行为测试（OutboxEfCoreTests 走 InMemory 变体）。以下三用例锁定下推前语义，
    // 下推后须双态全绿（排序键 Id→CreatedAt 在同进程 ULID 单调下同序）。

    /// <summary>到期取/未来重试不取/活跃租约不取——资格谓词的时间语义。</summary>
    [Test]
    public async Task GetPending_ReturnsOnlyEligibleMessages()
    {
        var dueId = PalUlid.New();
        var futureRetryId = PalUlid.New();
        var activeLeaseId = PalUlid.New();
        await SeedMessageAsync(dueId, "orders.due");                     // 合格：无租约无重试
        await SeedMessageAsync(futureRetryId, "orders.future-retry");
        await SeedMessageAsync(activeLeaseId, "orders.active-lease",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(5));

        // futureRetry 行设未来重试时间 → 不合格
        await using (var ctx = new TestSqliteOutboxDbContext(_options))
        {
            var row = await ctx.OutboxMessages.SingleAsync(m => m.Id == futureRetryId);
            row.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5);
            await ctx.SaveChangesAsync();
        }

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var pending = await ((IPalOutboxStore)readerCtx).GetPendingMessagesAsync(10, 5, CancellationToken.None);

        var ids = pending.Select(m => m.Id).ToHashSet();
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(ids.Contains(dueId)).IsTrue();
        await Assert.That(ids.Contains(futureRetryId)).IsFalse();
        await Assert.That(ids.Contains(activeLeaseId)).IsFalse();
    }

    /// <summary>batch 上限：3 条合格只返回前 2。</summary>
    [Test]
    public async Task GetPending_RespectsBatchLimit()
    {
        for (var i = 0; i < 3; i++)
            await SeedMessageAsync(PalUlid.New(), "orders.batch");

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var pending = await ((IPalOutboxStore)ctx).GetPendingMessagesAsync(2, 5, CancellationToken.None);

        await Assert.That(pending).Count().IsEqualTo(2);
    }

    /// <summary>排序稳定：返回按 Id 升序（现状 ULID 创建序；下推后 CreatedAt 序，
    /// 同进程两序一致——双态绿即等价性的运行时验证）。</summary>
    [Test]
    public async Task GetPending_OrdersByIdAscending_Currently()
    {
        var ids = new List<ByteAether.Ulid.Ulid>();
        for (var i = 0; i < 4; i++)
        {
            var id = PalUlid.New();
            ids.Add(id);
            await SeedMessageAsync(id, "orders.order");
        }

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var pending = await ((IPalOutboxStore)ctx).GetPendingMessagesAsync(10, 5, CancellationToken.None);

        var returned = pending.Select(m => m.Id).ToList();
        await Assert.That(returned).Count().IsEqualTo(4);
        await Assert.That(returned.OrderBy(i => i, Comparer<ByteAether.Ulid.Ulid>.Default)
            .SequenceEqual(returned)).IsTrue();
    }

    /// <summary>SQL 次数锁定：下推后 GetPending 恒发 1 条命令（decision-2026-09-19
    /// 伸缩性验收的结构性形态——原 QueryEligibleAsync 翻页形态为 O(表/batchSize) 条；
    /// 防回归到逐页物化）。</summary>
    [Test]
    public async Task GetPending_SingleSqlCommand_RegardlessOfFutureRows()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            var counter = new CommandCountingInterceptor();
            var options = new DbContextOptionsBuilder<TestSqliteOutboxDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(counter)
                .Options;
            await using (var db = new TestSqliteOutboxDbContext(options))
                await db.Database.EnsureCreatedAsync();

            // 全未来重试的表（稳态退避形态）：原翻页形态会逐页扫全表（每页 1 条命令）
            await using (var seed = new TestSqliteOutboxDbContext(
                new DbContextOptionsBuilder<TestSqliteOutboxDbContext>().UseSqlite(connection).Options))
            {
                for (var i = 0; i < 300; i++)
                {
                    seed.OutboxMessages.Add(new OutboxMessage
                    {
                        Id = PalUlid.New(),
                        Type = "orders.future",
                        Payload = [1],
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        Status = OutboxStatus.Pending,
                        NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1),
                    });
                }
                await seed.SaveChangesAsync();
            }

            counter.Reset();
            await using (var ctx = new TestSqliteOutboxDbContext(options))
            {
                var pending = await ((IPalOutboxStore)ctx).GetPendingMessagesAsync(100, 5, CancellationToken.None);
                await Assert.That(pending).IsEmpty();
            }
            await Assert.That(counter.ExecutedCommands).IsEqualTo(1);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>批中取消：预取消令牌使 Lease 抛取消异常且零行被租——单语句原子性下
    /// 不存在"部分租约"中间态（decision-2026-09-17 §2.5-2「批中取消不重复不丢失」
    /// 在批量化形态下的正确性锁定：语句未执行或原子回滚，二选一无中间态）。</summary>
    [Test]
    public async Task LeasePending_CanceledBeforeExecution_NoPartialLease()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.cancel-probe");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        await Assert.That(async () =>
            await ((IPalOutboxStore)ctx).LeasePendingMessagesAsync(
                10, "worker-1", TimeSpan.FromMinutes(2), 5, cts.Token))
            .Throws<OperationCanceledException>();

        await using var reader = new TestSqliteOutboxDbContext(_options);
        var row = await reader.OutboxMessages.SingleAsync(m => m.Id == messageId);
        await Assert.That(row.LockedBy).IsNull();
        await Assert.That(row.Status).IsEqualTo(OutboxStatus.Pending); // 未租未丢，下 tick 可再租
    }

    /// <summary>SQLite override 自带守卫专测（第五十二轮评审补——InMemory 侧
    /// OutboxEfCoreTests 测的是基类守卫，override 的守卫随 2026-09-19 批量化从
    /// QueryEligibleAsync 迁入后无专测，ITM-659 覆盖不得因迁移丢失）。</summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task LeasePending_NonPositiveBatchOrMaxRetry_Throws(int batchSize)
    {
        await using var ctx = new TestSqliteOutboxDbContext(_options);
        await Assert.That(async () =>
            await ((IPalOutboxStore)ctx).LeasePendingMessagesAsync(
                batchSize, "w", TimeSpan.FromMinutes(2), 5, CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () =>
            await ((IPalOutboxStore)ctx).GetPendingMessagesAsync(
                10, batchSize, CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>种子一条 Pending 消息；可选预置租约字段（终态写测试聚焦语义的直建租约形态——见类尾 ITM-261 勘正注释）。</summary>
    private async ValueTask SeedMessageAsync(
        PalUlid id,
        string type,
        string? lockedBy = null,
        DateTimeOffset? lockedUntil = null)
    {
        await using var ctx = new TestSqliteOutboxDbContext(_options);
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = type,
            Payload = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = OutboxStatus.Pending,
            LockedBy = lockedBy,
            LockedUntil = lockedUntil
        });
        await ctx.SaveChangesAsync(CancellationToken.None);
    }

    // ITM-252（F10/F11 修复）：此处曾本地重写 LeasePendingMessagesAsync（时间过滤移到
    // ToListAsync 之后的内存过滤）——5 个测试（含 3 个 ITM-210 fencing 回归）的 Lease 端
    // 测的是重写版，生产 EF Lease 零覆盖。重写已删除：本类仅继承生产抽象类
    // SqliteOutboxDbContext，租约/终态写全部走生产实现路径，时钟用生产默认 TimeProvider.System。
    //
    // ITM-261（R40 主线程修复，历史勘正——原"缺陷固化 Skip 等 src 修复"描述已过时）：
    // 删除重写后曾暴露生产 GetPending/Lease 的 "DateTimeOffset <= now" 在 EF Core 11 preview7
    // SQLite provider 下不可翻译（== 可译、<= 与 ORDER BY 均抛）。src 侧已改为分页物化 + 内存
    // 时间过滤 + OrderBy(Id)（ULID 字典序=创建序）——Lease 互斥测试已恢复直调生产路径（无 Skip）；
    // MarkProcessed/ReleaseForRetry/fencing 回归保留直接种子租约字段形态（终态写仍走生产路径）。
    /// <summary>ITM-109 同 tick 回读（姊妹栈已接受语义的运行时锁定，decision-2026-09-17 §2.5-2）：
    /// 冻结时钟下同 owner 连续两次 Lease 得相同 until——第二次的 (LockedBy, LockedUntil)
    /// 等值守卫回读把第一批一并带回（守卫不带批次标识，Dapper/PalORM 同款接受形态——
    /// 调用方不得假设"第二次返回集 = 第二次新租集"）。锁定此行为防止未来无声变更契约。</summary>
    [Test]
    public async Task LeasePending_SameTickTwoBatches_SecondReadbackIncludesFirstBatch()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            var frozen = DateTimeOffset.UtcNow;
            var options = new DbContextOptionsBuilder<FrozenClockOutboxDbContext>()
                .UseSqlite(connection).Options;
            await using (var db = new FrozenClockOutboxDbContext(options) { FrozenUtc = frozen })
                await db.Database.EnsureCreatedAsync();

            var ids = Enumerable.Range(0, 4).Select(_ => PalUlid.New()).ToList();
            await using (var seed = new TestSqliteOutboxDbContext(
                new DbContextOptionsBuilder<TestSqliteOutboxDbContext>().UseSqlite(connection).Options))
            {
                foreach (var id in ids)
                    seed.OutboxMessages.Add(new OutboxMessage
                    {
                        Id = id, Type = "orders.same-tick", Payload = [1],
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5), Status = OutboxStatus.Pending,
                    });
                await seed.SaveChangesAsync();
            }

            await using var first = new FrozenClockOutboxDbContext(options) { FrozenUtc = frozen };
            var firstLeased = await ((IPalOutboxStore)first).LeasePendingMessagesAsync(
                2, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
            await Assert.That(firstLeased).Count().IsEqualTo(2);

            // 同 tick 第二次：谓词租到剩余 2 条（UPDATE 影响新行），但回读按同 until
            // 等值命中全部 4 条——第一批对第二次可见（ITM-109 接受语义）
            await using var second = new FrozenClockOutboxDbContext(options) { FrozenUtc = frozen };
            var secondLeased = await ((IPalOutboxStore)second).LeasePendingMessagesAsync(
                2, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

            await Assert.That(secondLeased).Count().IsEqualTo(4);
            var firstIds = firstLeased.Select(m => m.Id).ToHashSet();
            await Assert.That(secondLeased.Select(m => m.Id).ToHashSet().SetEquals(ids)).IsTrue();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>SQL 命令计数拦截器（GetPending_SingleSqlCommand 的结构性验收用；
    /// 只计数读路径——ToListAsync 走 ReaderExecutingAsync，同步重载不会被异步路径调用）。</summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _count;
        public int ExecutedCommands => _count;
        public void Reset() => _count = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _count++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>冻结时钟 DbContext（ITM-109 同 tick 用例——override 生产虚方法
    /// GetUtcNow 构造确定性同 until，无需注入 TimeProvider 基建）。</summary>
    private sealed class FrozenClockOutboxDbContext(DbContextOptions<FrozenClockOutboxDbContext> options)
        : SqliteOutboxDbContext(options)
    {
        public DateTimeOffset FrozenUtc { get; init; }
        protected override DateTimeOffset GetUtcNow() => FrozenUtc;
    }

    private sealed class TestSqliteOutboxDbContext(DbContextOptions<TestSqliteOutboxDbContext> options)
        : SqliteOutboxDbContext(options);
}
