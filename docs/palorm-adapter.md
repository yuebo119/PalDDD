# PalDDD.PalORM 适配层 — 设计与迁移指南

> **版本**：v5.2（2026-08-16，二十一轮评审勘正：EventLog 列名/乐观锁列描述、补方言包与 SagaSnapshot 章节）
> **状态**：核心层 + 三方言包（SQLite/PostgreSQL/MySQL）已落地，AOT 验证通过
> **定位**：吸取 Dapper + EFCore 双方经验的第三条 ORM 适配路

---

## 一、三轨并行定位

PalDDD 同时维护三套 ORM 适配层，按场景选择：

| 适配层 | 定位 | 何时选 | AOT |
|---|---|---|---|
| **PalDDD.Dapper** | 手写 SQL + Dapper.AOT 声明（但 `[module:DapperAot]` 实际禁用，靠 NoWarn 假装兼容） | 维护遗留，逐步弃用 | ⚠️ 假象（NoWarn IL3058） |
| ~~`PalDDD.EntityFrameworkCore`~~ | ~~全功能 + EF Core 11 + 反射重~~ | ~~需要 Migration / LINQ / ChangeTracker~~ | ~~❌ 全员 `IsAotCompatible=false`~~ |
| **PalDDD.PalORM** | PalORM 源生成 + 编译期 SQL + 真 AOT | AOT 发布 / 高性能 / 编译期类型安全 | ✅ **真 AOT**（`PublishAot=true` 验证） |

> **注**：`PalDDD.EntityFrameworkCore` 适配层源码未入库（`src/PalDDD.EntityFrameworkCore/` 空目录已删除，见 OBS-068），仅 `nupkgs/` 有旧版 preview.1 包。当前推荐使用 PalDDD.PalORM。

---

## 二、8 项核心设计决策

### 决策 1：命名约定 — 统一 snake_case

与 PalDDD.Dapper 兼容（迁移成本最低）。**不使用** `WithNamingConvention`（PalORM 该选项不参与列名生成，仅供手写 SQL 调用），每属性显式 `[Column("snake_case")]`。

**EventLog 亦无例外**：`EventLogRow` 全列 snake_case（`global_position`/`stream_name`/`stream_version` 等，逐属性 `[Column]` 固化）。旧文档声称"EventLog 保留 PascalCase 列名（Dapper + EFCore 双实现历史一致）"与实现不符（二十一轮勘正）。

### 决策 2：枚举存储 — 统一 int

替代 Dapper 的 string 字面量（Outbox/Inbox）和 EFCore 的默认 int。索引效率高，与 Saga 现状一致。

**破坏性变更**：需配套数据迁移脚本（见第六节）。

### 决策 3：Saga 模型 — 单 `saga_data` JSON 列

开放泛型 `TState` 在编译期未知，`[OwnedJson(typeof(Ctx))]` 无法静态绑定 —— 保留 Dapper 风格的手写 `JsonSerializer.Serialize(state, _jsonTypeInfo)` 序列化整 `TState` 到 `saga_data` 列。

### 决策 4：复合主键表 — 全程手写 SQL

`projection_checkpoints`（三列主键）和 `idempotency_records`（两列主键）被 PALORM019 拒绝实体注册 → 不定义 `[Table]` 实体，全部 `ExecuteAsync(FormattableString)` + `QueryFirstAsync<T>` 手动映射。

### 决策 5：方言分发 — `DataSession<TProvider>` 编译期特化

替代 Dapper 的 `DapperSqlDialect` 字符串 switch 和 EFCore 的 abstract base + virtual 方法。方言差异（如 `RETURNING` / `INSERT IGNORE`）通过 `TProvider.SupportsReturningClause` 静态属性分支。

### 决策 6：DI 模式 — 方言包中间类固化 TProvider

PalORM 的 `IDbProvider` 是纯 `static abstract` 接口（无实例成员），与 DI 容器"实例注入"语义不兼容 —— 必须由方言包提供具体中间类（如 `SqliteSagaStateStore<TState> : PalOrmSagaStateStore<SqliteProvider, TState>`）固化 TProvider。

### 决策 7：事务 — 单 Scoped DataSession + 自动传播

所有 Store 注入同一 Scoped `DataSession<TProvider>`。UnitOfWork 调 `BeginTransactionAsync` 后，Store 的每个 ExecuteAsync 自动附加 `GetActiveTransaction()`，无需显式传 transaction 参数。

### 决策 8：乐观锁 — `[ConcurrencyCheck]` 声明式（仅 int/long）

PALORM012 约束：`[ConcurrencyCheck]` 仅支持非 nullable int/long（源生成器 emit `++` 自增）。**推翻** v4 方案的 DateTimeOffset 时间戳乐观锁假设 —— Inbox 用 `Attempts` 计数器替代 EFCore 的 `ProcessingStartedAt`。

---

## 三、7 Store 实现分级

### A 级：QueryBuilder + 声明式特性（5 Store）

