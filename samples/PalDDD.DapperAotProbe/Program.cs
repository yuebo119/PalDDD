// ═══════════════════════════════════════════════════════════════
// 🔬 Dapper 栈 AOT 全链路探针（experiment/dapper-aot-full 实验分支专用）
// ═══════════════════════════════════════════════════════════════
// 验证目标：[module: DapperAot] 1.1.0 拦截器接管全部 Dapper 调用点后，
//   SQLite（NativeAOT publish 二进制）下四组件行为正确：
//   ① Outbox：AddMessage（含 byte[] Payload → DynamicParameters+DbType.Binary 绕行）
//      → Lease → MarkProcessed → pending 归零
//   ② EventLog：AppendAsync（byte[] Payload/Metadata）→ ReadStreamAsync 往返
//   ③ Idempotency：TryStart → MarkCompleted（byte[] response payload）→ GetAsync 往返
//   ④ Saga：SaveChanges（jsonTypeInfo 源生成序列化）→ GetById 往返
// 验证点覆盖：snake_case 列名映射（生成器 NormalizedEquals）、声明式 TypeHandler
//   （Ulid/Guid/DateTimeOffset）、byte[] blob 往返、Revision CAS。
// 运行：dotnet run（JIT 对照）→ dotnet publish -r win-x64 /p:PublishAot=true →
//   bin/Release/net11.0/win-x64/publish/PalDDD.DapperAotProbe.exe（AOT 实测）

using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using PalDDD.Core;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Transactions;
using PalDDD.DapperAotProbe;
using PalUlid = ByteAether.Ulid.Ulid;

[assembly: DapperAot]

// ═══ 1. 建库建表（DDL 对齐 Integration.Tests 夹具）═══
var conn = new SqliteConnection("Data Source=:memory:");
conn.Open();

await conn.ExecuteAsync("""
    CREATE TABLE outbox_messages (
        id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL,
        content_type TEXT NOT NULL DEFAULT 'application/json',
        schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0,
        processed_at TEXT, next_attempt_at TEXT, retry_count INTEGER NOT NULL DEFAULT 0,
        error TEXT, locked_by TEXT, locked_until TEXT,
        created_at TEXT NOT NULL, correlation_id TEXT, causation_id TEXT,
        trace_parent TEXT, trace_state TEXT);
    CREATE TABLE events (
        global_position INTEGER PRIMARY KEY AUTOINCREMENT,
        event_id TEXT NOT NULL, event_name TEXT NOT NULL, stream_name TEXT NOT NULL,
        stream_version INTEGER NOT NULL,
        schema_version INTEGER NOT NULL DEFAULT 1,
        content_type TEXT NOT NULL DEFAULT 'application/json',
        payload BLOB NOT NULL, metadata BLOB, recorded_at TEXT NOT NULL,
        actor_id TEXT, reason TEXT, correlation_id TEXT, causation_id TEXT,
        trace_parent TEXT, trace_state TEXT);
    CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version);
    CREATE TABLE saga_states (
        saga_id TEXT PRIMARY KEY, current_state TEXT NOT NULL,
        status INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
        completed_at TEXT, error TEXT, error_at TEXT,
        version INTEGER NOT NULL DEFAULT 0, saga_data TEXT,
        leased_by TEXT, leased_until TEXT);
    CREATE TABLE idempotency_records (
        operation_name TEXT NOT NULL, idempotency_key TEXT NOT NULL,
        status INTEGER NOT NULL DEFAULT 0, locked_until TEXT NOT NULL,
        expires_at TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
        response_payload BLOB, error TEXT, revision INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY (operation_name, idempotency_key));
    """);
Console.WriteLine("schema created");

var checks = new List<(string Name, bool Pass)>();
void Check(string name, bool pass)
{
    checks.Add((name, pass));
    Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}");
}

