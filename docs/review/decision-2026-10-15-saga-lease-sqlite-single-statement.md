# 决策论证：EF Saga 租约 SQLite 单语句下推（R7/P0-4）

> 编号：DECISION-2026-10-15-saga-lease-sqlite-single-statement
> 基线：commit `1f13cf9` · 日期：2026-10-15
> 性质：实施决策（性能实施轮 R 方案 P0-4 移交项；回应 `SagaStateDbContext.cs` 三处声明注释：
> v23 C1（ITM-261 退让形态）/ v25 P2-1（翻页防饥饿）/ ITM-066（"单语句改造需后续 ADR"））
> 结构约束：本模板三段（消费状态/证据锚点表/开放决策点）为 V11 门禁必填段——
> `dotnet run scripts/verify-conventions.cs -- --quick` 机械校验段存在与锚点格式。

---

## 消费状态

> 决策文档的生命周期账本：落盘 ≠ 可采纳。评审逐行回查锚点表并全部置 ☒ 后，
> 状态从「未评审」流转为「已评审」；开放决策点经用户拍板后为「已裁决」。

| 项 | 值 |
|------|------|
| 落盘日期 | 2026-10-15 |
| 评审状态 | **已实施并验证**（2026-10-15）：回归网三件先行（语句计数判别器在旧码判红：批 5 → 5 条 UPDATE）→ 单语句路径落地 → 判别器转绿（1 条 UPDATE）；ITM-632 用例迁 InMemory（回退路径回归网）；全仓 16 项目面板 1581 = 1509 过 + 72 跳 0 失败；format-verify 与 encoding-gate 绿 |
| 评审记录 | 反方四问在案：①【高】原移交稿写"ExecuteUpdate 四方言"——侦察实证 EF 栈在 rc.2 线无 PG/MySQL provider 包（本地与 CI 均无真库车道），四方言形态**不可验证**；收敛为 SQLite 单语句（可全验）+ 方言分派判定按 provider 名精确匹配（有界扩展面）。②【高】"单语句 + AsNoTracking 回读"照抄 Outbox 先例会**破坏补偿路径**——`ISagaStateStore.SaveChangesAsync(state)` 对 Detached 返回 0（本类显式实现），SagaTimeoutProcessor 依赖租约返回实体可直存；故回读保持**跟踪**并补补偿端到端锁定测试。③【中】raw UPDATE 绕过 ChangeTracker：同 context 预读同批 Saga 的跟踪快照属越界用法（租约声明为 context 首个写操作）；ITM-066/632 的 Detach 兜底对单语句路径不再适用（无部分写入），保留于回退路径不删。④【低】取批序 SagaId vs 姊妹栈 created_at——EF 栈 v23 C1 既定 ULID 序声明内（单进程等价），观测/取批/读回三处同序自洽，锁定测试在案 |
| 裁决记录 | 开放决策点 1-3 按推荐执行（用户连续「继续」授权）：①范围收敛 SQLite 先行、PG/MySQL/SQL Server 留待真库车道（登记在案）；②取批序维持 SagaId；③回读维持跟踪（非 AsNoTracking） |

## 证据锚点表