| Store | 主键 | PalORM 特性 | 手写 SQL 降级点 |
|---|---|---|---|
| **Outbox** | `id` (Ulid string) | `[ConcurrencyCheck]retry_count` | LeasePending 的 `UPDATE...WHERE id IN (SELECT...LIMIT n) RETURNING *` |
| **Inbox** | `id` (long 自增) | `[ConcurrencyCheck]attempts` | 三方言 INSERT 分叉（`ON CONFLICT` vs `INSERT IGNORE`） |
| **Saga** | `saga_id` (Ulid string) | 手写 `WHERE version=@expected` | 开放泛型 JSON 无法 `[OwnedJson]` |
| **EventLog** | `global_position` (long 自增) | `[ConcurrencyCheck]stream_version` | 循环 INSERT（事件溯源语义） |
| **UnitOfWork** | — | 包装 `BeginTransactionAsync` | — |

### B 级：纯手写 SQL（2 Store，复合主键）

| Store | 复合主键 | 实现 |
|---|---|---|
| **Projection** | `(projection_name, source_name, position)` | `ExecuteAsync` + 手动 `DbDataReader` 列序映射 |
| **Idempotency** | `(operation_name, key)` | 同上 |

---

## 四、消费方零改动

所有 8 个消费方（OutboxBatchProcessor / OutboxProcessor / SagaTimeoutProcessor / ProjectionProcessor / ProjectionRebuilder / EventLogReplaySource / IdempotencyProcessor / PostgreSqlOutboxNotifier）均接口注入，PalORM 替换后**零代码改动**。

DI 切换示例：

```csharp
// 原 Dapper
services.AddPalDapperTransactions(DapperDbType.Sqlite, connectionString);

// 切换为 PalORM（一行替换）
services.AddPalOrmSqlite(connectionString);
```

### 4.1 方言包矩阵（SQLite / PostgreSQL / MySQL）

三方言包同构（7 中间固化类 + DI 扩展），方言差异由 `TProvider.SupportsReturningClause` 等编译期属性分支（决策 5）：

| 方言包 | DI 入口 | 方言特性 |
|---|---|---|
| `PalDDD.PalORM.Sqlite` | `AddPalOrmSqlite(services, cs, options?, clock?)` | 嵌入式基准路径，AOT 验证载体 |
| `PalDDD.PalORM.PostgreSql` | `AddPalOrmPostgreSql(services, cs, options?, clock?)` | 支持 RETURNING —— Outbox LeasePending / Inbox TryStart 单语句原子路径（无两步回读）；BulkInsert 走 Npgsql Binary COPY |
| `PalDDD.PalORM.MySql` | `AddPalOrmMySql(services, cs, options?, clock?)` | 不支持 RETURNING（两步 UPDATE+SELECT）；BulkInsert 检测 `local_infile` 系统变量自动选 BulkCopy 或多值 INSERT |

### 4.2 Saga 快照持久化注册（saga_data 列）— 必读

三方言的一键注册中，`ISagaStateStore<>` 开放泛型（如 `PostgreSqlSagaStateStore<>`）无 `JsonTypeInfo` 传入通道——`jsonTypeInfo` 恒 null。

> ⚠️ **不调用 SagaSnapshot 便捷注册则 saga_data 不持久化（重启丢业务字段）**——TState 业务字段不落库，仅框架字段存活。须在对应 `AddPalOrmXxx` 之后调用（依赖其 DataSession 注册）：

```csharp
services.AddPalOrmPostgreSql(connectionString);
// 覆盖开放泛型（MS DI 具体泛型优先），闭包传入 JsonTypeInfo
services.AddPalOrmPostgreSqlSagaSnapshot<OrderSagaState>(
    OrderSagaStateContext.Default.OrderSagaState);
// MySQL: services.AddPalOrmMySqlSagaSnapshot<TState>(jsonTypeInfo)
// SQLite: services.AddPalOrmSqliteSagaSnapshot<TState>(jsonTypeInfo)
```

（Dapper 侧同款陷阱见 `MySqlServiceCollectionExtensions.AddPalMySqlSagaSnapshot` 与 `DapperServiceCollectionExtensions.AddPalDapperSagaSnapshot`。）

---

## 五、AOT 验证（核心卖点）

`samples/PalDDD.PalOrmSample` 用真实 `PublishAot=true` 验证：

```bash
dotnet publish samples/PalDDD.PalOrmSample/PalDDD.PalOrmSample.csproj \
  -c Release -r win-x64
# 编译期 0 警告（TreatWarningsAsErrors）
# 运行时 PASSED（7 Store 端到端 CRUD + 事务）
```

**填补的缺口**：PalDDD 此前从未做过真实 AOT publish（AotSample 是纯内存 + 未触发 PublishAot）。

### AOT 净收益

