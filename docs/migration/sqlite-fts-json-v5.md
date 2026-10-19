# SQLite FTS5/JSON1 v5 迁移指南

> 适用对象：从 v4 升级到 v5，且直接使用 `PalDDD.Dapper.Sqlite.SqliteFts` / `SqliteJson` 的项目。
> 本轮为破坏式清理：旧签名不保留兼容层。新 API 同时存在于 `PalDDD.Dapper.Sqlite`
> 与 `PalDDD.PalORM.Sqlite`，命名空间不同、语义一致。

## 1. 为什么改

v4 的 `CreateOutboxIndex` 默认生成 external-content 索引，但 `outbox_messages.id`
是 TEXT/ULID，FTS5 的 `content_rowid` 需要 INTEGER rowid；真实表插入会报
`SQLite Error 20: datatype mismatch`。同时 JSON 路径拒绝特殊键、查询值内联成字面量、
触发器名清洗后可能碰撞。v5 用定义对象与独立索引把这些问题一次性收敛。

## 2. API 对照

| v4 | v5 |
|---|---|
| `SqliteFts.CreateOutboxIndex(sourceTable)` 返回 `string` | `SqliteFts.CreateOutboxIndex(sourceTable)` 返回 `SqliteFtsIndexDefinition`，用 `.ToSql()` 执行；TEXT 主键安全 |
| `SqliteFts.CreateEventLogIndex(sourceTable)` 返回 `string` | 同签名返回定义对象；默认 standalone，`payload` 以 `CAST(... AS TEXT)` 入索引 |
| `SqliteFts.Match(index, query)` | `MatchLiteral(index, query)`（常量）或 `MatchParameter(index, "q")`（参数） |
| `SqliteFts.Rebuild(indexName)` | `definition.RebuildStatements()` |
| `SqliteFts.Optimize(indexName)` | `definition.OptimizeStatements()` |
| `SqliteFts.Drop(indexName)` | `definition.DropStatements()` 或 `SqliteFts.DropStatements(indexName, standalone)` |
| `SqliteJson.Extract(column, key)` | `SqliteJson.Extract(column, params path)`，路径始终引号化 |
| `SqliteJson.ExtractPath(column, a, b)` | `SqliteJson.Extract(column, a, b)` |
| `SqliteJson.Array(...)` | `SqliteJson.ArrayLiteral(...)` 或 `ArrayParameters(...)` |
| `SqliteJson.BuildObject(...)` | `SqliteJson.ObjectLiteral(...)` 或 `ObjectParameters(...)` |
| 旧触发器名 `{清洗名}_ai/ad/au` | `fts_{清洗名}_{hash}_ai/ad/au` |

## 3. 存量索引升级顺序

v4 创建的触发器仍挂在源表上。先删旧触发器与旧虚表，再建新索引：

```csharp
foreach (var statement in SqliteFts.DropLegacyStatements("events_fts"))
{
    using var command = connection.CreateCommand();
    command.CommandText = statement;
    command.ExecuteNonQuery();
}

var definition = SqliteFts.CreateEventLogIndex("events");
await SqliteFts.ValidateAsync(connection, definition);
foreach (var statement in definition.Statements)
{
    using var command = connection.CreateCommand();
    command.CommandText = statement;
    command.ExecuteNonQuery();
}
```

顺序不能反：先建新索引再删旧触发器，旧触发器仍会写入旧虚表；旧虚表被删后源表写入会报
`no such table`。

## 4. Dapper 用法

```csharp
var definition = SqliteFts.CreateOutboxIndex("outbox_messages");
await SqliteFts.ValidateAsync(connection, definition);

foreach (var statement in definition.Statements)
    await connection.ExecuteAsync(statement);

const string sql = """
    SELECT s.*
    FROM outbox_messages s
    JOIN outbox_messages_fts_map m ON m.source_key = CAST(s.id AS TEXT)
    JOIN outbox_messages_fts f ON f.rowid = m.fts_rowid
    WHERE outbox_messages_fts MATCH @q
    ORDER BY bm25(outbox_messages_fts)
    """;

var rows = await connection.QueryAsync<OutboxRow>(sql, new { q = "urgent" });
```

