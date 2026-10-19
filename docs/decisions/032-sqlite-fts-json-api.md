# ADR 032：SQLite FTS5/JSON1 双栈 API（共享核心 + 独立索引）

> 状态：**已采纳（2026-10-19 实施中；破坏性清理，随 v5.0.0 发布）**
> 日期：2026-10-19
> 关联：ADR-012（方言项目独立）、ADR-019（不新增抽象项目）、ADR-022（共享谓词用链接源码）、ADR-026（契约窗口）、`src/PalDDD.Dapper.Sqlite/SqliteFts.cs`、`src/PalDDD.Dapper.Sqlite/SqliteJson.cs`

## 背景

SQLite 的 FTS5 与 JSON1 目前只存在于 `PalDDD.Dapper.Sqlite`，`PalDDD.PalORM.Sqlite` 的包注释明确写着“待迁移”。现状有四个结构性问题：

1. `CreateOutboxIndex` 默认生成 external-content 索引，但 `outbox_messages.id` 是 TEXT/Ulid，FTS5 external-content 的 `content_rowid` 实际要求 INTEGER rowid。探针实测插入即报 `SQLite Error 20: datatype mismatch`，该便利方法对真实 Outbox 表不可用。
2. 触发器名用 indexName 清洗后拼接，`a-b` 与 `a.b` 清洗后同为 `ab`，第二个 `CREATE TRIGGER IF NOT EXISTS` 静默跳过，索引停更。
3. JSON 路径 helper 拒绝键中的 `.`、`"`、`[`、`]`，用户只能退回手写 SQL；返回的 SQL 片段又把值内联成字面量，查询侧无法走参数化。
4. PalORM 包不能引用 Dapper 包（会把 Dapper/Dapper.AOT/Npgsql/MySqlConnector 拖进 PalORM 的依赖图），但纯 SQL 生成逻辑与 Dapper 无运行时关系。

2026-10-19 探针（SQLite 3.53.4，Microsoft.Data.Sqlite 11.0.0-rc.2）确认：

- external-content + TEXT rowid 报 `datatype mismatch`；
- 普通 FTS5 表 + `AUTOINCREMENT` 映射表 + 三触发器可以完整同步 insert/update/delete/rebuild；
- `MATCH @q`、JOIN 中的 `bm25(实际表名)` 可用；
- JSON 引号路径 `$."a.b"`、`$."c[0]"`、`$."d e"` 可用；键内双引号写作 `\"`、反斜杠写作 `\\`；
- `json_extract` 接受 UTF-8 JSON 的 BLOB 输入；`CAST(BLOB AS TEXT)` 后可进 FTS5 分词；
- PalORM `GetRawConnection()` 返回打开的连接，多语句 DDL 可执行。

## 决策

**D1 共享核心用链接源码，不新增包。** 三个 internal 文件放 `src/PalDDD.Shared/`，两个 SQLite 方言包各自 `<Compile Include>`。共享核心只依赖 BCL，不引用 Dapper 或 PalORM。沿用 ADR-022 的判定：新包会引入额外发布单元与依赖图变更，而这里共享的是纯函数，链接源码足够。

**D2 每栈保留薄公开门面。** Dapper 与 PalORM 各自拥有 `SqliteFts`、`SqliteJson`、`SqliteFtsIndexDefinition`、`SqliteFtsColumn`，命名空间分别为 `PalDDD.Dapper.Sqlite` 与 `PalDDD.PalORM.Sqlite`。门面只做参数校验、类型映射与文档，全部生成逻辑在共享核心。

**D3 FTS5 以定义对象为中心。** `SqliteFtsIndexDefinition` 携带源表、索引名、键列、列描述、触发器名、映射表名与创建语句，并提供 `ToSql`、`JoinClause`、`RebuildStatements`、`OptimizeStatements`、`DropStatements`。external-content 与 standalone 两种形态由不同工厂产生，不再用“一个方法猜主键类型”。

**D4 高层便利方法默认 standalone。** `CreateEventLogIndex` 与 `CreateOutboxIndex` 直接用独立索引，键分别为 `global_position` 与 `id`，默认把 `payload` 以 `CAST(... AS TEXT)` 纳入索引。这样 Dapper 与 PalORM 的真实 BLOB payload 都能工作。external-content 作为低层 API 保留，并在 `ValidateAsync` 中强制校验 INTEGER 主键与列存在性。

