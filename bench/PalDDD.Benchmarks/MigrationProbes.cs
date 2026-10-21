using ByteAether.Ulid;
using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;
using PalORM;
using PalORM.PostgreSql;
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

    /// <summary>
    /// M1 规模演练：10k 活跃 Saga，NextCheckAt 在 100s 内均匀到期，2s 轮询、256 批量。
    /// 断言单 tick 租约量不超过批量，且任一到期 Saga 的租约延迟不超过 2 个轮询周期。
    /// </summary>
    public static async Task<int> RunM1LoadAsync(bool postgreSql, CancellationToken ct = default)
    {
        const int count = 10_000;
        const int batch = 256;
        var poll = TimeSpan.FromSeconds(2);
        var spread = TimeSpan.FromSeconds(100);

        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;

        await EnsureColumnAsync(conn, postgreSql, "saga_states", "next_check_at",
            postgreSql ? "TIMESTAMPTZ" : "DATETIME(6) NULL", ct);
        await EnsureIndexAsync(conn, postgreSql, "saga_states", "idx_saga_due",
            "status, next_check_at", ct);
        await ExecAsync(conn, "DELETE FROM saga_states", ct);

        // 留 5s 播种宽限，避免把播种耗时算进首行的租约延迟。
        var t0 = DateTimeOffset.UtcNow.AddSeconds(5);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO saga_states (saga_id, current_state, status, created_at, version, next_check_at) VALUES (@id, 'Initial', 0, @created, 0, @next)";
            var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
            var pCreated = cmd.CreateParameter(); pCreated.ParameterName = "@created"; cmd.Parameters.Add(pCreated);
            var pNext = cmd.CreateParameter(); pNext.ParameterName = "@next"; cmd.Parameters.Add(pNext);
            // Prepare 需要可推断的参数类型：先赋同型初值，再复用参数。
            pId.Value = string.Empty;
            pCreated.Value = postgreSql ? t0 : t0.UtcDateTime;
            pNext.Value = postgreSql ? t0 : t0.UtcDateTime;
            await cmd.PrepareAsync(ct);
            for (var i = 0; i < count; i++)
            {
                var due = t0 + TimeSpan.FromTicks(spread.Ticks * i / count);
                var createdAt = t0.AddMilliseconds(i);
                pId.Value = Ulid.New().ToString(null, CultureInfo.InvariantCulture);
                pCreated.Value = postgreSql ? createdAt : createdAt.UtcDateTime;
                pNext.Value = postgreSql ? due : due.UtcDateTime;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        var store = new DapperSagaStateStore<ProbeSagaState>(conn, null, null, null, dialect);
        var lags = new List<double>(count);
        var leased = 0;
        var ticks = 0;
        var maxPerTick = 0;
        var deadline = t0 + spread + (poll * 4);
        while (leased < count && DateTimeOffset.UtcNow < deadline)
        {
            var tickStart = DateTimeOffset.UtcNow;
            var rows = await store.LeaseDueSagasAsync(
                $"m1-load-{ticks}", TimeSpan.FromMinutes(5), batch, tickStart, ct);
            var leasedAt = DateTimeOffset.UtcNow;
            ticks++;
            maxPerTick = Math.Max(maxPerTick, rows.Count);
            foreach (var row in rows)
            {
                if (row.NextCheckAt is not { } due)
                    throw new InvalidOperationException("M1 load leased a row without NextCheckAt");
                lags.Add((leasedAt - due).TotalMilliseconds);
            }
            leased += rows.Count;

            var wait = tickStart + poll - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
        }

        if (leased != count)
            throw new InvalidOperationException($"M1 load leased {leased}/{count} (ticks={ticks})");
        if (maxPerTick > batch)
            throw new InvalidOperationException($"M1 load leased {maxPerTick} in one tick (batch={batch})");

        lags.Sort();
        var p50 = lags[(int)(lags.Count * 0.50)];
        var p99 = lags[(int)Math.Ceiling((lags.Count - 1) * 0.99)];
        var max = lags[^1];
        var threshold = 2 * poll.TotalMilliseconds;
        if (max > threshold)
            throw new InvalidOperationException(
                $"M1 load tail {max:F0}ms exceeds {threshold:F0}ms (2 poll cycles)");

        Console.WriteLine(
            $"M1 LOAD PASS ({dialect}): active={count}, leased={leased}, ticks={ticks}, " +
            $"maxPerTick={maxPerTick}, p50={p50:F0}ms, p99={p99:F0}ms, max={max:F0}ms, threshold={threshold:F0}ms");
        return 0;
    }

    public static Task<int> RunM4Async(bool postgreSql, CancellationToken ct = default)
        => RunM4CoreAsync(postgreSql, count: 500, batch: 100, measureIndexSizes: false, ct);

    /// <summary>
    /// M4 规模演练：10 万行旧数据回填，测量回填吞吐与文本/二进制唯一索引体积。
    /// </summary>
    public static Task<int> RunM4ScaleAsync(bool postgreSql, CancellationToken ct = default)
        => RunM4CoreAsync(postgreSql, count: 100_000, batch: 1_000, measureIndexSizes: true, ct);

    /// <summary>
    /// PalORM PG 单事件追加快路径行为探针：四种 ExpectedStreamVersion、守卫失匹、
    /// EventId 重复分类与读回。仅连接 palddd_bench 前缀库。
    /// </summary>
    public static async Task<int> RunPalOrmAppendPgAsync(CancellationToken ct = default)
    {
        var connectionString = Environment.GetEnvironmentVariable("PALDDD_BENCH_PG_CONN");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("缺少 PALDDD_BENCH_PG_CONN（库名须 palddd_bench 前缀）");
        var database = (string?)new DbConnectionStringBuilder { ConnectionString = connectionString }["Database"];
        if (database is null || !database.StartsWith("palddd_bench", StringComparison.Ordinal))
            throw new InvalidOperationException($"探针拒绝连接非专用库 '{database}'（须 palddd_bench 前缀）");

        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(
            DbOptions.Development(connectionString), ct);
        var log = new PalOrmEventLog<PostgreSqlProvider>(session);
        const string stream = "probe-palorm-append";
        await DeleteStreamAsync(session, stream, ct);

        static EventData NewEvent(string name)
            => new(Ulid.New(), name, 1, "application/json",
                new byte[] { 0x7B, 0x7D }, ReadOnlyMemory<byte>.Empty, EventAuditMetadata.Empty);

        try
        {
            var r1 = await log.AppendAsync(stream, ExpectedStreamVersion.NoStream, [NewEvent("e1")], ct);
            if (r1.FirstStreamVersion != 0 || r1.LastStreamVersion != 0)
                throw new InvalidOperationException($"NoStream expected version 0, got {r1.FirstStreamVersion}");
            var r2 = await log.AppendAsync(stream, ExpectedStreamVersion.Any, [NewEvent("e2")], ct);
            if (r2.FirstStreamVersion != 1)
                throw new InvalidOperationException($"Any expected version 1, got {r2.FirstStreamVersion}");
            var r3 = await log.AppendAsync(stream, ExpectedStreamVersion.Exact(1), [NewEvent("e3")], ct);
            if (r3.FirstStreamVersion != 2)
                throw new InvalidOperationException($"Exact expected version 2, got {r3.FirstStreamVersion}");
            var r4 = await log.AppendAsync(stream, ExpectedStreamVersion.StreamExists, [NewEvent("e4")], ct);
            if (r4.FirstStreamVersion != 3)
                throw new InvalidOperationException($"StreamExists expected version 3, got {r4.FirstStreamVersion}");

            var guardRejected = false;
            try
            {
                await log.AppendAsync(stream, ExpectedStreamVersion.NoStream, [NewEvent("e5")], ct);
            }
            catch (EventStreamConcurrencyException)
            {
                guardRejected = true;
            }
            if (!guardRejected)
                throw new InvalidOperationException("NoStream guard did not reject existing stream");

            var duplicate = NewEvent("dup");
            await log.AppendAsync(stream, ExpectedStreamVersion.Any, [duplicate], ct);
            var duplicateRejectedAsConcurrency = false;
            try
            {
                await log.AppendAsync(stream, ExpectedStreamVersion.Any, [duplicate], ct);
            }
            catch (EventStreamConcurrencyException)
            {
                duplicateRejectedAsConcurrency = true;
            }
            catch (DbException)
            {
                // 期望形态：EventId 重复原样上抛（不误译并发）。
            }
            if (duplicateRejectedAsConcurrency)
                throw new InvalidOperationException("EventId duplicate was misclassified as concurrency");

            var read = 0;
            await foreach (var unused in log.ReadStreamAsync(stream, 0, 100, ct))
                read++;
            if (read != 5)
                throw new InvalidOperationException($"read expected 5, got {read}");

            Console.WriteLine($"PALORM PG APPEND PASS: versions=0..3, guard=rejected, duplicate=original, read={read}");
            return 0;
        }
        finally
        {
            await DeleteStreamAsync(session, stream, ct);
        }
    }

    /// <summary>
    /// Outbox PG 强类型 COPY 探针（2026-10-21）：1000 行写入后按计数、载荷、追踪列与
    /// 可用时刻回读，验证专用写入与通用路径的列语义一致。仅连接 palddd_bench 前缀库。
    /// </summary>
    public static async Task<int> RunOutboxBulkProbeAsync(bool postgreSql, CancellationToken ct = default)
    {
        await using var session = await RemoteDialectBenchSession.CreateAsync(isPostgreSql: postgreSql, ct);
        var conn = session.Connection;
        await ExecAsync(conn, "DELETE FROM outbox_messages", ct);

        const int count = 1000;
        var baseline = DateTimeOffset.UtcNow;
        var messages = new List<OutboxMessage>(count);
        for (var i = 0; i < count; i++)
        {
            messages.Add(new OutboxMessage
            {
                Type = "probe.outbox.v1",
                Payload = System.Text.Encoding.UTF8.GetBytes($"{{\"i\":{i}}}"),
                ContentType = "application/json",
                SchemaVersion = 1,
                CreatedAt = baseline.AddMilliseconds(i),
                CorrelationId = Ulid.New(),
                CausationId = Ulid.New(),
                TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                TraceState = "tenant=probe"
            });
        }

        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;
        var store = new DapperOutboxStore(conn, dialect);
        var inserted = await store.AddMessagesAsync(messages, ct);
        if (inserted != count)
            throw new InvalidOperationException($"inserted expected {count}, got {inserted}");

        var stored = Convert.ToInt64(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM outbox_messages WHERE status = 0", ct), CultureInfo.InvariantCulture);
        if (stored != count)
            throw new InvalidOperationException($"stored expected {count}, got {stored}");

        var sample = messages[count - 1];
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload, correlation_id, causation_id, trace_parent, trace_state, next_attempt_at FROM outbox_messages WHERE id = @id";
        var idParam = cmd.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = sample.Id.ToString(null, CultureInfo.InvariantCulture);
        cmd.Parameters.Add(idParam);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("sample row not found");

        if (!reader.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(sample.Payload))
            throw new InvalidOperationException("payload mismatch");
        if (reader.GetString(1) != sample.CorrelationId!.Value.ToString(null, CultureInfo.InvariantCulture))
            throw new InvalidOperationException("correlation mismatch");
        if (reader.GetString(2) != sample.CausationId!.Value.ToString(null, CultureInfo.InvariantCulture))
            throw new InvalidOperationException("causation mismatch");
        if (reader.GetString(3) != sample.TraceParent)
            throw new InvalidOperationException("traceparent mismatch");
        if (reader.GetString(4) != sample.TraceState)
            throw new InvalidOperationException("tracestate mismatch");

        // PG timestamptz 精度为微秒；源值 100ns 分辨率，允许 <10 ticks 的截断差。
        var nextAttempt = reader.GetDateTime(5);
        var expectedNext = sample.NextAttemptAt!.Value.UtcDateTime;
        if (Math.Abs(nextAttempt.Ticks - expectedNext.Ticks) >= 10)
            throw new InvalidOperationException(
                $"next_attempt_at mismatch: stored={nextAttempt:O}, expected={expectedNext:O}");

        Console.WriteLine(
            $"OUTBOX BULK PASS ({(postgreSql ? "PG" : "MySQL")}): inserted={inserted}, stored={stored}, sample=ok");
        return 0;
    }

    /// <summary>
    /// Inbox 批量认领探针（2026-10-21）：100 条一次认领、重复认领为空、批量完成标记、
    /// 再次认领为空。PG/SQLite 走 INSERT/UPDATE RETURNING，MySQL 走读-插-更-回读。
    /// </summary>
    public static async Task<int> RunInboxBatchProbeAsync(bool postgreSql, CancellationToken ct = default)
    {
        await using var session = await RemoteDialectBenchSession.CreateAsync(isPostgreSql: postgreSql, ct);
        var conn = session.Connection;
        await ExecAsync(conn, "DELETE FROM inbox_messages", ct);

        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;
        var store = new DapperInboxStore(conn, dialect);
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 100).Select(static i => "batch-" + i).ToList();

        var claimed = await store.TryStartProcessingManyAsync(
            "probe", ids, now, TimeSpan.FromMinutes(5), ct);
        if (claimed.Count != ids.Count)
            throw new InvalidOperationException($"claimed expected {ids.Count}, got {claimed.Count}");

        var duplicate = await store.TryStartProcessingManyAsync(
            "probe", ids, now.AddSeconds(1), TimeSpan.FromMinutes(5), ct);
        if (duplicate.Count != 0)
            throw new InvalidOperationException($"duplicate claim expected 0, got {duplicate.Count}");

        var marked = await store.MarkProcessedManyAsync("probe", claimed, now.AddSeconds(2), ct);
        if (marked != ids.Count)
            throw new InvalidOperationException($"marked expected {ids.Count}, got {marked}");

        var after = await store.TryStartProcessingManyAsync(
            "probe", ids, now.AddSeconds(3), TimeSpan.FromMinutes(5), ct);
        if (after.Count != 0)
            throw new InvalidOperationException($"post-mark claim expected 0, got {after.Count}");

        Console.WriteLine(
            $"INBOX BATCH PASS ({(postgreSql ? "PG" : "MySQL")}): claimed={claimed.Count}, marked={marked}, duplicate=0");
        return 0;
    }

    private static async Task DeleteStreamAsync(
        DataSession<PostgreSqlProvider> session, string stream, CancellationToken ct)
    {
        await using var cmd = session.GetRawConnection().CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE stream_name = @p0";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p0";
        p.Value = stream;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> RunM4CoreAsync(
        bool postgreSql, int count, int batch, bool measureIndexSizes, CancellationToken ct)
    {
        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;

        await EnsureColumnAsync(conn, postgreSql, "events", "event_id_bin",
            postgreSql ? "BYTEA" : "BINARY(16) NULL", ct);
        await EnsureIndexAsync(conn, postgreSql, "events", "idx_events_event_id_bin",
            "event_id_bin", ct);

        const string stream = "probe-m4";
        await ExecAsync(conn, "DELETE FROM events WHERE stream_name = @p0", ct, stream);

        var ids = new List<Ulid>(count);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var recordedAt = postgreSql ? (object)DateTimeOffset.UtcNow : DateTime.UtcNow;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO events (event_id, event_name, stream_name, stream_version, payload, recorded_at) VALUES (@id, 'probe.event', @stream, @sv, @payload, @now)";
            var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
            var pStream = cmd.CreateParameter(); pStream.ParameterName = "@stream"; pStream.Value = stream; cmd.Parameters.Add(pStream);
            var pSv = cmd.CreateParameter(); pSv.ParameterName = "@sv"; cmd.Parameters.Add(pSv);
            var pPayload = cmd.CreateParameter(); pPayload.ParameterName = "@payload"; pPayload.Value = new byte[] { 0x7B, 0x7D }; cmd.Parameters.Add(pPayload);
            var pNow = cmd.CreateParameter(); pNow.ParameterName = "@now"; pNow.Value = recordedAt; cmd.Parameters.Add(pNow);
            pId.Value = string.Empty;
            pSv.Value = 0;
            await cmd.PrepareAsync(ct);
            for (var i = 0; i < count; i++)
            {
                var id = Ulid.New();
                ids.Add(id);
                pId.Value = id.ToString(null, CultureInfo.InvariantCulture);
                pSv.Value = i;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        var insertMs = started.ElapsedMilliseconds;

        // 分批回填：每次取 batch 行，模拟应用侧迁移任务；每批一个事务。
        var backfilled = 0;
        var backfillStarted = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var rows = new List<(long Position, string Text)>();
            await using (var select = conn.CreateCommand())
            {
                select.CommandText = "SELECT global_position, event_id FROM events WHERE stream_name = @p0 AND event_id_bin IS NULL ORDER BY global_position LIMIT @limit";
                Add(select, "@p0", stream);
                Add(select, "@limit", batch);
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            if (rows.Count == 0)
                break;

            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE events SET event_id_bin = @bin WHERE global_position = @pos AND event_id_bin IS NULL";
            var pBin = update.CreateParameter(); pBin.ParameterName = "@bin"; update.Parameters.Add(pBin);
            var pPos = update.CreateParameter(); pPos.ParameterName = "@pos"; update.Parameters.Add(pPos);
            pBin.Value = new byte[16];
            pPos.Value = 0L;
            await update.PrepareAsync(ct);
            foreach (var (position, text) in rows)
            {
                pBin.Value = Ulid.Parse(text, CultureInfo.InvariantCulture).ToByteArray();
                pPos.Value = position;
                backfilled += await update.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        var backfillMs = backfillStarted.ElapsedMilliseconds;
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

        var indexStarted = System.Diagnostics.Stopwatch.StartNew();
        if (measureIndexSizes)
            await DropIndexIfExistsAsync(conn, postgreSql, "events", "idx_events_event_id_bin_uq", ct);
        await EnsureUniqueIndexAsync(conn, postgreSql, "events", "idx_events_event_id_bin_uq", "event_id_bin", ct);
        var indexMs = indexStarted.ElapsedMilliseconds;

        long textBytes = 0, binBytes = 0;
        if (measureIndexSizes)
        {
            (textBytes, binBytes) = await ReadIndexSizesAsync(conn, postgreSql, ct);
            if (textBytes <= 0 || binBytes <= 0 || binBytes >= textBytes)
                throw new InvalidOperationException(
                    $"M4 index size unexpected: text={textBytes}B, bin={binBytes}B");
        }

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
        if (measureIndexSizes)
        {
            var reduction = (1 - (binBytes / (double)textBytes)) * 100;
            Console.WriteLine(
                $"M4 SCALE PASS ({dialect}): seeded={count}, backfilled={backfilled}, sample={sample.Count}, read={read}, " +
                $"insert={insertMs}ms, backfill={backfillMs}ms, index={indexMs}ms, " +
                $"text_idx={textBytes}B, bin_idx={binBytes}B, reduction={reduction:F1}%, elapsed={started.ElapsedMilliseconds}ms");
        }
        else
        {
            Console.WriteLine(
                $"M4 PASS ({dialect}): seeded={count}, backfilled={backfilled}, sample={sample.Count}, read={read}, " +
                $"elapsed={started.ElapsedMilliseconds}ms");
        }
        return 0;
    }

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

    private static async Task<(long Text, long Bin)> ReadIndexSizesAsync(
        DbConnection conn, bool postgreSql, CancellationToken ct)
    {
        if (postgreSql)
        {
            await ExecAsync(conn, "ANALYZE events", ct);
            var text = Convert.ToInt64(await ScalarAsync(conn,
                "SELECT pg_relation_size('idx_events_event_id')", ct), CultureInfo.InvariantCulture);
            var bin = Convert.ToInt64(await ScalarAsync(conn,
                "SELECT pg_relation_size('idx_events_event_id_bin_uq')", ct), CultureInfo.InvariantCulture);
            return (text, bin);
        }

        await using (var analyze = conn.CreateCommand())
        {
            analyze.CommandText = "ANALYZE TABLE events";
            await using var reader = await analyze.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // 消费 ANALYZE 的结果集。
            }
        }

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT index_name, SUM(stat_value) * @@innodb_page_size FROM mysql.innodb_index_stats " +
                "WHERE database_name = DATABASE() AND table_name = 'events' AND stat_name = 'size' " +
                "AND index_name IN ('idx_events_event_id', 'idx_events_event_id_bin_uq') GROUP BY index_name";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sizes[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        return (sizes["idx_events_event_id"], sizes["idx_events_event_id_bin_uq"]);
    }

    private static async Task DropIndexIfExistsAsync(
        DbConnection conn, bool postgreSql, string table, string index, CancellationToken ct)
    {
        var exists = Convert.ToInt64(await ScalarAsync(conn,
            postgreSql
                ? "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @p0 AND indexname = @p1"
                : "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @p0 AND index_name = @p1",
            ct, table, index), CultureInfo.InvariantCulture) > 0;
        if (!exists)
            return;
        await ExecAsync(conn,
            postgreSql ? $"DROP INDEX {index}" : $"DROP INDEX {index} ON {table}", ct);
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
