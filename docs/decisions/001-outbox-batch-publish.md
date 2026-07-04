# ADR 001：Outbox 批量发布语义

> 状态：提案  
> 日期：2026-06-28  
> 决策者：Pal.DDD 架构组

## 背景

当前 `OutboxProcessor` 逐条获取待发布消息、逐条发布、逐条标记状态。对于高吞吐场景，逐条发布存在两个潜在问题：

1. **网络往返开销**：每条消息需要独立的 Broker RPC 往返
2. **状态更新碎片化**：逐条 UPDATE 导致大量小事务

批量发布（一次获取 N 条 → 全部发布 → 批量标记）可以降低上述开销，但引入了 at-least-once 语义下的失败处理复杂性。

## 问题

**当批量发布中的某条消息失败时，如何处理同一批次中已成功发布的消息？**

```
批次：[msg1 ✓] [msg2 ✗] [msg3 ✓]

msg1 已成功发布 → 标记 Processed ✓
msg2 发布失败     → 重试 or 死信
msg3 已成功发布 → 但 msg2 失败，是否标记 Processed？
```

## 方案

### 方案 A：全或无（All-or-Nothing）

整个批次要么全部成功标记，要么全部回退重试。

**优点**：语义简单，实现最简。

**缺点**：
- msg1/msg3 会被重复发布（Broker at-least-once 投递 + Outbox 重复发布 = **at-least-twice**）
- 下游必须幂等，否则 msg1/msg3 会产生副作用
- 一条毒药消息阻塞整个批次

### 方案 B：逐条独立（Per-Message Independence）

每条消息独立决定 success/retry/dead，批量只用于获取和发布。

**优点**：
- 语义精确——成功的不重发，失败的独立重试
- 不因一条失败阻塞同批次其他消息
- 下游幂等要求与逐条模式相同

**缺点**：
- 需要部分成功/部分失败的批量状态更新
- 实现更复杂（需要跟踪批内每条的发布结果）

### 方案 C：分组提交（Grouped Commit）

按 Broker 能力分组，单次 RPC 发布多消息（如 Kafka 批量 produce、RabbitMQ 批量 publish）。

**优点**：真实减少网络往返，与 Broker 原生批量能力对齐。

**缺点**：
- Kafka `ProduceAsync` 已支持批量 produce（linger.ms + batch.size），应用层干预可能适得其反
- RabbitMQ 无原生批量 publish，需要 `IModel.BasicPublish` 逐条
- 不同 Broker 的批量语义差异大，抽象成本高

## 决策

**采纳方案 B（逐条独立），暂不实现。**

理由：

1. **语义正确性优先于性能优化**：at-least-once 的核心保证是"不丢失消息"，方案 B 在失败时不会导致已成功消息被重复发布，与当前逐条模式语义一致
2. **当前逐条模式已满足大部分场景**：`OutboxProcessor` 已在多实例部署下通过原子租约获取保证并发安全，逐条发布对大多数应用已足够
3. **批量获取已在 AddMessagesAsync 中实现**：Dapper 的 `DapperBulkCopy`（PG COPY / MySQL BulkCopy / SQLite 批事务）已提供批量插入，瓶颈主要在发布端而非获取端
4. **方案 C 的收益由 Broker 驱动**：Kafka 的生产者批处理由客户端配置控制（`linger.ms`），应用层不应重复实现；RabbitMQ 无原生批量，应用层批量无额外收益
5. **实现成本高、收益不确定**：方案 B 的 per-message 追踪 + 批量状态更新需要修改 `IPalOutboxStore` 接口和所有实现，在没有真实性能瓶颈数据前，不应投入

## 后续

- 如果实际生产中出现 Outbox 发布延迟成为瓶颈，先评估 Broker 端批处理配置（Kafka linger.ms、RabbitMQ publisher confirm 批处理）
- 确认瓶颈在应用层后，再考虑实现方案 B，并同时提供 BenchmarkDotNet 对比数据
- 保持 `IPalOutboxStore` 接口稳定，方案 B 可作为内部实现优化，不改变公共 API

## 后果

- **正面**：接口保持简单，语义清晰，不会引入 at-least-twice 风险
- **负面**：高吞吐场景下，逐条发布的网络往返可能成为瓶颈
- **风险**：如果未测量就实现批量发布，可能引入难以调试的部分失败语义
