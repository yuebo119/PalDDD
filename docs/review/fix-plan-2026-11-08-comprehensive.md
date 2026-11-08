# 2026-11-08 综合审计与彻底根治方案

> 来源：四份独立审计报告的综合。A 本对话审计（F1/F2 后）；B 贴入报告一（v4 完成后、F1/F2 前）；C 贴入报告二（F1/F2 后复查）；D 贴入报告三（F1/F2 后，含 CI 证据）。
> 版本窗口：`v5.0.0-preview.1` 之后，dev 领先 `origin/dev` 2 提交（`0d7884d`/`98f4859`），未推送。
> 基线（四报告实测一致）：build 0 警告 0 错误；测试 0 失败，跳过项集中在真容器与方言面。
> 本文含「做什么、为什么、怎么验收」与可执行任务清单，不复述审计过程。

---

## 一、四报告综合与交叉验证

### 1.1 综合方法

四份报告的时间基线不同，互补而非重复：

| 报告 | 时间基线 | 覆盖维度 | 发现规模 |
|---|---|---|---|
| A | F1/F2 修复后 | Messaging 批处理、三栈、CQRS/AspNetCore | P1×1 + P2×7 + P3×17 |
| B | v4 完成后、F1/F2 前 | Kafka 深度、安全、契约 | P1×2 + P2×5 + P3×8 |
| C | F1/F2 修复后 | 新眼复查、数据面、文档 | P1×0 + P2×2 + P3×14 |
| D | F1/F2 修复后，含 CI 证据 | 门禁可信度、CI、端点安全 | P1×5 + P2×多条 + P3 |

方法：去重、交叉验证、源码逐行复验、重新定级。凡 P1 与冲突发现，均由主代理读源码确认，不采信单一报告。

### 1.2 交叉验证矩阵（多份独立确认 = 高置信）

| 发现 | A | B | C | D | 综合定级 |
|---|---|---|---|---|---|
| Rabbit OCE catch 缺 `IsSettled` | P1 | | P2 | | P1（三份确认） |
| Kafka rebalance 无回调 | | P1 | | P1 | P1（两份确认，已核实无接线） |
| Kafka manualStore 运行中死锁 | | P1 | | | P1（机制成立，F1 未覆盖） |
| TimeoutBehavior 吞 TimeoutException | P2 | | | P2 | P1（两份确认，已核实） |
| MessageBatchPump 通道内 item 不结算 | P3 | P3 | | P2 | P2（三份确认） |
| F2 不完整（try 范围） | | P2 | | | P2（已核实） |
| OutboxBatchProcessor 关停窗口 | | P2 | P3 | | P2（已核实） |
| InMemory Outbox 契约违背 | | | P2 | | P2（已核实） |
| DomainEventOutboxBridge 事件丢失 | | P2 | | | P2（已核实） |
| new-skip-guard 漏 `Skip.Test` | | | | P1 | P1（已核实） |
| CI Skip 披露恒 0 | | | | P1 | P1（已核实） |
| 真库测试静默早退 | | | | P1 | P1（已核实） |
| state.md 状态漂移 | | | | P1 | P1（已核实） |

### 1.3 关键裁决

**裁决 1：B 的 Kafka 死锁与 D 的「F1 已修死锁」是两个不同的死锁，B 成立。**

D 说的 F1 修复，修的是关停时批量 ack 命令被丢弃（`drainerStopped` 信号，`KafkaBroker.cs:540/612/1035`）。B 说的死锁是运行中：`maxConcurrent=1`（默认）时 worker 逐条 `await handlerTask`（`:718-719`），而 `slots.Release()` 在 `ReadAsync` 之后（`:714`），不在 handler 之后。slot 被「已 TryWrite 未被 worker ReadAsync」的消息占用，消费线程取下一条时阻塞在 `slots.Wait`（`:585`，无超时），无法执行循环顶部的 `DrainAckCommands`（`:548`）。handler 若等待 ack（`EnqueueAsync` → `await completion.Task`，`:1044`），而 ack 命令只能由消费线程排空，形成循环等待。

触发条件：`maxConcurrent=1` + handler 结算慢（ack 入队晚于消费线程第 3 次 Wait 阻塞）+ 积压 ≥2 条在 channel。B 说「即使 handler 不写一行 ack 代码也会死锁」，机制上成立：finally 兜底（`:897`）的 `RejectAsync` 同样入队命令、同样等消费线程。

**裁决 2：D 的 P1-4（rebalance）与 B 的 P1-2 是同一问题，合并。** 已核实 `KafkaBroker.cs:426-431` 只挂 Error/Statistics/Log，无 `SetPartitionsAssigned/Revoked/Lost`。`OffsetTracker.Ack`（`:1098`）在 `offset < partition.Next` 时早退，rebalance 后新起点前移则 `Done.Remove(Next)` 恒失败，位点永久卡住；`Done`（`SortedSet`）只增不减，内存按吞吐线性增长。

