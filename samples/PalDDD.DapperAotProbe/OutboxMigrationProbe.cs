// ═══════════════════════════════════════════════════════════════
// Outbox 可用时刻迁移与积压量测探针（PG/MySQL）
// ═══════════════════════════════════════════════════════════════
// 模拟旧版行：90k 未来重试行在 created_at 头部、9k 无 next 的到期行、1k 旧活跃租约。
// 顺序：旧索引 → 旧谓词量测 → 回填 → 零违例校验 → 新索引 → 新谓词量测/EXPLAIN。
// 仅用于迁移验收；不进入生产代码路径。

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using PalDDD.Dapper;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.DapperAotProbe;

internal static class OutboxMigrationProbe
{
    private const int Total = 100_000;
    private const int FutureRows = 90_000;
    private const int DueRows = 9_000;

    public static async Task RunAsync(DbConnection conn, DapperDbType dbType, Action<string, bool> check)
    {
        if (dbType == DapperDbType.Sqlite)
        {
            check("outbox migration probe supports PG/MySQL only", false);
            return;
        }

        await ExecAsync(conn, dbType == DapperDbType.PostgreSql
            ? "DROP INDEX IF EXISTS idx_outbox_due"
            : "ALTER TABLE outbox_messages DROP INDEX idx_outbox_due").ConfigureAwait(false);
        await ExecAsync(conn, dbType == DapperDbType.PostgreSql
            ? "CREATE INDEX idx_outbox_due_old ON outbox_messages(created_at) WHERE status=0"
            : "CREATE INDEX idx_outbox_due_old ON outbox_messages(status, created_at)").ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await SeedAsync(conn, dbType, now).ConfigureAwait(false);
        check("outbox migration seeded 100k legacy/mixed rows", true);

        var old = await MeasureAsync(conn, dbType, now, newPredicate: false).ConfigureAwait(false);
        check("outbox old predicate returns 100", old.Count == 100);

        await ExecAsync(conn, dbType == DapperDbType.PostgreSql
            ? "UPDATE outbox_messages SET next_attempt_at = GREATEST(COALESCE(next_attempt_at, created_at), COALESCE(locked_until, created_at)) WHERE status=0"
            : "UPDATE outbox_messages SET next_attempt_at = GREATEST(COALESCE(next_attempt_at, created_at), COALESCE(locked_until, created_at)) WHERE status=0").ConfigureAwait(false);

        var violations = await ScalarAsync(conn, dbType,
            "SELECT COUNT(*) FROM outbox_messages WHERE status=0 AND (next_attempt_at IS NULL OR (locked_until IS NOT NULL AND next_attempt_at < locked_until))").ConfigureAwait(false);
        check("outbox backfill zero violations", violations == 0);
        var due = await ScalarAsync(conn, dbType,
            $"SELECT COUNT(*) FROM outbox_messages WHERE status=0 AND next_attempt_at <= '{TimeLiteral(dbType, now)}'").ConfigureAwait(false);
        check("outbox backfill due rows = 9000", due == DueRows);

        await ExecAsync(conn, dbType == DapperDbType.PostgreSql
            ? "DROP INDEX idx_outbox_due_old"
            : "ALTER TABLE outbox_messages DROP INDEX idx_outbox_due_old").ConfigureAwait(false);
        await ExecAsync(conn, dbType == DapperDbType.PostgreSql
            ? "CREATE INDEX idx_outbox_due ON outbox_messages(next_attempt_at, created_at) WHERE status=0"
            : "CREATE INDEX idx_outbox_due ON outbox_messages(status, next_attempt_at, created_at)").ConfigureAwait(false);

        var updated = await MeasureAsync(conn, dbType, now, newPredicate: true).ConfigureAwait(false);
        check("outbox new predicate returns 100", updated.Count == 100);

        var plan = await ExplainAsync(conn, dbType, now).ConfigureAwait(false);
        check("outbox new predicate uses idx_outbox_due", plan.Contains("idx_outbox_due", StringComparison.OrdinalIgnoreCase));
        check("outbox EXPLAIN has no full scan", !plan.Contains("Seq Scan", StringComparison.OrdinalIgnoreCase)
            && !plan.Contains("\"type\": \"ALL\"", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"  旧谓词+旧索引 {old.MedianMs:F2}ms；新谓词+新索引 {updated.MedianMs:F2}ms；" +
            $"比值 {(old.MedianMs / Math.Max(updated.MedianMs, 0.001)):F2}×");
    }

    private static async Task SeedAsync(DbConnection conn, DapperDbType dbType, DateTimeOffset now)
    {
        const int batch = 500;
        for (var start = 0; start < Total; start += batch)
        {
            var count = Math.Min(batch, Total - start);
            var dp = new DynamicParameters();
            var timeDbType = dbType == DapperDbType.MySql ? DbType.DateTime : DbType.DateTimeOffset;
            var sb = new System.Text.StringBuilder(80 + count * 100);
            sb.Append("INSERT INTO outbox_messages (id,type,payload,status,created_at,next_attempt_at,locked_by,locked_until) VALUES ");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                var row = start + i;
                sb.Append("(@id").Append(i).Append(",@type").Append(i).Append(",@payload").Append(i)
                  .Append(",0,@created").Append(i).Append(",@next").Append(i).Append(",@lb").Append(i)
                  .Append(",@lu").Append(i).Append(')');
                dp.Add("id" + i, PalUlid.New().ToString());
                dp.Add("type" + i, "migration.probe");
                dp.Add("payload" + i, Array.Empty<byte>());
                DateTimeOffset created;
                object? next;
                string? lockedBy = null;
                DateTimeOffset? lockedUntil = null;
                if (row < FutureRows)
                {
                    created = now.AddYears(-1).AddSeconds(row);
                    next = TimeParam(dbType, now.AddHours(1));
                }
                else if (row < FutureRows + DueRows)
                {
                    created = now.AddMinutes(-(row - FutureRows));
                    next = null;
                }
                else
                {
                    created = now.AddYears(-2).AddSeconds(row);
                    next = null;
                    lockedBy = "legacy-worker";
                    lockedUntil = now.AddMinutes(5);
                }
                dp.Add("created" + i, TimeParam(dbType, created), timeDbType);
                dp.Add("next" + i, next, timeDbType);
                dp.Add("lb" + i, lockedBy, DbType.String);
                dp.Add("lu" + i, lockedUntil is null ? null : TimeParam(dbType, lockedUntil.Value), timeDbType);
            }
            await conn.ExecuteAsync(sb.ToString(), dp).ConfigureAwait(false);
        }
    }

