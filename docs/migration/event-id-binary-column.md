# EventId 二进制列（M4 expand）

> 日期：2026-10-19
> 关联：`docs/review/perf-migration-plan-2026-10-19.md` M4

## 变更

`events` 增加 `event_id_bin`（16 字节）可空列与普通索引；EF/Dapper/PalORM 三栈追加时
同时写文本 `event_id` 与二进制 `event_id_bin`。读取默认仍用文本列；存量行回填后把
`UseBinaryEventId=true`（EF `EventLogDbContext`、Dapper `DapperEventLog`、
PalORM `PalOrmEventLog`）切换到二进制列。文本列与文本唯一索引保留一个 major 作为兼容/诊断面。

## DDL（expand）

PostgreSQL：

```sql
ALTER TABLE events ADD COLUMN IF NOT EXISTS event_id_bin BYTEA;
CREATE INDEX IF NOT EXISTS idx_events_event_id_bin ON events(event_id_bin);
```

MySQL：

```sql
ALTER TABLE events ADD COLUMN event_id_bin BINARY(16) NULL;
CREATE INDEX idx_events_event_id_bin ON events(event_id_bin);
```

SQLite：

```sql
ALTER TABLE events ADD COLUMN event_id_bin BLOB;
CREATE INDEX idx_events_event_id_bin ON events(event_id_bin);
```

新库直接使用 `docs/sql/*/000_schema.sql` 已包含该列与索引。

## 回填（应用侧批处理）

二进制列的写入依赖 Ulid 编解码，SQL 无法直接转换文本 ULID。回填应由应用迁移任务执行，
按 `global_position` 分批读取并更新（示例使用 `DbConnection`，参数名 `@p0/@p1` 三方言通用）：

```csharp
await using var select = connection.CreateCommand();
select.CommandText = "SELECT global_position, event_id FROM events WHERE event_id_bin IS NULL ORDER BY global_position LIMIT 1000";
await using var reader = await select.ExecuteReaderAsync(ct);
var rows = new List<(long Position, string Text)>();
while (await reader.ReadAsync(ct))
    rows.Add((reader.GetInt64(0), reader.GetString(1)));
await reader.DisposeAsync();

foreach (var (position, text) in rows)
{
    var bytes = Ulid.Parse(text).ToByteArray();
    await using var update = connection.CreateCommand();
    update.CommandText = "UPDATE events SET event_id_bin = @p0 WHERE global_position = @p1 AND event_id_bin IS NULL";
    var p0 = update.CreateParameter(); p0.ParameterName = "@p0"; p0.Value = bytes;
    var p1 = update.CreateParameter(); p1.ParameterName = "@p1"; p1.Value = position;
    update.Parameters.Add(p0); update.Parameters.Add(p1);
    await update.ExecuteNonQueryAsync(ct);
}
```

重复直到 `SELECT COUNT(*) FROM events WHERE event_id_bin IS NULL` 为 0。批间可限流，避免长事务。
回填完成后可把普通索引替换为唯一索引：

```sql
-- 先确认无重复：SELECT event_id_bin, COUNT(*) FROM events GROUP BY event_id_bin HAVING COUNT(*) > 1;
CREATE UNIQUE INDEX idx_events_event_id_bin_uq ON events(event_id_bin);
DROP INDEX idx_events_event_id_bin; -- 视方言语法
```

文本唯一索引 `idx_events_event_id` 暂不删除；下一 major 收缩时再移除。

## 切读

```csharp
context.UseBinaryEventId = true;      // EF
dapperLog.UseBinaryEventId = true;    // Dapper
palOrmLog.UseBinaryEventId = true;    // PalORM
```

读取路径在 `event_id_bin` 为 NULL（未回填/旧行）时仍回退文本列，因此切换不必等待
全量回填完成，但未回填行不会获得索引变窄的收益。

## 验收

