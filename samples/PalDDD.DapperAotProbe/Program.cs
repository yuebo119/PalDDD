// ═══════════════════════════════════════════════════════════════
// 🔬 Dapper 栈 AOT 全链路探针（experiment/dapper-aot-full 实验分支专用）
// ═══════════════════════════════════════════════════════════════
// 验证目标：[module: DapperAot] 1.1.0 拦截器接管全部 Dapper 调用点后，三方言下
//   四组件（Outbox/EventLog/Idempotency/Saga）行为正确。
// 用法：
//   dotnet run --project samples/PalDDD.DapperAotProbe -c Release          # SQLite（:memory:）
//   dotnet run --project ... -c Release -- --provider Pg                   # 外部 PG
//   dotnet run --project ... -c Release -- --provider MySql                # 外部 MySQL
//   dotnet publish -r win-x64 /p:PublishAot=true 后从仓库根实跑二进制        # NativeAOT 实测
// 外部库凭据解析顺序：PALDDD_TEST_PG / PALDDD_TEST_MYSQL 环境变量 →
//   appsettings.test.local.json → appsettings.test.json（密码不打印——P0 #1）。
// DDL 来源：仓库 docs/sql/{dialect}/000_schema.sql（单一事实源，探针不内嵌副本）。
// 外部库安全约定：只创建六张固定名表，探针结束 DROP IF EXISTS 自清理（测试库约定）。

using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalDDD.Core;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Transactions;
using PalDDD.DapperAotProbe;
using PalUlid = ByteAether.Ulid.Ulid;

[assembly: DapperAot]

// ═══ 参数解析 ═══
string provider = "Sqlite";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--provider") provider = args[i + 1];
}
Console.WriteLine($"═══ Dapper AOT 探针 · provider={provider} ═══");

var checks = new List<(string Name, bool Pass)>();
void Check(string name, bool pass)
{
    checks.Add((name, pass));
    Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}");
}

// ═══ 连接建立 ═══
DbConnection conn;
DapperDbType dbType;
string repoRoot = FindRepoRoot();
string ddlPath = Path.Combine(repoRoot, "docs", "sql",
    provider switch { "Pg" => "postgresql", "MySql" => "mysql", _ => "sqlite" }, "000_schema.sql");
string ddl = File.ReadAllText(ddlPath);
bool external = provider is "Pg" or "MySql";

switch (provider)
{
    case "Pg":
        conn = new NpgsqlConnection(ResolveExternalCs("PostgreSql", "PALDDD_TEST_PG", repoRoot));
        dbType = DapperDbType.PostgreSql;
        break;
    case "MySql":
        conn = new MySqlConnection(ResolveExternalCs("MySql", "PALDDD_TEST_MYSQL", repoRoot));
        dbType = DapperDbType.MySql;
        break;
    default:
        conn = new SqliteConnection("Data Source=:memory:");
        dbType = DapperDbType.Sqlite;
        break;
}
await conn.OpenAsync().ConfigureAwait(false);
Console.WriteLine($"连接建立（DDL: {Path.GetFileName(Path.GetDirectoryName(ddlPath))}）");

// ═══ 建表（外部库先 DROP 自清理再建，保证幂等）═══
if (external)
{
    foreach (var t in new[] { "outbox_messages", "inbox_messages", "saga_states", "events", "idempotency_records", "projection_checkpoints" })
        await conn.ExecuteAsync($"DROP TABLE IF EXISTS {t}").ConfigureAwait(false);
}
await conn.ExecuteAsync(ddl).ConfigureAwait(false);
Console.WriteLine("schema created");

// ═══ 六组件全链路 ═══
await ProbeOutboxAsync(conn, dbType, Check).ConfigureAwait(false);
await ProbeEventLogAsync(conn, dbType, Check).ConfigureAwait(false);
await ProbeIdempotencyAsync(conn, dbType, Check).ConfigureAwait(false);
await ProbeSagaAsync(conn, dbType, Check).ConfigureAwait(false);
await ProbeCheckpointAsync(conn, dbType, Check).ConfigureAwait(false);
await ProbeInboxAsync(conn, dbType, Check).ConfigureAwait(false);

// ═══ 外部库清理 ═══
if (external)
{
    foreach (var t in new[] { "outbox_messages", "inbox_messages", "saga_states", "events", "idempotency_records", "projection_checkpoints" })
        await conn.ExecuteAsync($"DROP TABLE IF EXISTS {t}").ConfigureAwait(false);
    Console.WriteLine("外部库表已清理");
}
await conn.DisposeAsync().ConfigureAwait(false);

