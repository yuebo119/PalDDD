# 迁移：PostgreSQL 序列缓存（2026-10-21）

> 适用：已有 PostgreSQL 部署按本文件执行一次；新建部署直接使用更新后的 `postgresql/000_schema.sql`。
> 目标：降低 `inbox_messages.id` 与 `events.global_position` 两个 `BIGSERIAL` 序列在高写入下的 WAL 与页争用。

## 1. 依据

`BIGSERIAL` 背后的序列默认 `CACHE 1`：每次 `nextval` 都推进序列页并产生 WAL 记录，插入密集时序列成为单点争用。这两列都只作代理键/全局排序：

| 序列 | 用途 | 跳号是否可接受 |
| --- | --- | --- |
| `inbox_messages_id_seq` | `inbox_messages.id` 代理主键 | 是，业务按 `(consumer_name, message_id)` 唯一键判等 |
| `events_global_position_seq` | `events.global_position` 全局排序 | 是，`EventLogPositionReserver` 本就按块预留位置，跳号是既有常态 |

`CACHE 100` 把一个序列页持久化一次并服务 100 次取值；进程崩溃或重启后最多浪费 100 个值，不会重发已用值。

## 2. 核对当前值

```sql
SELECT sequencename, cache_size, last_value
FROM pg_sequences
WHERE sequencename IN ('inbox_messages_id_seq', 'events_global_position_seq');
```

`cache_size = 1` 表示仍是默认值。`last_value` 在序列尚未取值时为 null。

## 3. 执行

```sql
ALTER SEQUENCE inbox_messages_id_seq CACHE 100;
ALTER SEQUENCE events_global_position_seq CACHE 100;
```

`ALTER SEQUENCE` 只改元数据，不重写表、不阻塞读写，可在线执行。

## 4. 验证

```sql
SELECT sequencename, cache_size
FROM pg_sequences
WHERE sequencename IN ('inbox_messages_id_seq', 'events_global_position_seq');
```

两行 `cache_size` 均为 100 即完成。业务侧无需改动：EF Core 的 identity 列与 Dapper/PalORM 的 `DEFAULT` 插入都从同一序列取值。

## 5. 回滚

```sql
ALTER SEQUENCE inbox_messages_id_seq CACHE 1;
ALTER SEQUENCE events_global_position_seq CACHE 1;
```

回滚不回填已跳过的值；跳号对两张表都无业务含义。

## 6. 与 EF Core 模型的关系

EF Core 不管理序列缓存，`EnsureCreated` 新建的库仍是 `CACHE 1`。用 EF 建库的部署需手工执行第 3 节两条 `ALTER`；用 `000_schema.sql` 建库的部署已包含。
