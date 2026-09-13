using Dapper;
#pragma warning disable DAP005 // 测试项目不需要 Dapper.AOT 编译期拦截
using Microsoft.Data.Sqlite;
using PalDDD.Core;
using PalDDD.Dapper;
using PalDDD.Idempotency;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 💾 DapperIdempotencyStore 行为测试（ITM-667 缺口清偿）
// ═══════════════════════════════════════════════════════════════
// Dapper 栈 IIdempotencyStore 全量行为测试——SQLite 内存库实跑，
// 锁定：GetAsync 物化+过期 / TryStartAsync 全路径 / Mark* CAS / 唯一冲突回收。
// PalORM 参照：PalOrmIdempotencyStoreTests 同语义矩阵。

public sealed class DapperIdempotencyStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DapperIdempotencyStore _store;
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IdempotencyPolicy Policy = new()
    {
        ProcessingTimeout = TimeSpan.FromSeconds(5),
        Retention = TimeSpan.FromMinutes(10),
    };

    public DapperIdempotencyStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _conn.Execute("""
            CREATE TABLE idempotency_records (
                operation_name TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                locked_until TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                response_payload BLOB,
                error TEXT,
                revision INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (operation_name, idempotency_key)
            );
            """);
        _store = new DapperIdempotencyStore(_conn, DapperDbType.Sqlite);
    }

    public void Dispose() => _conn.Dispose();

    // ── TryStartAsync ──

    [Test]
    public async Task TryStartAsync_NewKey_ReturnsProcessingRecord()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();
        await Assert.That(record!.OperationName).IsEqualTo("TestOp");
        await Assert.That(record.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    [Test]
    public async Task TryStartAsync_DuplicateKeyWhileProcessing_ReturnsNull()
    {
        await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        var second = await _store.TryStartAsync("TestOp", "key-1", Now.AddSeconds(1), Policy);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task TryStartAsync_CompletedRecord_ReturnsNull()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await _store.MarkCompletedAsync(record!, "response"u8.ToArray(), Now.AddSeconds(1));
        var second = await _store.TryStartAsync("TestOp", "key-1", Now.AddSeconds(2), Policy);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task TryStartAsync_ExpiredRecord_ReclaimsAndReturnsNewRecord()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();
        // 推进到过期时间之后（Retention=10min → Now+10min 过期）
        var afterExpiry = Now.AddMinutes(11);
        var reclaimed = await _store.TryStartAsync("TestOp", "key-1", afterExpiry, Policy);
        await Assert.That(reclaimed).IsNotNull();
        await Assert.That(reclaimed!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    [Test]
    public async Task TryStartAsync_ExpiredCompleted_ReclaimsAndReturnsNewRecord()
    {
        // 三十七轮 P2-5 口径：过期即回收无论终态
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await _store.MarkCompletedAsync(record!, "done"u8.ToArray(), Now.AddSeconds(1));
        var afterExpiry = Now.AddMinutes(11);
        var reclaimed = await _store.TryStartAsync("TestOp", "key-1", afterExpiry, Policy);
        await Assert.That(reclaimed).IsNotNull();
    }

    [Test]
    public async Task TryStartAsync_BlankOperationName_Throws()
    {
        await Assert.That(async () => await _store.TryStartAsync(" ", "key-1", Now, Policy))
            .Throws<ArgumentException>();
    }

    // ── GetAsync ──

    [Test]
    public async Task GetAsync_ExistingRecord_ReturnsRecord()
    {
        await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        var loaded = await _store.GetAsync("TestOp", "key-1", Now);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.OperationName).IsEqualTo("TestOp");
    }

    [Test]
    public async Task GetAsync_ExpiredRecord_ReturnsNull()
    {
        await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        var loaded = await _store.GetAsync("TestOp", "key-1", Now.AddMinutes(11));
        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task GetAsync_NonExistent_ReturnsNull()
    {
        var loaded = await _store.GetAsync("NonExistent", "no-key", Now);
        await Assert.That(loaded).IsNull();
    }

    // ── MarkCompletedAsync + GetAsync 往返 ──

    [Test]
    public async Task MarkCompletedAsync_ThenGetAsync_ReturnsPayload()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();
        var payload = "response-body"u8.ToArray();
        await _store.MarkCompletedAsync(record!, payload, Now.AddSeconds(1));

        var loaded = await _store.GetAsync("TestOp", "key-1", Now.AddSeconds(1));
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        // ITM-277：空响应与无响应可区分
        await Assert.That(loaded.ResponsePayload).IsNotNull();
        await Assert.That(loaded.ResponsePayload!.Value.ToArray()).IsEquivalentTo(payload);
    }

    [Test]
    public async Task MarkCompletedAsync_StaleRevision_DoesNotMutate()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();

        // 模拟并发抢占：手动推进 revision
        await _conn.ExecuteAsync("UPDATE idempotency_records SET revision = revision + 1 WHERE operation_name = 'TestOp' AND idempotency_key = 'key-1'");

        // 旧 revision CAS 恒 0 行 → 本地对象不变
        await _store.MarkCompletedAsync(record!, "stale"u8.ToArray(), Now.AddSeconds(1));
        await Assert.That(record!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    // ── MarkFailedAsync ──

    [Test]
    public async Task MarkFailedAsync_ThenGetAsync_ReturnsError()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();
        await _store.MarkFailedAsync(record!, "something broke", Now.AddSeconds(1));

        var loaded = await _store.GetAsync("TestOp", "key-1", Now.AddSeconds(1));
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Status).IsEqualTo(IdempotencyRecordStatus.Failed);
        await Assert.That(loaded.Error).IsEqualTo("something broke");
    }

    [Test]
    public async Task MarkFailedAsync_TruncatesLongReason()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();
        var longReason = new string('x', 3000);
        await _store.MarkFailedAsync(record!, longReason, Now.AddSeconds(1));
        // FailureReason.Normalize 截断到 2000
        await Assert.That(record!.Error!.Length).IsLessThanOrEqualTo(2000);
    }

    [Test]
    public async Task MarkFailedAsync_BlankReason_Throws()
    {
        var record = await _store.TryStartAsync("TestOp", "key-1", Now, Policy);
        await Assert.That(record).IsNotNull();
        await Assert.That(async () => await _store.MarkFailedAsync(record!, " ", Now))
            .Throws<ArgumentException>();
    }
}
