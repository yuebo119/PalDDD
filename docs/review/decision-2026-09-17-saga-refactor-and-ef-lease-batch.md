# 决策论证：Saga 四车道重构 · EF SQLite 租约批量化

> 日期：2026-09-17 · 基线：`3c887c7`（审计清偿后）
> 性质：分析与建议，**未改代码**。证据均引自当前树与仓存基准。

## 消费状态

| 项 | 值 |
|------|------|
| 落盘日期 | 2026-09-17 |
| 评审状态 | 已裁决（2026-09-18 人工交叉核对 + 反方角色代理实证评审 + 用户整体采纳） |
| 评审记录 | 反方实证四发现：① 方案 B「`"O"` 文本序比较」**实证为错**——Microsoft.Data.Sqlite 11.0.0-rc.1（本仓 Directory.Packages.props:54 钉扎版）把 DateTimeOffset 列写为空格分隔格式，`"O"` 参数使资格谓词恒真（错租，正确性级非性能级）；主线程已独立复现（原生参数命中正确）。② 基准区间上下界仓内有锚（audit-2026-09-15-full.md:290），系 09-13/09-15 两次运行拼接，且该审计 Ratio 5.04 与其引用数字 24,043.8/2,655=9.06 不自洽，验收基线须先复测。③ §2.2 示例 SQL 缺 `ORDER BY`，与三栈已验证版本（SqlTemplates.cs:157 等）不符。④ §2.4 引 ADR-020 v3.0 排期，但该时间表已在 ADR-020:63 取消。①③④已按裁决修订入正文 |
| 裁决记录 | 2026-09-18 用户整体采纳六项：①③④ 正文修订（原生参数主路径 shape 2 / 补 ORDER BY / ADR-020 引用修正与结论强化）；4.1 表征测试本轮开做；4.2 接受 raw SQL；4.3 验收 = medium 复测基线 + 比值 ≤2× + <8ms 退出条款；4.4 不引入事务包裹（单语句原子性）；4.5 ChildSaga 开 ITM-787。遗留：ORM 优化旧清单「批量 lease 不做」为本裁决翻案，已在 open-items E 节记录 |

---

## 0. 结论速览

| 项 | 建议 | 一句话理由 |
|---|---|---|
| **Saga 四车道完整重构** | **先表征测试，再抽骨架；禁止整管线重写** | 3/4 车道在 `ProcessEventAsync` 层几乎零覆盖，现在重构是「只 Normal 被证明」的无网高危动作 |
| **EF SQLite 租约批量化** | **spike 前置后可做（非破坏性）；时间参数直传原生 DateTimeOffset；不必等 v3.0** | N+1 是「修复形态」副作用，不是 SQLite 语义必需；Dapper/PalORM 已在同方言用单语句证明正确性；`"O"` 文本参数路径已实证排除（谓词恒真，见锚点表） |

---

## 1. Saga 四车道完整重构

### 1.1 问题是否真实

**是。** 四条车道共享同一控制流骨架（自承复制于 `Saga.cs:540-541, 641-642, 899-900`）：

```
failures[] → SafeObserveStarted
for attempt 0..MaxRetries:
  try: Stopwatch → execute → metric → RecordExecutedStep → ObserveCompleted → return
  catch OCE: rethrow
  catch (attempt < MaxRetries): failures.Add + Delay(ComputeRetryDelaySafely)
  catch 非 OCE: failures.Add → ObserveFailed
    → try Compensate → 补偿异常并入 AggregateException（“and compensation also failed”）
    → 否则 AggregateException(步骤失败)
```

| 车道 | 位置 | 真正独特处 |
|---|---|---|
| Normal | `:408-475` | `step.ExecuteAsync`；metric 用 `result.Status` |
| FanOut | `:481-559` | `ExecuteFanOutAsync`；`!AllSucceeded` 抛 AggEx；metric 用 `current.Status` |
| ChildSaga | `:567-660` | 解析 + 每 attempt 重建 child state + `ApplyOutput`；**唯一反射面** |
| Dynamic | `:796-918` | 循环前 Route/未知 key/ITM-069/ITM-775；**补偿用 matchedKey，观察者用 stepKey** |

每次补偿失败嵌套修复必须落四遍——这是已经付过的维护税。

### 1.2 为什么「死信安全网」不足以直接开工

刚落盘的死信测试覆盖的是 **Outbox** 处理器（`OutboxBatchProcessor`），不是 Saga 车道。