> 每个可验证的关键声明（数字 / 行为 / 覆盖现状）一行。来源锚 = 仓内 `文件.md:行号`
> 或 `文件.cs:行号`；**查不到仓内出处的声明如实填「仓内未定位」并留 ☐，禁止
> 「审计归档」类无锚引用**。核对列由评审逐行打开来源核对后置 ☒——全部 ☒ 才算消费完成。

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| 旧形态根因：EF SQLite LINQ 不能翻译 DateTimeOffset 有序比较（ITM-261 姊妹），LeasedUntil 过滤被迫物化后内存做、OrderBy 改 SagaId（v23 C1 声明——本案部分回应：手写 SQL 不受 LINQ 翻译限制，该退让仅剩观测查询消费） | SagaStateDbContext.cs:112 | ☒ |
| 旧形态防饥饿翻页循环（v25 P2-1 声明——本案将其语义等价上移进 SQL 谓词，回退路径保留原实现） | SagaStateDbContext.cs:99 | ☒ |
| ITM-066 声明"单语句原子租约改造（SQLite 不支持）需后续 ADR"——本案以 docs/review 决策文档落地（先例：decision-2026-09-17 outbox 同族形态），无需新 ADR | SagaStateDbContext.cs:156 | ☒ |
| 单语句启用判定：仅 `Microsoft.EntityFrameworkCore.Sqlite` provider（精确串匹配，本适配包零 provider 依赖）；非 SQLite（含 InMemory）保底走跟踪分页路径 | SagaStateDbContext.cs:96 | ☒ |
| 新实现：集合式 UPDATE（资格谓词子查询 + ORDER BY + LIMIT + version 同句换代）+(owner, until) 等值守卫回读——整批固定 2 条语句与批次大小无关 | SagaStateDbContext.cs:205 | ☒ |
| SQL 文本构造：标识符取自 EF 模型映射（用户重映射生效），值全部 FormattableStringFactory 参数化（无字面量拼接注入面） | SagaStateDbContext.cs:236 | ☒ |
| 跟踪契约：`ISagaStateStore.SaveChangesAsync(state)` 对 Detached 返回 0——租约回读必须保持跟踪，补偿路径（SagaTimeoutProcessor）依赖 | SagaStateDbContext.cs:324 | ☒ |
| 单语句租约先例：SqliteOutboxDbContext 同族形态已实证（2026-09-19 spike：谓词不恒真 + 等值读回命中；raw SQL 不受 ITM-261 限制） | SqliteOutboxDbContext.cs:123 | ☒ |
| Dapper 模板同形：`SagaLeaseActive` 的 UPDATE…WHERE IN(SELECT…ORDER BY…LIMIT) 恒为本案 SQL 的方言平移源（唯一差异：ORDER BY SagaId vs created_at，见决策点 2） | SqlTemplates.cs:323 | ☒ |
| PalORM 同形三分支（PG SKIP LOCKED / 通用 / MySQL JOIN 两步）——本案仅落 SQLite；PG 同形可平移、MySQL 需 JOIN 形态 | PalOrmSagaStateStore.cs:130 | ☒ |
| 消费方经接口租约（SagaProcessor 租约后按积分支执行/释放；跟踪契约的调用面） | SagaProcessor.cs:122 | ☒ |
| 语句计数判别器：整批 5 行恰 1 条 UPDATE——旧跟踪分页形态实测 5 条判红（红证明在案）、新码 1 条绿 | SagaStateEfCoreLeasePushdownTests.cs:46 | ☒ |
| 语义锁定：活跃租约跳过 / 过期接管（快照 3 → 换代 4）/ AWD 纳入 / 终态排除 / 未触碰行 DB 真值复核 | SagaStateEfCoreLeasePushdownTests.cs:72 | ☒ |
| 饥饿消除锁定：头部 batchSize 条全为活跃租约 + batchSize=1 仍租到窗口外未租 Saga（谓词下推后结构性成立） | SagaStateEfCoreLeasePushdownTests.cs:125 | ☒ |
| ITM-632 幽灵租约保护迁移：SQLite 不再经 SaveChanges（注入点不存在），用例迁 InMemory 锁定回退路径的全批 Detach 保护 | SagaStateEfCoreTests.cs:319 | ☒ |
| EF 栈 PG/MySQL 真库车道"待上游 Provider"既有登记（rc 线无可用 provider 包发布）——本案范围收敛的约束来源 | docs/performance.md:574 | ☒ |
| P0-4 移交原文"ExecuteUpdate 四方言"——本案按可验证性收敛为 SQLite 单语句下推（其余方言留待车道，SQL 形态已备） | docs/performance.md:591 | ☒ |

## 开放决策点

> 需用户拍板的取舍，附推荐项与放弃理由。无则写「无——本决策不改变行为契约」。

1. **范围收敛：四方言一次做 vs SQLite 可验证先行**。推荐：**SQLite 先行**——EF 栈 rc.2 线无 PG/MySQL provider 包（本地/CI 均无车道），方言 SQL 属不可验证面；按验证纪律先落可全验的 SQLite 单语句路径，PG/MySQL/SQL Server 等价下推保留在既有"待上游 provider"登记项内（PG 形态即本案 SQL 换标识符引用，MySQL 需 JOIN 两步——形态已在案，车道开线即可补）。放弃理由：先写不可验证分支即违反「不验证不声称」纪律，且 ITM-659 级守卫族教训（方言 override 漏守卫）表明未实测分支是缺陷温床。
2. **取批序：SagaId（现状） vs created_at（姊妹栈）**。推荐：**维持 SagaId**——EF 栈 v23 C1 声明"ULID 字典序=创建序"（单进程形态），观测/取批/读回三处同序自洽，且 `GetActiveSagasAsync_ReturnsOnlyActiveStatesInCreatedOrder` 已锁定该契约；改 created_at 会使 EF 栈取批序与观测序分叉，且回读侧 LINQ 排序受 ITM-261 限制改不动。行为差异如实记录：Dapper/PalORM 按 created_at 取批，跨栈语义差仅影响"同批超 batchSize 时先取哪些"的组合，不影响正确性。
3. **回读物化：跟踪 vs AsNoTracking（Outbox 先例）**。推荐：**跟踪**——补偿路径 `ISagaStateStore.SaveChangesAsync(state)` 以实体被跟踪为前提（Detached→0 契约），AsNoTracking 会使租约返回实体静默不可存；AsNoTracking+Attach 组合虽可绕过，却引入"同键已跟踪即抛"的额外失败面。跟踪的代价（回读实体进 ChangeTracker）与旧形态一致（旧形态同样全跟踪并额外滞留未选中实体直到 Detach 清理——单语句路径反而更少）。

