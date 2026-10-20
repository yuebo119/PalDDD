# RabbitMQ 通道拓扑（M3）

> 日期：2026-10-19
> 关联：`docs/review/perf-migration-plan-2026-10-19.md` M3

## 变更

`RabbitMqBroker` 默认不再让发布、publisher confirms、所有订阅的 QoS/consume/ack
共用一个 `IChannel`：

- 发布继续使用构造注入的 `_channel`（推荐经 `CreateAsync` 创建并启用 confirm tracking）。
- 每个 `SubscribeAsync` 经 `IConnection.CreateChannelAsync` 创建独立 consumer channel，
  在该 channel 上声明 exchange/queue、绑定、设置 prefetch、consume、ack/nack。
- 订阅句柄释放时依次 cancel consumer、释放自有 consumer channel、释放 linked CTS；
  创建失败路径同样释放已创建的 consumer channel。
- `UseSharedConsumerChannel = true` 恢复 4.x 单 channel 行为（兼容/受限拓扑）。
  连接仍由调用方管理，broker 只释放自己创建的 channel。

## 为什么

channel 级串行面：QoS、consume、ack 与 consumer dispatch 在同一 channel 上共享；
一个慢消费者会阻塞同一 channel 上的其他订阅（实测见下节）。拆分后每个订阅的 prefetch、
消费进度与分发面独立。发布确认走 channel 的 RPC 路径：本机客户端的实测显示慢消费者
不阻塞发布（isolation 下两种拓扑的 publish 均 0.7~1.6s），故 M3 的收益定位于跨订阅
消费隔离，而非发布解耦。

## 回滚

设置 `UseSharedConsumerChannel = true` 即回到单 channel 形态；无需改连接串或队列声明。
若需要完全回退到旧版本，consumer channel 是新增的，不影响既有 exchange/queue 名称。

## 压测结果（2026-10-19）

量具：`--m3-probe`（`bench/PalDDD.Benchmarks/RabbitChannelProbe.cs`）。本机 wslc
rabbit436（127.0.0.1:5672，palddd-test），每轮 2,000 条 fanout 消息，1/4/8 个订阅各收
全量；测量前先跑 200 条丢弃预热，保证各行同温。延迟从发布前取时间戳到 handler 入口，
p50/p99 按全部投递样本计算。两个场景：

- `baseline`：所有订阅 handler 为空，测拓扑自身成本。
- `isolation`：订阅 0 的 handler 每次延迟 2ms，其余为空；`fast p99` 只看其余订阅，
  用于测慢订阅对快订阅的队头阻塞。Windows 计时器粒度使 `Task.Delay(2ms)` 实际约
  15ms，故 isolation 的绝对毫秒只用于同轮对照，不用于跨机对比。

| scenario | mode | subs | delivered | throughput (msg/s) | publish (ms) | p50 (ms) | p99 (ms) | fast p99 (ms) |
|---|---|---|---|---|---|---|---|---|
| baseline | shared | 1 | 2,000 | 2,768 | 722 | 0.35 | 0.50 | 0.50 |
| baseline | shared | 4 | 8,000 | 8,408 | 951 | 0.45 | 0.70 | 0.70 |
| baseline | shared | 8 | 16,000 | 14,335 | 1,116 | 0.52 | 0.81 | 0.81 |
| baseline | per-sub | 1 | 2,000 | 2,740 | 730 | 0.39 | 0.53 | 0.53 |
| baseline | per-sub | 4 | 8,000 | 8,353 | 958 | 0.43 | 0.76 | 0.76 |
| baseline | per-sub | 8 | 16,000 | 12,351 | 1,295 | 0.54 | 0.97 | 0.97 |
| isolation | shared | 4 | 8,000 | 251 | 697 | 15,693.43 | 30,818.39 | 30,817.72 |
| isolation | shared | 8 | 16,000 | 506 | 928 | 15,376.64 | 30,387.17 | 30,387.17 |
| isolation | per-sub | 4 | 8,000 | 252 | 962 | 0.43 | 29,544.42 | 0.70 |
| isolation | per-sub | 8 | 16,000 | 530 | 1,568 | 0.67 | 26,497.73 | 1.22 |

结论：

1. **跨订阅隔离效果成立且量级极大**：共享 channel 下，一个慢订阅把其余订阅的 p99
   拖到 30.8s/30.4s；独立 consumer channel 下快订阅 p99 为 0.70ms/1.22ms，低约
   2.5~4.4 万倍（探针输出 24,930×/44,025×）。原因是单 channel 的 consumer dispatch
   串行化：慢 handler 占住分发面，其他订阅的消息只能排队。
2. **发布确认不受慢消费者阻塞（两种拓扑一致）**：isolation 下 publish 耗时
   shared 697ms/928ms、per-sub 962ms/1,568ms。发布与 consumer dispatch 在本机客户端
   版本走不同路径，M3 不宣称改善这一项。
3. **独立 channel 自身的成本很小**：baseline（全空 handler、发布确认受限）下 per-sub
   相对 shared 的 p99 为 1.05~1.19×（绝对差 0.03~0.16ms），吞吐 0.86~0.99×。这是
   额外 channel 的 RPC/调度成本，量级远小于隔离收益。
4. **per-sub 的 all p99 30s 不是缺陷**：该值属于刻意慢的订阅 0 自身（2,000 条 × 约
   15ms 的排空时间）；快订阅的 fast p99 才是隔离指标。

局限：本机单 broker、单进程、handler 无 DB/网络 I/O、发布串行且受 publisher confirms
约束；每轮 2,000 条样本（p99 约 20 个样本），单次运行。结论用于验证拓扑差异与量级，
不作为容量上限或生产 SLO。复现：

```bash
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m3-probe
```

结论落到默认值：保持每订阅独立 consumer channel；`UseSharedConsumerChannel=true`
仅作兼容/受限拓扑回退，不应作为吞吐优化选项。

## 验收

- `PalDDD.Messaging.Integration.Tests` 在本地 RabbitMQ（wslc rabbit436，palddd-test 用户）
  18/18 通过（2026-10-19），覆盖发布/订阅往返、多消息、handler 取消、handler 失败弃置、
  不可路由发布异常。
- 1/4/8 订阅混合负载：见上节压测结果（2026-10-19）。
- 单 channel 兼容模式回归：同一测试套件设 `UseSharedConsumerChannel=true` 复跑。
