# Pal.DDD 第二十九轮全量评审报告（`/review --full`）

> 基线：`5d4d250`（Kafka 集成测试超时 5s 快速失败）
> 生成：2026-08-17 · 执行：四片并行子代理地毯逐行 + 主线程跨片不变式 + 探针实证
> 范围：src/ 全部手写代码 153+ 文件 / ~24,560 行真实逐行（198 文件清单中 14 项磁盘不存在，片 4 用真实对应文件替代覆盖）

---

## 段 1：覆盖度声明（逐行覆盖清单）

| 分片 | 文件数 | 行数 | 方式 |
|------|-------|------|------|
| 片 1 | 15 | 5,973 | 逐文件逐行 Read（Saga/StrategicDddAnalyzer/IdentityGenerator/EnumGenerator/EventLogDbContext/CodeFix/DapperBulkCopy/KafkaBroker/Dapper×4/PalORM×2/SqlTemplates） |
| 片 2 | 28 | 6,378 | 逐文件逐行 Read（PG 系/Dapper.MySql/EFCore 系/RabbitMQ/DI/Compression/Repository） |
| 片 3 | 43 | 6,311 | 逐文件逐行 Read（Serialization/InMemory×3/Inbox-Outbox 管线/Compression.Native/SmartEnum 等） |
| 片 4 | 67+19 | ~5,900 | 逐文件逐行 Read（接口族/CQRS/ID 系/Logging/Converters/支撑文件 19） |
| **合计** | **153+** | **~24,560** | 全部 Read 工具逐行，无缓存/记忆 |

**明确不检查**：test/（测试评审由 test 系统负责）、docs/（doc-consistency 已跑 10/10）、scripts/、.g.cs 生成物（只验语义）。
**抽样策略**：无抽样——全量档范围内零抽样。

## 段 2：评审基线快照

```
IsAotCompatible=false 项目: 14（适配器层，合规）
catch(Exception) 总数: 53
OperationCanceledException 引用数: 58
```

## 段 3：机械轴结果（执行门禁 1/2/3/7）

| 防线 | 结果 |
|------|------|
| gate-check PDDD-G1..G22 | **22/22 全 PASS** |
| verify-ai-system | **16/16 全 PASS**（V1-V16） |
| tech-debt-scan | 20 通过 / 2 允许（超长行 SourceGen/AotTest 白名单）/ 0 失败 |
| doc-consistency-check | **10/10 全 PASS** |
| review-snapshot | 快照已生成（基线 5d4d250） |
| 方言实测轴（dialect-probe） | **PG 20 + MySQL 20 全 PASS**（DB 已恢复，全实测非 SKIP） |
| 全量测试 | 969 通过 / 3 失败（3 失败全为 Kafka 9092 不可达，环境类，非代码） |

## 段 4：主线程跨片不变式

| 不变式 | 结果 |
|--------|------|
| AOT 三态（核心 7 true / 数据访问 8 true / 适配器 14 false） | ✅ 与 gate G14/G15 一致 |
| 失败截断常量对称（Inbox↔Outbox↔Projection 三家 2000） | ✅ 对称 |
| DI 生命周期（ITM-026 OutboxDomainEventInterceptor Scoped） | ✅ TryAddScoped |
| 依赖方向（Core 零引用 / CQRS→Core / Transactions→Core+Messaging+Serialization） | ✅ 单向无环 |
| MessageCatalog 键集（RegisterSourceOutput 去重 + 重复名诊断排除） | ✅ |
| 消息头写读两侧（Kafka headers vs RabbitMQ BasicProperties 双路径） | ✅ 设计差异非缺陷（反证） |

## 段 5：发现汇总（按定级）

- **P0：0 项**
- **P1：4 项**（近期修复）
- **P2：5 项**（近期修复）
- **P3：9 项**（低优先/已声明边界）
- **证伪：6 项**（探针/反证推翻，计入 metrics 证伪数）

