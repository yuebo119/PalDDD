using ByteAether.Ulid;
using Dapper;
using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;
using PalORM;
using PalORM.PostgreSql;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

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

    /// <summary>
    /// #45（2026-10-24 产品性能轮）：Saga 批量推进 NextCheckAt 的真库语句数/计划。
    /// 256 行一次 Advance；MySQL 用 Com_update 增量计数，PG 用 xact_commit 增量加源码
    /// 语句数（每 300 行一语句）；两边跑 EXPLAIN ANALYZE 并回滚，验证走主键索引。
    /// </summary>
    public static async Task<int> RunSagaAdvanceProbeAsync(bool postgreSql, CancellationToken ct = default)
    {
        const int count = 256;
        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;
        await EnsureColumnAsync(conn, postgreSql, "saga_states", "next_check_at",
            postgreSql ? "TIMESTAMPTZ" : "DATETIME(6) NULL", ct);
        await ExecAsync(conn, "DELETE FROM saga_states", ct);

        var now = DateTimeOffset.UtcNow;
        var next = now.AddMinutes(1);
        var until = now.AddMinutes(5);
        var sagas = new List<ProbeSagaState>(count);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO saga_states (saga_id, current_state, status, created_at, version, next_check_at, leased_by, leased_until) " +
                "VALUES (@id, 'Initial', 0, @created, 0, @next, @owner, @until)";
            var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
            var pCreated = cmd.CreateParameter(); pCreated.ParameterName = "@created"; cmd.Parameters.Add(pCreated);
            var pNext = cmd.CreateParameter(); pNext.ParameterName = "@next"; cmd.Parameters.Add(pNext);
            var pOwner = cmd.CreateParameter(); pOwner.ParameterName = "@owner"; cmd.Parameters.Add(pOwner);
            var pUntil = cmd.CreateParameter(); pUntil.ParameterName = "@until"; cmd.Parameters.Add(pUntil);
            pId.Value = string.Empty;
            pCreated.Value = postgreSql ? now : now.UtcDateTime;
            pNext.Value = postgreSql ? now : now.UtcDateTime;
            pOwner.Value = "probe";
            pUntil.Value = postgreSql ? until : until.UtcDateTime;
            await cmd.PrepareAsync(ct);
            for (var i = 0; i < count; i++)
            {
                var id = Ulid.New();
                pId.Value = id.ToString(null, CultureInfo.InvariantCulture);
                pCreated.Value = postgreSql ? now.AddMilliseconds(i) : now.AddMilliseconds(i).UtcDateTime;
                await cmd.ExecuteNonQueryAsync(ct);
                sagas.Add(new ProbeSagaState
                {
                    SagaId = id,
                    Version = 0,
                    NextCheckAt = next,
                    LeasedBy = "probe",
                    LeasedUntil = until,
                });
            }
            await tx.CommitAsync(ct);
        }

        var store = new DapperSagaStateStore<ProbeSagaState>(conn, null, null, null, dialect);
        var before = await ReadSagaUpdateCounterAsync(conn, postgreSql, ct);
        var sw = Stopwatch.StartNew();
        var affected = await store.AdvanceNextCheckAtAsync("probe", sagas, ct);
        sw.Stop();
        var after = await ReadSagaUpdateCounterAsync(conn, postgreSql, ct);
        if (affected != count)
            throw new InvalidOperationException($"Saga advance affected {affected}/{count}");
        // 全局计数只作参考：PG pg_stat_database 有提交后可见性延迟，MySQL Com_update
        // 对这条 Dapper 命令未即时增量（可能是 prepared 执行路径）。语句数以源码块宽
        // 为准（256 行 ≤ 300 → 1 条 UPDATE），EXPLAIN 计划作第二证据。

        var build = typeof(DapperSagaStateStore<ProbeSagaState>).GetMethod(
            "BuildAdvanceNextCheckBatch", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("BuildAdvanceNextCheckBatch not found");
        var (sql, parameters) = ((string Sql, DynamicParameters Parameters))build.Invoke(
            store, ["probe", sagas, 0, count])!;
        var prefix = postgreSql ? "EXPLAIN (ANALYZE, BUFFERS) " : "EXPLAIN ANALYZE ";
        List<string> plan;
        await using (var planTx = await conn.BeginTransactionAsync(ct))
        {
            plan = (await conn.QueryAsync<string>(prefix + sql, parameters, planTx).ConfigureAwait(false)).ToList();
            await planTx.RollbackAsync(ct);
        }

        var counterName = postgreSql ? "xactDelta" : "updateStatements";
        var sourceStatements = (count + 299) / 300;
        Console.WriteLine(
            $"SAGA ADVANCE PASS ({dialect}): rows={count}, affected={affected}, " +
            $"{counterName}={after - before}, sourceStatements={sourceStatements}, " +
            $"elapsed={sw.Elapsed.TotalMilliseconds:F1}ms");
        foreach (var line in plan.Take(12)) Console.WriteLine("  " + line);
        return 0;
    }

    /// <summary>
    /// #46（2026-10-24 产品性能轮）：legacy 活跃扫描在 10k 活跃、256 批量下的轮转覆盖。
    /// 每 tick 租约后以 checkedAt 释放写游标；断言所有 10k 行在 ≤40 tick 内至少被租一次
    ///（旧形态释放清 NULL 会让前 256 行永远重租，seen 停在 256）。
    /// </summary>
    public static async Task<int> RunSagaRotateProbeAsync(bool postgreSql, CancellationToken ct = default)
    {
        const int count = 10_000;
        const int batch = 256;
        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;
        await EnsureColumnAsync(conn, postgreSql, "saga_states", "next_check_at",
            postgreSql ? "TIMESTAMPTZ" : "DATETIME(6) NULL", ct);
        await ExecAsync(conn, "DELETE FROM saga_states", ct);

        var t0 = DateTimeOffset.UtcNow.AddSeconds(1);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO saga_states (saga_id, current_state, status, created_at, version, next_check_at) " +
                "VALUES (@id, 'Initial', 0, @created, 0, NULL)";
            var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
            var pCreated = cmd.CreateParameter(); pCreated.ParameterName = "@created"; cmd.Parameters.Add(pCreated);
            pId.Value = string.Empty;
            pCreated.Value = postgreSql ? t0 : t0.UtcDateTime;
            await cmd.PrepareAsync(ct);
            for (var i = 0; i < count; i++)
            {
                pId.Value = Ulid.New().ToString(null, CultureInfo.InvariantCulture);
                pCreated.Value = postgreSql ? t0.AddMilliseconds(i) : t0.AddMilliseconds(i).UtcDateTime;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        var store = new DapperSagaStateStore<ProbeSagaState>(conn, null, null, null, dialect);
        var cursor = (ISagaScanCursorStore<ProbeSagaState>)store;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tickMs = new List<double>(64);
        var maxPerTick = 0;
        var ticks = 0;
        while (seen.Count < count && ticks < 60)
        {
            var owner = $"rotate-{ticks}";
            var sw = Stopwatch.StartNew();
            var rows = await store.LeaseActiveSagasAsync(owner, TimeSpan.FromMinutes(5), batch, ct);
            sw.Stop();
            tickMs.Add(sw.Elapsed.TotalMilliseconds);
            ticks++;
            if (rows.Count > batch)
                throw new InvalidOperationException($"rotation leased {rows.Count} > batch {batch}");
            maxPerTick = Math.Max(maxPerTick, rows.Count);
            foreach (var row in rows)
                seen.Add(row.SagaId.ToString(null, CultureInfo.InvariantCulture));
            if (rows.Count > 0)
                await cursor.ReleaseLeasesAsync(owner, rows, DateTimeOffset.UtcNow, ct);
        }

        if (seen.Count != count)
            throw new InvalidOperationException($"rotation leased {seen.Count}/{count} in {ticks} ticks");

        tickMs.Sort();
        var p50 = tickMs[(int)(tickMs.Count * 0.50)];
        var p99 = tickMs[(int)Math.Ceiling((tickMs.Count - 1) * 0.99)];
        Console.WriteLine(
            $"SAGA ROTATE PASS ({dialect}): active={count}, seen={seen.Count}, ticks={ticks}, " +
            $"maxPerTick={maxPerTick}, p50={p50:F1}ms, p99={p99:F1}ms");
        return 0;
    }

    private static async Task<long> ReadSagaUpdateCounterAsync(
        DbConnection conn, bool postgreSql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (postgreSql)
        {
            cmd.CommandText = "SELECT xact_commit FROM pg_stat_database WHERE datname = current_database()";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        cmd.CommandText = "SHOW GLOBAL STATUS LIKE 'Com_update'";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
            : -1;
    }

    /// <summary>
    /// #43（2026-10-24 产品性能轮）：幂等 StartOrGet 冲突路径基线/复测。
    /// Completed 与 Processing 各 100 次，验证结果仍为 Existing；用于对比
    /// 「INSERT + 四列分类读 + 整行读」与「INSERT + 整行读」的墙钟差。
    /// </summary>
    public static async Task<int> RunIdempotencyStartOrGetProbeAsync(bool postgreSql, CancellationToken ct = default)
    {
        const int iterations = 100;
        await using var conn = await OpenAsync(postgreSql, ct);
        var dialect = postgreSql ? DapperDbType.PostgreSql : DapperDbType.MySql;
        await ExecAsync(conn, "DELETE FROM idempotency_records", ct);

        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now.AddMinutes(5);
        var expiresAt = now.AddHours(1);
        await SeedIdempotencyRecordAsync(conn, postgreSql, "probe-completed", "k1",
            (int)IdempotencyRecordStatus.Completed, lockedUntil, expiresAt, now, "done"u8.ToArray(), 1, ct);
        await SeedIdempotencyRecordAsync(conn, postgreSql, "probe-processing", "k2",
            (int)IdempotencyRecordStatus.Processing, lockedUntil, expiresAt, now, null, 1, ct);

        var store = new DapperIdempotencyStore(conn, dialect);
        var startOrGet = (IIdempotencyStartOrGetStore)store;
        var policy = new IdempotencyPolicy
        {
            ProcessingTimeout = TimeSpan.FromMinutes(5),
            Retention = TimeSpan.FromHours(1),
        };

        var (completedMean, completedP50) = await MeasureStartOrGetAsync(startOrGet, "probe-completed", "k1", now, policy, iterations, ct);
        var (processingMean, processingP50) = await MeasureStartOrGetAsync(startOrGet, "probe-processing", "k2", now, policy, iterations, ct);
        Console.WriteLine(
            $"IDEM STARTORQ PASS ({dialect}): iterations={iterations}, " +
            $"completed mean={completedMean:F3}ms p50={completedP50:F3}ms, " +
            $"processing mean={processingMean:F3}ms p50={processingP50:F3}ms");
        return 0;
    }

    private static async Task SeedIdempotencyRecordAsync(
        DbConnection conn, bool postgreSql, string operation, string key, int status,
        DateTimeOffset lockedUntil, DateTimeOffset expiresAt, DateTimeOffset updatedAt,
        byte[]? payload, long revision, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload, error, revision) " +
            "VALUES (@op, @key, @status, @lock, @exp, @upd, @payload, NULL, @rev)";
        Add(cmd, "@op", operation);
        Add(cmd, "@key", key);
        Add(cmd, "@status", status);
        Add(cmd, "@lock", postgreSql ? lockedUntil : lockedUntil.UtcDateTime);
        Add(cmd, "@exp", postgreSql ? expiresAt : expiresAt.UtcDateTime);
        Add(cmd, "@upd", postgreSql ? updatedAt : updatedAt.UtcDateTime);
        Add(cmd, "@payload", payload is null ? DBNull.Value : payload);
        Add(cmd, "@rev", revision);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(double Mean, double P50)> MeasureStartOrGetAsync(
        IIdempotencyStartOrGetStore store, string operation, string key, DateTimeOffset now,
        IdempotencyPolicy policy, int iterations, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
            _ = await store.StartOrGetAsync(operation, key, now, policy, ct).ConfigureAwait(false);

        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = await store.StartOrGetAsync(operation, key, now, policy, ct).ConfigureAwait(false);
            sw.Stop();
            if (result.Outcome != IdempotencyStartOutcome.Existing)
                throw new InvalidOperationException($"StartOrGet expected Existing, got {result.Outcome}");
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (samples.Average(), samples[samples.Count / 2]);
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

        // 抽样读完成后释放 reader，同一连接上才能执行 A/B 的后续命令。
        await reader.DisposeAsync();

        // A/B：1000 条单条写入 vs 批量写入（清表后各跑一次，同一连接、同一进程）。
        await ExecAsync(conn, "DELETE FROM outbox_messages", ct);
        var singleWatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var message in messages)
            await store.AddMessageAsync(message, ct);
        singleWatch.Stop();

        await ExecAsync(conn, "DELETE FROM outbox_messages", ct);
        var bulkWatch = System.Diagnostics.Stopwatch.StartNew();
        var bulkInserted = await store.AddMessagesAsync(messages, ct);
        bulkWatch.Stop();
        if (bulkInserted != count)
            throw new InvalidOperationException($"bulk A/B inserted expected {count}, got {bulkInserted}");

        var singleMs = singleWatch.Elapsed.TotalMilliseconds;
        var bulkMs = bulkWatch.Elapsed.TotalMilliseconds;
        Console.WriteLine(
            $"OUTBOX BULK PASS ({(postgreSql ? "PG" : "MySQL")}): inserted={inserted}, stored={stored}, sample=ok, " +
            $"single={singleMs:F1}ms, bulk={bulkMs:F2}ms, ratio={singleMs / Math.Max(bulkMs, 0.001):F1}x");
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

        // A/B：100 条逐条认领+完成（200 次往返）vs 一次批量认领+批量完成（PG/SQLite
        // 3 次、MySQL 5 次往返）。
        await ExecAsync(conn, "DELETE FROM inbox_messages", ct);
        var singleIds = Enumerable.Range(0, 100).Select(static i => "single-" + i).ToList();
        var singleWatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var messageId in singleIds)
        {
            var record = await store.TryStartProcessingAsync(
                "probe", messageId, now, TimeSpan.FromMinutes(5), ct);
            if (record is null)
                throw new InvalidOperationException($"single claim returned null for {messageId}");
            await store.MarkProcessedAsync(record, now, ct);
        }
        singleWatch.Stop();

        await ExecAsync(conn, "DELETE FROM inbox_messages", ct);
        var batchIds = Enumerable.Range(0, 100).Select(static i => "batch-ab-" + i).ToList();
        var batchWatch = System.Diagnostics.Stopwatch.StartNew();
        var batchClaimed = await store.TryStartProcessingManyAsync(
            "probe", batchIds, now, TimeSpan.FromMinutes(5), ct);
        var batchMarked = await store.MarkProcessedManyAsync("probe", batchClaimed, now, ct);
        batchWatch.Stop();
        if (batchClaimed.Count != 100 || batchMarked != 100)
            throw new InvalidOperationException(
                $"batch A/B expected 100/100, got {batchClaimed.Count}/{batchMarked}");

        var singleMs = singleWatch.Elapsed.TotalMilliseconds;
        var batchMs = batchWatch.Elapsed.TotalMilliseconds;
        Console.WriteLine(
            $"INBOX BATCH PASS ({(postgreSql ? "PG" : "MySQL")}): claimed={claimed.Count}, marked={marked}, duplicate=0, " +
            $"single={singleMs:F1}ms, batch={batchMs:F2}ms, ratio={singleMs / Math.Max(batchMs, 0.001):F1}x");
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
