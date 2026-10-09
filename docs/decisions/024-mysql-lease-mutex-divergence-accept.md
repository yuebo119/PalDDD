# ADR 024：MySQL 三栈租约互斥分叉 — 显式接受现状

> 状态：**已采纳（2026-09-19 用户裁决「显式接受」）**
> 日期：2026-09-19
> 关联：ADR-020（三栈并行策略）、ADR-017（Saga 租约乐观并发）、ITM-794（首席审计 A-1，
> 论证：`docs/review/audit-2026-09-19-findings-confirmation.md`）

## 背景

MySQL 方言的 Outbox 租约获取在三栈上存在互斥语义分叉（首席审计 A-1，三处 SQL 逐行亲证）：

- **EF 栈**（`MySqlOutboxDbContext.cs:116-127`）：JOIN 派生表内 `FOR UPDATE SKIP LOCKED`——
  并发双 worker 败者租他行，互斥强保证。
- **Dapper 栈**（`SqlTemplates.cs:127-131`）与 **PalORM 栈**（`PalOrmOutboxStore.cs:126-135`）：
  同 JOIN 形态**无锁子句**——语义为 last-writer-wins，且 v43 声明补充：(A写,A读,B写,B读)
  交错时序下后写者覆盖先写者租约并回读整批，构成**双 worker 同批双执行**（重复投递）。

分叉根源是两侧**有意的取舍**（亲证补充，PalORM:129-130）：Dapper/PalORM 弃行锁以避开
MySQL 派生表锁的版本兼容矩阵（8.0.18 以下行为差异）；EF 栈弃兼容面保互斥。

## 决策

**显式接受现状分叉，不统一。** 理由：

1. **两侧取舍各有成立面**：Dapper/PalORM 的兼容矩阵理由真实（8.0.18 以下部署存在），
   统一 SKIP LOCKED 意味着收窄这两栈的部署面——为消解一个已兜底的窗口违反部署承诺。
2. **正确性兜底已分层在位**：fencing token（(locked_by, locked_until) 终态守卫）防双终态写；
   重复投递在 at-least-once 投递契约内，由下游幂等消费兜底（Outbox 契约本身要求消费方幂等）。
3. **统一成本不对称**：三栈行为变更 + 多实例并发回归 + 版本兼容矩阵变更，换取的是
   触发条件受限（同批同 tick 交错）且已有兜底的窗口收窄——成本高于收益。

## 消费方指引（本决策的对外契约）

- **多实例 Dapper/PalORM MySQL 部署**：重复投递率结构性高于 EF 部署——消费方**必须**
  实现幂等消费（本框架 Outbox 契约的既有要求，非新增负担；本决策明确该要求在
  Dapper/PalORM MySQL 场景下不是理论项而是实际项）。
- **需要强互斥的 MySQL 场景**：选 EF 栈（SKIP LOCKED），或升级至 MySQL 8.0.18+ 后
  自行在 Dapper/PalORM 的 SqlTemplates 基础上添加派生表锁（模板是 public const，可复制改造）。
- SQLite/PG 方言不受本决策影响（SQLite 单写者天然互斥；PG 三栈一致 SKIP LOCKED）。

## 补记（2026-10-09）：并发实证与指引补强

> 背景：2026-10-08/09 性能基准系统补齐 Outbox 并发车道（`bench/PalDDD.Benchmarks`
> `--concurrency`/`--persist-mysql`，数字与实录见 `docs/performance.md`「并发车道注」
> 与「PG/MySQL 网络真库基线」段），本决策描述的行为首次获得受控并发实测。
> **本补记只补充风险实证与消费方指引，不改变「显式接受分叉」的决策本身。**

### 实证一：无锁子句形态的并发后果是死锁异常，不止重复投递窗口

本决策背景段记录的风险形态是 last-writer-wins 下的**重复投递窗口**。实测（MySQL 8.4.11，
wslc 容器，N=2/4/8 worker 并发租约，800 条种子/波）：**Dapper 与 PalORM 两栈的并发租约
全部以 InnoDB 死锁异常失败**（`Deadlock found when trying to get lock`，两轮共 8 处实录），
而非静默双执行——无锁子句形态在并发写竞争下先触发死锁检测，异常中断先于重复投递发生。
消费方含义：多实例 Dapper/PalORM MySQL 部署除幂等消费外，**还必须对死锁异常
（MySqlException 0x80004005）实现重试**，否则发布循环中断。

### 实证二：EF 栈 SKIP LOCKED 形态的互斥声明获验证

本决策指引「需要强互斥的 MySQL 场景选 EF 栈」的验证缺口（2026-10-09 之前无并发数据）
已闭合：EF 栈步骤 1 租约 SQL（JOIN 派生表 `FOR UPDATE SKIP LOCKED`）在 MySQL 8.4.11
直跑实测（形态逐字保留，标识符适配基线表）——**N=2/4/8 全部零死锁、UPDATE 受影响行数
之和恒等于种子数（互斥不变量全波通过）、串行 92ms → 8 worker 53ms 真加速**。
注：EF 栈级基准车道待上游出 EF 11 rc 配套 Provider（Npgsql.EF 11 线仅 preview 且钉死
EF preview 精确版本、Pomelo 无 11 线，2026-10-09 实证）后可升级；SQL 形态车道见
`MySqlSkipLockedShapeBenchmarks` 类头注。

### 指引补强（在原三 bullets 之上追加）

- **多实例 Dapper/PalORM MySQL**：幂等消费（原要求）**+ 死锁重试**（本补重新增，见实证一）；
  或直接采用实证二验证过的 EF 形态锁（自加派生表锁改造模板即可，见原指引第二条）。

## 被否决方案

- **统一为 SKIP LOCKED**（ITM-794 原始选项 A）：收窄 Dapper/PalORM 的 MySQL 版本
  兼容面 + 三栈行为变更回归成本——被"部署承诺优先"否决。
- **Dapper/PalORM 加可选锁子句参数**：配置面爆炸（三栈 × 有锁/无锁 × 方言），
  且默认值无论怎么选都改变现有消费者行为——被复杂度否决。

## 后续动作

- ITM-794 关闭（本 ADR 即裁决）；ITM-807（三栈谓词对照测试）仍执行——对照测试固化
  **现状**形态（含本分叉作为白名单项），防的是"无意的"漂移而非本决策接受的分叉。
- PalORM/Dapper 的 v13/v43 分叉声明注释与本 ADR 互为印证，无需修改。
