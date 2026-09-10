using ByteAether.Ulid;
using PalORM;
using PalORM.Sqlite;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// v74 P1 回归锁定：PalOrmOutboxStore MarkProcessed/MarkDead 的 locked_until 租约 token 谓词
/// 曾被 v72 内嵌 SQL 注释（-- 到语句尾）吞掉——同 owner 重租（仅 until 前移 = 新 token）后，
/// 旧快照终态写被放行。本测试隔离 until 腿（owner 不变），与
/// <see cref="PalOrmConcurrencyTests.Outbox_MarkProcessed_LeaseTakenOver_StaleWriteRejected"/>
/// （换 owner 腿）互补构成双腿锁定。修复前双红（mutation 实证），修复后双绿。
/// </summary>
public class OutboxStaleTokenUntilLegTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(string dbPath, CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(DbOptions.Development($"Data Source={dbPath}"), ct);
        await session.ExecuteAsync($"PRAGMA busy_timeout=5000", ct);
        return session;
    }

    [Test]
    public async Task Probe_MarkProcessed_SameOwner_NewToken_StaleWriteMustBeRejected()
    {
        var dbPath = $"probe_v74_{Guid.NewGuid():N}.db";
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }

        try
        {
            await using (var setup = await CreateSessionAsync(dbPath))
            {
                await setup.ExecuteAsync($"CREATE TABLE IF NOT EXISTS outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)", default);
                var s = new SqliteOutboxStore(setup);
                s.AddMessage(new OutboxMessage
                {
                    Id = Ulid.New(),
                    Type = "probe.event.v1",
                    Payload = System.Text.Encoding.UTF8.GetBytes("[]"),
                });
            }

            // w1 第一次租约（快照 token = (w1, T1)）
            await using var session1 = await CreateSessionAsync(dbPath);
            var store1 = new SqliteOutboxStore(session1);
            var leased = await store1.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
            await Assert.That(leased).Count().IsEqualTo(1);
            var stale = leased[0];

            // 同 owner w1 重租（仅 locked_until 前移 = 新 token；owner 不变——本探针与
            // PalOrmConcurrencyTests.Outbox_MarkProcessed_LeaseTakenOver_StaleWriteRejected
            // 的差异点：那个测试同时换了 owner，本探针隔离 until 腿）
            await using (var session2 = await CreateSessionAsync(dbPath))
            {
                var newUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                await session2.ExecuteAsync(
                    $"UPDATE outbox_messages SET locked_until = {newUntil} WHERE id = {stale.Id.ToString()}",
                    default);
            }

            // 旧快照 (w1, T1) 终态写——locked_until 谓词生效则 affected=0
            store1.MarkProcessed(stale, DateTimeOffset.UtcNow);

            var status = await session1.ScalarAsync<long>(
                $"SELECT status FROM outbox_messages WHERE id = {stale.Id.ToString()}", default);
            // 断言：不应被写成 Processed（2）。若实测 status==Processed → until 腿失效实锤
            await Assert.That(status).IsNotEqualTo((long)OutboxStatus.Processed);
            // P3 补租约列断言（镜像 PalOrmConcurrencyTests 换 owner 腿的 locked_by 断言）：
            // token 拒绝不仅"不写终态"——陈旧写不得触碰租约列，重租后的租约原样保留
            var lockedBy = await session1.ScalarAsync<string>(
                $"SELECT locked_by FROM outbox_messages WHERE id = {stale.Id.ToString()}", default);
            await Assert.That(lockedBy).IsEqualTo("w1"); // 同 owner 重租：w1 未被旧写清除
            var lockedUntil = await session1.ScalarAsync<string>(
                $"SELECT locked_until FROM outbox_messages WHERE id = {stale.Id.ToString()}", default);
            await Assert.That(lockedUntil).IsNotNull(); // 重租后的新 until 未被陈旧写清空
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }

    [Test]
    public async Task Probe_MarkDead_SameOwner_NewToken_StaleWriteMustBeRejected()
    {
        var dbPath = $"probe_v74d_{Guid.NewGuid():N}.db";
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }

        try
        {
            await using (var setup = await CreateSessionAsync(dbPath))
            {
                await setup.ExecuteAsync($"CREATE TABLE IF NOT EXISTS outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)", default);
                var s = new SqliteOutboxStore(setup);
                s.AddMessage(new OutboxMessage
                {
                    Id = Ulid.New(),
                    Type = "probe.event.v1",
                    Payload = System.Text.Encoding.UTF8.GetBytes("[]"),
                });
            }

            await using var session1 = await CreateSessionAsync(dbPath);
            var store1 = new SqliteOutboxStore(session1);
            var leased = await store1.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
            await Assert.That(leased).Count().IsEqualTo(1);
            var stale = leased[0];

            await using (var session2 = await CreateSessionAsync(dbPath))
            {
                var newUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                await session2.ExecuteAsync(
                    $"UPDATE outbox_messages SET locked_until = {newUntil} WHERE id = {stale.Id.ToString()}",
                    default);
            }

            store1.MarkDead(stale, "probe-reason", DateTimeOffset.UtcNow);

            var status = await session1.ScalarAsync<long>(
                $"SELECT status FROM outbox_messages WHERE id = {stale.Id.ToString()}", default);
            // 断言：不应被写成 Dead（3）。若实测 status==Dead → until 腿失效实锤
            await Assert.That(status).IsNotEqualTo((long)OutboxStatus.Dead);
        }
        finally
        {
            if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
        }
    }
}
