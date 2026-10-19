using PalDDD.Shared;

namespace PalDDD.PalORM.Sqlite;

/// <summary>
/// FTS5 索引定义：创建语句、触发器名、重建/优化/删除语句与查询 JOIN 片段的单一事实源。
/// <para>实例由 <see cref="SqliteFts"/> 的工厂方法产生；构造期已预计算全部 SQL 文本。</para>
/// </summary>
public sealed class SqliteFtsIndexDefinition
{
    private readonly string _sql;

    internal SqliteFtsIndexDefinition(FtsDefinitionCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        Core = core;
        SourceTable = core.SourceTable;
        IndexName = core.IndexName;
        IsStandalone = core.Kind == FtsIndexKind.Standalone;
        KeyColumn = core.KeyColumn;
        Columns = core.Columns.Select(c => new SqliteFtsColumn(c.Name, c.CastToText)).ToArray();
        TriggerNames = core.TriggerNames;
        MappingTableName = core.MappingTableName;
        Statements = core.Statements;
        _sql = SqliteSqlCore.JoinStatements(core.Statements);
    }

    internal FtsDefinitionCore Core { get; }

    /// <summary>源表名。</summary>
    public string SourceTable { get; }

    /// <summary>FTS5 虚表名。</summary>
    public string IndexName { get; }

    /// <summary>true 表示独立索引（映射表 + 触发器）；false 表示 external-content 索引。</summary>
    public bool IsStandalone { get; }

    /// <summary>
    /// 源表键列。external-content 时是 INTEGER rowid 列；standalone 时是任意主键列，
    /// 触发器与 JOIN 会把它 CAST 成 TEXT 后写入映射表。
    /// </summary>
    public string KeyColumn { get; }

    /// <summary>索引列描述。</summary>
    public IReadOnlyList<SqliteFtsColumn> Columns { get; }

    /// <summary>三个同步触发器的完整名称。</summary>
    public IReadOnlyList<string> TriggerNames { get; }

    /// <summary>standalone 索引的映射表名；external-content 为 null。</summary>
    public string? MappingTableName { get; }

    /// <summary>按执行顺序排列的创建语句（不含末尾分号）。</summary>
    public IReadOnlyList<string> Statements { get; }

    /// <summary>以分号换行拼接的全部创建语句，可直接交给支持多语句的 SQLite 命令执行。</summary>
    public string ToSql() => _sql;

    /// <summary>
    /// 生成查询侧 JOIN 片段。调用方把源表别名传给 <paramref name="sourceAlias"/>，
    /// 并把该片段放在源表 FROM 之后。
    /// <para>standalone 形态会先 JOIN 映射表再 JOIN FTS5 虚表；external-content 形态直接按 rowid JOIN。</para>
    /// </summary>
    /// <param name="sourceAlias">调用方源表别名，默认 <c>s</c>。</param>
    /// <param name="mapAlias">standalone 映射表别名，默认 <c>m</c>；external-content 忽略。</param>
    public string JoinClause(string sourceAlias = "s", string mapAlias = "m")
        => SqliteFtsCore.BuildJoinClause(Core, sourceAlias, mapAlias);

    /// <summary>
    /// 重建索引：external-content 使用 FTS5 <c>rebuild</c> 命令；
    /// standalone 清空映射表与索引后从源表全量重灌。
    /// </summary>
    public IReadOnlyList<string> RebuildStatements()
        => SqliteFtsCore.BuildRebuildStatements(Core);

    /// <summary>合并 FTS5 索引碎片（<c>optimize</c> 命令）。</summary>
    public IReadOnlyList<string> OptimizeStatements()
        => SqliteFtsCore.BuildOptimizeStatements(Core);

    /// <summary>删除三个同步触发器、FTS5 虚表；standalone 还会删除映射表。</summary>
    public IReadOnlyList<string> DropStatements()
        => SqliteFtsCore.BuildDropStatements(Core);
}
