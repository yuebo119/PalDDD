using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.Testing;
using PalDDD.Transactions;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧪 ITM-650 探针 — Inbox 时间戳 token 精度跨方言验证
// ═══════════════════════════════════════════════════════════════
// 疑点：内存时间戳 100ns 精度（DateTimeOffset ticks）vs DB 列微秒精度
//（PG TIMESTAMPTZ / MySQL DATETIME(6)）。MarkProcessed 的 token 守卫
// `WHERE id=@id AND status=1 AND processing_started_at=@startedAt` 用写入时
// 的内存值做等值比较——若 provider 写 INSERT 参数与 WHERE 参数的舍入/截断
// 不一致（如写侧截 6 位、比侧送 7 位），token 零命中 → affected=0 →
// status 停留 Processing → 重投被判超时/可重入 → 双处理。
//
// 探针：注入非整微秒 now（.1234567——7 位小数，ticks 尾数非 0），
// TryStart → MarkProcessed → 直查 DB 断言 status 落库 Processed(2)，
// 再以 now+1s 重投断言幂等跳过（null）——即 ITM-650 双处理后果的反证。
//
// 执行环境：
//   - SQLite：本地实跑（:memory:，零依赖）
//   - PG/MySQL：Testcontainers 可用时自动跑；Docker 不可达自动 Skip
//    （对齐 BrokerIntegrationTests 的 Skip.Test 环境守卫模式），
//     外部连接串配置（UseXxxTestcontainers=false）时 Skip——对齐
//     PalORM MultiDialectFixture "禁止连接外部数据库"裁决
//
// Dapper 栈时间参数路径（ToTimeParam）：
//   SQLite → "O"（7 位小数字符串，TEXT 列存全精度）
//   MySQL → "yyyy-MM-dd HH:mm:ss.ffffff"（6 位微秒，DATETIME(6) 列）
//   PG    → 原生 DateTimeOffset（Npgsql timestamptz 微秒）
// 写侧与比侧同经一函数/驱动路径是 token 命中的前提——本探针实证之
// ═══════════════════════════════════════════════════════════════