**裁决 3：D 的 P2-1（Kafka finally 缺 `enqueueConfirmed`）成立。** `MessageBatchPump.EnqueueAsync`（`:92-97`）在 `WriteAsync` 之前 `MarkDeferred`；`WriteAsync` 失败时 ack 未入队但 `IsDeferred` 已置位，Kafka finally（`:897`）因 `!IsDeferred` 为假而跳过，泵 finally（`:239-258`）只结算 `pendingRead` 不结算通道内 item，ack 悬空。Rabbit 侧 `:635` 的 `!IsDeferred || !enqueueConfirmed` 判据正是为此设计，Kafka 侧缺等价物。

**裁决 4：B 的 P1-1 与 A/C 的 Rabbit OCE 是独立缺陷，都成立。** 前者是消费线程与 worker 的 slot 循环等待，后者是 OCE catch 绕过 `TryClaim` 的二次 Nack。

---

## 二、重新定级清单

### 2.1 P1（7 个，立即处理）

| # | 发现 | 位置 | 影响 | 来源 |
|---|---|---|---|---|
| 1 | Kafka `manualStore` 运行中死锁 | `KafkaBroker.cs:585/714/718/897/1044` | 默认配置下生产停摆，健康检查恒绿 | B（已复验） |
| 2 | Kafka rebalance 无回调，水印停滞 + 内存增长 | `KafkaBroker.cs:426-431/1098/1104` | 位点永久不推进，重启全量重放，内存无界 | B+D（已复验） |
| 3 | Rabbit OCE catch 缺 `IsSettled`，二次 Nack 关 channel | `RabbitMqBroker.cs:604/620` vs `:635` | 共享 channel 时全部订阅与发布瘫痪 | A+C（已复验） |
| 4 | `new-skip-guard` 漏 `Skip.Test(` 形态 | `scripts/new-skip-guard.cs:196` | 门禁假绿：以 `Skip.Test` 修绿可无限新增，test/ 已有 20 处 | D（已复验） |
| 5 | CI Skip 披露恒为 0 | `ci.yml:137` | grep 英文格式匹配不到中文「已跳过: N」，面板失真 | D（已复验） |
| 6 | 3 个真库测试静默早退、零断言 | `EfCoreRetentionRealProviderTests.cs:24-29` 等 | 计入「通过」但什么都没测，不进 skipped 计数 | D（已复验） |
| 7 | `.ai/state.md` 状态漂移 | `.ai/state.md:4-5` | 声称「12+5 提交待授权」实测 2；会话恢复读到错误前提 | D（已复验） |

### 2.2 P2（约 20 个，按主题）

**数据正确性**：TimeoutBehavior 吞 handler 自抛 TimeoutException（A+D）；Kafka finally 缺 `enqueueConfirmed` 等价判据（D）；MessageBatchPump 通道内 item 不结算（A+B+D）；OutboxBatchProcessor 关停窗口异常逃逸（B+C）；InMemory Outbox `GetByStatusAsync(Processed)` 契约违背 + 测试缺口（C）；DomainEventOutboxBridge 在 `CommitAsync` 前清空事件（B）；Rabbit `RejectAsync(requeue:true)` 无退避无 DLX（B，需裁决补退避还是改文档）。

**观测与契约**：Rabbit 反序列化 null 漏计 `BrokerConsumeFailed`（A）；Rabbit 发布失败 Activity 无 `SetStatus`（A）；Saga `CompensationFailed` 终态无 `OnStatusChanged`（A）；`IRetentionStore` DIM fail-visible 分支零测试（A）；批量 ack DIM 吞错误回调（A）；`StreamRunRegistry.Register` 硬编码 `owner:null` + `Cancel(runId)` 无校验（A+B）。

**门禁与文档**：F2 不完整，`EnsureExchangeAsync`/`Serialize` 在 try 外（B）；CHANGELOG `[Unreleased]` 顺序违反 §11.2（D）；README 测试数字 1502 项过时（A+D）；`docs/aot.md:175` 与 README/ADR-020 矛盾（C）。

**端点安全**：ExceptionMiddleware 把 `BadHttpRequestException.Message` 入响应（C+D）；SSE `eventSelector`/`idSelector` 未消毒 CR/LF（A+D）；HealthCheck 回传异常 Description（D）；`PalDDD.Extension.csproj` 引用非 AOT 的 Transactions（D）；`RunOwnerSelector` 默认 null 使所有权恒真（A+B）。

### 2.3 P3（约 25 个，择要）

