// ─────────────────────────────────────────────────────────────
// 🧪 Dapper SQLite 方言固化层测试 — 审计 2026-09-20 T1
// ─────────────────────────────────────────────────────────────
// 背景：Dapper 三方言层是全仓覆盖率最低的生产代码（SQLite 15.81%、
// PG 38.11%、MySQL 53.14%，对比 Dapper 主包 72.49%），其中三个文件
// 覆盖率 0/56、0/66、0/52。SQLite 方言**不依赖容器**（vs PG/MySQL 需
// Testcontainers），零覆盖没有环境借口——方言 SQL 行为分叉（本项目
// CI #94 那类）会在这些文件里静默复发。
//
// 本文件覆盖两个纯 SQL 生成器（无需连接即可完整测试）：
//   SqliteJson  — JSON1 扩展的 SQL 片段生成（Extract/Type/Array/...）
//   SqliteFts   — FTS5 索引 DDL 与查询片段生成
// 外加 SqliteRowFactory 的 IDataReader 解析路径（用真 SQLite 驱动）。
//
// 两个生成器的共同特征是「字符串拼接进 SQL」，故测试重点是：
//   ① 注入防线（引号翻倍 / 标识符双引号包裹）；
//   ② 历史回归——三十六轮 P1-1 曾把 JSON 路径的违禁字符 fail-fast 错误
//      应用到 SQL **值**位置，导致 .NET 消息类型全名（Order.Created，
//      几乎必然含 '.'）直接抛 ArgumentException。该回归必须有锁定测试。
//   ③ 生成的 SQL 真能在 SQLite 执行（不只是字符串相等——字符串相等
//      锁不住语法错误）。

using Microsoft.Data.Sqlite;
using PalDDD.Dapper.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>SqliteJson / SqliteFts SQL 生成器测试（审计 T1）。</summary>
public sealed class SqliteDialectTests
{
    // ───────────────────────────────────────────────────────────
    // SqliteJson：注入防线
    // ───────────────────────────────────────────────────────────

    [Test]
    public async Task Extract_EscapesIdentifierAndKey()
    {
        var sql = SqliteJson.Extract("payload", "Type");

        // 标识符双引号包裹 + JSON 路径 $.key
        await Assert.That(sql).IsEqualTo("json_extract(\"payload\", '$.Type')");
    }

    [Test]
    [Arguments("col\"name")]
    public async Task Extract_IdentifierWithDoubleQuote_IsEscaped(string column)
    {
        // 标识符位置：双引号翻倍（SQLite 标识符引用语法）
        var sql = SqliteJson.Extract(column, "k");
        await Assert.That(sql).Contains("\"\"");
    }

    [Test]
    [Arguments("a'b")]
    public async Task Extract_IdentifierWithSingleQuote_NotEscapedAsLiteral(string column)
    {
        // 标识符位置的单引号**不**翻倍——SQLite 双引号标识符内单引号无特殊义，
        // 翻倍反而改列名（与 EscapeSqlLiteral 的值位置语义相反，P2/P3 拆分声明）
        var sql = SqliteJson.Extract(column, "k");
        await Assert.That(sql).IsEqualTo("json_extract(\"a'b\", '$.k')");
    }

    [Test]
    public async Task Extract_KeyWithSingleQuote_IsEscaped()
    {
        // JSON 路径位置：单引号翻倍（' → ''）
        var sql = SqliteJson.Extract("payload", "a'b");
        await Assert.That(sql).Contains("''");
    }

