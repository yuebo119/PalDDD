# Pal.DDD 评审报告

> 报告编号：REVIEW-2026-08-22-v2（第四十轮）
> 评审基准：commit `b2bd64a` · 全仓档（src 4 片 + test/samples/bench 4 片 = 8 片并行地毯）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD33）
> 本轮双重身份：全量地毯轮 + **三十九轮修复轮（b2bd64a）的强制验证轮**。

---

## 执行摘要

**评审结论**：三十九轮全部 P1 修复经 8 片敌对核**验证通过（无假修、无回退、无新引入 P1+ 缺陷）**；本轮新发现 P0×1（连接串入异常消息，探针实锤）+ P2×3 + P3~45；机械轴出现一项棘轮回归（弱断言 186>173）。

| 指标 | 本轮 | 上轮（39 轮） | 趋势 |
|------|:--:|:--:|:----:|
| P0 | 1 | 0 | ↑（存量缺陷新暴露，非新引入） |
| P1 | 0 | 4 | ↓（上轮 4 项全部修复并验证） |
| P2 / P3 | 3 / ~45 | 20 / 55 | ↓ / ↓ |
| 逃逸（累计）/ 复发 | 0 / 0 | 0 / 0 | → |
| 证伪数 | 1 | 1 | P0 探针 v1 被前置守卫拦截证伪→v2 直达实锤 |
| 修复轮自带缺陷率 | 0（本轮验证轮口径） | 1（AsyncLocal，当场根治） | ↓ 收敛 |

### 与上轮对比

| 上轮发现 | 上轮 | 本轮 | 状态 |
|----------|:---:|:---:|:----:|
| F1 时间漂移 / F2 事务不可达 / F3 容器泄漏 / F4 基准耗尽 / ITM-261 | P1×5 | — | ✅ 全部修复且本轮 8 片敌对核确认（详见 §3.3） |
| F20 Rabbit 四层防线 | P2 | P2×1 残留 | 🟡 主体完成，轴对称收尾 3 项（ITM-264） |
| 弱断言棘轮 172≤173 | 门禁 | **186>173** | 🔴 b2bd64a 净增 15 冗余 IsNotNull 守卫行（ITM-263） |

---

## 第一部分：评审基础

### 1.1 覆盖度账本

```
src 198 文件（+1 PalOrmAmbientTransaction.cs）26,4xx 行——4 片各 49/49/50/50 逐行读到 EOF；
test/samples/bench 100+ .cs——片 5（41）/片 6（31）/片 7（24 项目全部）/片 8（16 文件含配置）全读完。
七流覆盖度: 架构 100% · 安全 100% · 错误 100% · AOT 100% · 并发 100% · 资源 100% · 生成语义 100%
三轴状态:
  机械轴: ❌ gate 22/22 + tech-debt 20/0/2a + encoding 4/4 + verify-ai 21/21 通过；
          **assertion-strength FAIL（186>173）**——本轮唯一机械轴回归
  实测轴: dialect-probe 40/40（真库 PG+MySQL）+ P0 探针 2 轮（证伪→实锤）
  静态轴: ❌ 新发现 P0×1 / P2×3
```

### 1.2 评审基线

```
Commit: b2bd64a · 分支 dev · 双仓清洁（G22 绿）
源 36 项目 / src 198 / test 94 · boundary 38 用例（+5）· PDDD 诊断 15
构建 Release 0/0 · 测试 1041 = 996 通过 + 45 fail-closed（与 39 轮终基线逐项一致）
PG/MySQL 真库双可达（192.168.200.120）
```

### 1.3 范围声明