也可以用 `definition.JoinClause()` 生成 JOIN 片段：

```csharp
var sql = $"SELECT s.* FROM outbox_messages s {definition.JoinClause()} " +
          $"WHERE {SqliteFts.MatchParameter(definition.IndexName, "q")} " +
          SqliteFts.OrderByRank(definition.IndexName);
```

external-content 索引：

```csharp
var definition = SqliteFts.CreateExternalContentIndex(
    "events", "events_fts", "global_position", ["event_name", "payload"]);
await SqliteFts.ValidateAsync(connection, definition); // TEXT rowid 会在这里失败
```

## 5. PalORM 用法

```csharp
await using var session = await DataSession<SqliteProvider>.CreateAsync(
    DbOptions.Development(connectionString));

var definition = SqliteFts.CreateOutboxIndex("outbox_messages");
await session.EnsureIndexAsync(definition); // 校验 + 建表 + 触发器

var format = "SELECT s.* FROM outbox_messages s "
    + definition.JoinClause()
    + " WHERE "
    + SqliteFts.MatchExpression(definition.IndexName)
    + " {0}";
var sql = FormattableStringFactory.Create(format, "urgent");
var rows = await session.QueryAsync<OutboxRow>(sql);
```

JSON 谓词同样用格式洞，不要用字符串插值把片段塞进 `FormattableString`：

```csharp
var predicate = SqliteJson.Extract("payload", "Type") + " = {0}";
var sql = FormattableStringFactory.Create(
    "SELECT COUNT(*) FROM outbox_messages WHERE " + predicate,
    "Order.Created");
var count = await session.ScalarAsync<long>(sql);
```

原生命令与 `@q` 参数：

```csharp
using var command = session.GetRawConnection().CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM outbox_messages WHERE "
    + SqliteJson.EqualsParameter("payload", "type", "Type");
var parameter = command.CreateParameter();
parameter.ParameterName = "@type";
parameter.Value = "Order.Created";
command.Parameters.Add(parameter);
```

## 6. JSON 路径行为

- 对象键始终用双引号包裹，支持 `.`、`[`、`]`、空格。
- 键内反斜杠写作 `\\`，双引号写作 `\"`；helper 负责转义，调用方直接传原始键。
- 空路径表示根文档：`Extract("payload")` 生成 `json_extract("payload", '$')`。
- 值优先用 `*Parameter` 方法。`*Literal` 方法只用于常量，且只做单引号翻倍。

## 7. 数据形态约束

- FTS5/JSON1 只处理文本 JSON。PalORM 与 Dapper 的 `payload` 是 BLOB；SQLite 3.45.1+
  会把看起来像文本 JSON 的 BLOB 当作文本处理。被 PalDDD.Compression 压缩过的 payload
  不是文本，必须先解压，或只索引 `event_name`/`type`。
- `CreateEventLogIndex` 与 `CreateOutboxIndex` 默认把 `payload` 以 `CAST(... AS TEXT)`
  纳入索引；确实不需要全文检索 payload 时传 `includePayload: false` 降低索引体积。
- 独立索引会额外创建 `{indexName}_map` 映射表；`DropStatements()` 会一并删除。

## 8. 验证

升级后至少运行：

```bash
dotnet test test/PalDDD.Integration.Tests -- --treenode-filter "/*/*/Sqlite*Tests/*"
dotnet test test/PalDDD.PalORM.Tests -- --treenode-filter "/*/*/SqliteFtsPalOrmTests/*"
```

如果生产库已有旧索引，先在影子库执行第 3 节顺序，确认 `sqlite_master` 中只剩新触发器，
再对生产库执行。
