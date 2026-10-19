using ByteAether.Ulid;
using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.Transactions;
using System.Data.Common;
using System.Globalization;

namespace PalDDD.Benchmarks;

/// <summary>
/// 迁移演练探针（2026-10-19）：
/// --m1-probe-pg / --m1-probe-mysql：在真库上执行 Saga 到期索引 expand，验证
///   LeaseDueSagasAsync 只租到期行，旧 LeaseActiveSagasAsync 仍可取其余活跃行。
/// --m4-probe-pg / --m4-probe-mysql：在真库上执行 EventId 二进制列 expand，播种旧行、
///   分批回填、创建唯一索引，并用 UseBinaryEventId 读回校验。
/// 仅允许连接库名以 palddd_bench 开头的专用库（会清空探针数据）。
/// </summary>
internal static class MigrationProbes
{
    public static async Task<int> RunM1Async(bool postgreSql, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;

        await EnsureColumnAsync(conn, postgreSql, "saga_states", "next_check_at",
            postgreSql ? "TIMESTAMPTZ" : "DATETIME(6) NULL", ct);
        await EnsureIndexAsync(conn, postgreSql, "saga_states", "idx_saga_due",
            "status, next_check_at", ct);

        await ExecAsync(conn, "DELETE FROM saga_states", ct);

        var now = DateTimeOffset.UtcNow;
        var dueId = Ulid.New();
        var futureId = Ulid.New();
        var idleId = Ulid.New();
        await InsertSagaAsync(conn, postgreSql, dueId, "Initial", 0, now, now.AddMinutes(-1), ct);
        await InsertSagaAsync(conn, postgreSql, futureId, "Initial", 0, now, now.AddHours(1), ct);
        await InsertSagaAsync(conn, postgreSql, idleId, "Initial", 0, now, null, ct);

        var store = new DapperSagaStateStore<ProbeSagaState>(conn, null, null, null, dialect);
        var due = await store.LeaseDueSagasAsync("probe", TimeSpan.FromMinutes(1), 10, now, ct);
        if (due.Count != 1 || due[0].SagaId != dueId)
            throw new InvalidOperationException($"M1 due lease expected 1 due saga, got {due.Count}");

        var active = await store.LeaseActiveSagasAsync("probe", TimeSpan.FromMinutes(1), 10, ct);
        if (active.Count != 2 || active.Any(s => s.SagaId == dueId))
            throw new InvalidOperationException($"M1 active lease expected 2 non-due sagas, got {active.Count}");

        Console.WriteLine($"M1 PASS ({dialect}): due=1, active-non-due={active.Count}, index=idx_saga_due");
        return 0;
    }