`ConcurrentDictionary` 应改 `ConditionalWeakTable`（C）；MySQL 键长度无入口校验致 1406（B）；`PostgreSqlAuditor.EscapeLiteral` 只翻倍单引号（B+C）；`FanOutStep` 信号量泄漏（B）；EF `OutboxDbContext` Detach 不对称（B）；`MessageBatchPump` 遗留 `Task.Delay` 计时器（B）；`RetryExecutor` 用户委托异常无降级（A）；PalORM Outbox 读副本语义漂移（A）；`docs/sql` saga_states 注释仍写 `4:DeadLettered`（A）；Dapper `MarkFailedAsync` 死回退（A+C）；Rabbit 健康态粘滞（A）；断线续跑无按主体配额（A）；豁免环境变量无机械校验（D）；退避计数不按分区分桶（D）。

---

## 三、彻底根治方案

### 3.1 P1-1 Kafka 运行中死锁

#### 根因（三层）

1. **直接层**：`slots.Wait`（`:585`）无超时，消费线程可无限阻塞。
2. **结构层**：`slots.Release()` 在 `ReadAsync` 之后（`:714`），而 `maxConcurrent=1` 时 worker 逐条 `await handlerTask`（`:718-719`），worker 的 `ReadAsync` 被 handler 完成阻塞，slot 被「已 TryWrite 未被 ReadAsync」的消息长期占用。
3. **架构层**：ack 命令的应用（`DrainAckCommands`，消费线程）与消息排空（`slots.Wait` + worker `ReadAsync`）在同一线程串行，而 handler 在 worker 线程等待 ack 完成。三者形成循环等待：handler 等消费线程排空，消费线程等 worker ReadAsync，worker 等 handler 完成。

**关键约束**：`IConsumer` 非线程安全，ack 命令必须由消费线程应用，这是 Confluent.Kafka 的硬约束。因此不能简单把 ack 应用移到 worker 线程。

#### 方案对比

| 方案 | 做法 | 消除根因 | 代价 | 结论 |
|---|---|---|---|---|
| A 超时轮询 | `slots.Wait` 改 `WaitAsync` 带超时，超时回顶部排空 | 否，只缩短死锁窗口 | 100ms ack 延迟，判据仍分散 | 创可贴，不彻底 |
| B 有界 channel 替代 slots | 去掉 `slots`，channel 改有界，消费线程 `TryWrite` 非阻塞 | 是，消费线程永不阻塞 | 消费线程改异步或加轮询 | 最优 |
| C 独立 ack 线程 | ack 应用移到专用线程 | 是，彻底解耦 | 依赖 `IConsumer` 跨线程行为（需查证），新增线程 | 查证成本高，暂不选 |

#### 最优方案（B）：有界 channel + 非阻塞消费

**核心论证**：死锁的必要条件是「消费线程阻塞在某处，导致 ack 无法排空」。方案 B 让消费线程的 channel 写入永不阻塞（`TryWrite` 失败即回顶部排空），从根上切断循环等待。channel 容量承载原 `slots` 的并发限流语义，worker 侧 `inFlight` 列表保留 `maxConcurrent` 的并发控制，两者不重叠。

**代码骨架**（`KafkaBroker.cs` 消费线程段）：

```csharp
// 1. channel 创建改有界（容量 = maxConcurrent，与原 slots 语义一致）
var channel = Channel.CreateBounded<ConsumeResult<string, byte[]>>(
    new BoundedChannelOptions(maxConcurrent)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });

// 2. 消费线程改异步，去掉 slots
var consumerTask = Task.Run(async () =>
{
    ConsumeResult<string, byte[]>? pending = null;
    while (!tokenSnapshot.IsCancellationRequested)
    {
        DrainAckCommands();          // 每轮排空，不再被 slot 阻塞
        WaitSeekBackoff();

        if (pending is null)
        {
            ConsumeResult<string, byte[]>? consumed;
            try
            {
                consumed = consumer.Consume(TimeSpan.FromMilliseconds(PollIntervalMilliseconds));
            }
            catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested) { break; }
            catch (ConsumeException ex) { /* 记录 + 退避 */ continue; }
            if (consumed is null) continue;
            if (manualStore && consumed.Message is not null && consumed.Message.Value is not null)
                offsetTracker.Consumed(consumed.TopicPartition, consumed.Offset.Value);
            pending = consumed;
        }

        if (!channel.Writer.TryWrite(pending))
        {
            // channel 满：等待可写或超时，超时回顶部排空 ack（关键：不阻塞死锁）
            await channel.Writer.WaitToWriteAsync(
                TimeSpan.FromMilliseconds(PollIntervalMilliseconds), tokenSnapshot)
                .ConfigureAwait(false);
            continue;
        }
        pending = null;                // 写入成功，清空待重试
    }
});

// 3. worker 去掉 slots.Release()
while (!tokenSnapshot.IsCancellationRequested)
{
    var result = await channel.Reader.ReadAsync(tokenSnapshot).ConfigureAwait(false);
    var handlerTask = HandleMessageAsync(result);
    if (maxConcurrent == 1) { await handlerTask.ConfigureAwait(false); continue; }
    // inFlight 并发控制保持不变
}
```