**Saga 车道覆盖现状（ProcessEventAsync 入口）：**

| 车道 | 覆盖 | 证据 |
|---|---|---|
| Normal | **重**（~15+） | `SagaTests.cs:286-365,410-624` |
| FanOut | **≈0** | `FanOutStepTests` 只测 `ExecuteFanOutAsync` 单元，不进车道 |
| ChildSaga | **0** | 全 `test/**` 无 ChildSaga 命中；源码自承「测试覆盖 0% 掩盖至今」 |
| Dynamic | **部分**（路由拒绝/超时，非重试补偿） | 无 matchedKey 补偿、无耗尽补偿嵌套 |

**结论：** 现在抽骨架 = 在未锁定的行为上做机械移动。Dynamic 的 matchedKey vs stepKey、ChildSaga 每 attempt 重建、FanOut 部分失败 AggEx——任一静默漂移都会通过「全绿」。

### 1.3 三个方案

| | A. 类内私有 `RunRetryLaneAsync` | B. 独立 `RetryLaneExecutor` | C. 整管线重写 |
|--|--|--|--|
| 规模 | Saga.cs 内 ~250-320 行，净删 ~150-180 | ~350-400 + 新类型 + 接线 | 600+，含 Interrupt/Timeout/Manager |
| 行为风险 | 中（须原样保留 catch 顺序与消息族） | 中+生命周期/接线 | **高** |
| 收益 | 立刻消四份复制 | 可单测骨架 | 理论优雅，代价不成比例 |
| 建议 | **采纳** | 仅当出现第 5 种 DispatchKind | **否决** |

### 1.4 推荐路径（强制前置）

**Phase 0 — 表征测试（必做，独立提交）**

为 FanOut / ChildSaga / Dynamic 各补：

1. 成功路径（事件进 `ExecutedStepKeys` 的 key 正确）  
2. 重试耗尽 → 补偿 + `AggregateException`  
3. 补偿自身再失败 → “and compensation also failed” 内层并入  
4. Dynamic 专项：失败补偿 **matchedKey**，观察者事件仍归 **stepKey**（P3-SRC-603）  
5. Dynamic：Route 抛 → 不进补偿；未知 key → 静默 `return current`  
6. ChildSaga：每 attempt 新 child state（两次失败可断言状态不复用）

断言策略：类型 + `InnerExceptions.Count` + 关键消息子串；避免锁死整句英文文案。

**Phase 1 — Option A 渐进**

1. Normal 先迁（测试最厚，作证明）  
2. FanOut → ChildSaga（setup / per-attempt body 留在车道内）  
3. Dynamic：循环前逻辑**留在车道**；骨架只包重试补偿；`compensate` 回调参数化 `(failedKey, failedStep)`  
4. **同 commit 禁止**改：`HandleEventAsync`、`SagaCompensation`、`SafeObserve*`、ChildSaga 反射助手、补偿顺序语义  
5. 注释四份「见 ExecuteNormalStepAsync」收敛为一处指针（三方一致）

**明确不做（本轮）**

- 抽 ChildSaga 反射到共享层 / 工厂委托（AOT 拆分是另一议题，需独立 API 设计）  
- 改 AggregateException 文案语义  
- 与 Interrupt 超时兜底同 PR  

### 1.5 工作量与风险

| 阶段 | 量级 | 风险 |
|---|---|---|
| 表征测试 | **M–L**（半天–1 天） | 低（纯加测） |
| Option A 迁移 | **M**（半天）+ 全量测试 | 中（行为保真靠 Phase 0） |
| Option C | XL | 高 — **不推荐** |

### 1.6 最终裁决

| 问题 | 答案 |
|---|---|
| 值得做吗？ | **值得**——四份控制流复制是真实维护税 |
| 现在直接改骨架吗？ | **否** |
| 前置条件 | FanOut/ChildSaga/Dynamic 的 ProcessEventAsync 级表征测试全绿 |
| 死信安全网够吗？ | **不够**——那网在 Outbox，不在 Saga |
| 与 AOT 拆分关系 | 解耦：骨架重构不扩大反射面，也不解决整包 `IsAotCompatible=false` |

---

## 2. EF SQLite 租约批量化

### 2.1 问题是否真实

**是，且可量化。**

- 现状：`SqliteOutboxDbContext.cs:108-128` 候选页后 **逐行** `ExecuteUpdateAsync`  
- 仓存 BDN（`docs/review/bench-baseline-2026-09-13.md:16` 及审计归档）：