---

## 0. 结论速览

| 项 | 建议 | 一句话理由 |
|---|---|---|
| **EF Saga 租约 SQLite 单语句下推** | **做**（SQLite 先行，其余方言留待车道） | v23 C1 的翻页物化退化根因是 LINQ 翻译限制而非 SQL 能力——手写 SQL 单语句已是三栈同形契约（Dapper/PalORM/Outbox 先例），SQLite 是 EF 栈唯一可真库验证的方言 |

## 1. 问题

`SagaStateDbContext.LeaseActiveSagasAsync` 因 ITM-261（EF SQLite LINQ 不能翻译 DateTimeOffset 有序比较）对所有方言统一退化为：翻页物化（含大型 JSON 快照列的全实体）× 内存时间过滤 × 逐行 UPDATE + SaveChanges × 头部占满时的 O(跳过页) 翻页。每 tick（SagaProcessor 30s 节拍）批 batchSize 行的成本随表头活跃租约堆积而放大，且未被选中的行也被完整物化（saga_data JSON 等大列）。

根因边界已由同仓先例划清：**ITM-261 只限 LINQ 翻译，手写 SQL 不受限**——SqliteOutboxDbContext 的 2026-09-19 spike 已证 ExecuteSqlAsync 原生 DateTimeOffset 参数与 EF 写入格式自洽（谓词不恒真、等值守卫回读命中），Dapper/PalORM 的 Saga 租约本就以同族单语句 SQL 实现（含 v53 P2"租约即换代"同句 version+1）。

## 2. 方案

**范围（收敛）**：仅 SQLite provider 启用单语句路径（provider 名精确匹配）；其余 provider（含 InMemory）保底走原跟踪分页路径逐字不变。PG/MySQL/SQL Server 下推留待"待上游 provider"车道（形态已备：PG 为标识符引用差异，MySQL 需 JOIN 两步）。

**实现**：`Database.ExecuteSqlAsync` + `FormattableStringFactory` 参数化单条 UPDATE——资格谓词（Status ∈ {Active, AwaitingHumanDecision} 且 LeasedUntil 空/到期）子查询内评估，ORDER BY + LIMIT 取批，`version = version + 1` 同句换代；随后按精确租约标识 (owner, until) 等值守卫回读（保持跟踪——补偿路径契约）。

**语义不变量**：资格集合 / 租约即换代 / 批量上限 / SagaId 取批序 / 补偿端到端（租约返回实体经接口直存）逐项由回归网锁定；原子性由单条 SQL 承担（无部分写入，ITM-066/632 的 Detach 兜底对本案不再需要，回退路径保留）。

## 3. 风险与验证

- **行为差异**（均声明在案）：取批序维持 EF 栈 SagaId 契约（决策点 2，与姊妹栈 created_at 的差异见上）；同一 owner 同 tick 两次租约混批限制（等值回读形态固有，Dapper/PalORM/EF-MySql 同款声明）；raw UPDATE 绕过 ChangeTracker（租约为 context 首个写操作的用法契约；同 context 预读同批属越界）。
- **验证**：语句计数判别器红→绿（旧码批 5 → 5 条 UPDATE 判红、新码 1 条绿）；语义锁定三件 + ITM-632 迁移件；全仓 16 项目面板 1581 = 1509 过 + 72 跳 0 失败（本地实测，测试面含 SQLite 真库）。
- **不触碰**：公共 API（全私有成员，快照零变化）；回退路径实现与原守卫族；PalORM/InMemory/Dapper 栈。
- **Not-Tested 登记**：PG/MySQL/SQL Server 的等价下推（待真库车道）；MySQL 形态需 JOIN 两步（形态设计已定，未实现）。
