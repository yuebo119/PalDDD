#!/usr/bin/env bash
# ⚠ 本文件是 .ai/scripts/dialect-probe.sh 的 CI 分发副本（unified v2.0 Phase 2a，2026-08-20）。
# 真源在 .ai 独立仓（不随主仓分发）；修改探针断言以 .ai 版为准，改后必须同步重新生成本副本。
# 本副本仅差异：ROOT 定位按根 scripts/ 深度修正（../..→..）；其余逐行一致。
# 方言实测探针：只允许在显式授权下创建和清理唯一的测试数据库。
# 用法：bash .ai/scripts/dialect-probe.sh --allow-destructive-probe
# 凭据从环境变量或配置文件读取，绝不打印连接串。
set -euo pipefail

usage() {
  printf '用法：%s --allow-destructive-probe | --self-test-safety\n' "$0" >&2
}

ALLOW_DESTRUCTIVE_PROBE=0
SELF_TEST_SAFETY=0
for arg in "$@"; do
  case "$arg" in
    --allow-destructive-probe) ALLOW_DESTRUCTIVE_PROBE=1 ;;
    --self-test-safety) SELF_TEST_SAFETY=1 ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'FAIL: 未知参数：%s\n' "$arg" >&2; usage; exit 2 ;;
  esac
done

if [[ "$SELF_TEST_SAFETY" -eq 0 && "$ALLOW_DESTRUCTIVE_PROBE" -ne 1 ]]; then
  printf 'FAIL: 必须显式传入 --allow-destructive-probe；未执行任何连接或 SQL。\n' >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROBE_DIR="$(mktemp -d /tmp/palddd-dialect-probe.XXXXXX)"
trap 'rm -r -- "$PROBE_DIR"' EXIT

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

# 只接受生成的测试库名。该检查同时保护 SQL 标识符不会来自用户输入。
validate_generated_database() {
  local db="$1" prefix="$2"
  [[ "$db" == "$prefix"* && "${#db}" -gt "${#prefix}" ]] || fail "拒绝非专用测试数据库目标"
  [[ "$db" =~ ^[a-z0-9_]+$ ]] || fail "测试数据库名包含不安全字符"
}

# 通过 Python 标准库严格解析连接串。数据库别名只能出现一次，重复或冲突都拒绝。
connection_value() {
  local cs="$1" requested="$2"
  python3 - "$cs" "$requested" <<'PY'
import sys

connection_string, requested = sys.argv[1:]
values = {}
database_aliases = []
for raw_segment in connection_string.split(";"):
    if not raw_segment.strip():
        continue
    if "=" not in raw_segment:
        raise SystemExit("invalid connection string segment")
    key, value = raw_segment.split("=", 1)
    normalized = key.strip().lower()
    value = value.strip()
    if normalized in values:
        raise SystemExit(f"duplicate connection key: {key.strip()}")
    values[normalized] = value
    if normalized in {"database", "initial catalog", "catalog"}:
        database_aliases.append((normalized, value))

if requested == "database":
    if len(database_aliases) > 1:
        raise SystemExit("duplicate or conflicting database aliases")
    print(database_aliases[0][1] if database_aliases else "")
elif requested == "host":
    print(values.get("host", values.get("server", "")))
elif requested == "port":
    print(values.get("port", ""))
else:
    raise SystemExit("unknown requested field")
PY
}

# 创建库的管理连接不能指向任意业务库；目标库仍由上面的唯一前缀生成。
validate_admin_database() {
  local cs="$1" provider="$2" database=""
  database="$(connection_value "$cs" database)" || fail "$provider 连接串包含重复、冲突或无效数据库别名"
  case "$provider:$database" in
    PG:|PG:postgres|PG:palddd_probe_admin|PG:palddd_test_admin|PG:palddd_test_*) ;;
    MySQL:|MySQL:mysql|MySQL:palddd_probe_admin|MySQL:palddd_test_admin|MySQL:palddd_test_*) ;;
    *) fail "拒绝 $provider 非专用管理数据库目标" ;;
  esac
}