| 操作 | EF SQLite | Dapper | PalORM |
|---|---:|---:|---:|
| Lease Batch100 | **15.7–24.0 ms** | 2.7–3.1 ms | 3.5–4.0 ms |
| GetPending Batch100 | ~0.5 ms 级 | 同量级 | 同量级 |

GetPending 只约 2×，**爆炸点专在 100 次 UPDATE 往返**。热路径轮询 500ms，表增长时代价线性上升。

### 2.2 关键论证：逐条 CAS 是「修复形态」，不是 SQLite 必然

旧实现「SELECT 跟踪 → 内存改 → SaveChanges」三步分离，双实例可同时租同一批（`SqliteOutboxDbContext.cs:7-13`）。逐条 CAS 把资格检查放进每条 UPDATE——**这是对的**。

但 **同一语句内重估资格** 已足够，且三栈已在生产证明：

```sql
UPDATE outbox_messages
SET locked_by=@owner, locked_until=@until
WHERE id IN (
  SELECT id FROM outbox_messages
  WHERE status=0 AND retry_count < @n
    AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
    AND (locked_until IS NULL OR locked_until <= @now)
  ORDER BY created_at LIMIT @batch)
```

- SQLite WAL 单写者，语句原子  
- 并发租约使子查询谓词对已租行失效 → 影响 0 行  
- **与 Dapper SQLite / PalORM SQLite / MySQL EF 同构**（含 `ORDER BY created_at LIMIT`）  
- 时间参数**直传原生 DateTimeOffset**：Microsoft.Data.Sqlite 对原生参数的谓词行为正确（2026-09-18 主线程复现实验：past/future 各一行，原生参数命中 1 行、`"O"` 文本参数命中 2 行恒真）  
- 因此：N+1 是当年正确性修复的**形状选择**，不是方言硬约束  

反驳「批量 lease 风险>收益」旧清单：该结论针对**削弱 fencing**；本方案 **不削弱**，只是把资格检查从 N 条语句收成 1 条。

### 2.3 方案对比

| | A. LINQ `Id IN` + 等值守卫 | B. Raw SQL IN-subquery（推荐） | C. SELECT+N 事务包裹 | D. EF 内混用 Dapper |
|--|--|--|--|--|
| 正确性 | 未租 `LockedUntil==null` OK；过期租约需按原值分组；ITM-261 禁止 `LockedUntil<=now` 下推 | 与 Dapper/PalORM 同契约；时间参数**直传原生 DateTimeOffset**（`"O"` 文本参数已实证排除——谓词恒真） | 同 CAS，另加批次原子性（改变现有 delay-not-loss 语义） | **否决**（违背 ADR-020 三栈独立） |
| 往返 | 1–几 + 可能回读 | **1**（RETURNING）或 **2**（UPDATE+readback） | 仍是 N | — |
| 预期 | 全 null 场景接近 Dapper | Lease 预计 ~5–8 ms（约 2× Dapper） | 远达不到 2.6 ms | — |
| 风险 | 中（部分竞态返回集） | **中**（正确性级坑已识别且有规避路径，方言层细节须 spike 锁定；EF1002；参数/ct） | 低风险低收益 | 架构风险 |

**推荐 B**，形态优先：

1. **主路径（shape 2）**：`ExecuteSqlRaw` + `(LockedBy, LockedUntil)` 等值守卫回读（MySQL EF 同款两步形态，ITM-109 姊妹先例）  
2. 探索项（shape 1）：`UPDATE … WHERE id IN (SELECT …) RETURNING *` 单语句物化——`FromSqlRaw` 能否物化 UPDATE…RETURNING 由 spike 验证，成则省一次往返  

**Fencing 契约不变：** 仍用返回的 `LockedUntil` 走 `FencedTarget`；不改 DDL、不改公共 API。

### 2.4 与 open-items / v3.0 的关系

| 已登记项 | 是否覆盖本项 |
|---|---|
| B1 ITM-672 EF Pooling | 否（池化，非租约批量化） |
| B2 Outbox 异步化 + fencing 统一 | 否（API/签名窗口） |
| ADR-020（v3.0 退役时间表已取消，Dapper 为平等第三栈） | 否 |

**本项未被排期，也不需要等 v3.0**——非破坏性、可进 minor；且 v3.0 时间表已在 ADR-020 取消，「等窗口」已无对象。

### 2.5 实验设计（实施门槛）