**D5 触发器名稳定抗碰撞。** `fts_{清洗名}_{FNV-1a64}_{ai|ad|au}`，原 indexName 的哈希消除清洗碰撞。v4 旧名字通过 `DropLegacyStatements` 迁移。

**D6 JSON1 默认安全形态。** 路径始终用双引号包裹对象键，键内 `"` 与 `\` 按探针规则转义，单引号按 SQL 字面量规则翻倍；值默认走 `*Parameter` 方法，`*Literal` 只服务常量。所有标识符、参数名、路径段在构建期校验。

**D7 PalORM 只做 DDL 生命周期与片段。** `EnsureIndexAsync`、`RebuildIndexAsync`、`OptimizeIndexAsync`、`DropIndexAsync`、`IndexExistsAsync` 通过 `DataSession<SqliteProvider>.GetRawConnection()` 执行。查询侧给出原生 `DbCommand + @q` 与 `FormattableStringFactory.Create` 两条配方，不造 `FormattableString` 查询构建器。原因：PalORM 的 PD18 约束禁止把 SQL 片段作为插值洞，片段组合会变成第二套不完全的 SQL builder。

**D8 破坏式清理。** 旧方法不保留兼容层，全部进 v5.0.0 迁移指南与 CHANGELOG `[Breaking]`。

## API 设计

```csharp
// 两个包各一份，命名空间不同
public readonly record struct SqliteFtsColumn(string Name, bool CastToText = false);

public sealed class SqliteFtsIndexDefinition
{
    public string SourceTable { get; }
    public string IndexName { get; }
    public bool IsStandalone { get; }
    public string KeyColumn { get; }
    public IReadOnlyList<SqliteFtsColumn> Columns { get; }
    public IReadOnlyList<string> TriggerNames { get; }
    public string? MappingTableName { get; }
    public IReadOnlyList<string> Statements { get; }
    public string ToSql();
    public string JoinClause(string sourceAlias = "s", string mapAlias = "m");
    public IReadOnlyList<string> RebuildStatements();
    public IReadOnlyList<string> OptimizeStatements();
    public IReadOnlyList<string> DropStatements();
}

public static class SqliteFts
{
    public static SqliteFtsIndexDefinition CreateExternalContentIndex(...);
    public static SqliteFtsIndexDefinition CreateStandaloneIndex(...);
    public static SqliteFtsIndexDefinition CreateEventLogIndex(...);
    public static SqliteFtsIndexDefinition CreateOutboxIndex(...);
    public static Task ValidateAsync(DbConnection connection, SqliteFtsIndexDefinition definition, CancellationToken ct = default);
    public static string MatchLiteral(string index, string query);
    public static string MatchParameter(string index, string parameterName);
    public static string MatchExpression(string index);
    public static string OrderByRank(string index, params double[] weights);
    public static string Rank(string index, params double[] weights);
    public static string Highlight(string index, int columnIndex, string open = "<b>", string close = "</b>");
    public static string Snippet(string index, int columnIndex, string start = "<b>", string end = "</b>", string ellipsis = "...", int tokenCount = 64);
    public static IReadOnlyList<string> TriggerNames(string indexName);
    public static IReadOnlyList<string> DropStatements(string indexName, bool standalone = false);
    public static IReadOnlyList<string> DropLegacyStatements(string indexName);
}

