# ADR 027：Outbox 终态批量标记 — IBatchMarkOutboxStore 能力接口 + BatchMarkThreshold opt-in

> 状态：**已采纳（2026-10-14 全量重读性能实施轮 R4，P0-2 / T2.1 收益锚正式立项）；
> PalORM 栈延后段已于 2026-10-16 清算（三方言 until 编码探针实证后实施）**
> 2026-10-19 更新：ADR-033 把 Auto 模式设为默认，并在计数阈值之外增加
> `BatchMarkFlushInterval` 时间上界；本 ADR 的能力接口与 fencing 语义不变，
> `DeliveryMode = Serial` 仍可复现此处的 opt-in 行为。
> 日期：2026-10-14
> 关联：ADR-026（fencing 统一契约——本决策的守卫等价基线）、ADR-011（RequeueDead 幂等前提，
> 同族「at-least-once 内的重复窗口」声明）、T0.2 投递量具（逐条确认串行化占投递耗时 80% @RTT20ms
> 实测锚）、T3.1 MaxInFlightPublishes（同族 opt-in 模式先例）、
> `docs/performance.md`（投递吞吐段）

## 背景

全量重读性能审计（2026-10-14）确认：三栈（Dapper/EF/PalORM）的 Outbox 成功路径终态标记
均为**逐消息一次 DB 往返**（`OutboxBatchProcessor` 循环体逐条 `MarkProcessedAsync`——
Dapper 每条一次 `ExecuteAsync`、EF 每条一次 `ExecuteUpdate`、PalORM 每条一条手写 UPDATE）。
100 条批 = 100 次标记往返。T0.2 投递量具（2026-10-11）实测 RTT20ms 档逐条确认串行化占
投递耗时的 80%——T3.1 流水线化解决了「发布确认」的串行相乘后，「标记」成为同一形态的
剩余串行项（T2.1 收益锚同轮锁定但未立项）。

## 决策

**能力接口 + 处理器 opt-in**（镜像 B-1 `ISagaLeaseBatchStore` 先例）：

1. **新公共能力接口 `PalDDD.Transactions.IBatchMarkOutboxStore`**：单一方法
   `MarkProcessedBatchAsync(IReadOnlyList<OutboxMessage>, DateTimeOffset, CancellationToken)`
   返回受影响行数。实现：`DapperOutboxStore`、`OutboxDbContext` 族（EF）、
   `InMemoryOutboxStore`、`PalOrmOutboxStore`（PalORM 栈 2026-10-16 清算延后段后补齐，
   见「被否决 / 延后方案」段）。
2. **fencing 守卫与逐条路径逐字等价**（非窄化）：按 `RetryCount` 分组单语句
   （组数 = 批内去重重试计数数，常态 1），每行守卫
   `Id ∈ 组内 IN 列表 AND RetryCount == 组快照 AND LockedBy == owner AND LockedUntil == until
   AND Status == Pending`——与逐条 `FencedTarget` / `SqlTemplates.OutboxMarkProcessed`
   的守卫集一一对应。组内 400 行分块（SQLite 参数上限 999 防御）。
3. **同租约前提防御**：批内 `LockedBy`/`LockedUntil` 同值且非空（处理器保证——批输入恒来自
   单次 `LeasePendingMessagesAsync` 结果）；混批 / 未租批抛 `InvalidOperationException`。
4. **内存回写契约**：全命中（affected == 批大小）全套六字段回写（ADR-026 ② 契约）；
   部分拒绝时**零回写**（无法逐行定位被拒者，保守不写——部分拒绝的 DB 真相以返回行数对账）。
5. **处理器 opt-in**：`OutboxOptions.BatchMarkThreshold`（默认 **1 = 逐条路径逐字不变**）。
   `>1` 时成功路径累积到阈值 / 批末（finally，含 OCE 关停路径——已发布消息按逐条路径同
   语义标记）单语句冲刷；失败路径（死信/重试释放）**恒逐条即时**；串行与 T3.1 流水线两
   路径同款；批序 = 租约序（保序声明同 T3.1）。批量冲刷自身失败（DB 故障）镜像逐条路径
   `MarkProcessedAsync` 抛出的处置：逐条退避释放重投。启动期 `ValidateOnStart` 校验 ≥1。

## 消费方指引（本决策的对外契约）

