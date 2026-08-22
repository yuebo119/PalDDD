# Pal.DDD 评审报告

> 报告编号：REVIEW-2026-08-22
> 评审基准：commit `070b42f` · 全仓档（src 4 片 + test/samples/bench 4 片 = 8 片并行地毯）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD32）
> 本轮双重身份：全量地毯轮 + 最近三提交（f5653a3 状态 int 化 / 9f88af3 依赖升级 / 070b42f payload 原生化）的**验证轮**。

---

## 执行摘要

**评审结论**：机械轴/实测轴全绿、三栈 status/payload 契约对称零回归，但 PalORM 三 Store 的手动 reader 路径存在两个探针实锤的 P1 家族（MySQL 时区漂移 + 事务内 raw command 拒绝）；测试面 P1×2（容器泄漏 + 基准失真）；P2×20、P3×55。

| 指标 | 本轮 | 上轮（38 轮） | 趋势 |
|------|:--:|:--:|:----:|
| P0 | 0 | 0 | → |
| P1 | 4 | 0 | ↑（2 项为探针新实证，1 项测试基建，1 项基准） |
| P2 / P3 | 20 / 55 | 17 / ~80 | P2 ↑ / P3 ↓ |
| 逃逸（累计）/ 复发 | 0 / 0 | 0 / 0 | → |
| 证伪数（误报治理） | 1 | 0 | 子代理 ❓ 疑点被既有方言探针证伪 1 项 |
| P0/P1 修复时延 | 未修复（本轮交付清单） | — | — |

### 与上轮对比

| 上轮发现 | 上轮 | 本轮 | 状态 |
|----------|:---:|:---:|:----:|
| 38 轮 P1：MySQL 幂等 ON DUPLICATE KEY 回归 | P1 | — | ✅ 已修复（6e03d8f），本轮 dialect-probe 40/40 复证 |
| 38 轮 P1：Inbox Mark 抢占 token fencing | P1 | — | ✅ 已修复，探针"MarkProcessed 后不再重投"复证 |
| 070b42f：payload 列原生化 | 特性 | — | ✅ 验证轮通过（三栈 DDL/参数/物化对称 + 探针 0x00 往返） |
| — | — | P1×4 | 🔴 新发现（F1/F2 为长期存量，非本轮引入） |

---

## 第一部分：评审基础

### 1.1 覆盖度账本

```
应读清单 197 文件 · 26237 行（review-scope.sh 生成）——4 片子代理各自 49/49、49/49、51/51、50/50
逐文件读到 EOF，全部勾销（各片覆盖度自报见片报告，主线程抽查复核）。
全仓扩展：test/samples/bench 100 个 .cs（22918 行）+ csproj/props/launchSettings/快照 12 个附属文件
——片 5（39 .cs）/片 6（31 .cs）/片 7（23 .cs + 3 .verified）/片 8（15 文件含配置）全部读完。
七流覆盖度:
  架构流: 100%    安全流: 100%    错误流: 100%
  AOT流: 100%     并发流: 100%    资源流: 100%
  生成语义流: 机械化基线全绿（PublicApiSnapshot/AotContract/boundary 33 方法）+ 人工增量核对
三轴状态:
  机械轴: gate 22/22 · tech-debt 20/0/2allow · encoding 4/4 · verify-ai-system 21/21 ·
          弱断言 172≤173 基线 · doc-consistency 10/0
  实测轴: dialect-probe 40/40（真库 PG+MySQL 192.168.200.120 × 5 Store）
          Saga 专项探针 2/2（本轮新增，见 F1/F2）
```

### 1.2 评审基线

```
时间: 2026-08-22T02:55:07Z · Commit: 070b42f · 分支: dev
源项目 36 / 测试项目 16（排除 Testing）
源文件 197 / 测试文件 92 · 架构守护 33 用例 · PDDD 诊断 15 条
AOT: 显式 true 8 / 显式 false 14 / 继承 14（核心 7 + 元包/Prompts 等无码项目）
catch(Exception) 69 · OCE 引用 68
构建: Release 0 警告 0 错误
测试: 997 总计 / 956 通过 / 41 失败——TUnit JSON 逐条归类全部为
      "多方言 Fixture 禁止连接外部数据库；必须启用 Testcontainers"（ITM-208 设计性 fail-closed，
      与基线口径一致，非代码失败；同一批 Store 语义由 dialect-probe 40/40 真库实测覆盖）
```