1. **Spike（可丢弃，两项）**：① EF 层原生 DateTimeOffset 参数传递形态——provider 层已实证原生参数谓词正确，待证 EF（`ExecuteSqlRaw`/`ExecuteUpdateAsync`）是否原样传递不转字符串；② `FromSqlRaw` 能否物化 UPDATE…RETURNING（shape 1 探索项，成则省一次往返）  
2. **正确性测试（必过）**  
   - 双 owner 并发租约：交集为空  
   - 过期租约回收  
   - 批中取消：不重复、不丢失（delay 语义保持）  
   - 重租后旧 worker `Mark*` 影响 0 行（fencing）  
   - ITM-109 同 tick 回读边角（姊妹栈已接受）  
   - 时间列写入偏移一致性（lease 链路恒 `GetUtcNow()` +00:00，验证无外部偏移混入）  
3. **BDN 验收（三段式，2026-09-18 裁决）**：① before 锚 = 实施首步 `--job medium` 跑 `Outbox_Lease_Batch100`（EF/Dapper 同 run）——历史两次 ShortRun 运行差 53%，拼接区间不作基线；② 标准 = 比值 **EF/Dapper ≤ 2×**（比值同 run 环境因子自抵消；百分比改善可被基线选择操纵，弃用）；③ 退出条款 = medium 复测后 EF 基线自身 <8ms 则 N+1 在严谨口径下不显著，重新评估 P-1 严重度后再决定实施  
4. **P-2 独立**：`QueryEligibleAsync:51-69` 整表分页**不会**被批量化修好，需另案（ITM-261 限制下的内存过滤）  
5. 批次原子性**裁决不引入事务包裹**（2026-09-18）：批量化后整批为单条 UPDATE，SQLite 语句级原子性即 all-or-nothing——「批中第 k 条失败」形态不复存在，原两难自动消解；shape 2 回读窗口由 `(LockedBy, LockedUntil)` 等值守卫过滤（ITM-109 姊妹形态），不混入他实例新租行

### 2.6 最终裁决

| 问题 | 答案 |
|---|---|
| 值得做吗？ | **值得**——热路径 5×，形状已被三栈证明 |
| 风险高吗？ | **中**（正确性级坑——`"O"` 谓词恒真——已识别且规避路径经实验验证；方言层剩余细节由 spike 锁定 + 并发/fencing 测试 + BDN 三段式验收） |
| 必须等 v3.0 吗？ | **不必**（v3.0 时间表已取消，无窗口可等） |
| 必须先有基准吗？ | **必须**——`--job medium` 复测单次自比为唯一合法基线（2026-09-18 裁决），拼接区间不作数 |
| 与 CAS 正确性冲突吗？ | **不冲突**——资格检查语句内重估，严格对齐 Dapper/PalORM |

---

## 3. 两项对比与排期建议

| 维度 | Saga 四车道 | EF SQLite 租约批量化 |
|---|---|---|
| 收益类型 | 可维护性（防四遍同步税） | 运行时性能（热路径 5×） |
| 用户可感知 | 间接 | **直接**（Outbox 轮询吞吐/延迟） |
| 前置成本 | 表征测试 **高** | Spike + 并发测试 + BDN **中** |
| 无网风险 | **高**（3/4 车道未锁定） | **中**（有姊妹栈 SQL + 既有并发测试可扩） |
| 是否阻塞其他项 | 否 | 否 |
| 建议窗口 | **独立里程碑**：表测 → 抽骨架 | **可与表征测试并行**，或紧随其后 |

**推荐执行顺序：**

```
并行轨 A（性能）          并行轨 B（可维护性）
─────────────────         ─────────────────────
1. medium 复测定基线        1. FanOut/ChildSaga/Dynamic
2. Spike 参数形态+RETURNING  表征测试（含 ITM-787）
3. 并发+fencing 测试        2. 确认全绿
4. BDN 三段式验收          3. Option A 从 Normal 起迁
5. 合入 minor             4. 同 PR 禁扩面
```

**刻意不修 / 推迟：**

| 不做 | 理由 |
|---|---|
| Saga Option C 全管线重写 | 回报与 Interrupt/超时回归风险不成比例 |
| 本轮拆 ChildSaga AOT | 需 API/工厂设计，与骨架重构正交 |
| 仅靠事务包裹「修」EF 租约 | 不解决 N 往返，达不到 2× 目标 |
| EF 栈内调用 Dapper 租约 | 破坏 ADR-020 三栈边界 |
| 把 P-2 整表分页与 P-1 捆一起做 | 不同根因；捆做会拖垮可验收性 |