**为什么这样彻底**：

- 消费线程的唯一阻塞点是 `WaitToWriteAsync`（带 100ms 超时），超时即回顶部 `DrainAckCommands`。ack 应用周期执行，handler 的等待最多 100ms 后被打通。
- `pending` 保留未写入的消息，不丢失。
- channel 容量 = `maxConcurrent`，与原 `slots` 容量一致，并发语义不变。

**取舍**：`maxConcurrent=1` 时，channel 容量 1，worker 逐条处理，消费线程会在 channel 满时等待。这与原 `slots` 行为一致，但原实现会死锁，新实现会周期排空。吞吐影响：ack 应用延迟最多 100ms（仅 manualStore 模式，且仅 channel 满时），对 at-least-once 语义无影响。

#### 锁定测试

```
真容器（Testcontainers Kafka）：
- 用例：maxConcurrent=1 + handler 内 await Task.Delay(500) + 投递 5 条消息
- 断言：5 条全部消费完成，无死锁（原实现会卡死）
- 变异验证：把 DrainAckCommands 从循环顶部移除，测试转红
```

#### 风险

- `WaitToWriteAsync` 异步化消费线程后，`consumer.Consume` 的同步调用仍在异步线程执行，无影响。
- `drainerStopped` 机制（F1 修复）保持不变，关停路径不受影响。
- 需验证 `BoundedChannelFullMode.Wait` 与 `WaitToWriteAsync` 的交互（`Wait` 模式下 `TryWrite` 失败后可 `WaitToWriteAsync`）。

### 3.2 P1-2 Kafka rebalance 无回调，水印停滞 + 内存增长

#### 根因

`OffsetTracker` 是进程内 `Dictionary<TopicPartition, Partition>`，keyed by 分区。rebalance 时分区被撤销/重新分配，但 `OffsetTracker` 不感知：

1. `KafkaBroker.cs:426-431` 只挂 Error/Statistics/Log，无 `SetPartitionsRevoked/Assigned/Lost`。
2. `OffsetTracker.Ack`（`:1098`）在 `offset < partition.Next` 时早退。rebalance 后新分配的分区起点 Y > 本进程旧 `Next=X`，`Done.Remove(X)` 恒失败，`advanced=false`，返回 null，`StoreOffset`/`Commit` 永不执行，位点永久卡在 X。
3. `Done`（`SortedSet<long>`）只增不减，按吞吐线性增长。

**对称缺口**：回退型重平衡（新起点 < Next）能自愈（`Done` 里的小 offset 被移除），前进型不能。这证明是遗漏而非设计。

#### 最优方案：补全分区生命周期回调 + 状态重置

**核心论证**：`OffsetTracker` 的状态必须与分区的所有权同步。分区被撤销时，其水印状态必须作废；重新分配时，必须以新起点初始化。这是「状态与所有权对齐」的根治，而非「在 Ack 里加特殊判断」的补丁。

**代码骨架**：

```csharp
// 1. ConsumerBuilder 补回调
.SetPartitionsAssignedHandler((_, partitions) =>
{
    foreach (var p in partitions)
        offsetTracker.Reset(p);          // 重新分配：作废旧状态，下次 Consumed 重新初始化
})
.SetPartitionsRevokedHandler((_, partitions) =>
{
    foreach (var p in partitions)
    {
        offsetTracker.Reset(p);          // 撤销：作废状态
        ackCommands.RemoveAll(c => c.Offset.TopicPartition == p);  // 清理未结算命令
    }
})
.SetPartitionsLostHandler((_, partitions) =>
{
    foreach (var p in partitions)
    {
        offsetTracker.Reset(p);
        ackCommands.RemoveAll(c => c.Offset.TopicPartition == p);
    }
});
```

**关键设计点**：

- `Reset` 是 O(1)（`Dictionary.Remove`），满足 librdkafka「回调快速返回」要求。
- `ackCommands.RemoveAll` 清理未结算命令。这些消息会重投（at-least-once），不丢失。
- 回调在消费线程触发（Confluent.Kafka 在 `Consume` 时调用），与 `DrainAckCommands` 串行，无并发问题。

**替代方案对比**：

| 方案 | 做法 | 问题 |
|---|---|---|
| 在 Ack 里加「offset 跳跃」判断 | 检测到 `offset > Next + 阈值` 时重置 | 启发式，阈值难定，前进型与乱序确认混淆 |
| 每次 Consume 都重置 | 简单 | 丢失已确认的前缀状态，破坏 at-least-once |
| 生命周期回调 | 与分区所有权同步 | 最优，事件驱动，无启发式 |

