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

channel 级串行面：发布、confirm、QoS、consume、ack 在同一 channel 上互相排队；
一个慢消费者或 channel 级异常会影响所有订阅与发布。拆分后每个订阅的 prefetch 与
消费进度独立，发布确认只与发布 channel 相关。

## 回滚

设置 `UseSharedConsumerChannel = true` 即回到单 channel 形态；无需改连接串或队列声明。
若需要完全回退到旧版本，consumer channel 是新增的，不影响既有 exchange/queue 名称。

## 验收

- `PalDDD.Messaging.Integration.Tests` 在本地 RabbitMQ（wslc rabbit436，palddd-test 用户）
  18/18 通过（2026-10-19），覆盖发布/订阅往返、多消息、handler 取消、handler 失败弃置、
  不可路由发布异常。
- 手工压测建议：1/4/8 订阅 + 发布混合，观察 p50/p99、ack 帧数、慢消费者不阻塞发布。
- 单 channel 兼容模式回归：同一测试套件设 `UseSharedConsumerChannel=true` 复跑。
