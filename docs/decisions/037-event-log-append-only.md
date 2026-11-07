# ADR 037：事件日志只追加不删除

> 状态：**已采纳（2026-11-04）**，无代码变更——本 ADR 把既有的隐式行为写成显式契约
> 日期：2026-11-04
> 关联：2026-11-04 公共 API 缺口审计（`docs/review/action-items-2026-11-04-api-gaps.md` §五「事件流删除或归档 API → 不做」）、ADR-030（流式读）、ADR-031（断线续跑）

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-11-04 |
| 评审状态 | 已采纳：`IEventLog` 与四个实现均无删除/归档入口（本次审计实证），本 ADR 将该事实从「没人提过」升格为「刻意契约」；无代码变更，故无门禁需要重跑 |
| 兼容性 | 无公共 API 变更、无行为变更——纯文档决策。已发布版本行为不变 |
| 触发原因 | 审计在补齐读侧 API 时把「事件流删除/归档」列为候选能力；核查后发现实现侧从未提供，且 36 份 ADR 无一份涉及事件日志的存储生命周期，属**未定义的空白**而非「已裁决不做」 |

## 背景

审计核查到三个事实：

1. **`IEventLog` 只有追加与读取**：`AppendAsync` / `ReadStreamAsync` / `ReadAllAsync`
   （`src/PalDDD.EventLog/IEventLog.cs:14-31`），四个实现（InMemory / EF Core / Dapper / PalORM）
   均无删除、归档、截断入口。
2. **保留清理不覆盖事件表**：`RetentionSql` 的 `DELETE` 只打三张表——
   `outbox_messages`（`src/PalDDD.Transactions/Retention/RetentionSql.cs:100,103,109,112`）、
   `inbox_messages`（`:106,115`）、`idempotency_records`（`:118,121,124`）。
   `events` 表不在保留策略内。
3. **决策空白**：36 份 ADR 无一份涉及事件日志的存储生命周期（保留/删除/归档）；
   唯一相邻的 ADR-004 只讨论核心类型去留（`docs/decisions/004-core-type-retention.md:44-46`），
   不涉及事件数据本身。

于是「事件日志会不会被清理」这个问题在当前版本里没有答案——使用者只能从代码反推。
审计把「事件流删除或归档 API」列为候选能力（`action-items-2026-11-04-api-gaps.md` §五），
本 ADR 对该候选给出裁决。

## 决策

**事件日志刻意不提供删除、归档、截断 API。** 理由与替代路径：

| # | 理由 |
|---|------|
| 1 | **事件日志是事实记录，不是状态缓存**。Outbox/Inbox/Idempotency 存的是「待办与去重标记」，到期即可丢；事件日志存的是「发生过什么」，删行等于篡改事实。两者共用一套保留策略是概念错误 |
| 2 | **框架不掌握保留期限的业务判据**。合规保留期、审计要求、业务回溯窗口因行业与地区而异；框架给出一个默认删除策略，要么误删合规数据，要么形同虚设 |
| 3 | **删除入口会破坏读侧契约**。`ReadStreamAsync` 的版本序与 `ReadAllAsync` 的位置序建立在「只追加」上；允许删行会引入空洞语义（版本跳跃算不算并发冲突？位置回退怎么解释？），把一个简单可推理的模型复杂化 |
| 4 | **真要收缩存储有正确的做法**：导出到冷存储后由使用者自行处置原始数据（见下） |

**合规删除权（right to erasure）用加密擦除实现**：使用者在写入前对 payload 加密，
删除需求转化为销毁对应密钥——数据仍在库中但不可解，等价于删除，且不破坏日志的
追加语义与版本连续性。密钥管理属使用者责任，框架不提供也不应提供。

**归档/冷存储**：`EventStreamJsonLines` 已提供整流的导入导出
（`src/PalDDD.Projections.EventLog/EventStreamJsonLines.cs:42` 的 `ExportAsync`、
`:85` 的 `ImportAsync`）——导出后可交接给冷存储；框架不承诺行级删除，
故「导出后删除原行」这个动作由使用者在其自有环境执行，不经过框架 API。

**后果（显式声明，不是副作用）**：

- 存储随业务量**单调上升**，无自动收敛。
- 需要控制体积的使用者应在**接入前**决定 payload 保留策略（加密 / 脱敏 / 缩短载荷），
  而不是寄望于事后调用某个删除 API。
- 框架侧若未来要提供任何「收缩」能力，必须先有 ADR 定义空洞语义与位置回退规则——
  在此之前不提供，避免制造一个语义含糊的入口。

## 被否方案

| 方案 | 否决理由 |
|------|---------|
| 给 `IEventLog` 加 `DeleteStreamAsync` / `TruncateBeforeAsync` | 引入空洞语义与位置回退歧义（理由 3）；框架无保留期限判据（理由 2）；删除即篡改事实（理由 1） |
| 把 `events` 纳入 `RetentionSql` 保留策略 | 与 Outbox/Inbox/Idempotency 混淆概念（理由 1、2）；`RetentionSql` 按「终态 + 时间界」删除，而事件没有终态 |
| 提供「归档到外部存储」的框架内建能力 | 归档目标（对象存储/文件系统/冷库）与凭证管理高度环境相关，框架内建会引入新的基础设施依赖面；现有 JSON Lines 导出已覆盖「搬走」这一半，另一半（删原行）刻意不覆盖 |

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| `IEventLog` 仅 3 个成员（Append + 2 Read），无删除入口 | `src/PalDDD.EventLog/IEventLog.cs:14-31` | ☒ |
| 保留清理的 DELETE 只覆盖 outbox / inbox / idempotency 三表 | `src/PalDDD.Transactions/Retention/RetentionSql.cs:100-124` | ☒ |
| `events` 表不在任何保留策略内 | `src/PalDDD.Transactions/Retention/RetentionSql.cs:100-124`（逐行核对无 events） | ☒ |
| 36 份 ADR 无一份涉及事件日志存储生命周期 | `docs/decisions/001-036`（全文检索「事件日志」仅 ADR-004 提及类型去留） | ☒ |
| 帧内已有整流的导入导出能力 | `src/PalDDD.Projections.EventLog/EventStreamJsonLines.cs:42`、`:85` | ☒ |
| 审计把「事件流删除或归档 API」列为不做项 | `docs/review/action-items-2026-11-04-api-gaps.md` §五 | ☒ |

## 开放决策点

无——本决策不改变行为契约，也不新增 API。若未来出现「事件日志体积成为实际障碍」的
消费方反馈，复活条件是：能明确回答「版本空洞是否算并发冲突」与「位置回退如何解释」
两个问题，并据此写出 ADR-038。