#### 锁定测试

```
真容器（Testcontainers Kafka，两个消费者同组）：
- 用例 A：消费者 1 处理分区 P，提交位点 X；触发 rebalance，P 分配给消费者 2；
  消费者 2 从 Y > X 继续，断言位点推进到 Y 之后
- 用例 B：断言 rebalance 后 OffsetTracker 的 Done 集合清空（内存不增长）
- 变异验证：移除 Reset 调用，用例 A 卡在位点 X
```

#### 风险

- rebalance 回调的线程模型需确认（Confluent.Kafka 文档：回调在调用 `Consume` 的线程触发，即消费线程，安全）。
- `ackCommands.RemoveAll` 需要 `ConcurrentQueue` 的清理方法（.NET 无内置 `RemoveAll`，需遍历重建或改用 `Channel`）。这是一个实现细节，可改用「标记跳过」：`DrainAckCommands` 检查分区是否仍在 `offsetTracker` 中，不在则跳过。

### 3.3 P1-3 Rabbit OCE catch 缺 `IsSettled`，二次 Nack 关 channel

#### 根因

`RabbitMqMessageAck.TryClaim()`（原子占位）只覆盖 `AcknowledgeAsync`/`RejectAsync` 方法。catch 分支直接调 `TryNackSafeAsync`，绕过 `TryClaim`。三个 catch 的结算判据不一致：

- 关停 OCE（`:604`）：`!IsDeferred || !enqueueConfirmed`，缺 `!ack.IsSettled`
- 非关停 OCE（`:620`）：同上，缺 `!ack.IsSettled`
- 通用 catch（`:635`）：`(!IsDeferred || !enqueueConfirmed) && !ack.IsSettled`，有

`RabbitMqMessageAck.IsSettled` 是 `TryClaim` 的 `_settled` 字段，handler 显式 `AcknowledgeAsync` 后置位。OCE catch 漏判导致对同一 deliveryTag 二次 `BasicNack`，触发 `PRECONDITION_FAILED` 关 channel。

#### 最优方案：结算门控单一来源

**核心论证**：判据分散在三个 catch 是根因（B2 修复轮只给通用 catch 补了 `IsSettled`）。根治方式是把结算门控抽成单一方法，所有 catch 分支调用，未来新增 catch 不会漏。

**代码骨架**：

```csharp
// 局部函数：broker 兜底结算的唯一判据来源
bool ShouldFallbackSettle(IMessageAck ack, bool enqueueConfirmed)
    => ack is not null
       && !ack.IsSettled
       && (!DeferredAckRegistry.IsDeferred(ack) || !enqueueConfirmed);

// 三个 catch 统一调用
// 关停 OCE（:604）
if (ShouldFallbackSettle(ack, enqueueConfirmed))
    await TryNackSafeAsync(consumerChannel, ea.DeliveryTag, requeue: false, queueName);

// 非关停 OCE（:620）
if (ShouldFallbackSettle(ack, enqueueConfirmed))
    await TryNackSafeAsync(consumerChannel, ea.DeliveryTag, requeue: false, queueName);

// 通用 catch（:635）
if (ShouldFallbackSettle(ack, enqueueConfirmed))
    await TryNackSafeAsync(consumerChannel, ea.DeliveryTag, requeue: false, queueName);
```

**更深层加固**：把 `!ack.IsSettled` 检查下沉到 `TryNackSafeAsync` 内部（或新建 `TrySettleFallbackAsync`），这样即使未来有调用点忘记判据，也不会二次结算。这是「防御性设计」，与判据单一来源双保险。

**替代方案对比**：

| 方案 | 做法 | 彻底性 |
|---|---|---|
| 两 catch 各补 `&& !ack.IsSettled` | 最小改动 | 判据仍分散，未来新增 catch 可能再漏 |
| 抽公共判据方法 | 单一来源 | 彻底，结构上消除遗漏可能 |
| 下沉到 `TryNackSafeAsync` | 双保险 | 更彻底，但改动面稍大 |

**最优**：抽公共方法 + 下沉检查（双保险）。

#### 锁定测试

```
真容器（Testcontainers RabbitMQ）：
- 用例：handler 内 await ack.AcknowledgeAsync() 后抛 OperationCanceledException
- 断言：channel 存活（无 PRECONDITION_FAILED），无二次 Nack
- 变异验证：移除 ShouldFallbackSettle 的 IsSettled 判据，channel 关闭，测试转红
```

#### 风险

- `TryNackSafeAsync` 下沉 `IsSettled` 检查后，需确认不影响批量路径（批量路径的 `IsDeferred` 判据已在 `ShouldFallbackSettle` 中）。
- 单测环境无真容器时，可用 mock channel 断言 `BasicNack` 调用次数。