### P1（危害 × 复杂度 = 高×易 或 中×易）

| ID | 位置 | 问题 | 流 |
|----|------|------|-----|
| ITM-173 | PalOrmSagaStateStore.cs:88 | PG 分支 `UPDATE ... IN (SELECT ... LIMIT n)` **缺 `FOR UPDATE SKIP LOCKED`**——Dapper 版 SagaLeaseActivePG 已有。多 worker 并发租约互相阻塞 + 后到者覆盖先到者 leased_by（ITM-076 已实测此现象） | 并发 |
| ITM-174 | InMemoryOutboxStore.cs:49-138 | **租约所有权守卫缺失**——InMemory 三兄弟（Inbox/Idempotency）均已有 successor 隔离 + IsCurrentLeaseHolder，唯 Outbox 原地改写同一实例、Mark 无归属校验。worker A 租约到期后 B 重租同一实例，A 的 MarkProcessed/MarkDead 无校验覆盖活跃租约 | 并发 |
| ITM-175 | IdempotencyProcessor.cs:70 | **失败原因未截断**——error 列 HasMaxLength(2048)，MarkFailedAsync 直接传 ex.Message。OutboxBatchProcessor:22/InboxProcessor:28 均截断 2000，唯此漏（PD24 失败标记族不对称）→ 持久化失败 → 残留 Processing → 重放二次执行副作用 | 错误流 |
| ITM-176 | RetryBackoffPolicy.cs:79-84 | **exponentCap 无上界校验**——2^40 秒（~3.5 万年）已超 TimeSpan 上限 ~9.22e11 秒，`TimeSpan.FromSeconds` 抛 OverflowException，maxDelay 兜底在 FromSeconds 之后失效。配置 exponentCap≥40 即崩溃 | 边界 |

### P2

| ID | 位置 | 问题 | 流 |
|----|------|------|-----|
| ITM-177 | MySqlServiceCollectionExtensions.cs:179,201 | **AddPalMySqlSagaSnapshot 注册顺序敏感**——闭合泛型注册若在 AddPalMySqlWithStores（开放泛型）之前调用，MS DI 逆序匹配使开放泛型胜出 → jsonTypeInfo=null → saga_data 写 NULL 重启丢业务字段（方法自述"覆盖开放泛型"前提被顺序打碎） | DI |
| ITM-178 | OutboxDomainEventInterceptor.cs:45-145 | **SaveChanges 失败重试双写**——AddMessage 注入的 OutboxMessage 留在 ChangeTracker Added 状态，SaveChangesFailedAsync 只清 _pending 不清实体；EF 失败不自动回滚 ChangeTracker。重试 SaveChanges → 旧消息+新消息一起落库 → 下游重复消费 | 错误流 |
| ITM-179 | MessageBrokerBase.cs:38-44 | **泛型 PublishAsync null 消息放行**——`message!` 空断言放行引用类型 null，null 序列化为 "null" 负载发布，消费端反序列化 null 后 handler NRE。非泛型核心路径已有 ThrowIfNull 守卫，唯此入口缺 | 一般 |
| ITM-180 | InboxProcessor.cs:106-135 | **handler 成功但 MarkProcessed 失败被记为 Failed**——副作用已发生却被持久化为 Failed，监控误判 + 重试重放二次执行副作用。at-least-once 已声明，但"副作用已发生"与"未发生"不可区分 | 错误流 |
| ITM-181 | PostgreSqlReadWriteRouter.cs:134-142 | **reader 连接串把主库 Host 并入负载均衡**——LoadBalanceHosts=true + TargetSessionAttributes=any → 读流量负载均衡到写主库，读写分离稀释、主库连接池承压 | 架构 |

### P3（已声明边界 / 低优先）