    [Test]
    [Arguments(".")]
    [Arguments("\"")]
    [Arguments("[")]
    [Arguments("]")]
    [Arguments("a.b")]
    [Arguments("notes[1]")]
    public async Task Extract_PathKeyWithForbiddenChars_Throws(string key)
    {
        // ITM-284 / 三十五轮 D4：点号（层级分隔符）、双引号、方括号（数组索引
        // 语法）在 JSON 路径位置必须构建期 fail-fast，否则静默查错位置。
        await Assert.That(() => SqliteJson.Extract("payload", key))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Extract_NullOrWhiteSpaceColumnOrKey_Throws()
    {
        // ITM-167：null/空白守卫——缺守卫时失败延迟到 SQLite 执行期
        await Assert.That(() => SqliteJson.Extract(null!, "k")).Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.Extract("  ", "k")).Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.Extract("payload", null!)).Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.Extract("payload", " ")).Throws<ArgumentException>();
    }

    // ───────────────────────────────────────────────────────────
    // SqliteJson：三十六轮 P1-1 回归（值位置不得套用路径违禁字符守卫）
    // ───────────────────────────────────────────────────────────

    [Test]
    [Arguments("Order.Created")]
    [Arguments("PalDDD.Samples.OrderCreatedEvent")]
    [Arguments("a.b.c.d")]
    public async Task OutboxByType_TypeNameWithDots_IsAllowed(string messageType)
    {
        // ⚠️ 三十六轮 P1-1 回归：修复前 D4 的 '.'/'"' fail-fast 被错误应用到 SQL
        // **值**位置，.NET 消息类型全名几乎必然含 '.'，合法调用直接抛
        // ArgumentException。值位置只做单引号翻倍（注入防线完整），不做路径守卫。
        var sql = SqliteJson.OutboxByType(messageType);

        await Assert.That(sql).IsEqualTo(
            $"json_extract(\"payload\", '$.Type') = '{messageType}'");
    }

    [Test]
    public async Task OutboxByType_TypeNameWithQuote_IsEscapedNotRejected()
    {
        // 值位置含单引号：翻倍转义而非抛异常（注入防线）
        var sql = SqliteJson.OutboxByType("Ord'er");
        await Assert.That(sql).Contains("Ord''er");
    }

    [Test]
    public async Task OutboxByType_NullOrWhiteSpace_Throws()
    {
        // ITM-195：补空白守卫（同文件其余 9 个方法已 ITM-167 对齐，唯此漏过）
        await Assert.That(() => SqliteJson.OutboxByType(null!)).Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.OutboxByType(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Array_And_BuildObject_ValuesWithQuotesAreEscaped()
    {
        var arr = SqliteJson.Array("a'b", "c");
        await Assert.That(arr).IsEqualTo("json_array('a''b','c')");

        var obj = SqliteJson.BuildObject("k1", "v1", "k2", "v'2");
        await Assert.That(obj).IsEqualTo("json_object('k1','v1','k2','v''2')");
    }

    [Test]
    public async Task BuildObject_OddPairCount_Throws()
    {
        await Assert.That(() => SqliteJson.BuildObject("k1", "v1", "k2"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ExtractPath_JoinsSegmentsWithDots()
    {
        var sql = SqliteJson.ExtractPath("payload", "a", "b", "c");
        await Assert.That(sql).IsEqualTo("json_extract(\"payload\", '$.a.b.c')");
    }

    // ───────────────────────────────────────────────────────────
    // SqliteJson / SqliteFts：生成的 SQL 真能执行（字符串相等锁不住语法错）
    // ───────────────────────────────────────────────────────────

    [Test]
    public async Task GeneratedJsonSql_ExecutesOnRealSqlite()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE t (payload TEXT);" +
                                "INSERT INTO t VALUES ('{\"Type\":\"Order.Created\",\"n\":7}');";
            setup.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SqliteJson.Extract("payload", "Type")}, " +
                          $"{SqliteJson.ExtractPath("payload", "n")} FROM t";
        using var reader = cmd.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        // 含点号的消息类型全名必须能取出（三十六轮 P1-1 回归的执行级验证）
        await Assert.That(reader.GetString(0)).IsEqualTo("Order.Created");
        await Assert.That(reader.GetInt64(1)).IsEqualTo(7);
    }

    [Test]
    public async Task OutboxByType_GeneratedSql_ExecutesOnRealSqlite()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE outbox_messages (payload TEXT);" +
                                "INSERT INTO outbox_messages VALUES ('{\"Type\":\"Order.Created\"}');" +
                                "INSERT INTO outbox_messages VALUES ('{\"Type\":\"Other\"}');";
            setup.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM outbox_messages WHERE {SqliteJson.OutboxByType("Order.Created")}";
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task FtsGeneratedSql_ExecutesOnRealSqlite()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        // events 表含 INTEGER 主键 global_position（FTS5 external content 的
        // rowid 要求，见 CreateEventLogIndex 的 P1 修复声明）
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE events (global_position INTEGER PRIMARY KEY, event_name TEXT, payload TEXT);";
            setup.ExecuteNonQuery();
            setup.CommandText = SqliteFts.CreateEventLogIndex("events");
            setup.ExecuteNonQuery();
            setup.CommandText = "INSERT INTO events (global_position, event_name, payload) " +
                                "VALUES (1, 'order.created', '{\"a\":1}'), (2, 'user.registered', '{\"b\":2}');";
            setup.ExecuteNonQuery();
        }

        // FTS5 external content 模式下 MATCH 作用于虚拟表本体（events_fts），
        // 查源表需 JOIN（见 SqliteFtsExtensions.cs:23 的推荐用法注释）
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*) FROM events e JOIN {SqliteFts.EventLogIndex} fts ON e.global_position = fts.rowid " +
            $"WHERE {SqliteFts.Match(SqliteFts.EventLogIndex, "order")}";
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task FtsTriggerName_KeepsUnderscore_RejectsOtherChars()
    {
        // 三十八轮 P3：下划线必须保留——原实现剔除下划线，导致
        // "outbox-messages" 与 "outbox_messages" 清洗同名，第二张表的
        // CREATE TRIGGER IF NOT EXISTS 静默跳过、其 FTS 索引停更。
        // 现只剔非字母数字下划线字符。
        var withUnderscore = SqliteFts.CreateEventLogIndex("events", "evt_a-index");
        var dashed = SqliteFts.CreateEventLogIndex("events", "evta-index");

        // 下划线保留：evt_a-index → evt_aindex_ai（连字符被剔、下划线保留）
        await Assert.That(withUnderscore).Contains("evt_aindex_ai");
        // 无下划线：evta-index → evtaindex_ai（与上面的名字**不同**，证明下划线未被剔）
        await Assert.That(dashed).Contains("evtaindex_ai");
        await Assert.That(dashed).DoesNotContain("evt_aindex_ai");
    }
}