### 3.4 批次 2：门禁假绿（4 项，根因 + 根治）

#### P1-4 `new-skip-guard` 漏 `Skip.Test(`

**根因**：正则 `[Skip\b|Skip\s*=\s*["']|\.Skip\s*\(\s*["']|\bSkip\.If\s*\(` 四形态不含 `\bSkip\.Test\s*\(`。test/ 已有 20 处 `Skip.Test(`，全部绕过门禁。

**根治**：正则补第五形态 `\bSkip\.Test\s*\(`，selftest 加正例（`Skip.Test("...")` 应命中）与负例（`Skip.If(...)` 不误伤）。更深层：TUnit 的 Skip 形态应全数覆盖（`Skip.If`/`Skip.Test`/`Skip.When` 等），可考虑从 TUnit 文档枚举形态，避免再次遗漏。

#### P1-5 CI Skip 披露恒为 0

**根因**：`ci.yml:137` 用 `grep -oE 'Skipped: [0-9]+'`（英文格式），而 TUnit/MTP 中文日志输出「已跳过: N」。grep 恒不命中，`skipped=0`。

**根治**：

1. 改 grep 为 `已跳过: [0-9]+`（中文格式）。
2. 加空转断言：若测试项目全部结束但 `skipped=0` 且已知有环境性跳过，则显式报错（防再次静默）。
3. 更深层：CI 日志解析依赖本地化格式，脆弱。应改用 MTP 的结构化输出（trx/junit XML）解析 skipped 数，与语言无关。

**取舍**：中文格式 grep 是止血，结构化输出是根治。建议先改中文格式（一行），排期结构化输出。

#### P1-6 真库测试静默早退、零断言

**根因**：`EfCoreRetentionRealProviderTests.cs:24-29` 等用 `Console.WriteLine("[skip]..."); return;` 代替 Skip 标注，注释明说「不写 Skip 标注（new-skip-guard 口径）」，即为规避 new-skip-guard 拦截而用早退。这导致测试计入「通过」但零断言，不进 skipped 计数。

**根治**：

1. 改用 `Skip.Test("...")` 标注（纳入 skipped 计数，面板可见）。
2. new-skip-guard 对环境性真库测试（`PALDDD_TEST_*` 前缀）加豁免，允许用 `Skip.Test`。
3. 更深层：真库测试的「未执行」状态必须在面板可见（skipped 或专用计数），不能计入通过。

**取舍**：改 `Skip.Test` 后 skipped 数上升（104 → 更多），但这是口径修正，不是退化。面板数字会变化，需同步更新 README 基线数字。

#### P1-7 `.ai/state.md` 状态漂移

**根因**：手工维护，声称「主仓 12 + .ai 5 提交待授权」「未推送 4 个（f3e0d0a…）」，实测 `origin/dev..HEAD`=2、`f3e0d0a` 已在 origin/dev、`.ai` 无 remote。会话恢复会读到错误前提。

**根治**：

1. 用脚本从 git 生成 state.md 的提交数/未决项（`git rev-list --count origin/dev..HEAD`）。
2. 加 CI 校验：state.md 的「未推送提交数」与实际 git 状态一致，失实即红。
3. 更深层：state.md 的「未决」应由 `.ai/` 账本（action-items）自动生成，而非手工。

---

## 四、任务清单

### 4.1 批次 1：P1 数据正确性（最高优先）

| 编号 | 发现 | 位置 | 修法 | 锁定测试 | 验收 | 依赖 |
|---|---|---|---|---|---|---|
| FIX-001 | Kafka 运行中死锁 | `KafkaBroker.cs:585/714/718/897/1044` | 有界 channel 替代 slots，消费线程 `TryWrite` + `WaitToWriteAsync` 超时排空，去掉 `slots.Wait/Release` | 真容器：maxConcurrent=1 + 慢 handler + 积压 5 条，断言全部消费完成；变异验证移除 `DrainAckCommands` 转红 | build 0 警告；真容器用例通过；变异转红 | 无 |
| FIX-002 | Kafka rebalance 无回调 | `KafkaBroker.cs:426-431/1098/1104` | ConsumerBuilder 补 `SetPartitionsAssigned/Revoked/Lost`，回调中 `offsetTracker.Reset` + 清理未结算 ack | 真容器：双消费者 rebalance，断言位点推进、`Done` 清空；变异验证移除 `Reset` 卡位点 | build 0 警告；真容器用例通过；变异转红 | 无 |
| FIX-003 | Rabbit OCE 二次 Nack | `RabbitMqBroker.cs:604/620/635` | 抽 `ShouldFallbackSettle` 单一判据，三 catch 共用；下沉 `IsSettled` 到 `TryNackSafeAsync` | 真容器：handler 结算后抛 OCE，断言 channel 存活、无二次 Nack；变异验证移除判据转红 | build 0 警告；真容器用例通过；变异转红 | 无 |

