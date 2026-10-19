# ADR 029：序列化直写 — IMessageSerializer 的 IBufferWriter 重载（DIM 默认 + Json 覆写）

> 状态：**已采纳（2026-10-14 全量重读性能实施轮 R8，P1-1）**
> 日期：2026-10-14
> 关联：ADR-027 / ADR-028（同轮先例）、C-F1 审计发现（每消息全载荷 ToArray 拷贝）、
> `docs/performance.md`（R8 入账段）

## 背景

全量重读性能审计（2026-10-14）确认：`IMessageSerializer.Serialize(object, descriptor)`
返回 <c>ReadOnlyMemory&lt;byte&gt;</c>，实现（Json/MemoryPack）经池化 writer 写完后必须
<b>精确尺寸 <c>ToArray()</c> 拷出</b>——每条消息固定 1 次堆分配（量级 = payload 尺寸）
+ 1 次 memcpy（KafkaBroker/RabbitMqBroker 发布路径每消息必经；1KB 消息 1 万 msg/s
≈ 10MB/s Gen0 churn）。TLS 池消除了 writer/buffer 分配，但返回契约强制拷贝。

## 决策

**接口 DIM 默认 + 实现覆写 + 消费端直传**：

1. `IMessageSerializer` 新增重载（**DIM 默认实现**——自定义序列化器零迁移成本，
   DIM 体为「内存形态 + 单次拷贝进 destination」）：
   `void Serialize(object message, MessageDescriptor descriptor, IBufferWriter<byte> destination)`；
2. `JsonMessageSerializer` 覆写为**真直写**：池化 <c>Utf8JsonWriter</c> 经
   <c>Reset(destination)</c> 直接写入调用方缓冲，零 ToArray、零中间拷贝
   （与内存重载共用同一 TLS Writer，同款非重入限制声明延续）；
3. `RabbitMqBroker` 发布路径改 TLS `ArrayBufferWriter` + <c>WrittenMemory</c> 直传
   `BasicPublishAsync(body:)`（入口 Clear 兼作异常复位；前提：RabbitMQ.Client 7 在
   发布完成前已消费 body——AMQP 帧组装即拷贝，confirm 追踪只存序号）；
4. **Kafka 不改**（Confluent API body 为 <c>byte[]</c>，ToArray 不可避免——其发布处
   已有勘正注释在案）；**MemoryPack 暂不覆写**（1.21.4 的 IBufferWriter 重载仅泛型形态，
   无 (writer, Type, object) 对应重载，本地编译实证；T=object 泛型动态语义未验证——
   P0 #4 纪律不臆测，走 DIM 回退，原生直写待验证后跟进）。

**重要调用形态约束（红证明实证）**：DIM 成员经**具体类型**不可见（CS1501），消费方
须持接口类型引用（RabbitMqBroker 字段即 <c>IMessageSerializer</c> ✓）；Json 覆写为
公共成员，具体类型持有者可直接调用。

## 消费方指引（本决策的对外契约）

- destination 的已写内容在调用返回后立即可读，实现不持有其引用；buffer 生命周期
  归调用方（Rabbit 路径以入口 Clear 复用）；
- 输出字节与内存重载逐位一致（锁定测试）；
- 自定义 `IMessageSerializer` 实现零迁移（DIM 回退语义正确，仅损失直写收益）。

## 验证（本决策的实现依托）

- 字节等价 + 零分配双锁 + ClrType 守卫三件（`SerializerDirectWriteTests`）；
  **变异红证明在案**：覆写体临时替换为回退形态 → 零分配断言判红（Expected 0 but
  found 560000，56B/op 载荷拷贝）→ 恢复绿；
- 快照 +2 行纯增量（接口 DIM 方法 + Json 公共覆写），零删除。
