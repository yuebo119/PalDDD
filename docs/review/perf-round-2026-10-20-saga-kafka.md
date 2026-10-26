# 2026-10-20 Saga/Kafka 后台性能轮

## 1. 结论

第三轮独立审计的最高价值缺口不在数据面，而在后台写放大与消费线程模型：

- `SagaTimeoutProcessor` 在到期推进、NextCheckAt 回填、补偿终态三处逐 Saga `SaveChangesAsync`；EF 每次保存还遍历整个 ChangeTracker，批内行数叠加成 O(n²)，PalORM/Dapper 每行一次完整快照序列化。
- Kafka 消费循环的 `LongRunning` 只覆盖首个同步段；第一次 `await handler` 之后阻塞的 `Consume` 回到 ThreadPool。
- JsonLines 导入导出每行新建 writer/buffer 并做 UTF-8→UTF-16→UTF-8 转码。
- Dapper/PalORM 幂等未实现单调用能力接口，新 key 固定两次往返。
- EF SQL Server Saga 租约仍走跟踪分页与深偏移。

本轮五项落地，配套测试与公共 API 快照更新。

## 2. 落地项

### 2.1 Saga 后台批量写入

新增能力接口 `ISagaBatchStateStore<TState>`：

- `AdvanceNextCheckAtAsync(owner, sagas, ct)`：只写 `next_check_at + version`，以 `leased_by = owner` 与逐行 `version` 守卫。
- `SaveManyAsync(sagas, ct)`：批量保存完整状态，返回与入参同序的逐行结果。

四栈实现：

| 栈 | 到期推进 | 完整保存 |
|---|---|---|
| Dapper | 整批一个事务，逐行单语句 UPDATE，成功后内存 Version++ | 整批一个事务，复用 `SaveCoreAsync` |
| PalORM | 整批一个事务，raw command 挂接 ITM-243 环境事务 | 整批一个事务，逐行 `SaveChangesAsync` |
| EF | 整批一次 `SaveChangesAsync`，`BumpVersionOnModifiedSagaStates` 只扫描一次 | 同左，冲突整体按 0 返回 |
| InMemory | 锁内批量更新，owner/version 双守卫 | 锁内批量写入并自纠活跃索引 |

`SagaTimeoutProcessor` 改为收集到环后统一冲刷；未实现能力接口的自定义 store 回落逐条路径，语义与旧行为一致。补偿终态的逐行告警与补偿指标按批量结果回填。

### 2.2 Kafka 消费线程

专用线程只执行阻塞 `Consume`，结果写入无界 Channel；`SemaphoreSlim` 在专用线程上提供同步背压（最多 1 条处理中 + 1 条排队）。异步 worker 从 Channel 读取并执行 handler，关停时先取消、等待专用线程退出，再 `Close/Dispose` consumer，避免 Consume 与释放并发。

### 2.3 幂等单调用补齐

`DapperIdempotencyStore` 与 `PalOrmIdempotencyStore<TProvider>` 实现 `IIdempotencyStartOrGetStore`：Insert 优先，命中冲突后回读完整记录返回 Existing。新 key 与过期/失败回收路径省掉处理器侧前置 `GetAsync`。

### 2.4 JsonLines UTF-8 直读直写

导出改为单个池化 `Utf8JsonWriter` + 单块 `ArrayBufferWriter`，按 64KB 批量写流；导入按字节找换行，`Utf8JsonReader` 直接在 UTF-8 上解析，去掉 `StreamReader`、`StringBuilder`、`string` 与 `JsonDocument.Parse(string)` 的整行转码链。行缓冲复用，峰值仍为 O(单行)。

### 2.5 EF SQL Server Saga 租约

新增 SQL Server 原生路径：`WITH candidates AS (SELECT TOP (@n) ... WITH (UPDLOCK, READPAST, ROWLOCK)) UPDATE ... INNER JOIN candidates`，随后按 `(owner, until)` 等值回读。语句数与批次大小无关，替代原 `Skip/Take` 深偏移回落路径。SQL Server 标识符使用方括号引用。

## 3. 验证

- `dotnet run scripts/check-all.cs`：IDE 风格 0 项、CA 0 错误、编译 0 错误。
- 全量测试面板：1765 个测试，1688 通过、77 跳过、0 失败。
- 关键面板：Integration 437（423 通过 / 14 跳过）、Transactions 236、PalORM 172（118 通过 / 54 跳过）、Core 323、DI 178、Projections.EventLog 12、Messaging 33。
- 新增锁定测试：四栈 Saga 批量推进/批量终态保存与 owner/version 守卫、Dapper `StartOrGetAsync` 新键与已存在两态、JsonLines 大载荷跨 64KB 边界往返/CRLF 与空行容忍/坏行异常。
- 公共 API 快照：+14 行、0 删除（新增 `ISagaBatchStateStore<TState>` 与四栈公开方法）。

未执行：

- Kafka 真实 broker 的消费线程切换验证（本机无 Kafka 容器车道；现有 Messaging.Integration 中 Kafka 用例按环境跳过）。
- SQL Server 真库租约四形态（本机无 SQL Server 容器车道）。
- 10k 活跃 Saga 的批量保存提交数/p99 复测（需要真库车道；本轮只做逻辑与单库行为验证）。

## 4. 复核后不实施

| 项 | 结论 |
|---|---|
| 投影检查点单行高水位 | 回放路径已按 100 条一组提交，检查点为 2 次写/100 事件；在线路径仍是 2 次写/事件，单行化不会改变在线写次数。剩余收益是行数增长与续读 SQL 的索引规模，低于迁移成本（四栈 schema/API/续读语义与存量数据迁移），本轮登记不做。 |
| `DefaultSagaManager._invalidated` 只增不减 | TTL 会让失效后的迟到 `RegisterInterrupted` 重新落条目，幽灵条目防护出现时间窗，属正确性回退；维持单调集合，按进程生命周期有界性接受。 |
| RabbitMQ 每消息 linked CTS | 这是订阅取消与单消息取消的组合语义；去掉后关停期 handler 无法及时取消，属行为变更，保留。 |
| CQRS 非 Unit 值响应装箱 | 每次请求一次装箱，消除需把执行器从 `object?` 改为类型化形态，属 API 级改动；当前无测量证据表明它是热点，暂不做。 |
| Kafka 对象发布 `WrittenMemory.ToArray` | Confluent `Message.Value` 的 `byte[]` 契约要求的唯一一次拷贝；raw 路径已复用 Outbox 数组，无第二次拷贝。 |
| Inbox 接管 3 往返 | 仅重复投递/超时接管冷路径；高重投率场景再单语句化。 |

## 5. 残余风险与下一步

1. 在真实 Kafka broker 下验证专用线程 + Channel 的关停、背压与吞吐，补一条运行时探针。
2. 在 SQL Server 车道补 Saga 租约四形态（与 PG/MySQL 已有的形态矩阵对齐）。
3. 在真库上复测 10k 活跃 Saga、256 批的到期推进：记录每 tick 提交数与 p99，确认批量写入的净效果。
4. 投影检查点单行化作为独立迁移项保留，需先给出存量数据迁移与在线切换方案。