### 4.2 批次 2：P1 门禁可信度（高优先，本地可验）

| 编号 | 发现 | 位置 | 修法 | 锁定测试 | 验收 | 依赖 |
|---|---|---|---|---|---|---|
| FIX-004 | new-skip-guard 漏 `Skip.Test` | `scripts/new-skip-guard.cs:196` | 正则补 `\bSkip\.Test\s*\(`，selftest 加正负例 | `--selftest` 全过；新增 `Skip.Test` 用例被拦截 | `dotnet run scripts/new-skip-guard.cs -- --selftest` 退出 0；变异验证翻转判定转红 | 无 |
| FIX-005 | CI Skip 披露恒 0 | `ci.yml:137` | grep 改 `已跳过: [0-9]+`；加空转断言 | CI run 后 `skipped>0`；空转断言在无命中时报错 | CI run 输出 skipped 数与实际一致 | 无 |
| FIX-006 | 真库测试静默早退 | `EfCoreRetentionRealProviderTests.cs:24-29` 等 3 处 | 改 `Skip.Test` 标注；new-skip-guard 对 `PALDDD_TEST_*` 环境性测试加豁免 | 面板 skipped 数上升；早退用例不再计入通过 | 测试面板 skipped 数含真库用例；new-skip-guard `--selftest` 全过 | FIX-004 |
| FIX-007 | state.md 状态漂移 | `.ai/state.md:4-5` | 脚本从 git 生成提交数/未决项；CI 校验一致性 | 脚本输出与 `git rev-list --count` 一致；失实报错 | state.md 数字与 git 一致 | 无 |

### 4.3 批次 3：P2 观测与契约（中优先）

| 编号 | 发现 | 位置 | 修法 | 锁定测试 | 验收 | 依赖 |
|---|---|---|---|---|---|---|
| FIX-008 | TimeoutBehavior 吞自抛 TimeoutException | `TimeoutBehavior.cs:79-83` | catch 内判 `running.IsFaulted` 则原样 rethrow | 单测：handler 抛 TimeoutException，断言异常保真 | 单测通过；变异验证转红 | 无 |
| FIX-009 | Kafka finally 缺 enqueueConfirmed | `KafkaBroker.cs:897` | 补等价判据（对齐 Rabbit `:635`） | 单测/真容器：批量 WriteAsync 失败后 ack 结算 | 测试通过；变异转红 | FIX-001 |
| FIX-010 | MessageBatchPump 通道内 item 不结算 | `MessageBatchPump.cs:239-258` | finally 结算通道内全部 item | 单测：关停时通道内 item 结算 | 测试通过；变异转红 | 无 |
| FIX-011 | F2 不完整 | `RabbitMqBroker.cs:219/224` | `EnsureExchangeAsync`/`Serialize` 移入 try | 单测：broker 不可达/序列化失败计 `BrokerPublishFailed` | 计数器测试通过 | 无 |
| FIX-012 | OutboxBatchProcessor 关停窗口异常逃逸 | `OutboxBatchProcessor.cs:552/587` | 补 catch 防异常逃逸 | 单测：broker 同步抛，断言异常被捕获 | 测试通过；变异转红 | 无 |
| FIX-013 | InMemory Outbox 契约违背 | `InMemoryOutboxStore.cs:213-220` | 对齐契约（保留 Processed 至 Retention）或文档声明例外 + 补测试 | 单测：`GetByStatusAsync(Processed)` 可见 | 四栈契约测试齐；文档同步 | 无 |
| FIX-014 | DomainEventOutboxBridge 事件丢失 | `DomainEventOutboxBridge.cs:112-115` | 事件清理移到提交后 | 单测：提交失败回滚后重试不丢事件 | 测试通过；变异转红 | 无 |
| FIX-015 | Saga CompensationFailed 无观察 | `SagaProcessor.cs:343` | catch 分支补 `SafeObserveStatusChangedAsync` | 单测：补偿失败触发观察事件 | 测试通过；变异转红 | 无 |
| FIX-016 | Rabbit 计数/Activity 不对称 | `RabbitMqBroker.cs:583-588/244-265` | 反序列化 null 补计数；发布失败补 `SetStatus(Error)` | 单测：计数器与 Activity 状态 | 测试通过 | 无 |
| FIX-017 | StreamRunRegistry 所有权绕过 | `StreamRunRegistry.cs:869/1071` | `Register` 加 owner 参数；`Cancel` 校验所有权 | 单测：null-owner run 不可被任意请求取消 | 测试通过；变异转红 | 无 |

### 4.4 批次 4：P2/P3 文档与端点安全（低优先）

