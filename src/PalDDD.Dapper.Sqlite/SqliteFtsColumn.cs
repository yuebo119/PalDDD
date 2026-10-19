namespace PalDDD.Dapper.Sqlite;

/// <summary>
/// FTS5 索引列描述。
/// <para><see cref="CastToText"/> 只用于 standalone 索引：生成
/// <c>CAST(NEW."列" AS TEXT)</c> 与重建时的 <c>CAST("pal_src"."列" AS TEXT)</c>，
/// 使 BLOB payload 可以进入 FTS5 分词。external-content 索引不接受该开关。</para>
/// </summary>
/// <param name="Name">源表列名，同时作为 FTS5 索引列名。</param>
/// <param name="CastToText">是否在触发器与重建语句中把该列 CAST 成 TEXT。</param>
public readonly record struct SqliteFtsColumn(string Name, bool CastToText = false);