- 必须检查：src 全部 198 手写 .cs 逐行；test/samples/bench 全量；b2bd64a 45 文件修复 diff 的敌对验证；机械防线。
- 明确不检查：`*.g.cs`、obj/bin/TestResults 产物、nupkgs、research/。
- 抽样策略：片外反证性阅读按软预算执行（各片自报 0-3% 片内行数）；docs/ 以 doc-consistency + 针对性核对（AsyncLocal 残留即此路径抓出）⚠。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:1]     [P1:0]     [P2:2]
中等     [P1:0]     [P2:1]     [P3:~40]
难修复   [P2:0]     [P3:~5]    [评估:0]
```

### 2.2 发现清单

#### 🔴 P0 — 立即修复

| ID | 发现 | 危害×复杂度 | 证据 | 已对照模式 | 定稿门 |
|----|------|------|------|:--:|:--:|
| F1 | `AddPalReadWriteRouter` 副本缺 Host 的 ArgumentException 消息内嵌**完整连接串原文（含 Password）**——异常进宿主日志即凭据泄漏 | 高×易 | 探针实锤 | 无 PD 豁免（P0#1 红线） | 三问✅ |

<details>
<summary><b>F1 · 连接串入异常消息（PostgreSqlReadWriteRouter.cs:147-149）</b></summary>

**探针证据**（file-based app 直调注册扩展，两轮）：

```
v1（凭据不一致的副本串）→ 被前置 ThrowIfCredentialsMismatch 拦截，该消息不含连接串——候选一度证伪
v2（凭据一致 + 缺 Host）→ 直达目标 throw：
Replica connection string 'Port=5433;Database=probe;Username=probe;Password=Sup3rS3cret!' has no Host.
[P0-probe] 异常消息内嵌密码: True
```

**定稿门三问**：①误判库对照——无对应豁免；ThrowIfCredentialsMismatch 姊妹（多节点凭据校验）消息只含角色名不含串，证明同文件已有正确形态。②反证搜索——做了（v1 探针即反证，被前置守卫拦截；构造直达条件后实锤）。③触发路径——"`AddPalReadWriteRouter(primary, [凭据一致但缺 Host 的副本串])` → ArgumentException.Message 含 Password → 宿主启动日志记录 → 凭据泄漏"。

**修复**：消息不内嵌 `'{cs}'` 原文——报副本索引（`replicaConnectionStrings[i]` 的下标）或仅脱敏 Host 段；补"消息不含敏感段"断言的单元测试。修复后本探针复跑转绿。

</details>

#### 🟠 P1 — 无（上轮 5 项全部修复验证通过）

#### 🟡 P2 — 计划修复

| ID | 发现 | 证据 | 已对照模式 |
|----|------|------|:--:|
| F2 | 弱断言棘轮违规 186>173：b2bd64a 测试净增 15 处冗余 `IsNotNull()` 守卫行（后续行即 `x!.Prop` 断言，守卫行为零增益）——机械轴回归 | assertion-strength FAIL + diff 计数（+26/-11） | T13/T14 棘轮 |
| F3 | Broker 轴对称收尾 3 项同源：Rabbit 两测试缺 `[NotInParallel]`、HandlerCancellation entered 等待 120s vs Kafka 30s 不一致、Fixture.InitializeAsync 并行双启 Testcontainers 竞态 | BrokerIntegrationTests.cs:422,456,493 + :66 | T-DDD-6（上轮 F20 残留） |
| F4 | AotSample `MarkProcessed(outboxMsg)` 传原始引用而非 `pending[0]`——InMemory 版 successor 双守卫必然拒绝、真库版 owner-null 分支 0 行，**三方实现全部静默 no-op**；且 Check 只验租约数不验标记成功——样本教错误用法 + 验证器自欺变体 | samples/PalDDD.AotSample/Program.cs:55-57（片 8 证据链含三实现源码核） | PD29 |

#### ⚪ P3 / 评估

~45 项登记 `.ai/review/action-items-p3-backlog.md`。代表性：三方一致残留 ×2（palorm-adapter.md:207 仍写"（AsyncLocal）挂接"、OutboxSqliteConcurrencyTests 类头注释描述已修复缺陷）、RequeueDeadAsync retriedBy 截断姊妹缺口、InMemoryOutboxStore.AddMessagesAsync 缺 ct 检查、AddMessagesAsync extractor 时钟副作用违反自家纯函数契约、DapperBulkCopy 重复列名 IndexOf 静默错列、EventLogEfCoreTests 批内自重复分支无覆盖、boundary 守卫加固族（obj 子串/注释过滤/泛型正则）、SagaTests Backward/Forward 同断言冗余、OutboxProcessorTests 时序阈值未对齐、FakeTimeProvider._timers 无锁等。

### 2.3 观察项

| ID | 观察 | 说明 |
|----|------|------|
| O1 | CWT 理论边界 | 同 session 双 UoW 并发 Begin 时键级 Remove 误清——当前三方言 provider 均单连接禁并行事务，不可达；注释补声明即可（P3） |
| O2 | 测试 DDL 双源 | DapperStoreTests 与 MultiDialectSchema 属跨栈平行副本——刻意分离可接受，补互指注释 |
| O3 | SagaProcessor 契约 | Mark* 兜底持久化依赖调用方 SaveChangesAsync——主路径（OutboxBatchProcessor）已闭环，外部直调面属文档级 |

---

## 第三部分：架构合规与收束判定

### 3.1 DDD/Clean Architecture 合规

六项原则全部 ✅（同上轮形态；片 1-4 架构流零发现）。

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ❌ | assertion-strength 186>173（其余全绿） |
| 静态轴 | ❌ | P0×1 / P2×3 新发现 |
| 实测轴 | ✅ | dialect-probe 40/40 + P0 探针闭环 |

**判定**：未收束 → 修复轮（ITM-262..265）→ 验证轮。

### 3.3 验证轮结论（本轮核心产出）

**b2bd64a 的 45 文件修复全部通过敌对核**：

| 上轮修复 | 本轮验证证据 |
|----------|--------------|
| F1 GetUtc（9 处） | 片 1/2/3 逐处核对：SpecifyKind(Utc) 覆盖全部时间列无遗漏；探针依赖的三方言返回值前提已在注释声明 |
| F2 CWT 事务挂接 | 片 2 全新文件敌对核：三清理点（Commit:75/Rollback:98/Dispose:118）全覆盖、Begin 幂等、Inbox/Outbox/EventLog 无 raw command 不需挂接；片 4 独立复核一致 |
| ITM-261 SQLite EF 分页 | 片 4：分页终止/翻页/Trim/asNoTracking 分支全部正确；ULID 序声明经字符串落列旁证成立；仅补"翻页非快照"声明（P3） |
| F3 容器泄漏 / ITM-244 | 片 5：六文件 await using 全覆盖、Saga 元组无漏网 |
| F4 基准重灌 / ITM-246 | 片 8：守卫覆盖全部耗尽形态（第 4 参实为 maxRetryCount 已片外实证）、成本声明诚实 |
| ITM-247 EventId 分类 | 片 3：EF 翻译路径成立（值转换器覆盖 IN 元素）、空批/单事件边界、优先级顺序正确；片 7：传感器修复前真能红 |
| ITM-248/249/250/251 | 片 3/4：违禁集与姊妹逐字一致、Dispose 首异常重抛在位、查空语义对齐 Dapper 姊妹、缓存列序对齐 |
| ITM-252..260 测试面 | 片 5/6/7：本地重写真删、fencing 测试仍有效（ExecuteUpdate 直写路径）、新正则负向自证真能红、前提断言丢失即红 |

**修复轮自带缺陷率**：本轮验证轮口径为 **0**（上轮修复轮内当场抓出 1 例 AsyncLocal 并根治；验证轮未再发现）——历轮 31%→8%→修复轮内闭环→本轮 0，收敛成立。

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖 | src 198 文件 + test/samples/bench 100+ 文件（8 片全勾销） |
| 探针数 | 方言 40 断言 + P0 探针 2 轮（证伪→实锤）+ 主线程 diff 复核 |
| 证伪数 | 1（P0 v1 被前置守卫拦截——反证搜索直接产出） |
| 下沉建议 | F2 → 冗余 IsNotNull 检测可考虑纳入 assertion-strength（区分"守卫后跟断言"形态）；F4 → 样本验证器三问（ECommerce/PalOrmSample 正确写法对照） |

### 4.2 自我局限声明

```
- EventLog ITM-247 的 EF 翻译结论基于官方文档行为 + 代码形态（片 3 标注 [推断·文档]），集成测试为确定性验证
- F4 三方 no-op 结论基于三实现源码核（片 8 证据链），未运行样本复现（修复时以运行样本为验证）
- docs/ 正文未逐字全读 ⚠（doc-consistency + 针对性核对）
- Messaging.Integration 的 broker 测试本轮未重跑（上轮 6/6 通过，本轮静态审查）
```

### 4.3 元审计自检

```
□ Z0 覆盖度 ✅ · □ S1 论证链 ✅（P0 探针实锤零 [推断] 定稿）· □ S2 合规 ✅ · □ S3 指标 ✅
□ S4 反模式 ✅ · □ 可信度 ✅（⚠/❓ 项未含确定结论）· □ 一致性 ✅ · □ 对比 ✅
违反项: 无
```