# 使用 Python 标准库 socket timeout，避免 GNU timeout 和 /dev/tcp 平台差异。
probe_tcp() {
  local host="$1" port="$2"
  [[ "$host" =~ ^[A-Za-z0-9._:-]+$ ]] || return 1
  [[ "$port" =~ ^[0-9]+$ && "$port" -ge 1 && "$port" -le 65535 ]] || return 1
  python3 - "$host" "$port" <<'PY' 2>/dev/null
import socket
import sys

host, port = sys.argv[1], int(sys.argv[2])
with socket.create_connection((host, port), timeout=5):
    pass
PY
}

# 配置文件存在即严格解析，解析错误不得回退默认值。
read_config_value() {
  local path="$1" provider="$2"
  python3 - "$path" "$provider" <<'PY'
import json
import sys

path, provider = sys.argv[1:]
with open(path, encoding="utf-8-sig") as stream:
    config = json.load(stream)
value = config.get("TestEnvironment", {}).get(provider, {}).get("ConnectionString")
if not isinstance(value, str) or not value.strip():
    raise SystemExit(f"missing {provider} connection string")
print(value)
PY
}

PG_CS=""
MY_CS=""
PG_OK=0
MY_OK=0
if [[ "$SELF_TEST_SAFETY" -eq 0 ]]; then
  PG_CS="${PALDDD_TEST_PG:-}"
  MY_CS="${PALDDD_TEST_MYSQL:-}"
  CONFIG_PATH="${PALDDD_TEST_CONFIG:-}"
  if [[ -z "$CONFIG_PATH" ]]; then
    LOCAL_JSON="$ROOT/appsettings.test.local.json"
    TEMPLATE_JSON="$ROOT/appsettings.test.json"
    if command -v cygpath >/dev/null 2>&1; then
      LOCAL_JSON="$(cygpath -w "$LOCAL_JSON")"
      TEMPLATE_JSON="$(cygpath -w "$TEMPLATE_JSON")"
    fi
    if [[ -f "$LOCAL_JSON" ]]; then CONFIG_PATH="$LOCAL_JSON"; elif [[ -f "$TEMPLATE_JSON" ]]; then CONFIG_PATH="$TEMPLATE_JSON"; fi
  elif [[ ! -f "$CONFIG_PATH" ]]; then
    fail "PALDDD_TEST_CONFIG 指向的配置文件不存在"
  fi

  if [[ -n "$CONFIG_PATH" && -z "$PG_CS" ]]; then
    PG_CS="$(read_config_value "$CONFIG_PATH" PostgreSql)" || fail "PostgreSQL 测试配置无法读取或解析"
  fi
  if [[ -n "$CONFIG_PATH" && -z "$MY_CS" ]]; then
    MY_CS="$(read_config_value "$CONFIG_PATH" MySql)" || fail "MySQL 测试配置无法读取或解析"
  fi

  if [[ -n "$PG_CS" ]]; then validate_admin_database "$PG_CS" PG; fi
  if [[ -n "$MY_CS" ]]; then validate_admin_database "$MY_CS" MySQL; fi

  if [[ -n "$PG_CS" ]]; then
    PG_HOST="$(connection_value "$PG_CS" host)" || fail "PostgreSQL host 配置无效"
    PG_PORT="$(connection_value "$PG_CS" port)" || fail "PostgreSQL port 配置无效"; PG_PORT="${PG_PORT:-5432}"
    if probe_tcp "$PG_HOST" "$PG_PORT"; then PG_OK=1; printf 'PG 可达（%s:%s）\n' "$PG_HOST" "$PG_PORT"; else printf 'PG 不可达，跳过 PG 实测\n'; fi
  else
    printf 'PG 连接串未配置，跳过\n'
  fi
  if [[ -n "$MY_CS" ]]; then
    MY_HOST="$(connection_value "$MY_CS" host)" || fail "MySQL host 配置无效"
    MY_PORT="$(connection_value "$MY_CS" port)" || fail "MySQL port 配置无效"; MY_PORT="${MY_PORT:-3306}"
    if probe_tcp "$MY_HOST" "$MY_PORT"; then MY_OK=1; printf 'MySQL 可达（%s:%s）\n' "$MY_HOST" "$MY_PORT"; else printf 'MySQL 不可达，跳过 MySQL 实测\n'; fi
  else
    printf 'MySQL 连接串未配置，跳过\n'
  fi

  if [[ "$PG_OK" -eq 0 && "$MY_OK" -eq 0 ]]; then
    printf 'SKIP：无可达方言环境；未执行数据库 SQL。\n'
    exit 0
  fi