### 1.3 范围声明（三项缺一不可）

- 必须检查：src/ 全部 197 个手写 .cs（逐行）；test/samples/bench 全部 100 个 .cs + 配置文件；机械防线脚本自身；最近三提交 diff 验证轮。
- 明确不检查：`*.g.cs` 生成文件（PD1）；`obj/`/`bin/`/`TestResults/` 产物；`nupkgs/`；`research/`（非工程代码）。
- 抽样策略：片外反证性阅读按引擎软预算（≤ 片内行数一半）执行，各片已自报；docs/ 正文以 doc-consistency-check 机械覆盖 + 针对性核对（release.md/tutorial.md 的 AOT 声明），未逐字全读 ⚠。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:0]     [P1:2]     [P2:8]
中等     [P1:2]     [P2:9]     [P3:40]
难修复   [P2:3]     [P3:15]    [评估:0]
```

### 2.2 发现清单

#### 🔴 P0 — 无

#### 🟠 P1 — 近期修复

| ID | 发现 | 危害×复杂度 | 证据 | 已对照模式 | 定稿门 |
|----|------|------|------|:--:|:--:|
| F1 | PalORM 三 Store 手动 `GetDateTime`→`DateTimeOffset?` 物化套本地时区偏移——MySQL 下租约/锁时间漂移 -8h 且读改写**累积回拨** | 高×中 | 真库探针实证 | PD25 邻族（新形态） | 三问✅ |
| F2 | PalORM 三 Store 5 处 raw command 未挂 `cmd.Transaction`——MySQL 活动事务下第一句即抛 | 高×易 | 真库探针实证 | PD13 部分豁免不适用（调用方是库自身） | 三问✅ |
| F3 | 多方言测试 TestSession 从不 dispose——Testcontainers 容器泄漏至进程退出 | 中×易（测试基建） | 6 类 × 3 方言 × 2-4 测试 ≈ 50+ 容器 | 无 PD 豁免 | 三问✅ |
| F4 | InfraBenchmarks 租约基准迭代内耗尽——ns/op 实测空扫描路径，数字失真 | 中×中（PD29） | 源码语义证实 | PD29 命中（空转基准） | 三问✅ |

<details>
<summary><b>F1 · PalORM 三 Store 手动 GetDateTime 物化本地偏移漂移（MySQL）</b></summary>

**证据**：`src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs:222-229`（4 处）、`PalOrmProjectionCheckpointStore.cs:44-45`（2 处）、`PalOrmIdempotencyStore.cs:46`（3 处）。

**探针**（真库 192.168.200.120，临时库 palddd_probe_sagaprobe_*，结束已删；本机时区 UTC+8 中国标准时间已确认）：

```
[A2] DB墙钟(按UTC解释)=2026-08-22T03:28:52.6385230+00:00
[A2] 物化LeasedUntil   =2026-08-22T03:28:52.6385230+08:00 (Offset=08:00:00)
[A2] 漂移 = -8.000 小时
[A3] UPDATE 1 行; DB leased_until 03:28:52 -> 前一日19:28:52 (Δ=-8.000h)
```

**论证链**：写路径经 MySqlConnector 参数以 UTC 墙钟落 DATETIME(6)；读路径 `reader.GetDateTime()` 返回 Kind=Unspecified 的 DateTime，C# 隐式转换 `DateTime→DateTimeOffset` 对 Unspecified 套**本地时区偏移**（+8）→ 物化值比真实瞬间早 8h；`SaveChangesAsync` 把漂移值再写回（参数取 .UtcDateTime）→ DB 墙钟每次保存回拨 8h。**后果**：租约提前"过期"→ SQL 侧 `leased_until <= now` 抢占谓词立即为真 → 多 worker 重复处理同一 Saga/Checkpoint/Idempotency 锁失效。

**定稿门三问**：①误判库对照——PD25 是 SQL 函数 naive 时间混用，本条是**物化路径**偏移，属新根因类（候选 PD33 关联）；Dapper 姊妹走 `QueryAsync<SagaStateRow>` 物化，实测保持 UTC（见证伪记录），**非全栈缺陷、精确隔离**。②反证搜索——PG 列为 timestamptz（Npgsql 返回 Kind=Utc → 隐式转换 offset 0，免疫）；SQLite "O" 带偏移字符串待验证（修复时一并）；方言探针 40/40 过是因为其断言不检查时间戳（见 F-测试 9 的漏网原因）。③触发路径——"MySQL + 非 UTC 时区应用进程 + 租约/保存循环 → 每周期漂移一个本地偏移量"。

**修复方案**：三 Store 的手动物化统一 `DateTime.SpecifyKind(dt, DateTimeKind.Utc)` 后再转换（或 `GetFieldValue<DateTimeOffset>` 若 MySqlConnector 语义等效——修复前先包行为探针）；dialect-probe 补 Saga 租约时间戳断言（堵 F-测试 9 漏网）；多方言测试补时间往返断言。验证：本探针红转绿 + 移除修复退回红（S3）。

</details>

<details>
<summary><b>F2 · PalORM raw command 未挂事务——MySQL 事务化路径不可达</b></summary>

**证据**：`PalOrmSagaStateStore.cs:63/130/142`、`PalOrmProjectionCheckpointStore.cs:33`、`PalOrmIdempotencyStore.cs:36`（后者头部有 P0-4 已知限制声明，但仅声明了 GetAsync 读路径，未覆盖 Saga/Checkpoint 的入口方法）。

**探针**（同上环境）：

```
[B] 事务内 SaveChanges => THREW InvalidOperationException:
    The transaction associated with this command is not the connection's active transaction
