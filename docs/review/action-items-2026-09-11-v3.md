# Pal.DDD 行动项清单 — 第三轮全仓运行（MIG 迁移后验证轮）2026-09-11

> 来源报告：[`review-2026-09-11-full-v3.md`](review-2026-09-11-full-v3.md)
> 基线 commit：`a80283b`（dev = main `17b2726` 内容）· 全仓重扫 + **MIG 迁移验证**（新门禁 7 文件逐行 + 三方映射逐项 + 上两轮全部改动面敌对复查）
> 生成方式：机械防线新形态全套 + 11 个并行子代理真读（src 5 片 215 文件 + test 4 片 113 文件 + 新门禁专项 + 三方一致性专项）+ 主线程裁决
> 编号衔接：自 **ITM-653** 起。

---

## 总体进度

| 优先级 | 条目数 | 待修复 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|
| **P0 / P1** | 0 | 0 | 0 | — |
| **P2** | 7 | 0 | 7 | 100% |
| **P3** | 30（汇总） | 12 | 18 | 60% |
| **合计** | 37 | 12 | 25 | **68%** |

**修复轮（2026-09-11，3 并行代理 + 主线程）**：7 P2 全清。P3 实修 18/30（其余 12 条为❓探针待环境/声明已固化/等价继承边界——见各项内标注）。

**本轮核心结论**：无 P0/P1。7 条 P2 **全部同属一个主题——"收口完整性"缺口**：上两轮修复与 MIG 迁移的姊妹同步/口径同步不完整（v66 OCE 过滤修复只收口 1/3；上轮 Rollback 测试姊妹漏 Commit 版；MIG 后 README/V2/断言数三处口径漂移）。这一主题本身是历轮"修复自带缺陷"模式的降维延续——从语义错误（前两轮 5 项）降为同步遗漏（本轮 0 项语义错误），质量体系收敛趋势明确。

---

## 🟠 P2 — 计划修复（7 条，全部"收口完整性"主题）

### [x] ITM-{n} · Idempotency/ProjectionProcessor 的 MarkCompletedAsync OCE 过滤姊妹残留（v66 修复只收口 1/3）· ✅
- **维度**：错误流（v66 修复不完整）
- **问题**：v66 在 `InboxProcessor.cs:114` 移除了 MarkProcessed 失败 catch 的 `when (not OCE)` 过滤（ITM-092 口径：Mark* 以 `CancellationToken.None` 调用，其 OCE 属存储异常而非取消），但**同构的姊妹两处未同步**：`IdempotencyProcessor.cs:138`、`ProjectionProcessor.cs:111` 的 MarkCompletedAsync 失败 catch 仍带过滤——OCE 逃逸使已成功的 handler 被调用方按取消/重试处理（副作用双执行风险）。
- **触发路径**：自定义 store 在 None token 下抛 OCE（内部超时包装）→ 过滤放走 → Executed 语义丢失。
- **修复**：两处移除过滤，对齐 InboxProcessor v66 形态（统一按 pending-confirmation 处理 + 日志）；补姊妹回归测试（mock store 抛 OCE 断言 Executed 返回）。
- **涉及**：src/PalDDD.Idempotency/IdempotencyProcessor.cs、src/PalDDD.Projections/ProjectionProcessor.cs + 对应测试

### [x] ITM-{n} · CommitAsync_AfterDispose 测试缺失（上轮姊妹收口漏 Commit 版）· ✅
- **维度**：测试覆盖（上轮收口不完整）
- **问题**：上轮补 `RollbackAsync_AfterDispose_ThrowsObjectDisposedException` 时，姊妹 **Commit 版全仓零测试**（grep 实证：Begin/Rollback 有测、Commit 缺）——`DapperUnitOfWork.CommitAsync:61` 的守卫无回归网。
- **修复**：镜像补 `CommitAsync_AfterDispose_ThrowsObjectDisposedException`（探针即测试本身）。
- **涉及**：test/PalDDD.Integration.Tests/DapperUnitOfWorkTests.cs

### [x] ITM-{n} · `.ai/README.md` 6 处旧口径 + V5 判定面升级 · ✅
- **问题**：README 6 处仍称"PDDD-G1..G24 全阻断"（实际 gate-check 仅 3 项且 G24 警告级）；**V5 只校验范围终点号故放过**（起点/中段/级别漂移零检测）。
- **修复**：README 6 处刷新（G22-G24 保留 + 21 项下沉去向）；V5 增 README 行级"保留集"断言（红测：改回旧口径必红）。
- **涉及**：.ai/README.md、.ai/scripts/verify-ai-system.sh

### [x] ITM-{n} · V2 存在性列表缺 encoding-gate.sh · ✅
- **问题**：encoding-gate 被 engine.md/README/ci.yml 引用，但不在 V2 存在性清单——被误删时 V2/V16 均不红。
- **修复**：V2 列表补入；核对全清单 vs 实际引用面（同类缺口一次清）。
- **涉及**：.ai/scripts/verify-ai-system.sh

### [x] ITM-{n} · DialectProbe 断言数 40/42 三方分叉 · ✅
- **问题**：`.ai/review/prompt.md:40` 写"每方言 20 项共 40"，实际 `DialectProbeTests.cs` 每方言 21 项共 42（文件头与 ci.yml 注释均 42）。
- **修复**：review prompt 改 42（其余两处已对）。
- **涉及**：.ai/review/prompt.md