var failed = checks.Count(c => !c.Pass);
Console.WriteLine($"═══ 探针结束：{checks.Count - failed}/{checks.Count} 通过 ═══");
return failed == 0 ? 0 : 1;

// ═══ ② Outbox：AddMessage（byte[] Payload → DynamicParameters+DbType.Binary）→ Lease → MarkProcessed ═══
static async Task ProbeOutboxAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
{
    var store = new DapperOutboxStore(conn, dbType);
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
    check("outbox add (byte[] blob)", true);

    var leased = await store.LeasePendingMessagesAsync(10, "probe-owner",
        TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, CancellationToken.None).ConfigureAwait(false);
    check("outbox lease acquired 1", leased.Count == 1);
    check("outbox payload round-trip", leased.Count == 1 && leased[0].Payload.SequenceEqual(payload));

    store.MarkProcessed(leased[0], TimeProvider.System.GetUtcNow());
    var pending = await store.GetPendingMessagesAsync(10,
        new OutboxOptions().MaxRetryCount, CancellationToken.None).ConfigureAwait(false);
    check("outbox processed terminal", pending.Count == 0);
}

// ═══ ③ EventLog：AppendAsync（byte[] Payload/Metadata）→ ReadStreamAsync 往返 ═══
static async Task ProbeEventLogAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
{
    var log = new DapperEventLog(conn, dbType: dbType);
    var payload = "{\"k\":1}"u8.ToArray();
    var metadata = "{\"m\":2}"u8.ToArray();
    var evt = new EventData(PalUlid.New(), "probe.event.v1", 1, "application/json",
        payload, metadata, EventAuditMetadata.Empty);
    var streamName = $"probe-stream-{PalUlid.New().ToString()[..8]}";
    await log.AppendAsync(streamName,
        ExpectedStreamVersion.NoStream, [evt], CancellationToken.None).ConfigureAwait(false);

    RecordedEvent? read = null;
    await foreach (var r in log.ReadStreamAsync(streamName,
        cancellationToken: CancellationToken.None).ConfigureAwait(false))
        read = r;
    check("eventlog append+read", read is not null);
    check("eventlog payload round-trip", read is not null && read.Payload.Span.SequenceEqual(payload));
    check("eventlog metadata round-trip", read is not null && read.Metadata.Span.SequenceEqual(metadata));
}

// ═══ ④ Idempotency：TryStart → MarkCompleted（byte[] response）→ GetAsync 往返 ═══
static async Task ProbeIdempotencyAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
{
    var store = new DapperIdempotencyStore(conn, dbType);
    var now = DateTimeOffset.UtcNow;
    var record = await store.TryStartAsync("probe-op", $"key-{PalUlid.New()}", now,
        IdempotencyPolicy.Default, CancellationToken.None).ConfigureAwait(false);
    check("idempotency try-start", record is not null);

    var response = "idempotent-response"u8.ToArray();
    await store.MarkCompletedAsync(record!, response, now.AddSeconds(1)).ConfigureAwait(false);

    var loaded = await store.GetAsync("probe-op", record!.Key, now.AddSeconds(1),
        CancellationToken.None).ConfigureAwait(false);
    check("idempotency completed status", loaded?.Status == IdempotencyRecordStatus.Completed);
    check("idempotency blob round-trip", loaded?.ResponsePayload is not null
        && loaded.ResponsePayload.Value.Span.SequenceEqual(response));
}

// ═══ ⑤ Saga：SaveChanges（jsonTypeInfo 源生成序列化）→ GetById 往返 ═══
static async Task ProbeSagaAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
{
    var store = new DapperSagaStateStore<ProbeSagaState>(conn,
        jsonTypeInfo: DapperAotProbeJsonContext.Default.ProbeSagaState,
        dbType: dbType);
    var state = new ProbeSagaState
    {
        SagaId = PalUlid.New(),
        CurrentState = "ProbeInitial",
        Status = SagaStatus.Active,
        CreatedAt = TimeProvider.System.GetUtcNow(),
        CustomerId = "probe-customer",
    };
    var rows = await store.SaveChangesAsync(state, CancellationToken.None).ConfigureAwait(false);
    check("saga insert", rows == 1);

    var loaded = await store.GetByIdAsync(state.SagaId, CancellationToken.None).ConfigureAwait(false);
    check("saga loaded", loaded is not null);
    check("saga state round-trip", loaded?.CurrentState == "ProbeInitial"
        && loaded?.CustomerId == "probe-customer");
}

