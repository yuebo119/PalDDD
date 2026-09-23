# 三栈 Outbox 契约矩阵（T-13 · 2026-09-22）

> **定位**：T-15（三栈统一到最强语义）与 T-06（v3.0 承诺兑现）的**前置工作面**。
> 本文件只汇总**既有已核实声明**，不重新推导——每个格都标注证据来源，未核实者显式标出。
> 性质：设计工件（T-13 的交付物），不含代码改动。

---

## 一、为什么需要这份矩阵

`OutboxStore.cs:13-20` 的 Remarks 声明了 v3.0 窗口的两项破坏性变更（ADR-020）：
① 跨栈 fencing 契约统一；② 接口异步化。而**同一接口下的行为差异目前散落在多处注释里**——
契约测试与实现改造都需要一个单一工作面，否则会重复"改一处漏两处"（本仓已实证的形态）。

T-13 的验收（计划原文）："同一套断言跑 InMemory/Dapper/Sqlite/PalORM/EF；目标语义先红后绿"。
本文件是**该断言集的定义面**：先有矩阵，才有可写的断言。

---

## 二、矩阵

### 2.1 `MarkProcessed(message, processedAt)` —— 对**未租约**消息（`LockedBy = null`）

| 栈 | 当前行为 | 证据 |
|---|---|---|
| PalORM | **放行**，且受 affected-rows 门控（fencing 最强） | `OutboxStore.cs` `MarkProcessed` Remarks：ITM-269 声明 |
| Dapper | **放行**，但"内存侧仅清租约字段不回写 Status"（fencing 弱于 PalORM 的 affected 门控） | 同上（ITM-272 **勘正 Dapper 阵营**——原声明把 Dapper 与 PalORM 并列，实测更弱） |
| InMemory | **拒绝**——引用守卫静默 no-op、零变异、零报错 | 同上 |
| EF | **放行**，附两道守卫：须仍为 `Pending`（v43 P2 终态守卫）且 `RetryCount` 与入参快照一致（v30/v33 P3）；持租分支另以 `LockedUntil` 作 **fencing token**（免 DDL 加列） | `OutboxDbContext.cs:110-114`（Remarks）+ `:116-154`（实现，本轮已读） |

**格局修正（本轮读 EF 实现后）**：这不是"三栈分歧"，而是 **PalORM / Dapper / EF 三栈一致
放行（各带门控）+ InMemory 单独拒绝（静默 no-op）**。这一修正**改变了 T-15 的方向**——

**二次修正（本轮读 InMemory 实现后，上一版的"契约要求"自相矛盾）**：

读 `InMemoryOutboxStore.cs:152-172` 后确认：InMemory 的 `return` **不是草率的静默 no-op，而是
fencing 守卫**（ITM-174：被 successor 重租后的旧引用标记静默忽略，不覆盖新持有者状态）——
**与 EF 的 fencing token、PalORM 的 affected-rows 门控目的相同**。且接口 `MarkProcessed` 返回
`void`，故**四栈对"被门控的标记"都是静默的**——"静默"是**统一属性，不是分歧**。

**真正的分歧只有一处**：对**从未被租约**的消息，三栈**执行标记**，InMemory **不执行**：

| | 未租约消息的标记 | 陈旧引用（已被重租）的标记 |
|---|---|---|
| PalORM / Dapper / EF | **成功**（owner-null 分支命中 → UPDATE） | 被门控（no-op） |
| InMemory | **被门控**（`IsCurrentLeaseHolder` 失败 → no-op） | 被门控（no-op）✓ 与三栈一致 |

**这产生一个真实的设计分叉，且"多数派"不等于"最强"**：

- 按"三栈多数"对齐 ⇒ 改 InMemory，允许未租约标记。**但 InMemory 能区分"从未租约"与
  "租约已释放"，三栈的 SQL 不能**（owner-null 分支无法区分）——即 InMemory 的严格性是
  持久化栈做不到的，**它更安全**。
- 按"语义最强"对齐 ⇒ 改三栈，让它们也拒绝未租约标记。**但那需要额外状态**（记录"曾租约"），
  是 schema 级改动。

**故 T-15 的这一格是设计决策，不是实施项**——"统一"的方向取决于你认为哪个语义正确：
三栈的"宽放行"（运维/测试路径可直呼标记）还是 InMemory 的"严拒绝"（未租约即无资格）。
**我不在实施流里替这个决定**：选错方向会改坏已在正确工作的栈（本会话已见 5 次同类误判）。

**另记一条独立观察（非分歧）**：四栈对"被门控的标记"都无返回值/无信号。若要"误用可观测"，
那是**新增能力**（如 `bool TryMarkProcessed` 或诊断计数器），不是对齐既有行为——属独立设计项。

**三次修正（读 `DapperOutboxStore.cs:248-266` 后）：Dapper 的 SQL 门控是齐的，差异在内存写回**

