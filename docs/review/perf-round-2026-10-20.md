# 极致性能轮（2026-10-20）

> 依据：2026-10-20 独立审计（237 个源文件全读）与用户确认的实施方案。
> 本文只登记本轮实际落地与明确未落地，不重复审计过程。

## 已落地

| 项 | 代码锚点 | 行为变化 | 验证 |
|---|---|---|---|
| 回放检查点默认批量 100 + 1 秒时间界 | `ProjectionProcessor.cs` 默认值；`ProjectionRebuilder.cs` `CheckpointFlushInterval` | `ProjectionRebuilder` 默认按 100 条或 1 秒提交；在线 `ProcessAsync` 不变；设 1 回退逐事件 | 100k 探针 200,000→2,000 次调用；新增时间界测试；Integration 414/14 跳过 |
| PalORM PG 单事件单往返追加 | `PalOrmEventLog.AppendSinglePostgreSqlAsync` | PG + 单事件走 `INSERT ... SELECT MAX+1 ... RETURNING`；四版本守卫、零行/唯一冲突分类保持 | `--palorm-append-pg` 在 wslc PG：versions=0..3、NoStream 拒绝、EventId 重复原样上抛、读回 5 |
| 幂等单调用（InMemory/EF） | `IIdempotencyStartOrGetStore`、`IdempotencyProcessor`、`InMemoryIdempotencyStore`、`IdempotencyDbContext` | 能力 store 一次返回 Started/Existing/Contended，处理器不再先 Get 再 TryStart；旧 store 自动回退 | IdempotencyTests 17、IdempotencyEfCoreTests 10；新增 Existing 完成态测试 |
| RabbitMQ 消费分发并发选项 | `RabbitMqBroker.ConsumerDispatchConcurrency` | 默认 null（连接默认 1）；设置后每订阅 handler 可并发；共享 channel 模式不生效 | 新增集成测试（两 handler 同时在途）在 wslc rabbit436 通过；Rabbit 18/18 |
| Dapper 读取按 ID 模式裁剪 | `DapperEventLog.ReadRecordedEvent` | 二进制模式回填行跳过文本 ID 解码；文本模式跳过二进制列；NULL 回退文本 | DapperStoreTests 56/56 |
| Saga 到期排序与推进 | `SqlTemplates.cs`、`PalOrmSagaStateStore.cs`、`SagaStateDbContext.cs`、`SagaProcessor.cs` | 到期租约按 `next_check_at, created_at` 排序；到期未超时行重算并推进 `NextCheckAt`；`EnableDueTimeFilter` 默认仍 false | Transactions 234/234；M1 10k 量具已过（p99 2.0s / 阈值 4s） |

## 公共 API 增量

- `IIdempotencyStartOrGetStore`、`IdempotencyStartOutcome`、`IdempotencyStartResult`。
- `RabbitMqBroker.ConsumerDispatchConcurrency`。
- `ProjectionRebuilder.CheckpointFlushInterval`、`ProjectionRebuilder.TimeProvider`。
- 公共 API 快照已更新（+27 行，零删除）。

## 明确未落地与原因

1. **EF PG 单事件单往返追加**：仓库没有 EF Npgsql provider 的测试面（EF Core 11 rc2
   与缓存中的 Npgsql EF 10.0.3 版本线不匹配）。未验证的方言 SQL 不进入主仓；PalORM
   与 Dapper 已覆盖该路径。需要单独的 provider 测试项目后再启用。
2. **Dapper/PalORM 幂等 `StartOrGetAsync`**：两栈仍走旧路径（Dapper 新键 2 往返、
   PalORM 冲突路径更多）；能力接口已就位，待各自状态矩阵测试后接。
3. **EF PG/MySQL Saga 单语句租约**：同样受 EF Npgsql provider 测试面限制；本轮只做
   排序与推进，默认到期过滤未开启。
4. **Outbox 租约索引**：需要 10 万积压的 `EXPLAIN (ANALYZE, BUFFERS)` 证据；没有生产
   积压画像前不盲加部分索引。
5. **PalORM 每 scope 同步建连**：需先测连接池等待占请求 p99 的比例；未达 5% 不做
   破坏面较大的懒打开改造。
6. **Kafka 发布管道化**：Outbox 已有 16 路窗口；直调 `PublishAsync` 的吞吐瓶颈未实测，
   不做猜测性重写。
7. **SSE 自适应 flush**：token 流必须逐项 flush 保 TTFT；只有回放/突发产出场景值得，
   本轮不改默认。
8. **SQLite PRAGMA 每 scope 重放、EventLog 位置块 100→1000**：前者需 scope 密集场景
   测量，后者有崩溃位置间隙扩大代价；均保持现状。

## 回滚

- 回放：`processor.CheckpointBatchSize = 1`；时间界不影响在线路径。
- Rabbit：不设置 `ConsumerDispatchConcurrency`（默认 1）。
- Saga：`EnableDueTimeFilter` 保持 false 即回旧全量扫描。
- EventLog 读取：`UseBinaryEventId = false` 回文本列。
- 幂等：未实现能力接口的自定义 store 自动走旧两段路径。

## 验证面板

Transactions 234 · Integration 415（14 跳过）· Dapper 56 · Idempotency 17 ·
IdempotencyEfCore 10 · Messaging 19（17 通过，2 个 Kafka 环境用例因 localhost IPv6
解析失败，非代码变更）· 全量 Release 0 警告 0 错误；公共 API 快照已更新。
