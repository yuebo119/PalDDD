using Microsoft.Data.Sqlite;
using PalDDD.Dapper;
using PalDDD.Idempotency;
using PalDDD.PalORM.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>
/// T1（基础库优化清单）：幂等 0 字节载荷的跨栈对照。Dapper 与 PalORM 共用同一个
/// SQLite 文件，各自写入一条 Completed + 空 BLOB 记录，再交叉读取；原缺陷是 Dapper
/// 把 0 字节载荷判为“无结果”，本测试锁住两栈一致回放空数组。
/// </summary>
public sealed class CrossStackIdempotencyMatrixTests
{
    private const string Ddl = """
        CREATE TABLE idempotency_records (
            operation_name TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            status INTEGER NOT NULL,
            locked_until TEXT,
            expires_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            response_payload BLOB,
            error TEXT,
            revision INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (operation_name, idempotency_key))
        """;

    [Test]
    public async Task CompletedEmptyPayload_DapperAndPalOrm_ReturnEmptyArray()
    {
        var path = Path.Combine(Path.GetTempPath(), $"palddd-idem-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = Ddl;
                await ddl.ExecuteNonQueryAsync();
            }

            var dapper = new DapperIdempotencyStore(connection, DapperDbType.Sqlite);
            await using var session = await DataSession<SqliteProvider>.CreateAsync(
                DbOptions.Development($"Data Source={path}"));
            var palorm = new SqliteIdempotencyStore(session);
            var now = DateTimeOffset.UtcNow;
            var empty = ReadOnlyMemory<byte>.Empty;

            var dapperRecord = await dapper.TryStartAsync("dapper", "k", now, IdempotencyPolicy.Default)
                ?? throw new InvalidOperationException("Dapper TryStartAsync 返回 null。");
            await dapper.MarkCompletedAsync(dapperRecord, empty, now);

            var palormRecord = await palorm.TryStartAsync("palorm", "k", now, IdempotencyPolicy.Default)
                ?? throw new InvalidOperationException("PalORM TryStartAsync 返回 null。");
            await palorm.MarkCompletedAsync(palormRecord, empty, now);

            // 同栈读 + 交叉读：同一行的 payload 语义必须一致
            await AssertEmptyAsync(await dapper.GetAsync("dapper", "k", now));
            await AssertEmptyAsync(await palorm.GetAsync("palorm", "k", now));
            await AssertEmptyAsync(await dapper.GetAsync("palorm", "k", now));
            await AssertEmptyAsync(await palorm.GetAsync("dapper", "k", now));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 连接池仍持有文件句柄时留给 OS 临时目录清理。
            }
        }
    }

    private static async Task AssertEmptyAsync(IdempotencyRecord? record)
    {
        var completed = record ?? throw new InvalidOperationException("幂等记录未读回。");
        var payload = completed.ResponsePayload
            ?? throw new InvalidOperationException("Completed 记录的空载荷被读成 null。");
        await Assert.That(completed.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(payload.Length).IsEqualTo(0);
    }
}