public static class SqliteJson
{
    public static string Path(params string[] segments);
    public static string Extract(string column, params string[] path);
    public static string ExtractText(string column, params string[] path);
    public static string ExtractJson(string column, params string[] path);
    public static string Type(string column, params string[] path);
    public static string IsValid(string column);
    public static string HasKey(string column, params string[] path);
    public static string ArrayLength(string column, params string[] path);
    public static string ArrayLiteral(params string[] values);
    public static string ArrayParameters(params string[] parameterNames);
    public static string ObjectLiteral(params (string Key, string Value)[] pairs);
    public static string ObjectParameters(params (string Key, string ParameterName)[] pairs);
    public static string SetParameter(string column, string parameterName, params string[] path);
    public static string InsertParameter(string column, string parameterName, params string[] path);
    public static string ReplaceParameter(string column, string parameterName, params string[] path);
    public static string PatchParameter(string column, string parameterName);
    public static string Remove(string column, params string[] path);
    public static string QuoteLiteral(string value);
    public static string GroupArray(string column);
    public static string GroupObject(string keyColumn, string valueColumn);
    public static string Each(string column, params string[] path);
    public static string Tree(string column, params string[] path);
    public static string EqualsLiteral(string column, string value, params string[] path);
    public static string EqualsParameter(string column, string parameterName, params string[] path);
    public static string OutboxByType(string messageType);
}
```

PalORM 额外提供：

```csharp
public static class SqliteFtsSessionExtensions
{
    public static Task EnsureIndexAsync(this DataSession<SqliteProvider> session, SqliteFtsIndexDefinition definition, CancellationToken ct = default);
    public static Task RebuildIndexAsync(this DataSession<SqliteProvider> session, SqliteFtsIndexDefinition definition, CancellationToken ct = default);
    public static Task OptimizeIndexAsync(this DataSession<SqliteProvider> session, SqliteFtsIndexDefinition definition, CancellationToken ct = default);
    public static Task DropIndexAsync(this DataSession<SqliteProvider> session, SqliteFtsIndexDefinition definition, CancellationToken ct = default);
    public static Task<bool> IndexExistsAsync(this DataSession<SqliteProvider> session, string indexName, CancellationToken ct = default);
    public static Task EnsureEventLogIndexAsync(this DataSession<SqliteProvider> session, string sourceTable = "events", bool includePayload = true, string? tokenizer = null, IReadOnlyList<int>? prefixIndexes = null, CancellationToken ct = default);
    public static Task EnsureOutboxIndexAsync(this DataSession<SqliteProvider> session, string sourceTable = "outbox_messages", bool includePayload = true, string? tokenizer = null, IReadOnlyList<int>? prefixIndexes = null, CancellationToken ct = default);
}
```

## 被否方案

| 方案 | 不选原因 |
|---|---|
| 新增 `PalDDD.Sqlite` 共享包 | 包数 35→36，release 的 `EXPECTED=35`、README/docs 计数、快照程序集、架构白名单全部要改；现有 Dapper 类型迁移属破坏性变更。共享的是纯函数，链接源码成本更低（ADR-022 先例）。 |
| PalORM.Sqlite 直接引用 Dapper.Sqlite | 会把 Dapper、Dapper.AOT、Npgsql、MySqlConnector 拖进 PalORM 包，破坏双栈平级与 AOT 边界。 |
| 两份复制实现 | 本仓已有共享谓词漂移的实证（ADR-022），复制必然分叉。 |
| 只支持 external-content | 真实 Outbox 的 TEXT 主键不可用，留下已知 P1 缺口。 |
| 做 LINQ/表达式树级 FTS 抽象 | FTS5 只有 SQLite；PG 是 tsvector/GIN，MySQL 是 FULLTEXT，语义交集极小。表达式树还会触碰零反射红线。 |
| 查询片段内联值 | 破坏语句计划缓存，用户输入无法走参数化。 |

## 影响

- 两个包各自新增/重写公开 API，公共 API 快照与 CHANGELOG 必须同批更新（G23）。
- `src/PalDDD.Shared/` 增加三个 internal 文件，`src/**/*.cs` 计数变化，`count-audit` 声明的源文件数需同步。
- 现有 FTS 索引升级必须先执行 `DropLegacyStatements`，否则旧触发器继续写入旧索引表。
- PalORM 包新增 SQLite FTS/JSON1 能力，但不新增依赖包；架构测试锁定其不得引用 Dapper/Microsoft.Data.Sqlite。
- 破坏性变更随 v5.0.0 发布；`[Unreleased] [Breaking]` 累积，发版提交统一转版本。

## 不做

- 不做 contentless FTS5、JSONB 二进制函数、跨方言全文检索统一抽象。
- 不在 Store 内自动创建 FTS 索引。索引属于应用 schema，框架只提供 DDL 与执行入口。
- 不把压缩后的 payload 直接送进 FTS/JSON。压缩数据不是文本，需应用层先解压。
- 不提供 PalORM 的 LINQ 式 FTS 查询；查询侧用原生 SQL 配方。

## 验证

- 探针与执行级测试覆盖：external-content INTEGER 主键、standalone TEXT 主键、BLOB 转文本、触发器 insert/update/delete、Drop 后源表 DML、rebuild、JSON 特殊键、BLOB JSON、`MATCH @q`、`bm25`。
- `ValidateAsync` 对 TEXT rowid 的 external-content 在 DDL 前抛出精确异常。
- PalORM 包依赖图不含 Dapper、Npgsql、MySqlConnector、Microsoft.Data.Sqlite。
- AOT 样例运行新 API；`gate.cs`、`count-audit`、`verify-conventions`、格式与质量分析器全绿。