| 编号 | 发现 | 位置 | 修法 | 锁定测试 | 验收 | 依赖 |
|---|---|---|---|---|---|---|
| FIX-018 | 文档失实（CHANGELOG/README/aot.md） | `CHANGELOG.md`/`README.md:24`/`docs/aot.md:175` | 顺序对齐 §11.2；数字更新；矛盾修正 | count-audit；verify-conventions | count-audit 0 失实；verify-conventions --quick 通过 | 无 |
| FIX-019 | 端点安全（异常/SSE/health/AOT） | `ExceptionMiddleware.cs:94/138`/`EndpointExtensions.cs:766`/`HealthCheckExtensions.cs:138`/`PalDDD.Extension.csproj:18` | 消息泛化；SSE 换行消毒；health 详情收敛；AOT false 覆盖 | 单测：异常不含内部消息；SSE 不含注入帧 | 测试通过；AOT 门禁通过 | 无 |
| FIX-020 | P3 机械清偿（11 项） | 见 §2.3 | 逐项修复 | 各项对应测试 | 全量 build/test 绿 | 前序批次 |

---

## 五、实施批次与验收

| 批次 | 内容 | 优先级 | 依赖 | 验收 |
|---|---|---|---|---|
| 1 | FIX-001/002/003 | 最高 | 真容器 | build 0 警告；真容器用例通过；变异转红 |
| 2 | FIX-004/005/006/007 | 高 | 本地 | selftest 全过；CI skipped 一致；state.md 一致 |
| 3 | FIX-008~017 | 中 | 部分真容器 | 单测通过；变异转红 |
| 4 | FIX-018/019/020 | 低 | 本地 | count-audit/verify-conventions 绿 |

**本地验收命令**：

```
dotnet build                                    # 0 警告 0 错误
dotnet test                                     # 0 失败，Skip 数披露（中文格式）
dotnet run scripts/gate-audit.cs                # REVIEW=0，探针全过
dotnet run scripts/new-skip-guard.cs -- --selftest   # 含 Skip.Test 正负例
dotnet run scripts/gate-audit.cs -- --inventory
```

**真容器验收**（批次 1 必须）：

```
wslc 容器（mysql84/pg186/kafka431/rabbit436）
- Kafka：maxConcurrent=1 + 慢 handler + 积压，断言无死锁
- Kafka：双消费者 rebalance，断言位点推进、Done 清空
- Rabbit：handler 结算后抛 OCE，断言 channel 存活
```

---

## 六、彻底性论证（为什么这套方案不是补丁）

| 发现 | 补丁式修法 | 本方案 | 彻底性依据 |
|---|---|---|---|
| Kafka 死锁 | 两 catch 补判据 | 有界 channel，消费线程永不阻塞 | 消除「ack 排空依赖 slot 释放」的循环依赖 |
| Kafka rebalance | Ack 里加跳跃判断 | 生命周期回调，状态与所有权对齐 | 事件驱动，无启发式阈值 |
| Rabbit OCE | 两 catch 各补 `IsSettled` | 结算门控单一来源 + 下沉检查 | 结构上消除判据分散的遗漏可能 |
| new-skip-guard | 补 `Skip.Test` 形态 | 补形态 + selftest 正负例 + 枚举 TUnit 全形态 | 防止再次遗漏新形态 |
| CI Skip 披露 | 改中文 grep | 中文 grep + 空转断言 + 排期结构化输出 | 止血 + 根治双轨 |
| 真库早退 | 改 Skip.Test | Skip.Test + new-skip-guard 豁免环境性测试 | 让「未执行」在面板可见，而非计入通过 |
| state.md 漂移 | 手工对齐 | 脚本生成 + CI 校验 | 消除手工维护的漂移根因 |

**核心原则**：每个方案都消除「允许缺陷存在的结构」，而非只修复当前缺陷实例。批次 1 的三条是架构级（并发模型、状态生命周期、判据单一来源），批次 2 的四条是门禁可信度（让「全绿」口径可信）。

---

## 七、局限与风险

1. **真容器车道**：批次 1 的运行时验证依赖真容器（本地无 Docker），需 wslc 或 CI Testcontainers。
2. **有界 channel 改动面**：消费线程异步化后，需确认 `drainerStopped`、`WaitSeekBackoff`、`offsetTracker` 的交互不受影响。
3. **`ackCommands.RemoveAll`**：`ConcurrentQueue` 无内置 `RemoveAll`，需实现细节设计（遍历重建或标记跳过）。
4. **CI Skip 披露修正后**：skipped 数会上升（口径修正），需同步更新 README 基线数字。
5. **P3 未逐条复验**：置信级为「单份报告 + 代码摘录」，修复前需逐条复验。
6. **四份报告时间基线不同**：B 在 F1/F2 前，A/C/D 在后，B 的部分发现可能已被 F1/F2 间接覆盖，综合时已按当前代码状态重新核实。