    public static async Task<int> RunM4Async(bool postgreSql, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;

        await EnsureColumnAsync(conn, postgreSql, "events", "event_id_bin",
            postgreSql ? "BYTEA" : "BINARY(16) NULL", ct);
        await EnsureIndexAsync(conn, postgreSql, "events", "idx_events_event_id_bin",
            "event_id_bin", ct);

        const string stream = "probe-m4";
        const int count = 500;
        await ExecAsync(conn, $"DELETE FROM events WHERE stream_name = @p0", ct, stream);

        var ids = new List<Ulid>(count);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            for (var i = 0; i < count; i++)
            {
                var id = Ulid.New();
                ids.Add(id);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = postgreSql
                    ? "INSERT INTO events (event_id, event_name, stream_name, stream_version, payload, recorded_at) VALUES (@id, 'probe.event', @stream, @sv, @payload, @now)"
                    : "INSERT INTO events (event_id, event_name, stream_name, stream_version, payload, recorded_at) VALUES (@id, 'probe.event', @stream, @sv, @payload, @now)";
                Add(cmd, "@id", id.ToString(null, CultureInfo.InvariantCulture));
                Add(cmd, "@stream", stream);
                Add(cmd, "@sv", i);
                Add(cmd, "@payload", new byte[] { 0x7B, 0x7D });
                Add(cmd, "@now", postgreSql ? now() : now().UtcDateTime);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        // 分批回填：每次取 100 行，模拟应用侧迁移任务。
        var backfilled = 0;
        while (true)
        {
            var rows = new List<(long Position, string Text)>();
            await using (var select = conn.CreateCommand())
            {
                select.CommandText = postgreSql
                    ? "SELECT global_position, event_id FROM events WHERE stream_name = @p0 AND event_id_bin IS NULL ORDER BY global_position LIMIT 100"
                    : "SELECT global_position, event_id FROM events WHERE stream_name = @p0 AND event_id_bin IS NULL ORDER BY global_position LIMIT 100";
                Add(select, "@p0", stream);
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            if (rows.Count == 0)
                break;

            foreach (var (position, text) in rows)
            {
                var bytes = Ulid.Parse(text, CultureInfo.InvariantCulture).ToByteArray();
                await using var update = conn.CreateCommand();
                update.CommandText = "UPDATE events SET event_id_bin = @bin WHERE global_position = @pos AND event_id_bin IS NULL";
                Add(update, "@bin", bytes);
                Add(update, "@pos", position);
                backfilled += await update.ExecuteNonQueryAsync(ct);
            }
        }
        if (backfilled != count)
            throw new InvalidOperationException($"M4 backfill expected {count}, got {backfilled}");

        var nulls = Convert.ToInt64(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM events WHERE stream_name = @p0 AND event_id_bin IS NULL", ct, stream), CultureInfo.InvariantCulture);
        if (nulls != 0)
            throw new InvalidOperationException($"M4 backfill left {nulls} NULL rows");

        // 抽样校验文本解析与二进制重建一致。
        var sample = new List<(string Text, byte[] Bin)>();
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT event_id, event_id_bin FROM events WHERE stream_name = @p0 ORDER BY global_position LIMIT 10";
            Add(check, "@p0", stream);
            await using var reader = await check.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sample.Add((reader.GetString(0), reader.GetFieldValue<byte[]>(1)));
        }
        foreach (var (text, bin) in sample)
        {
            if (!string.Equals(Ulid.New(bin).ToString(null, CultureInfo.InvariantCulture), text, StringComparison.Ordinal))
                throw new InvalidOperationException("M4 binary/text mismatch");
        }

        await EnsureUniqueIndexAsync(conn, postgreSql, "events", "idx_events_event_id_bin_uq", "event_id_bin", ct);

        var log = new DapperEventLog(conn, null, dialect) { UseBinaryEventId = true };
        var read = 0;
        var first = true;
        await foreach (var evt in log.ReadStreamAsync(stream, 0, 10, ct))
        {
            if (first)
            {
                if (evt.EventId != ids[0])
                    throw new InvalidOperationException("M4 binary read EventId mismatch");
                first = false;
            }
            read++;
        }
        if (read == 0)
            throw new InvalidOperationException("M4 binary read returned no events");

        await ExecAsync(conn, "DELETE FROM events WHERE stream_name = @p0", ct, stream);
        Console.WriteLine($"M4 PASS ({dialect}): seeded={count}, backfilled={backfilled}, sample={sample.Count}, read={read}, elapsed={started.ElapsedMilliseconds}ms");
        return 0;
    }

    private static DateTimeOffset now() => DateTimeOffset.UtcNow;

    private static async Task<DbConnection> OpenAsync(bool postgreSql, CancellationToken ct)
    {
        var connectionString = postgreSql
            ? Environment.GetEnvironmentVariable("PALDDD_BENCH_PG_CONN")
            : Environment.GetEnvironmentVariable("PALDDD_BENCH_MYSQL_CONN");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"缺少 {(postgreSql ? "PALDDD_BENCH_PG_CONN" : "PALDDD_BENCH_MYSQL_CONN")}（库名须 palddd_bench 前缀）");

        var database = (string?)new DbConnectionStringBuilder { ConnectionString = connectionString }["Database"];
        if (database is null || !database.StartsWith("palddd_bench", StringComparison.Ordinal))
            throw new InvalidOperationException($"探针拒绝连接非专用库 '{database}'（须 palddd_bench 前缀）");

        DbConnection connection = postgreSql
            ? new NpgsqlConnection(connectionString)
            : new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task InsertSagaAsync(
        DbConnection conn, bool postgreSql, Ulid id, string state, int status,
        DateTimeOffset created, DateTimeOffset? nextCheck, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO saga_states (saga_id, current_state, status, created_at, version, next_check_at) VALUES (@id, @cs, @st, @created, 0, @next)";
        Add(cmd, "@id", id.ToString(null, CultureInfo.InvariantCulture));
        Add(cmd, "@cs", state);
        Add(cmd, "@st", status);
        Add(cmd, "@created", postgreSql ? created : created.UtcDateTime);
        Add(cmd, "@next", nextCheck is null ? DBNull.Value : postgreSql ? nextCheck.Value : nextCheck.Value.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureColumnAsync(
        DbConnection conn, bool postgreSql, string table, string column, string type, CancellationToken ct)
    {
        var exists = Convert.ToInt64(await ScalarAsync(conn,
            postgreSql
                ? "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = @p0 AND column_name = @p1"
                : "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @p0 AND column_name = @p1",
            ct, table, column), CultureInfo.InvariantCulture) > 0;
        if (exists)
            return;

        await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {type}", ct);
    }

    private static async Task EnsureIndexAsync(
        DbConnection conn, bool postgreSql, string table, string index, string columns, CancellationToken ct)
    {
        var exists = Convert.ToInt64(await ScalarAsync(conn,
            postgreSql
                ? "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @p0 AND indexname = @p1"
                : "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @p0 AND index_name = @p1",
            ct, table, index), CultureInfo.InvariantCulture) > 0;
        if (!exists)
            await ExecAsync(conn, $"CREATE INDEX {index} ON {table}({columns})", ct);
    }

    private static async Task EnsureUniqueIndexAsync(
        DbConnection conn, bool postgreSql, string table, string index, string columns, CancellationToken ct)
    {
        var exists = Convert.ToInt64(await ScalarAsync(conn,
            postgreSql
                ? "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @p0 AND indexname = @p1"
                : "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @p0 AND index_name = @p1",
            ct, table, index), CultureInfo.InvariantCulture) > 0;
        if (!exists)
            await ExecAsync(conn, $"CREATE UNIQUE INDEX {index} ON {table}({columns})", ct);
    }

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
            Add(cmd, "@p" + i.ToString(CultureInfo.InvariantCulture), values[i]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(
        DbConnection conn, string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
            Add(cmd, "@p" + i.ToString(CultureInfo.InvariantCulture), values[i]);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private sealed class ProbeSagaState : SagaState
    {
    }
}
