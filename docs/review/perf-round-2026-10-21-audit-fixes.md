# 2026-10-21 第四轮独立审计实施

## 1. 范围与结论

本轮按 2026-10-20 独立审计实施 P0 与低风险 P1/P2。审计发现的最高优先级不是微优化，而是三处正确性缺陷：

1. PalORM 三个注册入口默认启动保留清理，与三处文档的“默认不注册、不运行”矛盾。
2. SQLite 保留清理的 cutoff 用 `"O"`（T 分隔），EF/PalORM 经 provider 写入的是空格分隔；同一天的行会被提前最多约 24 小时删除。
3. Saga 默认全量扫描按 `created_at` 取前 N 条，释放后租约清空又回到头部；活跃 Saga 超过批大小时第 N+1 条起永远不被扫描。

三项均已修复，并补了失败探针。第四项是上一轮“批量”只做到事务层批量，Dapper/PalORM 仍逐行往返；本轮改为单语句多行推进。

## 2. 已落地

### 2.1 PalORM 保留清理改为显式 opt-in

- 从 `PostgreSqlPalOrmExtensions.cs`、`MySqlPalOrmExtensions.cs`、`SqlitePalOrmExtensions.cs` 移除无条件 `AddPalRetention()`；只保留 `IRetentionStore` 能力注册。
- 需要清理的宿主在 `AddPalOrm*` 之后显式 `AddPalRetention()`。
- 锁定测试：`PalOrmRetentionRegistrationTests` 断言默认 descriptor 不含 `RetentionProcessorService`，显式调用后恰好 1 个。

### 2.2 SQLite 保留时间编码按栈对齐

- `RetentionSql.ToTimeParam` 增加 `sqliteProviderNative` 参数：EF/PalORM 的 SQLite 分支传原生 `DateTimeOffset`（provider 写空格格式），Dapper 保持 `"O"`。
- 锁定测试：`EfCoreRetention_Sqlite_DoesNotDeleteSameDayProviderFormattedRow`。同一天 23:00 的 EF 行在 cutoff 为当日 00:00 时必须保留；修复前该测试判红。
- 现有 Dapper 保留测试保持原编码路径。

### 2.3 Saga 扫描轮转（不新增列）

没有引入 `last_checked_at` 新列。复用 `leased_until` 作为轮转游标：

- 新增 `ISagaScanCursorStore<TState>`：批量释放时把 `leased_until` 写成当前扫描时刻；三参数旧接口保持清 NULL 兼容。
- legacy 租约排序改为 `leased_until NULLS FIRST, created_at`（PG）或 `(leased_until IS NULL) DESC, leased_until, created_at`（MySQL/SQLite/SQL Server）。
- 处理器释放时优先调用 4 参接口并传 tick 的 `now`；OCE/非批量释放路径也把 `leased_until` 写回 `now`。
- 四栈实现：Dapper、PalORM、EF Core、InMemory；无 schema 迁移。
- 锁定测试：InMemory/Dapper/PalORM/EF SQLite 各一条轮转测试，租出 1 条后释放，下一次必须租到另一条；修复前返回同一条。

### 2.4 Dapper/PalORM 单语句批量推进 NextCheckAt

- Dapper：按 300 行分块构造单条多行 UPDATE。PG `UPDATE ... FROM (VALUES ...)`；SQLite `UPDATE ... FROM (SELECT ... UNION ALL ...)`；MySQL `UPDATE ... JOIN (SELECT ... UNION ALL ...)`。
- PalORM：同样按 300 行分块，raw command 参数化；三种方言分别构造。
- 全命中才回写内存 Version；部分冲突时不猜测，调用方重新加载。
- 原有 2 行批量测试与 256 行以下路径均通过；SQLite 参数上限 999 由 300×3+1 守住。
- 事务边界保持：无 ambient 事务时整批一个事务，有 ambient 时并入。

### 2.5 其余落地