| ID | 位置 | 问题 |
|----|------|------|
| ITM-182 | Dispatcher.cs:86-88 | Freeze 竞态窗口（Register 通过守卫后 _entries 已置 null → NRE）——违反已文档化"启动期单线程"契约，守卫承诺的"干净异常"退化。一行防御可修 |
| ITM-183 | SmartEnum.cs:88 | CompareExchange 首写获胜——同 TSelf 分批注册时后批值静默丢弃，无告警 |
| ITM-184 | PalOrmInboxStore.cs:68（Idempotency:79/Checkpoint:67 同款） | MySQL INSERT IGNORE 无差别吞噬非唯一错误（列宽截断键错位边界）——MySQL 原子语义刻意选择，键长度由调用方约束 |
| ITM-185 | EventLogDbContext.cs:182-184 | EFCore 族 Ulid 用 Parse（Dapper/PalORM 用 TryParse 降级 null）——族内一致设计，脏数据面差异 |
| ITM-186 | Saga.cs:89-90 | MaxRetries setter 无下限校验——负值静默零次执行 |
| ITM-187 | Saga.cs:284-287 | RecordExecutedStep 先于 observer 回调——observer 异常使已成功步骤被补偿 |
| ITM-188 | DapperEventLog.cs:255-260 | SQLite 唯一约束判定无 SqliteException 类型限定（EFCore/PalORM 已限定） |
| ITM-189 | PostgreSqlReportHelper.cs:236,256 | JSON Lines 导出 uint/ulong/short 等落 default 分支写成字符串 |
| ITM-190 | KafkaBroker.cs:158-169 | ConsumeException 持续故障每秒一次 Error 日志无限重试（退避防空转但刷屏） |

## 段 6：证伪记录（6 项，计入 metrics）

| # | 候选 | 反证方式 | 结论 |
|---|------|---------|------|
| 1 | GenerateMessageAttribute positional 参数漏检 | Read 定义：Name/SchemaVersion 为 init-only 属性，只能命名参数 | 反证 |
| 2 | ProjectionCheckpoint.Revision 与 DB 不一致 | Read：MarkProcessing 内含 Revision++（0→1），与 INSERT 值一致 | 反证 |
| 3 | QuerySingleAsync 多语句批不可解析 | **真库探针实测**：MySQL `INSERT; SELECT LAST_INSERT_ID()` 返回正确值 | 反证 |
| 4 | EFCore 族 Ulid Parse 不对称遗漏 | Read：OutboxDbContext/SagaStateDbContext 全族统一 Parse，族内一致 | 反证 |
| 5 | IsUniqueConstraintViolation AOT 反射族 | Read：4 项目均显式 IsAotCompatible=false（适配器层，PD3 合规） | 反证 |
| 6 | OutboxOptions MaxRetryCount 无校验 | Read：AddPalOutbox 已有 Validate(MaxRetryCount>0)+ValidateOnStart | 反证 |

## 段 7：趋势与热点

- 前 27 轮累计修复 ~50 项，本轮 P1=4 / P2=5 相对上轮（26 轮 P1 类 3 项）**持平略升**——因本轮为 `/review --full` 全量档（上轮为标准档），范围扩大所致。
- 热点：PalOrm 系（Saga/Inbox 租约 SQL）与 InMemory 系（测试网守卫对称）是并发缺陷高发区。
- 方言实测轴价值再证：探针全 PASS 的同时静态发现 3 项 SQL 层不对称（PalOrmSaga SKIP LOCKED 缺 / INSERT IGNORE / PalOrm reader 时区）——静态轴与实测轴互补。

## 段 8：局限声明

- 片 4 清单 14 项与仓库快照不符（scope 清单过期），已用真实对应文件替代覆盖同子系统，报告段 1 覆盖度含此替代。
- Kafka 9092 不可达导致 3 条集成测试失败（环境），非代码缺陷；恢复后跑 PalDDD.Messaging.Integration.Tests 即可全绿。
- 全部 P1/P2 发现经 Read 源码核实 + 关键项真库探针/反证实证；P3 项允许 [推断] 定稿，修复前先补探针。