| 当前 Dapper/EFCore 风险 | PalORM 替换后 |
|---|---|
| Dapper `<NoWarn>IL3058;DAP005</NoWarn>` 假装 AOT | **消除**（真源生成） |
| EFCore 全员 `IsAotCompatible=false` | **消除** |
| `[ModuleInitializer]` 注册 TypeHandler（AppDomain 污染） | **消除** |
| `MatchNamesWithUnderscores=true`（AppDomain 全局） | **消除**（编译期 `[Column]`） |
| internal/private Row DTO 反射物化 | **消除**（public + 源生成） |
| `[module:DapperAot]` 注释禁用但文档说启用 | **消除** |

---

## 六、数据迁移脚本（Dapper → PalORM）

### 6.1 Outbox（status: string → int）

```sql
-- PostgreSQL / MySQL / SQLite 通用
UPDATE outbox_messages SET status = CASE status
    WHEN 'Pending' THEN 0
    WHEN 'Processed' THEN 1
    WHEN 'Dead' THEN 2
    ELSE 0
END;
```

### 6.2 Inbox（status: string → int）

```sql
UPDATE inbox_messages SET status = CASE status
    WHEN 'Pending' THEN 0
    WHEN 'Processing' THEN 1
    WHEN 'Processed' THEN 2
    WHEN 'Failed' THEN 3
    ELSE 0
END;
```

### 6.3 Payload 列（BLOB → Base64 TEXT）

PalORM 的 `byte[]` 经 `[Converter(typeof(ByteArrayBase64Converter))]` 转 Base64 string 存储。原 Dapper 的 BLOB 列需要转 Base64：

```sql
-- PostgreSQL
ALTER TABLE outbox_messages ALTER COLUMN payload TYPE TEXT USING encode(payload, 'base64');
-- MySQL
ALTER TABLE outbox_messages MODIFY COLUMN payload TEXT;
-- SQLite（需新建表 + 数据迁移，SQLite 不支持 ALTER COLUMN TYPE）
```

---

## 七、已知限制与未启用特性

### 已知限制（PalORM 当前版本）

- **复合主键**：PALORM019 拒绝（Projection/Idempotency 走手写 SQL）
- **`byte[]` 不在白名单**：PALORM016，必须 `[Converter]` 转 Base64
- **`[ConcurrencyCheck]` 仅 int/long**：PALORM012，DateTimeOffset 时间戳乐观锁不可用
- **多映射 `Query<T1, T2>`**：不支持（用 QueryBuilder JOIN 或手写 DTO）
- **动态表名**：不支持（表名编译期固化）

### 未启用特性（待后续增强）

- **`[TenantAware]` 多租户**：需建表 DDL 同步加 `tenant_id` 列（PALORM018 强制）
- **`WithRetry` / `WithCircuitBreaker`**：替代 Polly（OutboxProcessor 应用层重试仍需手写）
- **`ForRead()` 读写分离**：EventLog 回放场景适用（待补 sample）
- **`[SoftDelete]`**：列名硬编码 `deleted_at`，不适用于 Outbox 的 status='Dead' 过滤

---

## 八、相关文件索引

### 源码

- `src/PalDDD.PalORM/` — 核心层（7 Store + UnitOfWork + 3 Row DTO + 2 Converter）
- `src/PalDDD.PalORM.Sqlite/` — SQLite 方言包（7 中间固化类 + DI 扩展）
- `src/PalDDD.PalORM.PostgreSql/` — PostgreSQL 方言包（同构，RETURNING + Binary COPY）
- `src/PalDDD.PalORM.MySql/` — MySQL 方言包（同构，无 RETURNING + local_infile 自适应 BulkInsert）
- `samples/PalDDD.PalOrmSample/` — AOT 验证（真实 PublishAot + 运行时 CRUD）

### 关键类型

- `PalOrmOutboxStore<TProvider>` / `SqliteOutboxStore`
- `PalOrmInboxStore<TProvider>` / `SqliteInboxStore`
- `PalOrmSagaStateStore<TProvider, TState>` / `SqliteSagaStateStore<TState>`
- `PalOrmEventLog<TProvider>` / `SqliteEventLog`
- `PalOrmProjectionCheckpointStore<TProvider>` / `SqliteProjectionCheckpointStore`
- `PalOrmIdempotencyStore<TProvider>` / `SqliteIdempotencyStore`
- `PalOrmUnitOfWork<TProvider>` / `SqlitePalOrmUnitOfWork`

### DI 入口

- `SqlitePalOrmExtensions.AddPalOrmSqlite(services, connectionString, options?, clock?)`
- `PostgreSqlPalOrmExtensions.AddPalOrmPostgreSql(services, connectionString, options?, clock?)`
- `MySqlPalOrmExtensions.AddPalOrmMySql(services, connectionString, options?, clock?)`
- 各方言包的 Saga 快照注册：`AddPalOrmSqliteSagaSnapshot<TState>` / `AddPalOrmPostgreSqlSagaSnapshot<TState>` / `AddPalOrmMySqlSagaSnapshot<TState>`（不调用则 saga_data 不持久化，见 4.2）