- EF EventLog：每次 append 从两次 `ChangeTracker.Entries<T>()` 全量扫描改为按本批 `EntityEntry<StoredEvent>` 精确 Detach；allocator 由 `EventLogPositionReserver` 保存成功后自清。
- Outbox 排空：`Task.Delay(1ms)` 改为 `Task.Yield()`。本机实测 `Task.Delay(1)` 20 次平均 11.46ms，原实现把满批续批钉在约 11ms。
- Outbox 标记阈值：Auto 模式从固定 16 改为 `min(BatchSize, 400)`，100 条批从 7 次 UPDATE 收敛为 1 次。
- StreamRunRegistry：无心跳配置时从 500ms 超时轮询改为无限等待信号，空闲附着不再每秒醒两次。
- NullMessageBroker：声明 `SupportsRawPublish`/`SupportsConcurrentPublish` 并实现空 `PublishRawAsync`，单节点 Outbox 不再逐条反序列化后丢弃。

## 3. 验证

- `dotnet run scripts/check-all.cs`：IDE 0 项、CA 0 错误、编译 0 错误。
- 全量测试面板：1772 个测试，1695 通过、77 跳过、0 失败。
- 跳过分布：PalORM 54（Testcontainers 未启用）、Integration 14、Messaging.Integration 9（外部 broker/容器车道）。
- 公共 API 快照：+13 行、−6 行（新增 `ISagaScanCursorStore<TState>` 与四栈 4 参释放、`RetentionSql.ToTimeParam` 可选参数、Saga 租约 SQL 文本更新）。
- 新增锁定测试：PalORM 默认注册 2 件、EF/PalORM/InMemory/Dapper 扫描轮转 4 件、SQLite 保留跨栈 1 件；既有多行批测试继续覆盖 Dapper/PalORM 单语句路径。

## 4. 本轮明确未做

| 项 | 原因 |
|---|---|
| Outbox `next_attempt_at`/`locked_until` 的可 sargable 到期列/索引 | 需要新增 generated column 或三支 UNION 查询与三方言迁移；属 schema 变更，需单独迁移窗口与 `EXPLAIN` 验证，不能混在行为修复提交里 |
| EF EventLog binary 模式读投影（不拉 text 列） | 需要回放专用 read model 与 binary 回填前置；当前只做了 append 清理扫描收敛 |
| Saga 租约回读 `(leased_by, leased_until)` 索引 | DDL 变更，需三方言迁移与既有库在线加索引；代码侧无阻塞 |
| Saga 租约查询只取超时元数据 | 需要把 `StepStartedAt`/`ExecutedStepKeys` 从快照拆列或侧表，属结构迁移 |
| DomainEventDispatcher 类型索引 + 惰性解析 | 涉及 scoped handler 生命周期与 DI 注册形状，需独立设计 |
| 幂等 Dapper/PalORM 冲突路径 3→2 往返 | 需要重构 `TryStart` 内部返回 `IdempotencyStartResult`，与本轮 P0 不同批 |
| Kafka 默认发布窗口、SSE 批量 flush、MemoryPack 直写、投影在线检查点批处理 | 分别涉及行为默认值、TTFT/帧可见性与序列化器契约，需单独量测 |
| PalORM Scoped 会话异步化 | DI 工厂同步 API 的接口级改造，需宿主配合 |

## 5. 残余风险

- Saga 轮转依赖 `leased_until` 非空作为游标；三参数旧接口和自定义 store 仍清 NULL，会保留旧饿死语义。内置四栈与处理器路径已切换，自定义实现需实现 `ISagaScanCursorStore<T>`。
- 本轮 SQL 多行推进的 PG/MySQL 路径只在 SQLite 本地测试与既有方言 SQL 形状上验证；真库尚未跑 256 行批探针。
- Outbox 到期索引、EventLog binary 读投影、Saga 元数据投影仍是大表规模下的主要结构项。