---

## 4. 开放决策点（已裁决 2026-09-18）

1. **Saga 表征测试是否本轮立刻开做？** → **是**。覆盖现状声明经反方评审逐项核实，Phase 0 清单不变；并行轨 A/B 设计维持  
2. **EF 租约批量化是否接受 raw SQL 逃逸 EF LINQ？** → **接受**。仓内 EF 栈已有先例（MySqlOutboxDbContext 单语句 JOIN-UPDATE）；主路径 shape 2（`ExecuteSqlRaw` + 等值守卫回读），时间参数直传原生 DateTimeOffset  
3. **验收是否锁定「Lease ≤ 2× Dapper」？** → **锁比值 ≤2×**，且基线 = `--job medium` 复测单次自比（三段式见 §2.5 第 3 条，含 <8ms 退出条款）  
4. **批次原子性（方案 C 事务）要不要？** → **不要**。批量化后单条 UPDATE 语句级原子即 all-or-nothing，原两难自动消解（见 §2.5 第 5 条）  
5. **ChildSaga 测试 0% 是否单独开 ITM？** → **是，立即**。已登记 ITM-787（open-items-2026-09-14 B6），与表征测试 Phase 0 合并排期但独立立项——即使骨架重构推迟，测试强制做  

---

## 证据锚点表

> 2026-09-18 评审补录：核对 ☒ = 已打开来源逐项比对属实（含核对结果为否定的行）；☐ = 待查项。

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| EF Lease Batch100 15.7ms ≈ 5× Dapper（3.1ms）/ PalORM（4.0ms） | bench-baseline-2026-09-13.md:16 | ☒ |
| GetPending EF 约 2×（970 vs 507us） | bench-baseline-2026-09-13.md:15 | ☒ |
| EF Lease 区间上界 24.0ms / Dapper 下界 2.7ms / PalORM 3.5ms | audit-2026-09-15-full.md:290 | ☒ |
| 区间端点系 09-13 与 09-15 两次独立运行拼接（EF 差 53%，非同一测量误差条）；审计 Ratio 5.04 与其引用数字不自洽（实为 9.06） | audit-2026-09-15-full.md:290 | ☒ |
| EF SQLite 租约为逐行 CAS（N 次 UPDATE 往返） | SqliteOutboxDbContext.cs:108 | ☒ |
| ITM-261：DateTimeOffset 仅有序比较不可翻译、等值可翻译 | SqliteOutboxDbContext.cs:26 | ☒ |
| P-2：QueryEligibleAsync 稳态全表分页 | SqliteOutboxDbContext.cs:51 | ☒ |
| PalORM SQLite 单语句 `UPDATE…IN(SELECT…LIMIT) RETURNING` 形态 | PalOrmOutboxStore.cs:115 | ☒ |
| Dapper SQLite IN 子查询 + (locked_by, locked_until) 回读形态 | DapperOutboxStore.cs:171 | ☒ |
| 四车道位置 `Saga.cs:408-475/481-559/567-660/796-918`、分派入口 298-320 | Saga.cs:408 | ☒ |
| Normal 覆盖厚（重试/补偿测试群）；FanOut 仅单元级、ChildSaga 零命中、Dynamic 仅路由拒绝+超时 | SagaTests.cs:286 | ☒ |
| Dynamic 补偿用 matchedKey、观察者归 stepKey（P3-SRC-603） | Saga.cs:874 | ☒ |
| 原稿方案 B 时间参数用 `"O"` 文本序比较——**已实证否定**（provider 写空格分隔格式，`"O"` 参数谓词恒真；正文已改为直传原生 DateTimeOffset 参数，主线程复现验证） | SqlTemplates.cs:140 | ☒ |
| **before 锚（medium 复测，2026-09-19）**：Dapper 2.919ms / PalORM 2.465ms / EF 13.79ms（MediumRun，15 iter / 2 launch / 10 warmup，EF Error ±1.254ms）——比值 **EF/Dapper = 4.72×**；**退出条款未触发**（EF ≥ 8ms，P-1 有效实施继续）。环境：BDN 0.15.8 InProcessEmit · .NET 11.0.100-rc.1 · Ryzen 9 8945HX（运行 `--persist-medium`） | bench/PalDDD.Benchmarks/Program.cs:34 | ☒ |

---

*论证生成：2026-09-17 · 只读分析 · 未修改生产代码*
