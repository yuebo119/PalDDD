# ADR 026：v4.0 契约窗口总规划 — 接口异步化 / fencing 统一 / scope 免除 / CT 传播

> 状态：**已采纳（2026-10-10 用户裁决——开启 v4.0 契约窗口实施）**
> 日期：2026-10-10
> 关联：ADR-020（v4.0 破坏性变更排队的原始裁决，其 Dapper 退役线已被 2026-09-13 修订取消——
> 四栈并存是既定前提）、ADR-025（MySQL 租约双模板）、`DECISION-2026-09-22-dapper-markprocessed-lease-clear`
> （Dapper MarkProcessed 内存回写形态——本 ADR 完成其 ITM-130 意图的另一半）、
> `docs/performance.md`（优化轮一~四：scope 免除与 CT 的实证依据）
> 锚点：`src/PalDDD.Transactions/Outbox/OutboxStore.cs`（接口契约注）、`src/PalDDD.CQRS/Dispatcher.cs`、
> `src/PalDDD.Dapper/DapperOutboxStore.cs`

## 背景

ADR-020 在 `IPalOutboxStore` 的接口 remarks 排队了两项 v4.0 破坏性变更（跨栈 fencing 契约
统一、同步方法异步化），2026-09-13 修订取消 Dapper 退役后这两项继续有效（四栈并存使契约
统一更重要而非更不重要）。2026-10 优化轮新增两项登记：Dispatcher 的 per-request scope
分配（零行为 160B/op 的剩余大头，免 scope 需公共 API）与 `AddMessagesAsync` 的
CancellationToken 缺失（v8 声明的已知契约限制）。

破坏面清单（2026-10-10 grep 实测）：四个同步方法（`AddMessage` 79 处 / `MarkProcessed`
29 处 / `MarkDead` 10 处 / `ReleaseForRetry` 11 处，共约 129 个调用点）分布在 20+ 文件
（四栈实现、OutboxBatchProcessor、EF 拦截器、基准、伪 store、测试面板）。

## 决策

v4.0 契约窗口执行以下四项，按破坏面升序落地，每项独立提交与验证（红→绿锁定 + S3）：

### ① scope 免除：Dispatcher 实例注册（新增公共 API，向后兼容）

新增 `Dispatcher(IServiceProvider rootProvider)` 构造重载与
`Register<TRequest, TResponse, THandler>(THandler handlerInstance)` 实例注册重载。
实例注册的执行路径**不经 per-request scope**：handler 即注册实例；行为（pipeline
behaviors）从根容器解析。契约约束（XML doc 显式声明）：实例注册隐含「handler 无 scoped
依赖、行为以单例注册于根容器」——scoped 注册的行为与 handler 依赖在该路径不可见
（根容器解析语义）。DI 注册形态（现有 `Register<TReq,TResp,THandler>()`）路径不变，
仍走 per-request scope。收益依据：优化轮实测零行为 160B/op 中 scope 为最大剩余项，
实例路径预期降至 ~64B 级（结果对象 + ValueTask 基础设施）。

### ② fencing 统一：内存回写语义跨栈对齐（消费者可见的行为变更）

统一契约为「PalORM/InMemory 形态」：**门控拒绝（租约 token 失配/非当前持有者）→ 零
内存变异零 DB 写；成功（affected>0）→ DB 全套更新 + 入参对象全套六字段回写
（Status/ProcessedAt|DeadAt|NextAttemptAt/Error/LockedBy/LockedUntil）**。
变更点：Dapper 三方法（MarkProcessed/MarkDead/ReleaseForRetry）成功分支从「仅清
LockedBy/LockedUntil 两字段」升为全套回写——完成 DECISION-2026-09-22 声明的 ITM-130
「三姊妹对象字段语义对齐」意图的另一半；该决策的「调用方不读入参」前提保持成立
（OutboxBatchProcessor 只计数），全套回写使应用层自定义处理器读入参不再见半态。
EF/PalORM/InMemory 已是该形态（验证后不改）。SQL 模板不变（DB 侧本就全套 SET）。

**实施期 refinement（2026-10-10，测试实证）**：真值表比规划时更三向——Dapper MarkDead
原连 affected 都不消费（token 拒绝后仍无条件清入参租约，守卫缺口级分叉，同轮收口）；
EF 主路径原零入参回写且其 ReleaseForRetry remarks 声明了跟踪实体并发令牌假冲突保护
（既有测试 `ReleaseForRetry_ClearsLeaseAndSchedulesNextAttempt` 在首轮实现下实证了该
冲突）。EF 栈最终形态：**跟踪态守卫——仅 Detached 快照回写**（主消费者 OutboxBatchProcessor
传 AsNoTracking 租约快照，统一契约在主路径成立；跟踪实体自动跳过回写保持令牌安全）。

### ③ 接口异步化：IPalOutboxStore 四同步方法改异步签名（破坏性变更）