- 默认行为零变化（阈值 1）；启用（建议远程 DB 配 `BatchMarkThreshold` 16~64，与
  `MaxInFlightPublishes` 同档）后标记往返从每消息 1 次降为每批（组）1 次；
- 语义边界：标记时点从「发布后立即」延迟到「满阈值/批末」——崩溃窗口内已发布未标记的
  消息经租约过期重投（**at-least-once 契约内**，重复窗口随阈值增大，消费方应具备 Inbox
  幂等——与 `MaxInFlightPublishes` 的同族声明）；
- 自定义 `IPalOutboxStore` 实现方不受影响（能力接口不实现即自动回落逐条）；
- PalORM 栈消费者：批量路径已可用（2026-10-16 补齐），配置阈值 >1 即走分组单语句；

## 被否决 / 延后方案

- **行值元组 IN（`(id, retry_count) IN ((..),(..))`）**：单语句不分组的理想形态——
  被否决：EF LINQ 不可翻译（匿名类型 Contains 不可靠），跨栈 SQL 形态分叉；分组形态以
  「组数 ≤ 去重重试计数数（常态 1）」的代价换得四实现同构与全守卫保真；
- **EF 侧守卫窄化（丢弃 per-row RetryCount）**：被否决——v37 P3 快照守卫是 ADR-026 ②
  统一契约的组成部分，分组形态可零成本保真，不窄化；
- **PalORM 栈实现（原延后，2026-10-16 清算）**：原延后理由为批量 WHERE 的 `until`
  参数编码必须与上游 PalORM FormattableString 租约绑定**逐字节一致**（SQLite "O" 串 /
  MySQL UtcDateTime / PG 原生——方言陷阱面，T2.2「上游行为验证成本」同族教训）。
  清算方式为三方言 until 编码探针（file-based app，SQLite 本地 + PG/MySQL wslc 外部
  实例，各：租约 3 条 → 回读 until 走候选批量 UPDATE 正腿命中全批 + until±1s / 换
  owner / retry+1 / 已终态四负腿全拒）。实证结论：库内形态分别为 SQLite TEXT
  「…+00:00」（100ns 全精度）/ PG timestamptz（μs，第 7 位被截）/ MySQL DATETIME(6)
  （μs、无 offset 裸 UTC）——三者回读值重编码均精确命中；**同值以 DateTime(Utc)
  形态传递三方言全部拒命**（编码形态敏感，实现须以 DateTimeOffset 传 until，禁
  「简化」为 UtcDateTime）。探针通过后按本决策已定案的分组单语句形态实施，
  `FormattableStringFactory.Create` + `{N}` 索引孔构造动态 IN 列表（PalORM 每孔
  绑一 DbParameter，PalOrmEventLog A11 先例）。
- **默认启用（阈值 >1 为默认）**：被否决——标记延迟语义变化应显式 opt-in（T3.1/C1 同款
  保守先例），默认路径逐字不变是回归安全网。

## 验证（本决策的实现依托）

- 处理器集成：`Transactions.Tests.OutboxBatchMarkProcessorTests`（默认不变/批量激活与
  保序/批末冲刷/失败镜像/流水线路径）；
- 三实现 SQL 正确性：`Integration.Tests.OutboxBatchMarkSqliteTests`（Dapper/EF/PalORM
  SQLite 真库：全批行状态 raw 断言、fencing 部分拒绝、混批/未租防御、全命中回写；
  PalORM 腿另锁定 RetryCount 分组与 400 行分块边界）；
- 方言面：`PalORM.Tests.PalOrmOutboxMultiDialectTests` 三方言各两件（批量正腿 +
  until 前移负腿——SQLite 本地执行，PG/MySQL 经 Testcontainers 由 CI dialect-probe
  车道执行）；PalORM 能力/opt-in 门由 `PalOrmOutboxStoreTests` 断言锁定；
- InMemory fencing：`InMemoryBatchMarkTests`（successor 拒绝、终态清除、null 元素守卫）；
- until 编码实证：三方言探针（2026-10-16，SQLite 本地 + PG/MySQL wslc 外部实例，
  正腿命中 + 四负腿全拒 + DateTime 形态敏感信息项）；
- 公共 API 快照 +7 行纯增量（接口 + 三方法 + 模板常量 + 选项属性，R4）+ PalORM 栈
  方法 +1 行（延后段清算），零删除。
