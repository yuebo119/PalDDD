using ByteAether.Ulid;
using PalORM;
using PalORM.Sqlite;
using PalDDD.PalORM.Sqlite;
using PalDDD.Idempotency;
using PalDDD.Projections;
using PalDDD.Transactions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// 真并发测试 —— 填补现有 Dapper/EFCore 共同盲区。
/// <para>
/// <b>PalORM 并发模型</b>：DataSession 不支持并发操作（AsyncLocal 门禁——同一 session 重叠 await 抛异常）。
/// 真并发测试用<b>共享文件型 SQLite + 每 worker 独立 DataSession</b>——模拟多实例部署场景。
/// </para>
/// </summary>
public class PalOrmConcurrencyTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSharedFileSessionAsync(string dbPath, CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(DbOptions.Development($"Data Source={dbPath}"), ct);
        return session;
    }

    private static async Task InitSchemaAsync(DataSession<SqliteProvider> session, CancellationToken ct = default)
    {
        await session.ExecuteAsync($"CREATE TABLE IF NOT EXISTS outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)", ct);
        await session.ExecuteAsync($"CREATE TABLE IF NOT EXISTS inbox_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TEXT NOT NULL, processing_started_at TEXT, processed_at TEXT, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT)", ct);
        await session.ExecuteAsync($"CREATE UNIQUE INDEX IF NOT EXISTS idx_inbox_unique ON inbox_messages(consumer_name, message_id)", ct);
        await session.ExecuteAsync($"CREATE TABLE IF NOT EXISTS projection_checkpoints (projection_name TEXT NOT NULL, source_name TEXT NOT NULL, position TEXT NOT NULL, status INTEGER NOT NULL, updated_at TEXT NOT NULL, lease_until TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, error TEXT, PRIMARY KEY (projection_name, source_name, position))", ct);
        await session.ExecuteAsync($"CREATE TABLE IF NOT EXISTS idempotency_records (operation_name TEXT NOT NULL, idempotency_key TEXT NOT NULL, status INTEGER NOT NULL, locked_until TEXT NOT NULL, expires_at TEXT NOT NULL, updated_at TEXT NOT NULL, response_payload TEXT, error TEXT, PRIMARY KEY (operation_name, idempotency_key))", ct);
    }

    /// <summary>
    /// Outbox LeasePending 多 worker 并发 —— 验证无重复分配。
    /// <para>100 条 Pending 消息 + 10 个 worker（每个独立 DataSession），Task.WhenAll 并发。</para>
    /// </summary>
    [Test]
    public async Task Outbox_Lease_Concurrent_NoDoubleDispatch()
    {
        var dbPath = $"concurrent_outbox_{Guid.NewGuid():N}.db";
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }

        // 主 session 建表 + 插入数据
        await using (var setupSession = await CreateSharedFileSessionAsync(dbPath))
        {
            await InitSchemaAsync(setupSession);
            var setupStore = new SqliteOutboxStore(setupSession);
            for (var i = 0; i < 100; i++)
            {
                setupStore.AddMessage(new OutboxMessage
                {
                    Id = Ulid.New(),
                    Type = $"concurrent.event.{i}",
                    Payload = System.Text.Encoding.UTF8.GetBytes("[]"),
                });
            }
        }

        // 10 个 worker 并发抢租约（每个独立 DataSession）
        var workers = Enumerable.Range(0, 10)
            .Select(async w =>
            {
                await using var session = await CreateSharedFileSessionAsync(dbPath);
                var store = new SqliteOutboxStore(session);
                return await store.LeasePendingMessagesAsync(20, $"worker-{w}", TimeSpan.FromMinutes(5), 10, default);
            })
            .ToArray();

        var results = await Task.WhenAll(workers);
        var allLeased = results.SelectMany(r => r).ToList();

        var distinctCount = allLeased.DistinctBy(m => m.Id).Count();
        await Assert.That(distinctCount).IsEqualTo(allLeased.Count);
        await Assert.That(allLeased.Count).IsEqualTo(100);
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }
    }

    /// <summary>Inbox 同一 consumer 并发 —— 验证只抢一次。</summary>
    [Test]
    public async Task Inbox_SameConsumer_Concurrent_OnlyOnce()
    {
        var dbPath = $"concurrent_inbox_{Guid.NewGuid():N}.db";
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }

        await using (var setupSession = await CreateSharedFileSessionAsync(dbPath))
            await InitSchemaAsync(setupSession);

        var now = DateTimeOffset.UtcNow;
        var workers = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                await using var session = await CreateSharedFileSessionAsync(dbPath);
                var store = new SqliteInboxStore(session);
                return await store.TryStartProcessingAsync("consumer-same", "shared-msg", now, TimeSpan.FromMinutes(5), default);
            })
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);
        await Assert.That(successCount).IsEqualTo(1);
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }
    }

    /// <summary>Projection TryStart 并发 —— 验证同一检查点只被一个 worker 抢到。</summary>
    [Test]
    public async Task Projection_TryStart_Concurrent_SingleAcquisition()
    {
        var dbPath = $"concurrent_proj_{Guid.NewGuid():N}.db";
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }

        await using (var setupSession = await CreateSharedFileSessionAsync(dbPath))
            await InitSchemaAsync(setupSession);

        var now = DateTimeOffset.UtcNow;
        var workers = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                await using var session = await CreateSharedFileSessionAsync(dbPath);
                var store = new SqliteProjectionCheckpointStore(session);
                return await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromMinutes(5), default);
            })
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);
        await Assert.That(successCount).IsEqualTo(1);

        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }
    }

    /// <summary>Idempotency TryStart 并发 —— 验证同一幂等键只被一个 worker 抢到。</summary>
    [Test]
    public async Task Idempotency_TryStart_Concurrent_SingleAcquisition()
    {
        var dbPath = $"concurrent_idem_{Guid.NewGuid():N}.db";
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }

        await using (var setupSession = await CreateSharedFileSessionAsync(dbPath))
            await InitSchemaAsync(setupSession);

        var now = DateTimeOffset.UtcNow;
        var workers = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                await using var session = await CreateSharedFileSessionAsync(dbPath);
                var store = new SqliteIdempotencyStore(session);
                return await store.TryStartAsync("op-1", "shared-key", now, IdempotencyPolicy.Default, default);
            })
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);
        await Assert.That(successCount).IsEqualTo(1);

        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { /* SQLite 连接池延迟释放 */ }
    }
}
