# 决策论证：Saga 动态步骤双键轨迹定案（维持「失败路径保留 dynKey」）

> 编号：DECISION-2026-10-27-saga-dynkey-trajectory
> 基线：commit `5bf98d9` · 日期：2026-10-27
> 性质：**定案（不改行为）**——兑现 Saga.cs 中「改双键轨迹语义须先出决策文档」的 v5.0 契约窗口承诺（VersionPromises 守卫 #11）
> 结构约束：本模板三段（消费状态/证据锚点表/开放决策点）为 V11 门禁必填段。

---

## 消费状态

| 项 | 值 |
|------|------|
| 落盘日期 | 2026-10-27 |
| 评审状态 | 已裁决（2026-10-27 用户指令「全部实现，实现了再发版 5.0」——本定案为 12 处到期版本承诺的兑现件之一） |
| 裁决记录 | **维持现状**：DynamicStep 失败路径（路由目标重试耗尽）保留 dynKey 于 ExecutedStepKeys，CompensateExecutedStepsAsync 连带调用 DynamicStep 自身 compensate（目标步骤补偿优先）。备选「失败路径不记 dynKey」**否决**。 |

## 证据锚点表

> 核对 ☒ = 已打开来源逐项比对属实。

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| dynKey 在执行前记录（失败路径也留在轨迹），补偿连带调用 DynamicStep 的 compensate | Saga.cs:928 | ☒ |
| 刻意设计声明：DynamicStep 不执行业务动作，其 compensate 语义 = 回滚 router 路由决策的副作用 | Saga.cs:920 | ☒ |
| 双键轨迹表征测试两件（成功双记录 / 耗尽补偿双键） | SagaLaneCharacterizationTests.cs:328 | ☒ |
| 耗尽补偿观察断言（CompensationLog 含 target-compensated 与 dyn-compensated 两项） | SagaLaneCharacterizationTests.cs:347 | ☒ |
| 承诺原文：「若要改为失败路径不记 dynKey，须先出决策文档回应本声明与该表征测试」 | Saga.cs:926 | ☒ |

## 开放决策点

1. 失败路径是否保留 dynKey？**裁决：保留（维持现状）**——路由决策本身有副作用（按路由结果分配的资源/触发的下游动作），删 dynKey 会让「回滚路由决策副作用」的补偿静默消失；漏补偿无兜底（与 M3-3 回滚同理：轨迹是事实记录，宁可重复补偿——`SagaStep` 补偿幂等是既有契约——不可漏补偿）。
2. 是否需要为「目标步骤补偿优先于 dynKey 补偿」的顺序单独加断言？**裁决：不加**——表征测试 :347 已锁 CompensationLog 两项；顺序语义由 CompensateExecutedStepsAsync 的 failedStep 优先实现（Saga.cs:466 注释）承担，加断言属实现耦合。
3. 该承诺后续是否还有开放面？**裁决：无**——本定案关闭 Saga.cs:926-927 的「随 v5.0 契约窗口处理」承诺，源码注释改为引用本决策，不再命中版本承诺扫描。

---

## 结论

v5.0.0 维持双键轨迹现状，零代码行为变更；Saga.cs 注释由「须先出决策文档」改写为「已由 DECISION-2026-10-27 定案维持」。表征测试不改动。