/// <summary>ITM-650 探针测试 — DapperInboxStore 时间戳 token 等值命中的跨方言实证。</summary>
/// <remarks>
/// [NotInParallel("dapper-global")] 与 DapperStoreTests 同组序列化：本组测试均依赖
/// Dapper 全局静态状态（TypeHandler + MatchNamesWithUnderscores），DapperStoreTests 的
/// ClassCleanup 会 ResetTypeHandlers 清掉 ModuleInitializer 注册——交错执行会产生
/// SQLite TEXT→DateTimeOffset 映射竞态；同组串行 + 本类 Before(Class) 幂等重注册双保险。
/// </remarks>
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class InboxTimestampTokenPrecisionProbeTests
{
    // 测试专用常量（consumer/message 唯一命名，容器/内存库均为一次性实例）
    private const string Consumer = "it650-probe-consumer";
    private const string MessageId = "it650-token-precision";

    [Before(Class)]
    public static void ClassInitialize()
    {
        // 幂等重注册 DateTimeOffset TypeHandler（SQLite TEXT 列往返必需；镜像 DapperStoreTests
        // ClassInitialize 形态——防本类执行于 DapperStoreTests.ClassCleanup 的 ResetTypeHandlers 之后）
        var dtoHandler = new SqliteDateTimeOffsetTypeHandler();
        SqlMapper.AddTypeHandler(dtoHandler);
        SqlMapper.AddTypeHandler(typeof(DateTimeOffset), dtoHandler);
        SqlMapper.AddTypeHandler(typeof(DateTimeOffset?), dtoHandler);
    }

    [Test]
    public async Task Inbox_NonIntegralMicrosecondNow_MarkProcessedTokenHits_Sqlite()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await CreateInboxSchemaAsync(conn, DapperDbType.Sqlite);
        await AssertTokenHitAfterMarkProcessedAsync(conn, DapperDbType.Sqlite);
    }

    [Test]
    public async Task Inbox_NonIntegralMicrosecondNow_MarkProcessedTokenHits_PostgreSql()
    {
        if (!TestEnvironment.UsePostgreSqlTestcontainers)
            Skip.Test("ITM-650 PostgreSQL 探针仅走 Testcontainers（对齐 MultiDialectFixture 禁外部库裁决）；当前配置指向外部连接串，跳过。");

        var container = new PostgreSqlBuilder(TestEnvironment.PostgreSqlImage).Build();
        try
        {
            await container.StartAsync();
        }
#pragma warning disable CA1031 // Intentionally broad: detect Docker presence（对齐 BrokerIntegrationTests 环境守卫模式）
        catch (Exception)
#pragma warning restore CA1031
        {
            await container.DisposeAsync();
            Skip.Test("Docker/Testcontainers 不可达——ITM-650 PostgreSQL 探针跳过（待 CI 执行）。");
        }

        try
        {
            await using var conn = new NpgsqlConnection(container.GetConnectionString());
            await conn.OpenAsync();
            await CreateInboxSchemaAsync(conn, DapperDbType.PostgreSql);
            await AssertTokenHitAfterMarkProcessedAsync(conn, DapperDbType.PostgreSql);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    [Test]
    public async Task Inbox_NonIntegralMicrosecondNow_MarkProcessedTokenHits_MySql()
    {
        if (!TestEnvironment.UseMySqlTestcontainers)
            Skip.Test("ITM-650 MySQL 探针仅走 Testcontainers（对齐 MultiDialectFixture 禁外部库裁决）；当前配置指向外部连接串，跳过。");

        var container = new MySqlBuilder(TestEnvironment.MySqlImage).Build();
        try
        {
            await container.StartAsync();
        }
#pragma warning disable CA1031 // Intentionally broad: detect Docker presence（对齐 BrokerIntegrationTests 环境守卫模式）
        catch (Exception)
#pragma warning restore CA1031
        {
            await container.DisposeAsync();
            Skip.Test("Docker/Testcontainers 不可达——ITM-650 MySQL 探针跳过（待 CI 执行）。");
        }

        try
        {
            await using var conn = new MySqlConnection(container.GetConnectionString());
            await conn.OpenAsync();
            await CreateInboxSchemaAsync(conn, DapperDbType.MySql);
            await AssertTokenHitAfterMarkProcessedAsync(conn, DapperDbType.MySql);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>
    /// 探针核心：非整微秒 now（ticks 尾数非 0 → 小数第 7 位非零）经 TryStart 写入
    /// processing_started_at 后，MarkProcessed 以同一内存值为 token 等值比对——
    /// 命中则 status 落库 Processed(2)；零命中则停留 Processing(1)（双处理风险成立）。
    /// </summary>
    private static async Task AssertTokenHitAfterMarkProcessedAsync(DbConnection conn, DapperDbType dbType)
    {
        var store = new DapperInboxStore(conn, dbType);
        // .1234567（7 位小数）——PG TIMESTAMPTZ/MySQL DATETIME(6) 均为微秒列，
        // 内存值超出列精度：写/比两侧的截断/舍入必须一致 token 才能命中
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        // 自检：1 微秒 = 10 ticks，ticks 尾数非 0 即非整微秒（探针前提，防退化成整微秒无效探针）
        await Assert.That(now.Ticks % 10)
            .IsNotEqualTo(0)
            .Because("探针前提自检：注入时刻必须非整微秒（ticks 第 7 位非零），否则探针失效");

        var msg = await store.TryStartProcessingAsync(Consumer, MessageId, now, TimeSpan.FromMinutes(5), default);
        await Assert.That(msg).IsNotNull();
        await Assert.That(msg!.Status).IsEqualTo(InboxStatus.Processing);

        await store.MarkProcessedAsync(msg, now.AddSeconds(1), default);

        // 核心断言：直查 DB（不经 Store 回读路径）——status=2 证明
        // `WHERE ... AND processing_started_at = @startedAt` 等值命中
        var status = await ReadStatusAsync(conn);
        await Assert.That(status)
            .IsEqualTo((int)InboxStatus.Processed)
            .Because($"MarkProcessed 的 processing_started_at token 零命中（{dbType}）：写/比舍入不一致，"
                + "status 停留 Processing → 重投被判可重入 → 双处理（ITM-650 疑点成立）");

        // 双处理后果反证：Processed 后同消息重投（now+1s）应幂等跳过返回 null
        var redelivered = await store.TryStartProcessingAsync(
            Consumer, MessageId, now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(redelivered)
            .IsNull()
            .Because("token 零命中时 status 停留 Processing，重投在 timeout 窗口内仍会返回 null——"
                + "但 MarkProcessed 失败的行在超时后会被抢占重入（双处理）；本断言与 status 断言共同锁定终态");
    }

    /// <summary>按方言建 inbox_messages 表（列名 snake_case，与 SqlTemplates 的 SQL 一致；PG 无引号标识符折叠小写）。</summary>
    private static async Task CreateInboxSchemaAsync(DbConnection conn, DapperDbType dbType)
    {
        var ddl = dbType switch
        {
            // TIMESTAMPTZ 微秒精度（ITM-650 疑点的 PG 轴）
            DapperDbType.PostgreSql => """
                CREATE TABLE inbox_messages (id BIGSERIAL PRIMARY KEY, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TIMESTAMPTZ NOT NULL, processing_started_at TIMESTAMPTZ, processed_at TIMESTAMPTZ, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT);
                CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)
                """,
            // DATETIME(6) 微秒精度（ITM-650 疑点的 MySQL 轴）
            DapperDbType.MySql => """
                CREATE TABLE inbox_messages (id BIGINT AUTO_INCREMENT PRIMARY KEY, message_id VARCHAR(255) NOT NULL, consumer_name VARCHAR(128) NOT NULL, status INT NOT NULL DEFAULT 0, received_at DATETIME(6) NOT NULL, processing_started_at DATETIME(6) NULL, processed_at DATETIME(6) NULL, attempts INT NOT NULL DEFAULT 1, last_error TEXT NULL);
                CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)
                """,
            // TEXT 列存 "O" 全精度（7 位小数）
            _ => """
                CREATE TABLE inbox_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TEXT NOT NULL, processing_started_at TEXT, processed_at TEXT, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT);
                CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)
                """,
        };
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>原生 SQL 直查 status（参数化），不经 Store 回读路径。</summary>
    private static async Task<int> ReadStatusAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM inbox_messages WHERE consumer_name = @c AND message_id = @m";
        var c = cmd.CreateParameter();
        c.ParameterName = "@c";
        c.Value = Consumer;
        cmd.Parameters.Add(c);
        var m = cmd.CreateParameter();
        m.ParameterName = "@m";
        m.Value = MessageId;
        cmd.Parameters.Add(m);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
