# ADR 005：net11.0 单目标框架决策

> 状态：已采纳  
> 日期：2026-06-28  

## 背景

评审报告建议评估 `net11.0;net10.0` 多目标框架可行性，以拓广受众。.NET 10 是当前 LTS 版本，大量项目仍在其上运行。

## 决策

**不添加 net10.0 多目标，保持 net11.0 单目标。**

## 理由

### 硬阻塞：OrderedDictionary<TKey,TValue>

`MessageCatalogBuilder` 使用 `System.Collections.Specialized.OrderedDictionary<TKey,TValue>` 维护消息注册顺序。此类是 .NET 11 独占 BCL 新增类型，无 polyfill 轻量方案。这是唯一的硬阻塞项，但它是核心基础设施——消息目录的确定性枚举顺序对 OpenAPI 生成、诊断输出和事件溯源回放至关重要。

### 其他 .NET 11 强依赖

| 特性 | .NET 版本 | 使用位置 | 影响 |
|------|----------|---------|------|
| `OrderedDictionary<TKey,TValue>` | 11 | MessageCatalogBuilder | 硬阻塞——无替代 |
| `IUtf8SpanFormattable` | 6 | ValueObject<T> | 可降级（移除接口），但性能退化 |
| `static abstract` 接口 | 7 | IDomainEvent.EventName | 可降级为 instance，但 AOT 兼容性退化 |
| `$$"""..."""` | 7 | OutboxDbContext | 可替换为普通字符串，仅可读性退化 |
| `[OverloadResolutionPriority]` | 9 | Dispatcher | 可移除，仅消除歧义警告 |
| `Collection expressions []` | 8 | 多处 | 可替换为 `new()`，仅代码风格退化 |

多目标意味着核心路径（消息注册、事件分发、AOT 序列化）均需降级，导致性能退化 + 代码复杂度上升，与框架的"优先正确性+性能"原则冲突。

## 后果

- **正面**：保持单一 TFM 的代码简洁性，充分利用 .NET 11 特性
- **负面**：限制受众为 .NET 11 项目
- **缓解**：.NET 11 预计 2026 年 11 月正式发布；此框架当前版本为 0.1.0，正式发布时 .NET 11 已 GA

## 替代方案（已评估并拒绝）

- **条件编译 `#if NET11_0`**：每个多目标文件需维护两套实现，维护成本翻倍
- **Polyfill OrderedDictionary**：需引入第三方依赖或自己实现完整字典+链表结构，增加维护负担
- **放弃 OrderedDictionary 改用 Dictionary**：丧失确定性顺序，破坏 MessageCatalog 的契约
