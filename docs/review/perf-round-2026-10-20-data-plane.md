# 2026-10-20 数据面性能实施轮

## 1 结论

本轮把优化重心从进程内微分配转到数据面形状。真库实测确认，Outbox 租约查询的成本由索引与谓词决定，且在低积压、高历史占比的稳态下差距最大：

| 方言 | 场景 | 改前 | 改后 | 比值 |
|---|---|---:|---:|---:|
| PostgreSQL 18.6 | 20 万行 / 1000 待租，热缓存 | 0.279ms | 0.061ms | 4.6× |
| MySQL 8.4.11 | 20 万行 / 1000 待租 | 12.2ms | 1.59ms | 7.7× |

改前 PG 计划为 `idx_outbox_status` 扫描 1000 行后 top-N 排序，改后按 `idx_outbox_due` 顺序扫描并提前停止。MySQL 改前为范围扫描加排序，改后按 `(status, created_at)` 索引序直接取满批次。

10 万待租影子库复测（PG 18.6 / MySQL 8.4.11，20 万行，10 万待租且待租时间戳整体早于历史）：PG 旧 0.099ms、新 0.070ms，两者均走顺序索引扫描；MySQL 旧约 1.4ms、新约 1.3ms。该场景下 PG 优化器改走全局 `created_at` 索引，4.6×/7.7× 的差距来自 1k 待租/20 万历史的稳态（状态索引扫描加排序），不是 10 万全量待租的普遍倍数。部分索引的主要价值转为体积（PG 2.2MB vs 4.7MB）与不同积压比例下的计划稳定性，10 万待租场景没有回归。

## 2 已实施

| 项 | 落点 | 验证 |
|---|---|---|
| Outbox 租约索引对齐谓词 | `docs/sql/{postgresql,sqlite}/000_schema.sql` 部分索引 `(created_at) WHERE status=0`；MySQL `(status, created_at)`；EF 模型 `(Status, CreatedAt)` | 真库 `EXPLAIN (ANALYZE, BUFFERS)` 前后对照；迁移脚本含 `CONCURRENTLY` 与回滚 |
| EventLog 冗余索引移除 | 四个 DDL 删除 `idx_events_global`（`global_position` 主键已覆盖） | DDL 审阅；ReadAll 仍走主键 |
| M4 收缩配方 | `docs/sql/migration-2026-10-20-performance-indexes.md` 增加回填校验、二进制唯一索引、文本唯一索引删除与回滚 | 文档步骤；未自动执行 |
| EF Saga 租约下推 | `SagaStateDbContext` PG 子查询 `FOR UPDATE SKIP LOCKED` + 等值回读，MySQL JOIN 派生表按版本启用 SKIP LOCKED，SQLite 原路径保持 | EF 测试 15/15；PG 真库（Npgsql EF 11 preview 6）8/8（含 `ExecutedStepKeys` 往返）；MySQL 全栈真库 8/8（Microting.EntityFrameworkCore.MySql 11.0.0-preview.1 + EF Core 11 rc2 + MySQL 8.4.11，含 `ExecutedStepKeys` 往返） |
| Dapper EventLog PG 多条原子追加 | `DapperEventLog.AppendEventBatchPostgreSqlAtomicAsync`：`INSERT ... SELECT ... FROM (VALUES ...) WHERE 版本守卫 RETURNING`，Any 保持预检路径 | PG 真库探针新增 4 断言，22/22 通过 |
| EF 幂等 INSERT 优先 | 关系型 provider 先 INSERT，冲突再回读；InMemory 保持 SELECT 优先 | Integration 417 通过 / 14 跳过 |
| Dapper 幂等连接与载荷 | 显式开连接；精确数组响应载荷直接绑定 | Integration 全绿 |
| `EventData.FromOwned` | 所有权转移入口，去掉事件溯源写入的一次整载荷复制；原构造仍防御性复制 | EventLog 46/46，新增两件所有权测试 |
| 流运行计数与派发器 | `StreamRunRegistry` 原子计数；域事件派发器首次派发时构建处理器映射；子 Saga `GetMethod` 静态缓存 | Hosting 90/90、Messaging 33/33、DI 178/178 |
| 可选保留清理 | `RetentionOptions`、`IRetentionStore`、`RetentionProcessor`、`RetentionProcessorService`、`RetentionSql`、`PalOrmRetentionStore<TProvider>`、`EfCoreRetentionStore`、`AddPalRetention`、`AddPalEfCoreRetention`；Dapper/PalORM/EF 三栈接入；默认不注册 | 新增 SQLite 真库测试、清理索引计划测试与 EF 注册测试；Integration 420 通过 |

## 3 未实施与原因