Dapper **传了完整 token**（`owner = message.LockedBy, until = LeaseUntilParam(message),
retryCount = message.RetryCount`）⇒ **SQL 侧 fencing 与 PalORM 同级**——上一版矩阵的
"Dapper 需补齐门控"**不成立**。真正的差异在**被拒标记（affected=0）后的内存写回**，且已有
**显式声明**（`P3-SRC-301`）：

| 栈 | 被拒标记后的入参对象 | 依据 |
|---|---|---|
| PalORM | **不写回**（affected>0 才全套回写）——对象保持"仍持租" | 实现 |
| Dapper | **无条件清租约字段**（`LockedBy=null; LockedUntil=null`）但**不回写 Status** | `:257-265`，P3-SRC-301 声明 |
| InMemory | 门控即 return——对象不变 | `IsCurrentLeaseHolder` 守卫 |

**声明已给出处置**：`P3-SRC-301` 明写"affected 返回值不消费——与原语义一致"，且"调用方
（`OutboxBatchProcessor`）不读该状态故**无实害**"。

**判断**：Dapper 无条件清租约字段意味着——被拒标记后其本地对象**声称租约已释放，而 DB 行
仍持有该租约**。声明说不读故无实害，但这是"声明在、强制未建"的又一处（§1.2）。**对齐做法**
（消费 `affected`，仅 `>0` 时清租约字段）**会推翻该声明**——按 AGENTS.md §3（命中声明注释须
先落决策文档回应，**禁止删声明来"通过"**），须**先写决策文档再改代码，同一提交**。

**故本格转为"需决策文档"**，不在实施流里直接改（直接改它等于删掉声明）。

### 2.2 `SaveChangesAsync()` —— 同一接口名下的语义

| 栈 | 当前行为 | 证据 |
|---|---|---|
| EF | **提交**（真实落库） | 审计 §2.5 第 3 条 + `OutboxStore.cs:85-92` |
| Dapper / PalORM | **no-op**（各自立即写） | 同上 |

**契约要求（目标语义，T-15 执行）**：同一接口成员不得有两种语义。候选方向：① 从
`IPalOutboxStore` 移除该成员，需要提交语义的 EF 栈经**独立接口**暴露；② 三栈统一为提交语义
（Dapper/PalORM 需引入缓冲层，成本高）。**本矩阵不预设选择**——属 T-15 的设计决策，
且改动触及公共 API。

### 2.3 租约（`LeasePendingMessagesAsync`）

| 项 | 状态 | 证据 |
|---|---|---|
| 三栈均实现租约获取 | 一致 | 接口契约 + `OutboxStore.cs` 声明 |
| MySQL 方言租约互斥分叉 | **已知分叉**：Dapper/PalORM 无行锁 ⇒ 存在双执行窗口 | `PalOrmOutboxStore.cs:129-133` 注释自认；ADR-024（2026-09-19 用户裁决：**显式接受**该取舍） |

> ADR-024 已裁决接受该分叉，故**不在 T-15 范围内**——矩阵收录它是为了标明"这一格是
> 已裁决的例外，不是待修项"。

---

## 三、断言的写法要求（供 T-15 直接使用）

1. **同一套断言跑多栈**：抽象夹具 + 逐栈派生（InMemory / Dapper / Sqlite / PalORM / EF）。
   无 Docker 时 Dapper/PalORM/EF 方言路径按 T-17 的既定策略 **skip**（不得硬拒）——
   本地可跑的稳定子集是 **InMemory + SQLite**。
2. **目标语义先红后绿**：断言按 §二 的"契约要求"写，当前行为会红——**红是交付物的一部分**
   （它证明缺口真实存在）。红断言不得直接提交到主干（会打断 CI）；正确做法是在 T-15 的
   同一提交内让实现转绿，或先落 `[Skip]` 并注明"待 T-15 转绿"。
3. **禁止墙钟**：租约过期用注入的 `TimeProvider` 推进（`OutboxProcessor` 已接受该参数，
   见 T-21 定标），不用 `Task.Delay`。

---

## 四、未核实项（诚实声明）

- ~~EF 的 `MarkProcessed` 对未租约消息的行为~~：**已于本轮补齐**（放行 + 终态/快照守卫 +
  `LockedUntil` fencing token，见 §2.1）。**矩阵已无空格。**
- **SQLite 栈**：矩阵按"方言栈"归类（Dapper/PalORM 各有 Sqlite 方言），未单列——其行为
  应与其所属栈一致，但**未经逐方言核实**。
- 本矩阵全部内容来自**代码内既有声明**（ITM-269/ITM-272/ADR-024），未做运行期实测；
  即"声明层一致，实测层未验"。这与本仓 §1.2 主论点的已知缺口同型（声明在、强制未建）——
  T-15 的契约测试正是把声明转为强制的那一步。
