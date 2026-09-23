# Pal.DDD 决策论证

> 编号：DECISION-2026-09-22-dapper-markprocessed-lease-clear
> 基线：commit `4c37574` · 日期：2026-09-22
> 性质：实施决策（回应 `DapperOutboxStore` 的 P3-SRC-301 声明）
> 结构约束：本模板三段（消费状态 / 证据锚点表 / 开放决策点）为 V11 门禁必填段——
> `dotnet run scripts/verify-conventions.cs -- --quick` 机械校验段存在与锚点格式。

---

## 消费状态

| 项 | 值 |
|------|------|
| 落盘日期 | 2026-09-22 |
| 评审状态 | 未评审 |
| 评审记录 | —（评审后填：报告链接或核对结论，含证伪项） |
| 裁决记录 | —（用户 2026-09-22 授权实施本决策；逐行核对待评审） |

## 背景与被回应的声明

`DapperOutboxStore.MarkProcessed` 有一条声明（`P3-SRC-301`）声明了它与另两栈的分叉：

> "affected 返回值不消费——与原语义一致（token 拒绝时 DB 行不变，内存入参仍按下方 ITM-130
> 同步清租约字段）。P3-SRC-301 声明：affected=0（token 拒绝）时内存对象仅清租约字段不回写
> Status——与 InMemory 版（守卫内联设 Processed）/PalORM 版（affected>0 才全套回写）的分叉属
> ITM-210 历史语义，调用方（OutboxBatchProcessor）不读该状态故无实害。"

本决策回应它：**分叉的"无实害"前提成立，但分叉本身与同文件 ITM-130 的既定意图相矛盾**
——ITM-130 明写目标是"对齐 EFCore/PalORM/InMemory 三姊妹的对象字段语义"。故按 ITM-130 的
方向补齐，**不删除 P3-SRC-301 声明，而是在其上追加本决策的回应**（AGENTS.md §3）。

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| Dapper 的 `affected` 返回值不被消费 | src/PalDDD.Dapper/DapperOutboxStore.cs:255 | ☐ |
| Dapper 无条件清租约字段（不区分 affected） | src/PalDDD.Dapper/DapperOutboxStore.cs:264-265 | ☐ |
| 分叉的"无实害"理由 = 调用方不读入参状态 | src/PalDDD.Dapper/DapperOutboxStore.cs:257-259 | ☐ |
| **调用方确实只计数、不读入参状态**（"无实害"前提成立） | src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs:183-184 | ☐ |
| PalORM 的 affected=0 时**零内存变异** | src/PalDDD.PalORM/Stores/PalOrmOutboxStore.cs:182-183 | ☐ |
| ITM-130 的既定意图 = 三姊妹对象字段语义对齐 | src/PalDDD.Dapper/DapperOutboxStore.cs:262-263 | ☐ |
| Dapper 的 SQL 侧 fencing 与 PalORM 同级（owner/until/retryCount 三 token） | src/PalDDD.Dapper/DapperOutboxStore.cs:261 | ☐ |
| 接口 `MarkProcessed` 返回 void（无返回值可供调用方区分） | src/PalDDD.Transactions/Outbox/OutboxStore.cs:82 | ☐ |

## 结论

**实施对齐**：Dapper 消费 `affected`，仅 `affected > 0` 时清租约字段。

理由（按强度）：
1. **ITM-130 的既定意图就是"三姊妹对象字段语义对齐"**——不消费 affected 使该意图只完成一半。
2. **被拒标记后 Dapper 的入参对象声称租约已释放，而 DB 行仍持有该租约**——即对象状态与
   持久化真相矛盾。这在"调用方不读"时无害，但 `OutboxMessage` 是**公共 API 的入参对象**，
   框架无法约束所有调用方（含应用层自定义处理器）不读。
3. PalORM 已是"affected>0 才全套回写"，对齐后三栈一致，**减少一处需要记忆的分叉**。

**不改**：SQL 侧 fencing（已与 PalORM 同级，无需动）；`P3-SRC-301` 声明保留并追加回应。

## 开放决策点

1. **"被门控的标记"是否需要可观测信号？**（推荐：暂不；理由：接口返回 `void` 是既成契约，
   新增 `TryMarkProcessed` 属能力扩展，与本次对齐无关——见契约矩阵"另记一条独立观察"。）
2. **是否需为 `MarkProcessed` 的"被拒"路径补跨栈契约测试？**（推荐：补；理由：本次对齐的
   正确性依赖三栈一致，仅靠单栈测试无法发现未来的再次分叉。无 Docker 时方言栈按 T-17 skip。）