fi

cd "$PROBE_DIR"
dotnet new console -o . --force -v q >/dev/null 2>&1 || fail 'dotnet new console'
dotnet add reference "$ROOT/src/PalDDD.Dapper/PalDDD.Dapper.csproj" >/dev/null 2>&1 || fail 'add reference'

python3 - <<'PYEOF' > Program.cs
import os

program = r'''
using System.Data.Common;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Projections;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

var pgCs = Environment.GetEnvironmentVariable("PROBE_PG_CS") ?? "";
var myCs = Environment.GetEnvironmentVariable("PROBE_MY_CS") ?? "";
var pgDb = Environment.GetEnvironmentVariable("PROBE_PG_DB") ?? "";
var myDb = Environment.GetEnvironmentVariable("PROBE_MY_DB") ?? "";
var runPg = Environment.GetEnvironmentVariable("PROBE_PG_OK") == "1";
var runMy = Environment.GetEnvironmentVariable("PROBE_MY_OK") == "1";
var root = Environment.GetEnvironmentVariable("PROBE_ROOT")!;
var ddlPg = File.ReadAllText(Path.Combine(root, "docs/sql/postgresql/000_schema.sql"));
var ddlMy = File.ReadAllText(Path.Combine(root, "docs/sql/mysql/000_schema.sql"));
var failures = new List<string>();

if (runPg) await RunPg();
if (runMy) await RunMySql();
Console.WriteLine(failures.Count == 0 ? "=== 方言探针全部通过 ===" : $"=== 失败 {failures.Count} 项 ===\n{string.Join("\n", failures)}");
return failures.Count == 0 ? 0 : 1;

async Task RunPg()
{
    EnsureOwnedDatabase(pgDb, "palddd_probe_pg_");
    await Exec(() => new NpgsqlConnection(pgCs), $"CREATE DATABASE {pgDb}");
    var databaseCreated = true;
    var marked = false;
    try
    {
        await Exec(() => new NpgsqlConnection(WithDb(pgCs, pgDb)), OwnershipMarkerSql(pgDb));
        marked = await HasOwnershipMarker(() => new NpgsqlConnection(WithDb(pgCs, pgDb)), pgDb);
        if (!marked) throw new InvalidOperationException("PostgreSQL ownership marker 未确认");
        await Exec(() => new NpgsqlConnection(WithDb(pgCs, pgDb)), ddlPg);
        await using var conn = new NpgsqlConnection(WithDb(pgCs, pgDb));
        await conn.OpenAsync();
        await OutboxSmoke(conn, DapperDbType.PostgreSql, "PG");
        await EventLogSmoke(conn, DapperDbType.PostgreSql, "PG");
        await SagaSmoke(conn, DapperDbType.PostgreSql, "PG");
        await InboxSmoke(conn, DapperDbType.PostgreSql, "PG");
        await CheckpointSmoke(conn, DapperDbType.PostgreSql, "PG");
    }
    finally
    {
        if (marked && await HasOwnershipMarker(() => new NpgsqlConnection(WithDb(pgCs, pgDb)), pgDb))
        {
            await Exec(() => new NpgsqlConnection(pgCs), $"DROP DATABASE {pgDb} WITH (FORCE)");
        }
        else if (databaseCreated)
        {
            // 补偿清理：本进程创建了该严格前缀库，marker 未建立也必须回收，否则留孤儿库。
            try
            {
                await Exec(() => new NpgsqlConnection(pgCs), $"DROP DATABASE {pgDb} WITH (FORCE)");
                Console.Error.WriteLine($"PG compensating cleanup: dropped orphan probe database {pgDb}");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine($"PG orphan database needs manual cleanup: {pgDb} — {cleanupEx.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine("PG cleanup refused: database not created by this run");
        }
    }
}

async Task RunMySql()
{
    EnsureOwnedDatabase(myDb, "palddd_probe_mysql_");
    await Exec(() => new MySqlConnector.MySqlConnection(myCs), $"CREATE DATABASE {myDb}");
    var databaseCreated = true;
    var marked = false;
    try
    {
        await Exec(() => new MySqlConnector.MySqlConnection(WithDb(myCs, myDb)), OwnershipMarkerSql(myDb));
        marked = await HasOwnershipMarker(() => new MySqlConnector.MySqlConnection(WithDb(myCs, myDb)), myDb);
        if (!marked) throw new InvalidOperationException("MySQL ownership marker 未确认");
        await Exec(() => new MySqlConnector.MySqlConnection(WithDb(myCs, myDb)), ddlMy);
        await using var conn = new MySqlConnector.MySqlConnection(WithDb(myCs, myDb));
        await conn.OpenAsync();
        await OutboxSmoke(conn, DapperDbType.MySql, "MySQL");
        await EventLogSmoke(conn, DapperDbType.MySql, "MySQL");
        await SagaSmoke(conn, DapperDbType.MySql, "MySQL");
        await InboxSmoke(conn, DapperDbType.MySql, "MySQL");
        await CheckpointSmoke(conn, DapperDbType.MySql, "MySQL");
    }
    finally
    {
        if (marked && await HasOwnershipMarker(() => new MySqlConnector.MySqlConnection(WithDb(myCs, myDb)), myDb))
        {
            await Exec(() => new MySqlConnector.MySqlConnection(myCs), $"DROP DATABASE {myDb}");
        }
        else if (databaseCreated)
        {
            // 补偿清理：同 PG 路径——严格前缀库由本进程创建，marker 失败也必须回收。
            try
            {
                await Exec(() => new MySqlConnector.MySqlConnection(myCs), $"DROP DATABASE {myDb}");
                Console.Error.WriteLine($"MySQL compensating cleanup: dropped orphan probe database {myDb}");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine($"MySQL orphan database needs manual cleanup: {myDb} — {cleanupEx.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine("MySQL cleanup refused: database not created by this run");
        }
    }
}

static void EnsureOwnedDatabase(string database, string prefix)
{
    if (string.IsNullOrWhiteSpace(database) || !database.StartsWith(prefix, StringComparison.Ordinal)
        || database.Length <= prefix.Length || database.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        throw new InvalidOperationException($"拒绝非专用测试数据库：{database}");
}

static string OwnershipMarkerSql(string database) =>
    $"CREATE TABLE palddd_probe_ownership (marker VARCHAR(128) PRIMARY KEY); INSERT INTO palddd_probe_ownership(marker) VALUES ('{database}')";

async Task<bool> HasOwnershipMarker(Func<DbConnection> factory, string expected)
{
    await using var c = factory();
    await c.OpenAsync();
    await using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT marker FROM palddd_probe_ownership";
    return string.Equals(Convert.ToString(await cmd.ExecuteScalarAsync()), expected, StringComparison.Ordinal);
}

static string WithDb(string cs, string db)
{
    var builder = new DbConnectionStringBuilder { ConnectionString = cs };
    builder["Database"] = db;
    return builder.ConnectionString;
}

async Task OutboxSmoke(DbConnection conn, DapperDbType dbType, string tag)
{
    var clock = TimeProvider.System;
    var store = new DapperOutboxStore(conn, dbType, timeProvider: clock);
    var msg = new OutboxMessage
    {
        Type = "probe.event.v1", Payload = [1, 2, 3], ContentType = "application/json", SchemaVersion = 1,
        CorrelationId = PalUlid.New(), CausationId = PalUlid.New(), TraceParent = "00-abc-def-01", TraceState = "probe=1",
    };
    store.AddMessage(msg);

    var pending = await store.GetPendingMessagesAsync(10, 10, default);
    Check(tag, "Outbox GetPending 返回 1 条", pending.Count == 1, $"实际 {pending.Count}");
    Check(tag, "Outbox 追踪 4 列回读", pending.Count == 1 && pending[0].CorrelationId == msg.CorrelationId
        && pending[0].CausationId == msg.CausationId && pending[0].TraceParent == "00-abc-def-01"
        && pending[0].TraceState == "probe=1", "");

    var leased = await store.LeasePendingMessagesAsync(10, "probe-owner", TimeSpan.FromMinutes(2), 10, default);
    Check(tag, "Outbox Lease 返回 1 条", leased.Count == 1, $"实际 {leased.Count}");
    Check(tag, "Outbox Lease 归属", leased.Count == 1 && leased[0].LockedBy == "probe-owner", "");

    store.MarkProcessed(leased[0], clock.GetUtcNow());
    Check(tag, "Outbox MarkProcessed 后无 pending", (await store.GetPendingMessagesAsync(10, 10, default)).Count == 0, "");
}

async Task EventLogSmoke(DbConnection conn, DapperDbType dbType, string tag)
{
    var log = new DapperEventLog(conn, dbType: dbType);
    var audit = new EventAuditMetadata("probe-actor", "probe-reason", PalUlid.New(), PalUlid.New(), "00-abc-def-01", "probe=1");
    var events = new List<EventData>
    {
        new(PalUlid.New(), "probe.event.v1", 1, "application/json", new ReadOnlyMemory<byte>([1]), new ReadOnlyMemory<byte>([1]), audit),
        new(PalUlid.New(), "probe.event.v1", 1, "application/json", new ReadOnlyMemory<byte>([2]), new ReadOnlyMemory<byte>([2]), audit),
    };
    await log.AppendAsync("probe-stream", ExpectedStreamVersion.NoStream, events, default);

    var read = new List<RecordedEvent>();
    await foreach (var e in log.ReadStreamAsync("probe-stream", 0, 100, default)) read.Add(e);
    Check(tag, "EventLog Append→Read 2 条", read.Count == 2, $"实际 {read.Count}");
    Check(tag, "EventLog 审计列回读", read.Count > 0 && read[0].Audit.CorrelationId == audit.CorrelationId, "");

    try
    {
        await log.AppendAsync("probe-stream", ExpectedStreamVersion.Exact(0), events, default);
        Check(tag, "EventLog 陈旧版本抛并发异常", false, "未抛异常");
    }
    catch (EventStreamConcurrencyException) { Check(tag, "EventLog 陈旧版本抛并发异常", true, ""); }
    catch (Exception ex) { Check(tag, "EventLog 陈旧版本抛并发异常", false, $"实际 {ex.GetType().Name}: {ex.Message}"); }
}

async Task SagaSmoke(DbConnection conn, DapperDbType dbType, string tag)
{
    var store = new DapperSagaStateStore<ProbeSagaState>(conn, dbType: dbType);
    var state = new ProbeSagaState { CurrentState = "Waiting" };
    Check(tag, "Saga INSERT 1 行", await store.SaveChangesAsync(state, default) == 1, "");

    var leased = await store.LeaseActiveSagasAsync("probe-owner", TimeSpan.FromMinutes(2), 10, default);
    Check(tag, "Saga Lease 返回 1 条", leased.Count == 1, $"实际 {leased.Count}");
    Check(tag, "Saga Lease 归属", leased.Count == 1 && leased[0].LeasedBy == "probe-owner", "");
}

async Task InboxSmoke(DbConnection conn, DapperDbType dbType, string tag)
{
    var store = new DapperInboxStore(conn, dbType);
    var clock = DateTimeOffset.UtcNow;

    var first = await store.TryStartProcessingAsync("probe-consumer", "probe-msg-1", clock, TimeSpan.FromMinutes(5), default);
    Check(tag, "Inbox TryStart 首次返回记录", first is not null, first is null ? "null" : "ok");
    Check(tag, "Inbox TryStart 重复返回 null",
        await store.TryStartProcessingAsync("probe-consumer", "probe-msg-1", clock, TimeSpan.FromMinutes(5), default) is null, "");
    Check(tag, "Inbox 其他消费者可处理",
        await store.TryStartProcessingAsync("other-consumer", "probe-msg-1", clock, TimeSpan.FromMinutes(5), default) is not null, "");

    await store.MarkProcessedAsync(first!, clock.AddSeconds(1), default);
    Check(tag, "Inbox MarkProcessed 后不再重投",
        await store.TryStartProcessingAsync("probe-consumer", "probe-msg-1", clock.AddSeconds(2), TimeSpan.FromMinutes(5), default) is null, "");
    Check(tag, "Inbox 超时接管（timeout=0）可重入",
        await store.TryStartProcessingAsync("probe-consumer", "probe-msg-stale", clock, TimeSpan.Zero, default) is not null, "");
}

async Task CheckpointSmoke(DbConnection conn, DapperDbType dbType, string tag)
{
    var store = new DapperProjectionCheckpointStore(conn, dbType);
    var clock = DateTimeOffset.UtcNow;

    var cp = await store.TryStartAsync("probe-projection", "probe-source", "pos-1", clock, default);
    Check(tag, "Checkpoint TryStart 首次返回", cp is not null, cp is null ? "null" : "ok");

    await store.MarkCompletedAsync(cp!, clock.AddSeconds(1), default);
    Check(tag, "Checkpoint Completed 同位置跳过（replay 保护）",
        await store.TryStartAsync("probe-projection", "probe-source", "pos-1", clock.AddSeconds(2), default) is null, "");
    var nextPos = await store.TryStartAsync("probe-projection", "probe-source", "pos-2", clock.AddSeconds(2), default);
    Check(tag, "Checkpoint 新位置可重入", nextPos is not null, nextPos is null ? "null" : "ok");

    await store.MarkFailedAsync(nextPos!, "probe-failure", clock.AddSeconds(3), default);
    Check(tag, "Checkpoint MarkFailed 不抛", true, "");
}

void Check(string tag, string name, bool ok, string detail)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {tag} {name} {detail}");
    if (!ok) failures.Add($"{tag} {name} {detail}");
}
async Task Exec(Func<DbConnection> factory, string sql) { await using var c = factory(); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
internal sealed class ProbeSagaState : PalDDD.Transactions.SagaState;
'''
print(program)
PYEOF

PROBE_ROOT_W="$(cygpath -w "$ROOT" 2>/dev/null || printf '%s' "$ROOT")"
run_token="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
PG_DB="palddd_probe_pg_${run_token}"
MY_DB="palddd_probe_mysql_${run_token}"
validate_generated_database "$PG_DB" palddd_probe_pg_
validate_generated_database "$MY_DB" palddd_probe_mysql_
export PROBE_PG_CS="$PG_CS" PROBE_MY_CS="$MY_CS" PROBE_PG_OK="$PG_OK" PROBE_MY_OK="$MY_OK" PROBE_PG_DB="$PG_DB" PROBE_MY_DB="$MY_DB" PROBE_ROOT="$PROBE_ROOT_W"
printf '运行探针（唯一测试库；ownership marker 校验后才清理）\n'
dotnet run 2>&1
