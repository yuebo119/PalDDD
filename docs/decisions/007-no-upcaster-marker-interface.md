# ADR 007：不暴露 `IEventUpcaster` marker 接口

> 状态：已采纳  
> 日期：2026-06-29  

## 背景

`PalDDD.Serialization.Evolution` 提供 `MessageEvolutionPipeline` 消息升级链——在反序列化旧版本消息时按 `MessageDescriptor` 链式应用升级器将旧 schema 映射到新 schema。社区主流事件溯源框架（如 NEventStore / Axon / EventStore.CosmosDB SDK）通常提供 `IUpcaster<T>` 或 `IEventUpcaster` marker 接口，让升级器实现并被框架扫描注册。

Pal.DDD 评审中触发讨论：是否应提供类似的 `IUpcaster` marker 接口以提升扩展性？架构边界测试 `CoreLayer_DoesNotExposeIntegrationEventMarkerOrUpcasterPlaceholders`（`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`）显式断言源码中不出现 `IUpcaster` 字符串。

## 决策

**不暴露** `IEventUpcaster` 或 `IUpcaster` marker 接口，保持"执行链优先于 marker 接口"的设计立场。升级器协议通过以下既有形态表达：

- `MessageEvolutionPipeline` 接收显式注册的升级函数 / 升级步骤（委托 + `MessageDescriptor` 配对），编译期类型安全。
- 启动期校验由 `PalPlatformVerifier.ValidateMessageEvolutionPaths` / `ValidateMessageContractManifest` 负责——hosting 包通过 DI 注册它并配置必需的演化路径，运行时报错而非"找不到 marker 实现"的运行时反射扫描。

## 取舍

- **优点**
  - 维持零反射红线：marker 接口在主流框架中常通过 `Assembly.GetTypes()` 扫描注册，与 `AGENTS.md` 零反射红线冲突。
  - 显式注册优于隐式扫描：升级路径在启动期校验时就被发现缺失，避免运行时"找不到 IUpcaster 实现"的隐式失败。
  - 架构边界测试可保持对 marker 接口的拒绝断言，治理纪律不变。
- **代价**
  - 第三方使用者接入自有升级器时需通过显式 `AddPalMessageEvolution` API 注册，无法依赖"实现 marker 接口即被自动发现"。代价低——Pal.DDD 全生态已统一 `AddPal*` 显式注册风格（Handler / Serializer / Broker 均如此）。
  - 架构测试需持续维护 `Assert.DoesNotContain("IUpcaster", source)` 断言防止回潮。

## 边界条件

后续若出现以下任一场景，需重新评估本决策，考虑是否引入轻量、显式注册的升级器协议（而非反射 marker）：

1. 多个 hosting 包希望共享同一套升级器注册 DSL，发现当前委托式注册冗长；
2. 升级器需要 DI 容器生命周期管理（如自动启停 hosted service 注册的升级器）；
3. 评审中确认第三方扩展需求集中到"我希望写一个会被自动发现的升级器"。

## 验证

- 架构测试 `CoreLayer_DoesNotExposeIntegrationEventMarkerOrUpcasterPlaceholders` 仍通过；grep `IUpcaster` / `IEventUpcaster` 在 `src/PalDDD.Core` 范围零命中。
- `PalPlatformVerifier` 启动期校验覆盖演化路径报错路径，等价替代"反射扫描未找到 marker 实现"的发现能力。

## 关联

- 架构测试：`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`
- 演化管道：`src/PalDDD.Serialization.Evolution/MessageEvolutionPipeline.cs`
- 启动校验：`src/PalDDD.Serialization.Evolution/PalPlatformVerifier.cs`