[B] ⇒ 片4疑点2 成立（MySQL 事务内写路径不可达）
```

**论证链**：MySqlConnector 严格校验连接有活动事务时命令必须挂同一事务（Npgsql 连接级自动参与，PG 免疫）；`Session.ExecuteAsync` 创建的命令自动挂 `GetActiveTransaction()`，而 `GetRawConnection().CreateCommand()` 的手动命令没有 → 事务内第一句 raw 命令即抛。

**定稿门三问**：①误判库对照——PD13 豁免"第三方调用方使用逃生舱"，本条调用方是**库自身的 Store 方法**（SaveChangesAsync 第一步就是 GetByIdAsync raw 命令），不适用。②反证搜索——自动提交模式下全链路工作（A1 实证 + 历轮探针），仅事务模式炸；PalOrmUnitOfWork 类注释声称"事务自动传播……无需 Store 显式接收 transaction 参数"，实现与该声明矛盾。③触发路径——"`IUnitOfWork.BeginTransactionAsync` 后调 Saga SaveChanges / Checkpoint TryStart / Idempotency GetAsync → InvalidOperationException"。

**修复方案**：PalORM 5.3 若有公开 `GetActiveTransaction()` 则统一挂接；否则 raw 命令构造收敛为私有 helper 统一设置；SQLite/PG 路径行为不回归（探针三方言）。修复后本探针 [B] 转绿。

</details>

<details>
<summary><b>F3 · 多方言测试 TestSession 容器泄漏</b></summary>

**证据**：`test/PalDDD.PalORM.Tests/PalOrm*MultiDialectTests.cs` 六文件的全部工厂方法（如 PalOrmInboxMultiDialectTests.cs:14-26）——`=> await Helper(await CreateXxxAsync())` 临时 TestSession 无人持有、无 await using；Saga 工厂 `var ts = await ...; return new Store(ts.Session,...)` 直接丢弃 ts。对比：`MultiDialectFixture.CreateSqliteAsync:22-31` 有 catch-dispose 模式先例。

**后果**：PG/MySQL 每测试启动的 Testcontainers 容器泄漏至进程退出才被 Ryuk 回收（≈50+ 容器/全套件）——拖慢套件、耗尽 Docker 资源。**非生产缺陷**。

**修复方案**：Helper 改 `await using`；工厂返回复合 disposable。验证：docker ps 观察容器归零。

</details>

<details>
<summary><b>F4 · 租约基准迭代内耗尽</b></summary>

**证据**：`bench/PalDDD.Benchmarks/InfraBenchmarks.cs:56-77`——`LeasePending_Batch100`/`LeaseAndMarkAll_Batch100` 首次调用把 100 条全部租走（LockedUntil=now+30s），BDN 单迭代内方法被调用 N 次（内存级 μs 操作 N 数万），后续 N-1 次 `QueryPending` 因 `LockedUntil <= now` 过滤恒空——ns/op 实测"(1 次真实租约 + (N-1) 次空扫描)/N"。ITM-152 修的是跨迭代（GlobalSetup→IterationSetup），迭代内耗尽未修。

**修复方案**：基准体内检测耗尽重灌（配 OperationsPerInvoke 摊销）或改非耗尽型操作。对照 PD29 验证器三问第二问（每迭代面对真实数据）。

</details>

#### 🟡 P2 — 计划修复（允许 [推断] 定稿，行动项标"修复前先补探针"）

| ID | 发现 | 证据 | 已对照模式 |
|----|------|------|:--:|
| F5 | EventLogDbContext EventId 冲突分类只查 `events[0]`——非首位重复 EventId 误判为并发异常 → 调用方无限重试 | EventLogDbContext.cs:260-284，注释意图与实现不符 | PD14 不适用（Dapper 版 actualVersion 基线不受影响） |
| F6 | `ExtractJsonByPath` 缺逗号/花括号 fail-fast——姊妹 `ExtractTextByPath`（三十七轮 P2-2）已修，doc 声称同约束但代码只校验空白 | PostgreSqlJsonbExtensions.cs:161-178 | PD24（修一处漏孪生） |
| F7 | `PostgreSqlReadWriteRouter.DisposeAsync` 无异常隔离——Writer 抛则 Reader 永不释放；姊妹 ShardedDataSourceManager（三十七轮修复）已做逐 shard 隔离 | PostgreSqlReadWriteRouter.cs:63-68 | PD24（姊妹不对称） |
| F8 | `PalOrmInboxStore` 回查路径 `catch (InvalidOperationException)` 过宽——DB 故障被解释为"记录不存在"返回 null 静默跳过 | PalOrmInboxStore.cs:110-115 | ITM-065 变体 |
| F9 | `DapperBulkCopy` MySQL 路径 valueExtractor 实际调用次数与入口契约（"首行两次"）不符——首行最多 3 次且非首行也可能 2 次 | DapperBulkCopy.cs:96-98, 215-243 | 无 PD 豁免（纯提取函数无实害，文档失实） |
| F10 | `OutboxSqliteConcurrencyTests` 本地重写 Lease（时间过滤移到内存）遮蔽生产 EF Lease——5 个测试（含 3 个 ITM-210 回归）的 Lease 端测的是重写版 | OutboxSqliteConcurrencyTests.cs:187-217 | PD29（防线被遮蔽） |
| F11 | `TestOutboxDbContext.LeasePendingMessagesAsync` 死 override 零调用（注释自证"潜伏缺陷"） | OutboxEfCoreTests.cs:185-211 | PD29 |
| F12 | `DapperStoreTests` 类注释声称"序列化执行"但无 `[NotInParallel]`——ClassInitialize 改 Dapper 全局状态期间并行类可读污染 | DapperStoreTests.cs:32-86 | 模式 2（grep 计数≠语义，已逐行核） |
| F13 | 多方言测试类注释宣称验证"DateTimeOffset 时间戳往返"但**零时间断言**——F1 漂移漏网的直接原因 | PalOrmOutboxMultiDialectTests.cs:10-18 vs 全部测试体 | PD29（名字声称≠断言） |
| F14 | PD26：四类 EF store（Inbox/Idempotency/Projection/Saga）仅 InMemory 验证——Outbox 已有迁 SQLite 先例未跟进 | InboxEfCoreTests.cs 等 | PD26 命中 |
| F15 | 方言矩阵缺口：Saga 乐观锁冲突仅 SQLite 覆盖（MySQL 两步路径行数语义不同零证明）；Projection 过期抢占 PG/MySQL 零覆盖 | PalOrmSagaMultiDialectTests.cs:152-172 | PD21/22 关联 |
| F16 | 测试 SQLite DDL 4 副本互相漂移（CreateSchemaSql 死常量 / CreateAsync / MultiDialectSchema / InitSchemaAsync） | PalOrmStoreFixture.cs:24-135 | 维护性 |
| F17 | `CompensateAsync_NoCompensationHandlers_Succeeds` 全测试体零 Assert | TransactionsTests.cs:1246-1254 | PD29（弱断言棘轮） |
| F18 | AotContractTests 两个 "ReflectionDisabled" 测试未设置 `JsonSerializerIsReflectionEnabledByDefault=false` 前提 | AotContractTests.cs:356-381 | P0#3 关联 |
| F19 | ArchitectureBoundaryTests 命名正则假阴性：漏检带 [Arguments]/[MethodDataSource] 的参数化方法与 `Task<T>` 泛型返回——命名守护对大量目标不生效 | ArchitectureBoundaryTests.cs:787 | PD29（falsification） |
| F20 | Rabbit 轴四层防线不对称：2 个收数测试无 CapturingLogger/无 warmup 重发/裸 120s 超时（Kafka 轴四层全齐） | BrokerIntegrationTests.cs:421-508 | T-DDD-6 |
| F21 | release.md 称 AotSample 为"CI AOT 验证示例"——ci.yml 零命中，AOT publish 仅覆盖 PalOrmSample | docs/release.md:182 vs ci.yml | PD28（发布面失实） |
| F22 | RowVersion 属性测试在全 int 域断言 `Next()==start+1` 与 MaxValue 抛溢出语义冲突——FsCheck 生成边界值时慢性 flaky | PropertyTests.cs:54-69 | ⚠[推断]（未运行验证） |
| F23 | `DimBridge_EventHandlerTypes_AreKnownAtCompileTime` 注释声称验证不依赖 MakeGenericType，断言仅参数计数；DotNet11MigrationTests `AotCompatibility_AnalyzerEnabled` 名实不符 | AotContractTests.cs:245-275 / DotNet11MigrationTests.cs:42-50 | PD29 |

#### ⚪ P3 / 评估

55 项 P3 已按生命周期规则登记至 `.ai/review/action-items-p3-backlog.md`（30 天老化→升 P2）。代表性条目：Saga DynamicStep 路由未注册 key 静默返回、RequeueDeadAsync retriedBy 未截断（姊妹已截）、ExceptionMiddleware 404 body 暴露 ex.Message、Repository.EFCore UnitOfWork 构造无 null 守卫（姊妹有）、EnumGenerator 双 partial 重复 PALENUM001、CodeFix trivia 不一致、FanOut 超时记录丢失 Item 标识、时序脆弱测试 3 处、AOT 扫描面缺口（构造器/属性级标注）、PublicApiSnapshot 适配层范围决策、mojibake 注释残片、样本 Order Items 暴露 List 破坏聚合封装等——完整清单见行动项文件。

### 2.3 观察项（非问题，但值得注意）

| ID | 观察 | 说明 |
|----|------|------|
| O1 | Dapper 栈 token 时间链在非 UTC 进程下实测健康 | 片 1 ❓ 疑点"MySQL 非 UTC 租约 token 恒拒绝"被方言探针证伪（本机 UTC+8，MarkProcessed token 逐字节匹配）——Dapper `QueryAsync` 物化 DateTime→DateTimeOffset? 保持 UTC，与手动 GetDateTime 隐式转换行为**不同**。转误判库候选 PD33 |
| O2 | status int 三栈对称 | 契约枚举（Outbox 0/1/2、Inbox 0/1/2/3）= Dapper SqlTemplates 字面量 = PalORM Row 强转，零分叉 |
| O3 | payload 二进制三栈对称 | DDL 三方言全二进制（LONGBLOB/BLOB/BYTEA）+ 参数绑定 + EFCore IsRequired + 探针 0x00 往返 |
| O4 | MarkProcessed/MarkDead 内存清租约不看 affected | 声明在案的刻意语义（ITM-130 对齐），与 ReleaseForRetry 的 affected>0 不对称——已在 P3 记录权衡项 |
| O5 | Npgsql `Port` 默认值 5432 | 片 4 疑点经 Npgsql 源码核实排除；配套测试缺口（无 Port 场景）记 P3 |

---

## 第三部分：架构合规与收束判定

### 3.1 DDD/Clean Architecture 合规

| 原则 | 状态 | 证据 |
|------|:----:|------|
| 领域层零基础设施依赖 | ✅ | G2/G4 全绿；8 片架构流零发现 |
| 依赖方向外→内单向 | ✅ | boundary 33 方法 + DependencyInjection.Tests 89/89 |
| 跨 BC 仅通过领域事件 | ✅ | G13 红线零命中 |
| 无 IRepository\<T\> 等反模式 | ✅ | G13 |
| DIM 桥接替代反射 | ✅ | G7/G8 零命中；反射点全带 DAM/RUC 标注（8 片 AOT 流核对） |
| 聚合根保护不变量 | ✅（src）/ ⚠（样本） | src 抽样合规；ECommerce/MinimalApi 样本 List 暴露记 P3 |

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | gate 22/22 + tech-debt 20/0/2allow + encoding 4/4 + verify-ai-system 21/21 + 弱断言 172≤173 + 文档 10/0 |
| 静态轴 | ❌ | 本轮 P1×4 / P2×20 新发现——不可收束 |
| 实测轴 | ✅ | dialect-probe 40/40 + Saga 专项探针 2 断言（F1/F2 实证） |

**判定**：三轴未全绿 → 进入评审-修复循环（修复轮 → 验证轮）。F1/F2 修复后必须复跑 Saga 探针确认红转绿 + 移除修复退回红（S3 反向验证）。

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖文件/行数 | src 197 文件 / 26237 行 + test·samples·bench 100 文件 / 22918 行 + 配置附属 12 文件 |
| 分片 | 8 片并行（src 4 × ~6560 行 + test 面 4 片），各片覆盖度自报齐 |
| 探针数 | 方言 40 断言 + Saga 专项 2 断言（真库，临时库已清理） |
| 证伪数 | 1（Dapper token 链 ❓ → 探针证伪，转 PD33 候选） |
| 下沉建议 | F1 → dialect-probe 补时间戳断言 + tech-debt #23（GetDateTime→DateTimeOffset 赋值检测）；F2 → tech-debt #24（PalORM store 内 CreateCommand 无 Transaction 挂接检测）；F4 → 验证器三问已有，补 bench 专项抽查到 refine-scan |
| 机械防线自身 | ArchitectureBoundaryTests 发现假阴性盲区（F19）；快照防线真实能红（片 6 falsification 核验） |

### 4.2 自我局限声明

```
- docs/ 正文未逐字全读（doc-consistency-check 机械覆盖 + 针对性核对）⚠
- F1 的 SQLite 路径漂移未实测（"O" 带偏移字符串的 GetDateTime 行为待修复时一并验证）
- F2 的 PalORM 公开事务 API 可用性未核（修复方案依赖 PalORM 5.3 是否暴露 GetActiveTransaction 等价物）
- 集成测试 41 个 fail-closed 未在本机 Docker 上跑 Testcontainers 复验（CI 承载）
- Messaging.Integration 的 RabbitMQ/Kafka 真实 broker 测试未运行（环境未起 broker；测试面已静态审查）
```

### 4.3 元审计自检

```
□ Z0 覆盖度: ✅ 197+100 文件账本全勾销，零发现流均附覆盖证据
□ S1 论证链: ✅ P1×4 含证据+定稿门三问+触发路径（F1/F2 探针实证，零 [推断] 定稿）
□ S2 合规: ✅ 3.1 六项全查
□ S3 指标: ✅ 4.1 与 metrics.md 追加行一致（无评分——已废止）
□ S4 反模式: ✅ 无"行数少=更好"类判断
□ 可信度: ✅ P2 中仅 F22 标 ⚠[推断]（行动项注明修复前先验证）
□ 一致性: ✅ 摘要 ↔ 发现清单 ↔ 收束判定一致
□ 对比: ✅ 与 38 轮报告衔接，趋势快览无断档
违反项: 无
```