- 新追加行 `event_id_bin` 非 NULL 且长度 16；文本列同时写入（双写）。
- `UseBinaryEventId=true` 读回的 EventId 与文本解析结果逐事件相等（SQLite 真库测试在案）。
- 回填后无 NULL 行；二进制唯一索引创建成功；文本唯一索引冲突分类行为不变。
- 回滚：`UseBinaryEventId=false` 立即回退文本读取；列与索引可保留，不影响旧路径。

## 已知边界

- 文本列与二进制列同时维护，写入侧多 16B 参数/行；Dapper 批量 14 参/行、PalORM 16 参/行，
  PG/MySQL 4000 行上限仍在 65535 占位符内。
- 二进制列使用 `BYTEA/BINARY(16)/BLOB`；Ulid 的 16 字节为大端时间前缀 + 随机尾，按字节
  比较与文本字典序一致，但任何依赖文本排序的查询仍应继续使用文本列直到单独验证。
- 回填任务属于应用迁移职责；框架不内置跨库数据搬运，避免引入 provider 依赖。

## 真库演练（2026-10-19）

```bash
# PALDDD_BENCH_PG_CONN / PALDDD_BENCH_MYSQL_CONN 由环境注入（库名须 palddd_bench 前缀）
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m4-probe-pg
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m4-probe-mysql
```

两方言均通过：旧 `events` 表先 `ALTER TABLE ADD event_id_bin` 与普通索引，播种 500 条
`event_id_bin IS NULL` 的旧行，按 100 行一批回填，抽样 10 条校验二进制重建与文本一致，
创建 `idx_events_event_id_bin_uq`，再用 `UseBinaryEventId=true` 读回 10 条。结果：
PG `seeded=500, backfilled=500, sample=10, read=10, elapsed=1095ms`；MySQL
`seeded=500, backfilled=500, sample=10, read=10, elapsed=2825ms`。

演练中发现并修复一处真实缺陷：PG 的 `ALTER TABLE ADD COLUMN` 把 `event_id_bin` 追加在
物理列末，与逻辑列序不同；`DapperEventLog` 原先用 `CommandBehavior.SequentialAccess`
要求按物理列序单调读取，按列名取 ordinal 后跨列回读会抛
`Invalid attempt to read from column ordinal`。改为 `SingleResult` 后仍逐行流式
（不物化全部行），允许行内随机列访问。

## 10 万行规模演练（2026-10-20）

```bash
# PALDDD_BENCH_PG_CONN / PALDDD_BENCH_MYSQL_CONN 由环境注入（库名须 palddd_bench 前缀）
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m4-scale-pg
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m4-scale-mysql
```

口径：10 万条旧行（`event_id_bin IS NULL`），1,000 行一批、应用侧
`Ulid.Parse(...).ToByteArray()` 回填，每批一个事务；回填后删除并重建二进制唯一索引，
测量索引体积。体积来自 PG `pg_relation_size` / MySQL `mysql.innodb_index_stats`
（`stat_name='size'` × 页大小），包含库内既有行。

| 方言 | seeded | backfilled | insert | backfill | index | text idx | bin idx | 体积下降 |
|---|---|---|---|---|---|---|---|---|
| PostgreSQL | 100,000 | 100,000 | 23,570ms | 41,031ms | 42ms | 10,010,624B | 4,087,808B | 59.2% |
| MySQL | 100,000 | 100,000 | 29,258ms | 31,956ms | 398ms | 4,734,976B | 3,686,400B | 22.1% |

原计划验收写「1M 行索引大小下降约 2/3」，实测 10 万行 PG 为 59.2%、MySQL 为 22.1%，
2/3 不成立。MySQL 的 InnoDB 二级索引每项都带主键与页开销，26 字符文本与 16 字节二进制
的差距被摊薄；收益按方言登记，不再统一宣称 2/3。回填吞吐约 2.4k~3.1k 行/s（单连接、
逐行 UPDATE、1,000 行/事务），可用于估算生产窗口。insert 耗时是逐行播种成本，
不代表生产写入性能。