```csharp
ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default);
ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default);  // ④ 补 CT
ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default);
ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default);
ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default);
```

同步四方法**删除**（非 Obsolete 过渡——v4.0 major 窗口一次清偿，迁移指引入 CHANGELOG）。
动机：消除 PalORM 栈 7 处 sync-over-async（其同步方法内部对 DataSession 异步原语阻塞桥接，
v4.0 删除后该桥接面清零）。各栈形态：Dapper 内部改真异步 ADO（ExecuteAsync 族）；EF
沿 EF 异步 API；InMemory 返回同步完成 ValueTask；PalORM 直接对接 session 异步原语。
**EF 拦截器同步钩子（SavingChanges）的处置**：保留支持，经单点同步阻塞桥
（`GetAwaiter().GetResult()`）调用异步接口——桥只此一处且显式文档化，无同步上下文的
宿主（ASP.NET Core/控制台）安全；classic sync-context 宿主需改用 SaveChangesAsync
（迁移指引注明）。被否决形态见下。

### ④ CT 传播：AddMessagesAsync 补 CancellationToken（破坏性变更，随 ③ 一并）

接口签名加 `CancellationToken ct = default`；`IImmediateWriteOutboxStore` 同步重声明
对齐；调用方（拦截器批量路径经 v4.0 CT 不可达处传 default 并注释，处理器/基准传播真实
token）。v8 声明的「接口异步化属 v4.0」即指本项与 ③ 合并执行。

### 实施序与提交策略

按破坏面升序：①（新增面，零破坏）→ ②（行为变更，单栈三方法）→ ③+④（签名破坏，
129 调用点全链）→ 部署旋钮指南（文档，非窗口项但同批）。每项独立提交，CHANGELOG
`[Unreleased]` 以 **[Breaking]** 标注累积，发布时整体转正为 `[4.0.0]`（SemVer Major：
③④ 为签名破坏）。公共 API 快照逐项再生。

## 理由

1. **契约统一的维护成本实证**：ADR-020 背景记录「姊妹修复在 40+ 轮提交中反复出现」——
   fencing 分歧与同步/异步分叉是同一抽象下双标准的结构性成本，四栈并存下只有契约层
   收敛能根治。
2. **异步化收益的量级依据**：PalORM 7 处 sync-over-async 是该栈远程吞吐劣势的组成部分
   （`docs/performance.md` PalORM 栈注）；拦截器异步路径逐条同步写在远程库上的往返级
   线程阻塞（PG ~609μs/条）在优化轮四已实证并部分消除，接口异步化把同类问题在
   处理器/调用方面清零。
3. **scope 免除的实证依据**：优化轮 C5/P8 探针后零行为 160B 的构成分析——scope 创建
   为最大剩余项；实例注册以 opt-in API 提供免 scope 路径而不触碰默认 DI 语义。
4. **决策时机**：四项已在 remarks/登记中排队多轮，2026-10 优化轮提供了 fence/scope/CT
   的完整实证依据；继续推迟只增加新旧分叉共存时间。

## 后果

- 正面：跨栈行为契约单点化（fencing/内存回写）；sync-over-async 桥接面从 7+2 处收敛到
  1 处显式声明；Dispatcher 提供免 scope 高速路径；CT 传播补齐。
- 负面/迁移成本：实现 `IPalOutboxStore` 的应用层自定义 store 需迁移四方法签名（迁移
  指引入 CHANGELOG：机械重命名 + await 化）；调用同步 `SaveChanges()` + 拦截器 +
  classic sync-context 的组合不再适用（改用 SaveChangesAsync）；Dapper 三方法成功分支
  内存回写变全（读取入参 Status 等字段的自定义代码行为变化——文档标注）。
- 后续动作：①~④ 落地后 `docs/usage.md` 迁移段、README 栈表注同步；ADR-020 remarks 的
  v4.0 预告指针更新为指向本 ADR。

## 被否决方案

1. **同步钩子直接抛 NotSupportedException**：破坏 EF sync `SaveChanges()` 用户且回退
   拦截器 P1 修复（sync 路径静默不写事件的原始缺陷）；单点阻塞桥 + 文档边界是更小的
   破坏面。
2. **同步方法 Obsolete 过渡一个版本**：四栈实现需同版本维持双面（sync+async）8 个方法
   ×4 栈的姊妹维护成本，恰是本 ADR 要消除的成本形态；major 窗口一次清偿符合 ADR-020
   原始裁决口径。
3. **接口保留同步 + 异步双面（不删同步）**：PalORM 的 sync-over-async 根因无法消除
   （同步面仍在就必须提供阻塞桥），异步化目标落空。
4. **fencing 统一向 Dapper 两字段形态收敛**：与 ITM-130 既定意图相反，且两字段半态
   （DB 全套更新而内存只清租约）是四栈中最弱的观察一致性。