// ═══ 2. Outbox 全链路 ═══
{
    var store = new DapperOutboxStore(conn, DapperDbType.Sqlite);
    var payload = "outbox-payload"u8.ToArray();
    var msg = new OutboxMessage
    {
        Type = "probe.event.v1",
        Payload = payload,
        ContentType = "application/json",
        SchemaVersion = 1,
        Status = OutboxStatus.Pending,
        CreatedAt = TimeProvider.System.GetUtcNow(),
    };
    store.AddMessage(msg);
    Check("outbox add (byte[] blob)", true);

    var leased = await store.LeasePendingMessagesAsync(10, "probe-owner",
        TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, CancellationToken.None).ConfigureAwait(false);
    Check("outbox lease acquired 1", leased.Count == 1);
    Check("outbox payload round-trip", leased[0].Payload.SequenceEqual(payload));

    store.MarkProcessed(leased[0], TimeProvider.System.GetUtcNow());
    var pending = await store.GetPendingMessagesAsync(10,
        new OutboxOptions().MaxRetryCount, CancellationToken.None).ConfigureAwait(false);
    Check("outbox processed terminal", pending.Count == 0);
}

// ═══ 3. EventLog 全链路 ═══
{
    var log = new DapperEventLog(conn);
    var payload = "{\"k\":1}"u8.ToArray();
    var metadata = "{\"m\":2}"u8.ToArray();
    var evt = new EventData(PalUlid.New(), "probe.event.v1", 1, "application/json",
        payload, metadata, EventAuditMetadata.Empty);
    await log.AppendAsync("probe-stream", ExpectedStreamVersion.NoStream, [evt],
        CancellationToken.None).ConfigureAwait(false);

    RecordedEvent? read = null;
    await foreach (var r in log.ReadStreamAsync("probe-stream", cancellationToken: CancellationToken.None).ConfigureAwait(false))
        read = r;
    Check("eventlog append+read", read is not null);
    Check("eventlog payload round-trip", read is not null && read.Payload.Span.SequenceEqual(payload));
    Check("eventlog metadata round-trip", read is not null && read.Metadata.Span.SequenceEqual(metadata));
}

// ═══ 4. Idempotency 全链路（byte[] response payload 是绕行方案的关键验证点）═══
{
    var store = new DapperIdempotencyStore(conn, DapperDbType.Sqlite);
    var now = DateTimeOffset.UtcNow;
    var record = await store.TryStartAsync("probe-op", "key-1", now,
        IdempotencyPolicy.Default, CancellationToken.None).ConfigureAwait(false);
    Check("idempotency try-start", record is not null);

    var response = "idempotent-response"u8.ToArray();
    await store.MarkCompletedAsync(record!, response, now.AddSeconds(1)).ConfigureAwait(false);

    var loaded = await store.GetAsync("probe-op", "key-1", now.AddSeconds(1),
        CancellationToken.None).ConfigureAwait(false);
    Check("idempotency completed status", loaded?.Status == IdempotencyRecordStatus.Completed);
    Check("idempotency blob round-trip", loaded?.ResponsePayload is not null
        && loaded.ResponsePayload.Value.Span.SequenceEqual(response));
}

// ═══ 5. Saga 全链路（jsonTypeInfo 源生成序列化）═══
{
    var store = new DapperSagaStateStore<ProbeSagaState>(conn,
        jsonTypeInfo: DapperAotProbeJsonContext.Default.ProbeSagaState);
    var state = new ProbeSagaState
    {
        SagaId = PalUlid.New(),
        CurrentState = "ProbeInitial",
        Status = SagaStatus.Active,
        CreatedAt = TimeProvider.System.GetUtcNow(),
        CustomerId = "probe-customer",
    };
    var rows = await store.SaveChangesAsync(state, CancellationToken.None).ConfigureAwait(false);
    Check("saga insert", rows == 1);

    var loaded = await store.GetByIdAsync(state.SagaId, CancellationToken.None).ConfigureAwait(false);
    Check("saga loaded", loaded is not null);
    Check("saga state round-trip", loaded?.CurrentState == "ProbeInitial"
        && loaded?.CustomerId == "probe-customer");
}

// ═══ 汇总 ═══
var failed = checks.Count(c => !c.Pass);
Console.WriteLine($"═══ Dapper AOT 探针：{checks.Count - failed}/{checks.Count} 通过 ═══");
return failed == 0 ? 0 : 1;

namespace PalDDD.DapperAotProbe
{
    internal sealed class ProbeSagaState : SagaState
    {
        public string CustomerId { get; set; } = string.Empty;
    }

    [JsonSerializable(typeof(ProbeSagaState))]
    internal sealed partial class DapperAotProbeJsonContext : JsonSerializerContext;
}
