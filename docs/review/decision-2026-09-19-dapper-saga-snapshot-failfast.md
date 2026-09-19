# 决策论证：Dapper Saga 快照 fail-fast（对齐 PalORM ITM-228）

> 编号：DECISION-2026-09-19-saga-snapshot-failfast
> 基线：commit `80ab4e7` · 日期：2026-09-19
> 性质：**行为变更**（静默 → 异常，破坏性，2.x 内以「修复数据丢失缺陷」定性）
> 结构约束：本模板三段（消费状态/证据锚点表/开放决策点）为 V11 门禁必填段。

---

## 消费状态

| 项 | 值 |
|------|------|
| 落盘日期 | 2026-09-19 |
| 评审状态 | 已裁决（2026-09-19 用户指令「按这个顺序执行采纳」——v2 审计 M1-1 在授权清单内；决策文档按 AGENTS.md §3 先行落盘，同提交改代码） |
| 评审记录 | v2 首席审计 A-1（亲证复核确认）：发现、对照、修复草案均出自 review-2026-09-19-chief-audit-v2.md，本会话主线程逐行复证（DapperSagaStateStore.cs:241-242 vs PalOrmSagaStateStore.cs:205-214） |
| 裁决记录 | Dapper 栈 fail-fast 立即实施（2.x 修复定性）；EF 栈经亲证**无需修**（源生成架构无 null 分支——SagaStateDbContext.cs:291-292 用 HasConversion + SagaStateJsonContext，不依赖用户传入 JsonTypeInfo，v2 报告自认未验证此项，本决策补验）；破坏性以 CHANGELOG 显式声明 |

## 证据锚点表

> 核对 ☒ = 已打开来源逐项比对属实。

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| 缺陷：`_jsonTypeInfo is null ? null : Serialize` 静默写 NULL（saga_data 业务字段全丢，无异常无日志无启动诊断） | DapperSagaStateStore.cs:241 | ☒ |
| 姊妹正解：PalORM 同位置 fail-fast 带修复指引（ITM-228，三十二轮） | PalOrmSagaStateStore.cs:205 | ☒ |
| 默认 DI 路径无传参通道（AddPalDapperSagaSnapshot 是 opt-in :152） | DapperServiceCollectionExtensions.cs:120 | ☒ |
| 触发面：用户按默认注册使用 Saga + 重启恢复 → 业务字段丢失 | docs/usage.md:300 | ☒ |
| EF 栈无需修（源生成 HasConversion 架构，无用户传 JsonTypeInfo 依赖） | SagaStateDbContext.cs:291 | ☒ |

## 开放决策点

1. 静默→异常是否可接受为 2.x 破坏性变更？**裁决：是**——静默数据丢失是缺陷而非契约（PalORM 已按缺陷修复），「依赖静默丢数据的用户」不是应保护的用法；CHANGELOG 显式标注。
2. 抛点选保存期还是构造期？**裁决：保存期**（v2 草案）——构造期抛会让 GetByIdAsync 等读路径不可用且 DI 探测成本高；保存期抛精确命中「正要丢数据」的时刻。
3. 异常类型与文案？**裁决：逐字对齐 PalORM**（同构契约优先于本地口味），仅类名前缀不同。

---

## 0. 结论速览

| 项 | 建议 | 一句话理由 |
|---|---|---|
| Dapper saga 快照 fail-fast | **立即实施** | S 级工作量消灭唯一数据丢失面；PalORM 已有正解可逐字对齐；「"O" 参数」与「静默 null」同为静默错误类，前者已实证排除，后者今天收口 |

## 1. 方案

`SerializeState` 的 null 分支改抛 `InvalidOperationException`（复用 PalORM 文案结构，指引 `AddPalDapperSagaSnapshot`）；调用点 :170/:185 两分支天然继承；README + docs/usage.md:300 升级 ⚠️ 段；CHANGELOG 记 breaking；三栈参数化测试（未注册 → Dapper/PalORM 同型异常；EF 不适用）。

## 2. 风险

既有用户若「依赖」静默 null（例如故意只存元数据）会在升级后收到异常——该用法应显式改为不注册快照的替代路径（目前无，属新需求另案）。抛异常发生在后台 SagaProcessor 循环内：按处理器既有语义，异常计入该 saga 失败路径（可见、可重试），不崩进程。
