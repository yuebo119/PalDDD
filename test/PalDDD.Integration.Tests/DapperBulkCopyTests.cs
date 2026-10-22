namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using PalDDD.Dapper;

#pragma warning disable CA1303 // 真库测试的控制台跳过说明，不是本地化 UI 文案

public sealed class DapperBulkCopyTests
{
    [Test]
    [Arguments("outbox;DROP TABLE outbox_messages")]
    [Arguments("1outbox")]
    [Arguments("public.")]
    [Arguments("public..outbox_messages")]
    public async Task BulkInsertAsync_RejectsUnsafeTableName(string tableName)
    {
        var exception = await Assert.That(() =>
            DapperBulkCopy.BulkInsertAsync(
                new SqliteConnection("Data Source=:memory:"),
                DapperDbType.Sqlite,
                tableName,
                ["id"],
                Array.Empty<object>(),
                static item => [item]).AsTask()).Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("tableName");
    }

    // v66 PG 事务类型对称 fail-fast 回归网（守卫在 (NpgsqlConnection) 强转之前——
    // 传 SqliteTransaction 即触发，零 PG 连接依赖）
    [Test]
    public async Task BulkInsertAsync_PG_WrongTransactionType_ThrowsArgumentException()
    {
        var exception = await Assert.That(() =>
            DapperBulkCopy.BulkInsertAsync(
                new SqliteConnection("Data Source=:memory:"),
                DapperDbType.PostgreSql,
                "outbox_messages",
                ["id"],
                [new object()],
                static item => [item],
                transaction: OpenSqliteWithTransaction()).AsTask())
            .Throws<ArgumentException>();

        await Assert.That(exception!.Message).Contains("NpgsqlTransaction");
    }

    [Test]
    [Arguments("id;DROP TABLE outbox_messages")]
    [Arguments("1id")]
    [Arguments("schema.id")]
    [Arguments("")]
    public async Task BulkInsertAsync_RejectsUnsafeColumnName(string columnName)
    {
        var exception = await Assert.That(() =>
            DapperBulkCopy.BulkInsertAsync(
                new SqliteConnection("Data Source=:memory:"),
                DapperDbType.Sqlite,
                "outbox_messages",
                [columnName],
                Array.Empty<object>(),
                static item => [item]).AsTask()).Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("columns");
    }

    // ITM-251 传感器（F9）：带 null 首行值的批量插入——守护值提取/NULL 归一路径
    // 不被首行 null 短路（MySQL 推断路径的同型场景），并断言 extractor 调用次数契约：
    // 每行恰一次 + 首行入口长度探针额外一次（契约修复后三方言一致；修复前 MySQL 路径
    // 推断循环 + 填充循环对每行各调一次）。SQLite 路径可直接运行验证。
    [Test]
    public async Task BulkInsertAsync_WithNullValuesInFirstRow_InsertsAllRowsCorrectly()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE bulk_null_test (id INTEGER PRIMARY KEY, name TEXT, amount INT)";
            await create.ExecuteNonQueryAsync();
        }

        (int Id, string? Name, int? Amount)[] items =
        [
            (1, null, null),   // 首行含 null 列
            (2, "alpha", 10),
            (3, null, 30),
        ];

        var extractorCalls = 0;
        var inserted = await DapperBulkCopy.BulkInsertAsync(
            conn,
            DapperDbType.Sqlite,
            "bulk_null_test",
            ["id", "name", "amount"],
            items,
            item =>
            {
                extractorCalls++;
                return [item.Id, item.Name, item.Amount];
            });

        await Assert.That(inserted).IsEqualTo(3);
        await Assert.That(extractorCalls).IsEqualTo(items.Length + 1); // 每行一次 + 首行探针一次

        await using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT id, name, amount FROM bulk_null_test ORDER BY id";
        await using var reader = await verify.ExecuteReaderAsync();
        var rows = new List<(long Id, string? Name, long? Amount)>();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0].Name).IsNull();
        await Assert.That(rows[0].Amount).IsNull();
        await Assert.That(rows[1].Name).IsEqualTo("alpha");
        await Assert.That(rows[1].Amount).IsEqualTo(10L);
        await Assert.That(rows[2].Name).IsNull();
        await Assert.That(rows[2].Amount).IsEqualTo(30L);
    }

    [Test]
    public async Task BulkInsertAsync_MySql_LoadDataWarning_RollsBackWithoutExternalTransaction(
        CancellationToken cancellationToken)
    {
        // R12 C-21 回归锁：LOAD DATA 自动提交后才检查 Warnings，原实现抛错时行已落库，
        // 调用方重试会造成重复。无外部事务时自建事务并在检查失败时回滚。
        var connectionString = Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("[skip] PALDDD_TEST_MYSQL 未设置；MySQL BulkCopy 原子性未执行。");
            return;
        }

        var builder = new MySqlConnector.MySqlConnectionStringBuilder(connectionString)
        {
            AllowLoadLocalInfile = true,
        };
        await using var conn = new MySqlConnector.MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using (var setup = conn.CreateCommand())
        {
            // 非严格模式让超长值产生 warning 而非直接报错（LOAD DATA 路径仍完成写入）。
            setup.CommandText =
                "SET SESSION sql_mode=''; DROP TABLE IF EXISTS bulk_atomic_probe; "
                + "CREATE TABLE bulk_atomic_probe (id INT PRIMARY KEY, name VARCHAR(8));";
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var items = Enumerable.Range(0, 17)
                .Select(i => (Id: i, Name: i == 5 ? new string('x', 20) : "ok"))
                .ToArray();

            await Assert.That(async () => await DapperBulkCopy.BulkInsertAsync(
                    conn,
                    DapperDbType.MySql,
                    "bulk_atomic_probe",
                    ["id", "name"],
                    items,
                    static item => [item.Id, item.Name],
                    transaction: null,
                    cancellationToken))
                .Throws<InvalidOperationException>();

            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM bulk_atomic_probe";
            var rows = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(rows).IsEqualTo(0);
        }
        finally
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS bulk_atomic_probe";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static SqliteTransaction OpenSqliteWithTransaction()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn.BeginTransaction();
    }
}