| 项 | 原因 | 前置条件 | 验收 |
|---|---|---|---|
| 投影检查点单行高水位化 | 需改变检查点数据模型与 `GetAsync(position)` 语义合成，并迁移存量每事件行；当前框架没有在线投影泵，回放默认已批量 | 先落 `IHighWaterProjectionCheckpointStore` 能力接口与迁移脚本，保留旧接口回退 | 100 万事件回放行数 N→1，在线路径写次数 2/事件→1/事件，续读单行读取；崩溃重放窗口不扩大 |
| MySQL 二进制主键迁移 | `CHAR(26)` 主键在 InnoDB 每个二级索引中占 104 字节，收益大但涉及三栈模型、转换器与双写窗口 | 影子表量索引体积与写入延迟；准备双写与回滚开关 | 1 亿行 outbox 二级索引体积下降 ≥50%，写入 P99 不退化 |
| PalORM EventLog 读路径 raw reader | 会绕过 `readFromReplica: true` 的读副本路由，属功能回退 | PalORM 提供读路由下的 raw command 通道，或确认副本读在此路径非必需 | 100 万事件回放少 N 个行对象，读副本仍生效 |
| Dapper Inbox 冲突路径单语句化 | PG `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` 需重新证明超时抢占 CAS 语义，收益集中在接管路径 | 三方言探针覆盖新键/已处理/活跃租约/超时接管四形态 | 接管路径 3 次往返→1 至 2 次，四形态语义逐字保持 |
| Outbox 流水线闭包与 Task 分配 | 传输层通常占主导；仓库既有实测显示 SSE 框架可控段约 8%，本项预计低于 5% 线 | 先做 `--delivery` 分配分解，确认闭包占比 | 每消息分配下降且端到端吞吐提升 ≥5% |
| 压缩路径两次载荷复制 | 压缩组件未被持久化管线使用，属可选能力 | 消费方启用压缩且输出 >256KB | 压缩/解压各少一次整载荷复制 |
| Specification 表达式树缓存 | 低频路径，缓存会引入与参数化表达的语义耦合 | 出现高频规约构造热点 | 查询构造分配下降且语义不变 |
| MySQL 分片路由键分配 | 非热路径 | 出现路由热点 | 每次路由零中间数组 |

## 4 残余风险

- EF 栈 PG 新租约 SQL 已用 Npgsql EF 11 preview 6 在 PG 18.6 真库跑通四形态 8/8（含 `ExecutedStepKeys = ["Start","Step1"]` 往返）：首次租约 4 行、二次租约 0 行、到期过滤 2 行、租约后跟踪保存与完成态持久化。EF MySQL 已用 Microting.EntityFrameworkCore.MySql 11.0.0-preview.1 在 EF Core 11 rc2 + MySQL 8.4.11 真库跑通同样的 8/8（含 `ExecutedStepKeys` 往返）。该 provider 是 Pomelo 的社区 fork，编译于 EF Core 11 preview 7，rc2 烟测通过；接入 CI 需 test-only 依赖与 Testcontainers 车道。
- **Npgsql EF provider 兼容问题（已修复，ADR-035）**：`SagaState.ExecutedStepKeys` 由 `Collection<string>` 改为 `List<string>`，与 provider 物化类型对齐；去掉探针的忽略列后 PG 真库 8/8、MySQL 真库 8/8，SQLite EF 15/15。
- 保留清理默认不注册。启用前需按业务幂等窗口设定 `InboxRetention`，并确认 Dead Outbox 是否交由 `DeleteDeadOutboxMessages` 处理。
- M4 文本唯一索引尚未删除。必须先确认 `event_id_bin` 回填完整并把读路径切到二进制，否则重复事件 ID 检测失效。

## 5 验证

- 构建：全量 0 警告 0 错误；`dotnet format style --verify-no-changes` 通过。
- 测试：Integration 420 通过 / 14 跳过、EventLog 46、Transactions 234、DI 178、Messaging 33、Hosting 90、EF 15、Core 323、PalORM 117（54 跳过）、Serialization 54、CQRS 45、Analyzers 51、Compression 46、Projections.EventLog 9、Core.Abstractions 8、Messaging.Integration 10（9 跳过）。
- 门禁：`encoding-gate` 0 失败；`count-audit` 16 项 81 处声明一致；`verify-conventions --quick` 通过；`doc-consistency` 通过；`gate-audit --inventory` 43 脚本、25 接线、0 UNVERIFIED/UNWIRED/REVIEW；pre-commit 全绿。
- 真库：PG Dapper 探针 22/22（含新增多条守卫追加四断言）；EF PG 租约四形态 8/8（Npgsql EF 11 preview 6，含 `ExecutedStepKeys` 往返）；EF MySQL 全栈 8/8（Microting 11.0.0-preview.1 + EF Core 11 rc2，含该列往返）；Outbox 索引 1k 待租与 10 万待租的前后对照见 §1。

## 6 续轮修复（2026-10-20）

| 项 | 结果 |
|---|---|
| 清理索引 | 200k/100k 实测 PG 21.5→0.379ms（57×）、MySQL 318→0.349ms（911×）；PG/SQLite 部分/复合、MySQL 复合；DDL 与 EF 模型同步 |
| 索引整理 | 删除 `idx_outbox_created`，新增 `idx_inbox_processed`；`EXPLAIN` 无 Seq Scan/Sort |
| PG 原子批上限 | 限制 4,000 行；4,500 行真库探针 24/24 |
| EF SQLite 到期排序 | `ORDER BY next_check_at, saga_id`，新增逆序到期测试 |
| 三栈清理 | `RetentionSql` 共享 SQL/时间编码，ADO 执行下沉三个适配包；PalORM 三方言与 EF `AddPalEfCoreRetention` 接入；EF 注册测试 |
| 导入零拷贝 | `EventStreamJsonLines` 用 `EventData.FromOwned` |

未执行：投影检查点单行高水位化、M4 文本唯一索引收缩、MySQL 二进制主键迁移、EF MySQL Testcontainers 车道、Dapper Inbox 接管路径单语句、PalORM 读路径 raw reader、Outbox 流水线闭包（测量后再定）。