// ═══ ⑤ ProjectionCheckpoint:枚举参数 int 化回归守卫(CI #94 根因——2026-09-14 增段)═══
// 背景:Dapper.AOT 拦截器把 ProjectionCheckpointStatus 枚举直传驱动,PG 拒绝
// (InvalidCastException);经典路径驱动容忍 → 本地 SQLite 与 CI PG 行为分叉。
// 修复为显式 (int) 后,本段在真库上锁定该修复(枚举路径任一回归→PG/MySQL 段必红)。
static async Task ProbeCheckpointAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
{
    var store = new DapperProjectionCheckpointStore(conn, dbType);
    var now = TimeProvider.System.GetUtcNow();
    var projection = $"probe-{PalUlid.New().ToString()[..8]}";
    var checkpoint = await store.TryStartAsync(projection, "probe-source", "p-1",
        now, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
    check("checkpoint try-start (enum param int-ized)", checkpoint is not null);
    check("checkpoint processing status", checkpoint?.Status == PalDDD.Projections.ProjectionCheckpointStatus.Processing);

    await store.MarkCompletedAsync(checkpoint!, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);
    var loaded = await store.GetAsync(projection, "probe-source", "p-1", CancellationToken.None).ConfigureAwait(false);
    check("checkpoint completed round-trip", loaded?.Status == PalDDD.Projections.ProjectionCheckpointStatus.Completed);
}

// ═══ ⑥ Inbox:去重语义全链(枚举/状态写路径)═══
static async Task ProbeInboxAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
{
    var store = new DapperInboxStore(conn, dbType);
    var now = TimeProvider.System.GetUtcNow();
    var messageId = $"probe-msg-{PalUlid.New().ToString()[..8]}";
    var msg = await store.TryStartProcessingAsync("probe-consumer", messageId, now,
        TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
    check("inbox try-start", msg is not null);

    await store.MarkProcessedAsync(msg!, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);
    var again = await store.TryStartProcessingAsync("probe-consumer", messageId, now.AddSeconds(2),
        TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
    check("inbox processed → duplicate start returns null", again is null);
}

// ═══ 外部库连接串解析（密码不打印）═══
static string ResolveExternalCs(string section, string envName, string repoRoot)
{
    var env = Environment.GetEnvironmentVariable(envName);
    if (!string.IsNullOrWhiteSpace(env)) return env;

    foreach (var name in new[] { "appsettings.test.local.json", "appsettings.test.json" })
    {
        var path = Path.Combine(repoRoot, name);
        if (!File.Exists(path)) continue;
        var cs = ExtractConnectionString(File.ReadAllText(path), section);
        if (cs is not null) return cs;
    }
    throw new InvalidOperationException(
        $"外部库连接串未找到：设 {envName} 环境变量，或提供 {repoRoot}/appsettings.test.local.json（TestEnvironment.{section}.ConnectionString）");
}

// 轻量 JSON 提取（探针专用，避免反射序列化保持 AOT 安全）：
// 在 TestEnvironment.<section> 对象内取 "ConnectionString": "..." 的值。
static string? ExtractConnectionString(string json, string section)
{
    var secIdx = json.IndexOf($"\"{section}\"", StringComparison.Ordinal);
    if (secIdx < 0) return null;
    var csIdx = json.IndexOf("\"ConnectionString\"", secIdx, StringComparison.Ordinal);
    if (csIdx < 0) return null;
    var colon = json.IndexOf(':', csIdx);
    var quote1 = json.IndexOf('"', colon);
    var quote2 = json.IndexOf('"', quote1 + 1);
    var value = json[(quote1 + 1)..quote2];
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "appsettings.test.json")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

namespace PalDDD.DapperAotProbe
{
    /// <summary>探针 Saga 状态（源生成序列化）。</summary>
    internal sealed class ProbeSagaState : SagaState
    {
        public string CustomerId { get; set; } = string.Empty;
    }

    [JsonSerializable(typeof(ProbeSagaState))]
    internal sealed partial class DapperAotProbeJsonContext : JsonSerializerContext;
}
