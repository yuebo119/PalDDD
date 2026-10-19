using DapperColumn = PalDDD.Dapper.Sqlite.SqliteFtsColumn;
using DapperFts = PalDDD.Dapper.Sqlite.SqliteFts;
using DapperIndex = PalDDD.Dapper.Sqlite.SqliteFtsIndexDefinition;
using DapperJson = PalDDD.Dapper.Sqlite.SqliteJson;
using PalOrmColumn = PalDDD.PalORM.Sqlite.SqliteFtsColumn;
using PalOrmFts = PalDDD.PalORM.Sqlite.SqliteFts;
using PalOrmIndex = PalDDD.PalORM.Sqlite.SqliteFtsIndexDefinition;
using PalOrmJson = PalDDD.PalORM.Sqlite.SqliteJson;

namespace PalDDD.Integration.Tests;

/// <summary>
/// Dapper 与 PalORM 两个 SQLite 方言门面的对等性守卫（ADR-032 D2）。
/// <para>两个包共享 src/PalDDD.Shared 内部核心，但公开门面是两份独立源码；
/// 本测试锁定同输入下两者产出逐字相同，防止单侧演进导致双栈分叉。</para>
/// </summary>
public sealed class SqliteFtsJsonParityTests
{
    private static readonly string[][] s_pathCases =
    [
        [],
        ["Type"],
        ["a.b"],
        ["c[0]"],
        ["d e"],
        ["f\"g"],
        ["h\\i"],
        ["j'k"],
        ["x' OR '1'='1"],
    ];

    [Test]
    public async Task JsonFacades_ProduceIdenticalFragments()
    {
        foreach (var path in s_pathCases)
        {
            await Assert.That(PalOrmJson.Path(path)).IsEqualTo(DapperJson.Path(path));
            await Assert.That(PalOrmJson.Extract("payload", path)).IsEqualTo(DapperJson.Extract("payload", path));
            await Assert.That(PalOrmJson.ExtractText("payload", path)).IsEqualTo(DapperJson.ExtractText("payload", path));
            await Assert.That(PalOrmJson.ExtractJson("payload", path)).IsEqualTo(DapperJson.ExtractJson("payload", path));
            await Assert.That(PalOrmJson.Type("payload", path)).IsEqualTo(DapperJson.Type("payload", path));
            await Assert.That(PalOrmJson.ArrayLength("payload", path)).IsEqualTo(DapperJson.ArrayLength("payload", path));
            await Assert.That(PalOrmJson.Each("payload", path)).IsEqualTo(DapperJson.Each("payload", path));
            await Assert.That(PalOrmJson.Tree("payload", path)).IsEqualTo(DapperJson.Tree("payload", path));
            await Assert.That(PalOrmJson.Remove("payload", path)).IsEqualTo(DapperJson.Remove("payload", path));
            await Assert.That(PalOrmJson.EqualsLiteral("payload", "v'1", path)).IsEqualTo(DapperJson.EqualsLiteral("payload", "v'1", path));
        }

        await Assert.That(PalOrmJson.IsValid("payload")).IsEqualTo(DapperJson.IsValid("payload"));
        await Assert.That(PalOrmJson.HasKey("payload", "Type")).IsEqualTo(DapperJson.HasKey("payload", "Type"));
        await Assert.That(PalOrmJson.ArrayLiteral("a'b", "c")).IsEqualTo(DapperJson.ArrayLiteral("a'b", "c"));
        await Assert.That(PalOrmJson.ArrayParameters("a", "@b")).IsEqualTo(DapperJson.ArrayParameters("a", "@b"));
        await Assert.That(PalOrmJson.ObjectLiteral(("k", "v'2"))).IsEqualTo(DapperJson.ObjectLiteral(("k", "v'2")));
        await Assert.That(PalOrmJson.ObjectParameters(("k", "v"))).IsEqualTo(DapperJson.ObjectParameters(("k", "v")));
        await Assert.That(PalOrmJson.SetParameter("payload", "v", "a")).IsEqualTo(DapperJson.SetParameter("payload", "v", "a"));
        await Assert.That(PalOrmJson.InsertParameter("payload", "v", "b")).IsEqualTo(DapperJson.InsertParameter("payload", "v", "b"));
        await Assert.That(PalOrmJson.ReplaceParameter("payload", "v", "b")).IsEqualTo(DapperJson.ReplaceParameter("payload", "v", "b"));
        await Assert.That(PalOrmJson.PatchParameter("payload", "p")).IsEqualTo(DapperJson.PatchParameter("payload", "p"));
        await Assert.That(PalOrmJson.QuoteLiteral("a\"b")).IsEqualTo(DapperJson.QuoteLiteral("a\"b"));
        await Assert.That(PalOrmJson.GroupArray("k")).IsEqualTo(DapperJson.GroupArray("k"));
        await Assert.That(PalOrmJson.GroupObject("k", "v")).IsEqualTo(DapperJson.GroupObject("k", "v"));
        await Assert.That(PalOrmJson.EqualsParameter("payload", "type", "Type")).IsEqualTo(DapperJson.EqualsParameter("payload", "type", "Type"));
        await Assert.That(PalOrmJson.OutboxByType("Order.Created")).IsEqualTo(DapperJson.OutboxByType("Order.Created"));
    }

