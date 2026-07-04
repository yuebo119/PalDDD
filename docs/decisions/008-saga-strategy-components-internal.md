# ADR 008：SagaCompensation / SagaTimeoutDetector 维持 internal sealed

> 状态：已采纳  
> 日期：2026-06-29  

## 背景

`SagaCompensation<TState>`（`src/PalDDD.Transactions/SagaCompensation.cs`）与 `SagaTimeoutDetector<TState>`（`src/PalDDD.Transactions/SagaTimeoutDetector.cs`）当前为 `internal sealed`，由 `Saga<TState>` 基类组合委托。评审提出：是否应将这两个策略组件暴露为 `protected`（或公共基类），让子类作者直接复用其补偿 / 超时检测逻辑，而不必通过 `Saga` 基类委托调用。

## 决策

维持 `internal sealed`，**不暴露**为 `protected` / `public`。子类通过 `Saga<TState>` 公共委托方法访问补偿与超时能力：

- `Saga<TState>.CompensateExecutedStepsAsync` 公共委托
- `Saga<TState>.CompensateAsync` 公共委托
- `Saga<TState>.IsTimedOut` 公共委托

这是"强封装 + 组合优于继承"的策略组件模式。

## 取舍

- **优点**
  - 策略组件 API 表面可自由重构而不破坏子类作者。两次重构（v0.1 的 `CompensateExecutedStepsAsync` 内部优化 + 增加 `IsTimedOut` 返回所有超时步骤而非仅第一个）都未触及公共委托 API。
  - 子类作者只需关心 `When` / `ProcessEventAsync` 等编排 API，不受策略组件实现细节耦合——降低 Saga 入门门槛。
  - 与 ADR-004「PipelineStateMachine 等核心类型保留论证」一致：策略组件是内部实现细节，不构成对外公共 API。
- **代价**
  - 子类若需要"将补偿逻辑与外部协调器组合"等罕见场景，必须通过 `Saga` 基类委托调用，无法直接继承 `SagaCompensation<TState>` 复用。代价低——此类需求罕见，且通过公共委托 API 已可满足。

## 边界条件

后续若出现以下场景，需重新评估本决策：

1. 出现第三方扩展包希望继承 `SagaCompensation<TState>` 注入自定义补偿步骤选择策略（如跳过某些步骤补偿）；
2. 评审确认至少 2 个独立场景需要"子类直接复用策略组件"且公共委托 API 不足以表达。

## 验证

- `PalDDD.Transactions.Tests/SagaTests.cs` 通过 `Saga<TState>` 公共 API 完整覆盖补偿与超时场景，不依赖策略组件的直接实例化。
- `SagaCompensation<TState>` 与 `SagaTimeoutDetector<TState>` 保持 `internal sealed` —— `grep -r "class SagaCompensation\|class SagaTimeoutDetector" src/PalDDD.Transactions/` 仅出现 `internal sealed` 修饰。

## 关联

- 源码：`src/PalDDD.Transactions/SagaCompensation.cs`、`src/PalDDD.Transactions/SagaTimeoutDetector.cs`
- 调用方委托：`src/PalDDD.Transactions/Saga.cs` Compensate / IsTimedOut 公共方法
- 评审来源：`docs/review/audit-2026-06-29-v2.md` ITM-009