    private static async Task<(double MedianMs, int Count)> MeasureAsync(
        DbConnection conn, DapperDbType dbType, DateTimeOffset now, bool newPredicate)
    {
        var predicate = newPredicate
            ? "next_attempt_at <= @now ORDER BY next_attempt_at, created_at"
            : "(next_attempt_at IS NULL OR next_attempt_at <= @now) AND (locked_until IS NULL OR locked_until <= @now) ORDER BY created_at";
        var sql = $"SELECT id FROM outbox_messages WHERE status=0 AND retry_count < @max AND {predicate} LIMIT @n";
        var dp = new DynamicParameters();
        dp.Add("now", TimeParam(dbType, now));
        dp.Add("max", 5);
        dp.Add("n", 100);

        var samples = new List<double>(5);
        var count = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var ids = await conn.QueryAsync<string>(sql, dp).ConfigureAwait(false);
            sw.Stop();
            count = ids.Count();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (samples[samples.Count / 2], count);
    }

    private static async Task<string> ExplainAsync(DbConnection conn, DapperDbType dbType, DateTimeOffset now)
    {
        var nowLiteral = TimeLiteral(dbType, now);
        var sql = dbType == DapperDbType.PostgreSql
            ? $"EXPLAIN (FORMAT TEXT, COSTS OFF) SELECT id FROM outbox_messages WHERE status=0 AND next_attempt_at <= TIMESTAMPTZ '{nowLiteral}' ORDER BY next_attempt_at, created_at LIMIT 100"
            : $"EXPLAIN FORMAT=JSON SELECT id FROM outbox_messages WHERE status=0 AND next_attempt_at <= '{nowLiteral}' ORDER BY next_attempt_at, created_at LIMIT 100";
        var lines = await conn.QueryAsync<string>(sql).ConfigureAwait(false);
        return string.Join('\n', lines);
    }

    private static async Task<long> ScalarAsync(DbConnection conn, DapperDbType dbType, string sql)
        => await conn.ExecuteScalarAsync<long>(sql).ConfigureAwait(false);

    private static async Task ExecAsync(DbConnection conn, string sql)
        => await conn.ExecuteAsync(sql).ConfigureAwait(false);

    private static object TimeParam(DapperDbType dbType, DateTimeOffset value)
        => dbType == DapperDbType.MySql ? value.UtcDateTime : value;

    private static string TimeLiteral(DapperDbType dbType, DateTimeOffset value)
        => dbType == DapperDbType.MySql
            ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            : value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