    [Test]
    public async Task JsonPath_SingleQuote_IsSqlEscapedInBothFacades()
    {
        const string expected = "'$.\"a''b\"'";

        await Assert.That(DapperJson.Path("a'b")).IsEqualTo(expected);
        await Assert.That(PalOrmJson.Path("a'b")).IsEqualTo(expected);
    }

    [Test]
    public async Task FtsFacades_ProduceIdenticalDefinitions()
    {
        var dapperExternal = DapperFts.CreateExternalContentIndex(
            "events", "events_fts", "global_position", ["event_name", "payload"],
            tokenizer: "unicode61", prefixIndexes: [2, 3]);
        var palormExternal = PalOrmFts.CreateExternalContentIndex(
            "events", "events_fts", "global_position", ["event_name", "payload"],
            tokenizer: "unicode61", prefixIndexes: [2, 3]);
        await AssertDefinitionsEqual(dapperExternal, palormExternal);

        var dapperStandalone = DapperFts.CreateStandaloneIndex(
            "outbox_messages", "outbox_messages_fts", "id",
            [new DapperColumn("type"), new DapperColumn("payload", CastToText: true)]);
        var palormStandalone = PalOrmFts.CreateStandaloneIndex(
            "outbox_messages", "outbox_messages_fts", "id",
            [new PalOrmColumn("type"), new PalOrmColumn("payload", CastToText: true)]);
        await AssertDefinitionsEqual(dapperStandalone, palormStandalone);

        await AssertDefinitionsEqual(DapperFts.CreateEventLogIndex(), PalOrmFts.CreateEventLogIndex());
        await AssertDefinitionsEqual(DapperFts.CreateOutboxIndex(), PalOrmFts.CreateOutboxIndex());
    }

    [Test]
    public async Task FtsFacades_ProduceIdenticalFragments()
    {
        await Assert.That(PalOrmFts.MatchLiteral("events_fts", "a'b")).IsEqualTo(DapperFts.MatchLiteral("events_fts", "a'b"));
        await Assert.That(PalOrmFts.MatchParameter("events_fts", "q")).IsEqualTo(DapperFts.MatchParameter("events_fts", "q"));
        await Assert.That(PalOrmFts.MatchParameter("events_fts", "@q")).IsEqualTo(DapperFts.MatchParameter("events_fts", "@q"));
        await Assert.That(PalOrmFts.MatchExpression("events_fts")).IsEqualTo(DapperFts.MatchExpression("events_fts"));
        await Assert.That(PalOrmFts.OrderByRank("events_fts", 1.0, 0.5)).IsEqualTo(DapperFts.OrderByRank("events_fts", 1.0, 0.5));
        await Assert.That(PalOrmFts.Rank("events_fts", 2.0)).IsEqualTo(DapperFts.Rank("events_fts", 2.0));
        await Assert.That(PalOrmFts.Highlight("events_fts", 0)).IsEqualTo(DapperFts.Highlight("events_fts", 0));
        await Assert.That(PalOrmFts.Snippet("events_fts", 1, "[", "]", "…", 8)).IsEqualTo(DapperFts.Snippet("events_fts", 1, "[", "]", "…", 8));
        await Assert.That(PalOrmFts.DropStatements("events_fts", standalone: true).SequenceEqual(
            DapperFts.DropStatements("events_fts", standalone: true))).IsTrue();
        await Assert.That(PalOrmFts.DropLegacyStatements("events_fts").SequenceEqual(
            DapperFts.DropLegacyStatements("events_fts"))).IsTrue();
        await Assert.That(PalOrmFts.TriggerNames("a-b").SequenceEqual(
            DapperFts.TriggerNames("a-b"))).IsTrue();
    }

    private static async Task AssertDefinitionsEqual(DapperIndex dapper, PalOrmIndex palorm)
    {
        await Assert.That(palorm.SourceTable).IsEqualTo(dapper.SourceTable);
        await Assert.That(palorm.IndexName).IsEqualTo(dapper.IndexName);
        await Assert.That(palorm.IsStandalone).IsEqualTo(dapper.IsStandalone);
        await Assert.That(palorm.KeyColumn).IsEqualTo(dapper.KeyColumn);
        await Assert.That(palorm.MappingTableName).IsEqualTo(dapper.MappingTableName);
        await Assert.That(palorm.ToSql()).IsEqualTo(dapper.ToSql());
        await Assert.That(palorm.JoinClause()).IsEqualTo(dapper.JoinClause());
        await Assert.That(palorm.JoinClause("src", "map")).IsEqualTo(dapper.JoinClause("src", "map"));
        await Assert.That(palorm.Statements.SequenceEqual(dapper.Statements)).IsTrue();
        await Assert.That(palorm.TriggerNames.SequenceEqual(dapper.TriggerNames)).IsTrue();
        await Assert.That(palorm.RebuildStatements().SequenceEqual(dapper.RebuildStatements())).IsTrue();
        await Assert.That(palorm.OptimizeStatements().SequenceEqual(dapper.OptimizeStatements())).IsTrue();
        await Assert.That(palorm.DropStatements().SequenceEqual(dapper.DropStatements())).IsTrue();
        await Assert.That(palorm.Columns.Select(c => (c.Name, c.CastToText))
            .SequenceEqual(dapper.Columns.Select(c => (c.Name, c.CastToText)))).IsTrue();
    }
}