### [x] ITM-{n} · AssertionStrength raw string 净化器不识别 4+ 引号定界 · ✅（当前零影响）
- **问题**：行同时 StartsWith/EndsWith `"""` 即判定非 raw——`""""` 包裹的嵌套形态整体漏净化（内部不平衡花括号扰动深度计数）。当前全 test 仅守卫自身用 `""""` 且被排除，**零现实影响**。
- **修复**：定界识别改为最长引号前缀匹配（`"""`+ 时进入 raw 态并按同长定界退出）；红测加 `""""` 样本。
- **涉及**：test/PalDDD.DependencyInjection.Tests/AssertionStrengthGateTests.cs

### [x] ITM-{n} · maxRetryCount 负值三栈无守卫（与同方法 batchSize 守卫自相矛盾）· ✅❓
- **问题**：负值使 `retry_count < maxRetryCount` 恒假→静默空返回（"无待处理"假象）；同方法 batchSize 非正守卫的立论（"LIMIT 0 静默空返回无诊断"）逐字适用于此。三栈（PalORM :62/101/136、EFCore OutboxDbContext:81、Dapper :111/152/167）0 守卫；管线路径由 Options 覆盖，直调路径裸奔。
- **修复**：三栈 4+ 处同轮补 `ThrowIfNegativeOrZero`（对齐 batchSize 口径，避免只修一处造新分叉）；直调测试断言负值抛。
- **涉及**：三栈 Outbox store + 测试

---

## ⚪ P3 — 汇总（30 条）

**新门禁判定面（PD29 边界，9 条）**：#20 按 `*DbContext.cs` 文件名 glob（类名≠文件名漏检）；D10 只扫 *.md（bash 递归不限类型）；#16/#17 存在性不证明全部分派正确（bash 等价）；AssertionStrength 普通字符串/块注释不净化 + `Task<T>` 返回不匹配（python 等价）；DocConsistency D11 不枚举字段/事件 + 嵌套类型 docId `+` vs `.`（假红向）；D12a 锚过宽（任何"N 方法"声称都被比 37）；TestGateGuard T6 行号锚漂移 + finally 子串豁免；T8 `Contains("//")` URL 误判；boundary 注释剥离不感知字符串字面量 `//`（同型 4 处）+ TestMethodPattern `[^\]]*` 数组参数截断 + BackgroundService 只认 sealed 直接基类 + DomainTests 扫描面仅 Core.Tests + infraProjects 名单缺 3 项目（收紧向）+ 死豁免 DapperDbType + 恒真断言 :384。

**src（8 条）**：IdentityGenerator ValueSpan 腿 provider 仍 null（v66 注释"统一口径"未收全）+ CRLF 混排；OutboxDbContext MySQL/PG `Status = 0` 枚举序数耦合；PalOrmSagaStateStore saga_data `null` 文本兜底 new TState（与 ITM-228 fail-fast 哲学相悖，❓探针）；Sqlite 连接工厂 PRAGMA 抛半成品泄漏；Notifier gate 内自通知建连超时假设（❓探针）；SagaCompensation OCE rethrow 丢 failures（SagaProcessor 幂等重放兜底已确认，降级）；ReportHelper 同步句柄异步写；DapperUnitOfWork DisposeAsync check-then-act（后果全吞）。

**test（13 条）**：DapperBulkCopyTests 守卫抛后连接/事务泄漏；MessagingTests Contains(1) 弱断言无隔离；EventLogOptimizedSerializationTests 分配计量无隔离（共享线程池）；DialectProbe #8 EventId 实现顺序耦合（无守护注释）；NonFirstBatch 连接池隐性依赖；MarkDead 链路转移盲区（入口已覆盖）；FanOut/SagaProcessor/EntityTests/Cqrs 分配/Broker 精确值等真实时钟 flaky 批×5；TestHelpers FakeTimeProvider._timers 无锁 List；RecordingListener 跨项目同源污染残余；CompressionGuard 64MB 并行内存；PublicApiSnapshot 非 GH 平台 CI + 嵌套类型；AotContract 嵌套 public DAM；MessageRegistry 增量管线（AsSourceGenerator 包装）。

---

## 修复顺序建议

| 序 | ITM | 理由 |
|:--:|-----|------|
| 1 | 653 / 654 | 姊妹收口双件（同主题，一个代理/主线程顺手） |
| 2 | 655 / 656 / 657 | 口径同步三件（.ai 仓内集中改） |
| 3 | 658 / 659 | 判定加固双件 |
| 4 | P3 批 | backlog 按触碰 |

## 验收标准

- [ ] ITM-653 修复后 grep `when (markEx is not OperationCanceledException)` 在三个 Processor 中零残留 + 姊妹回归测试绿
- [ ] ITM-654/659 新测试各自红测（移除守卫/传负值）实证
- [ ] ITM-655 V5 红测：README 注入旧口径必 FAIL
- [ ] ITM-658 红测：`""""` 嵌套样本被正确净化
- [ ] 全套机械防线 + 全测试复跑绿
