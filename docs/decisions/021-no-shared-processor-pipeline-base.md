# ADR 021：四处理器"租约→执行→标记"管线不做基类提取——形状同构≠语义同构

> 状态：已采纳
> 日期：2026-08-26
> 关联：ADR-020（三栈收敛）、二轮架构评审 P2"四处理器同构管线复制"、`PalDDD.Core/FailureReason.cs`（本决策的替代收敛物）

## 背景

InboxProcessor / IdempotencyProcessor / ProjectionProcessor / OutboxBatchProcessor 四个处理器的核心路径形状相似：`try-start（租约/幂等抢占）→ handler 执行 → mark processed → 异常时 mark failed（内层 catch 保护标记失败）`。多轮评审反复指出这是"同构管线四处复制"，ITM-175/F2 族修复需四处同步，建议提取共享基类。

## 决策

**不提取管线基类。** 仅收敛语义完全一致的最小单元——失败原因归一化（`FailureReason.Normalize`：截断 2000 + 空白归一，五处含 SagaProcessor 已收敛到 `PalDDD.Core`）。

## 理由

四个处理器的"同构"是**形状同构**，不是**语义同构**。逐差异维度对比（2026-08-26 实测代码）：

| 维度 | Inbox | Idempotency | Projection | Outbox |
|------|--------|-------------|------------|--------|
| 起步 | TryStartProcessing（单消息幂等 INSERT） | TryStart(operation,key)+响应缓存 | checkpoint 租约批 | 租约批（方言分叉 SQL） |
| 成功标记失败策略 | **按成功返回 + pending-confirmation**（ITM-180：不得降级为可重试） | 响应缓存路径不同 | checkpoint 更新 | **吞异常 Warning**（下轮租约纠正） |
| 失败标记 | MarkFailedAsync | 失败缓存 | MarkFailedAsync | **RetryCount 分叉**：MarkDead vs ReleaseForRetry + 退避计算 |
| 指标/Activity | 四套独立 Meter 与 tag 命名 | 同左 | 同左 | 同左 |

提取基类需把 ≥8 个语义差异（起步方式、双标记策略、成功-标记失败降级策略、重试分叉、指标族、tag 命名、日志文案、取消语义）参数化为回调/选项——**抽象的参数面逼近行为本身**，这是 Wrong Abstraction 的教科书形态：错误的抽象比重复更昂贵（修改基类回调签名时四个调用方同时回归，而现状只需改语义变更的那一个）。

**"姊妹修复"成本的真实对账**：40+ 轮审计中该族的同步成本集中于两类——fencing 语义（随 ADR-020 的栈收敛自然缩减）与失败标记族（已由 FailureReason 收敛根治）。剩余的形状相似部分无独立修复历史，同步成本证据不足。

## 已采纳的替代收敛

1. `PalDDD.Core.FailureReason`（本轮）：五处失败原因归一化一字不差，零参数化提取。
2. 后续若再出现"四处一字不差"的逻辑（如退避延迟计算），按同一标准单独提取——**按语义等价提取最小单元，不按形状相似提取大结构**。

## 触发重评条件

- 第五个处理器出现且起步/标记策略与现有四者之一**语义一致**（而非仅形状相似）。
- 某类缺陷（非失败标记族）在四个处理器中反复四次以上同步修复——出现时优先提取那个具体单元而非整管线。

## 后果

- 四处理器保持独立演进；新增处理器时可复制本 ADR 的差异对照表决策（复制前先核对语义是否真的同构）。
- 评审清单更新："同构管线提取"不再作为默认建议提出，除非满足"语义等价"标准。
