# 审计后修复方案（2026-11-07 红队复核收敛版）

> 来源：2026-11-07 全量功能审计（8 路并行只读）+ 3 路独立红队复核（尽力推翻）+ 主代理源码复验。
> 基线：dev @ `5259676`。
> 本文只写「修什么、怎么修、怎么验收」，不复述审计过程。审计全文见对话记录与 `docs/review/audit-2026-11-06-full.md`。

## 0. 为什么需要这一轮

初版审计报 6 组 P1 + 30 条 P2 + 20 条 API 提案。红队复核后：

- P1 仅 2 组成立（Kafka 批量 ack 失效、README AOT 虚假声称），1 组被推翻（Prompts/Testing 不可打包），3 组降级（mutation 门禁、Skip.Test、两 Activity 工厂）。
- 红队新发现 1 条 P1（Retention `includeFailed` 假 API）。
- P2 仅 2 条完全成立（SagaCompensated 双计数、Specification EF 翻译零回归网）。
- API 提案 0 条完全成立、7 条被推翻。

三条 P1 全部由 v4 修复方案自己引入（批 A 的 Kafka 交互、批 G 的 `includeFailed`、README 未同步），属修复引入的回归，优先级最高。

## 1. 成立项与修法定案（到代码形态）

### 1.1 P1-A Kafka 批量订阅 ack 失效

**机制**：`KafkaBroker.cs:883` 的 finally 兜底判据 `ack is { IsSettled: false }` 未排除 `DeferredAckRegistry.IsDeferred`。批量路径的 handler 是 `EnqueueAsync`（只 `MarkDeferred`，不结算），finally 抢先补 `RejectAsync(requeue:false)` 占用 `TryClaim`，批次侧 `AcknowledgeAsync`/`RejectAsync(requeue:true)` 全被拦截。`EnableAutoOffsetStore=false`（文档推荐的 at-least-once 配置）下批次失败无法 Seek 重投，消息丢失。RabbitMQ 侧四处查 `IsDeferred`（`RabbitMqBroker.cs:562/589/605/620`），Kafka 侧不查。

**修法（3 处）**：

1. `src/PalDDD.Messaging.Kafka/KafkaBroker.cs:883`：判据加 `&& !DeferredAckRegistry.IsDeferred(ack)`，注释同步说明「批量路径已 MarkDeferred，finally 不抢占」。
   - IVT 已存在（`PalDDD.Messaging.csproj` 的 `InternalsVisibleTo Include="PalDDD.Messaging.Kafka"`，v4 批 A 已加），无需改 csproj。
2. `src/PalDDD.Messaging/DeferredAckRegistry.cs:10-11`：勘正「Kafka 零行为变化」的断言，改为「Kafka 的 finally 判据已并入 IsDeferred 判定（v4 批 A 遗漏，本轮补）」。
3. `docs/review/fix-plan-2026-11-07-v4.md` 与 `CHANGELOG.md`：补勘正条目（历史记录不改写，新增勘正段）。

**验收**：

| 测试 | 类型 | 断言 |
|---|---|---|
| `KafkaBatchAckTests.UncommittedOffsetDoesNotAdvance` | 集成（wslc kafka431） | 批量订阅 + `EnableAutoOffsetStore=false` + handler 未显式结算时，offset 不前进 |
| `KafkaBatchAckTests.RequeueRedelivers` | 集成 | 批次 `Reject(requeue:true)` 后同一消息再次交付 |
| `KafkaBatchAckTests.DirectSubscribeUnaffected` | 集成 | 直接订阅（无 MarkDeferred）行为不变 |

变异验证：删 `!IsDeferred` 判据，`UncommittedOffsetDoesNotAdvance` 必须转红。

### 1.2 P1-B Retention `includeFailed` 假 API

**机制**：`RetentionSql.cs:114-115` 的 `InboxDeleteAllSubquery` 条件 `status IN (2,3) AND processed_at IS NOT NULL AND processed_at < @cutoff`；三栈 `MarkFailedAsync` 都只设 `Status`/`LastError`，不设 `ProcessedAt`；`InboxMessage.ProcessedAt` 是 `DateTimeOffset?`（默认 null）。故 Failed 行 `processed_at = NULL`，永远清不掉。ADR-039:21/:31 承诺与实现直接矛盾。

**修法（推荐方案 2：让 Failed 行写 ProcessedAt）**：

1. 三栈 `MarkFailedAsync` 加 `message.ProcessedAt = failedAt`：
   - `InboxDbContext.cs:307-308`：`message.Status = Failed; message.LastError = ...; message.ProcessedAt = failedAt;`
   - `DapperInboxStore.cs:237-238`：同步（SQL `InboxMarkFailed` 加 `processed_at = @processedAt`）
   - `PalOrmInboxStore.cs:237`：同步（SQL 模板加 `processed_at`）
2. `InboxMessage.cs:22` 注释：明确 `ProcessedAt` 是「终态完成时间（Processed 或 Failed）」。
3. `RetentionOptions.cs:37-42` 注释：同步。
4. ADR-039 补勘正：说明 `ProcessedAt` 语义扩展。

**备选方案 1**（改 SQL）：`InboxDeleteAllSubquery` 改为 `status = 3 OR (status = 2 AND processed_at IS NOT NULL AND processed_at < @cutoff)`，但 Failed 行无时间窗口，会立即清理，可能不符运维预期。

**验收**：

| 测试 | 类型 | 断言 |
|---|---|---|
| `RetentionIncludeFailedTests`（三栈矩阵） | 单元 + 集成 | `includeFailed=true` 时 Failed 行被清理；`false` 时保留 |
| `MarkFailed_WritesProcessedAt`（三栈） | 单元 | Failed 行 `ProcessedAt` 非 null |

变异验证：删 `MarkFailedAsync` 的 `ProcessedAt` 赋值，`RetentionIncludeFailedTests` 必须转红。

### 1.3 P1-C README AOT 虚假声称

**机制**：`README.md:65` 写「原生 LZ4/ZStandard 绑定（`PalDDD.Compression.Native`，AOT 安全）」，但 `PalDDD.Compression.Native.csproj:5` Description 写「依赖原生二进制，不可 AOT」，`:11-13` 显式 `IsAotCompatible=false`；`README.md:396` 的 AOT 表不含 Native；`docs/features.md:206` 把 Native 列入豁免。

**修法（纯文档）**：

1. `README.md:65`：删「AOT 安全」，改为「非 AOT（P/Invoke 原生二进制）」。
2. `README.md` 的 AOT 表：把 `PalDDD.Compression.Native` 补进 ❌ 行。
3. `README.en.md` 同步。

**验收**：

- `grep -n "Native.*AOT 安全" README.md README.en.md` 零命中。
- AOT 表完整（✅ 与 ❌ 两栏覆盖所有基础设施包）。

### 1.4 P2-A SagaCompensated 双计数

**机制**：`SagaCompensation.RunAsync:160` 补偿成功即计（为重试耗尽车道）；超时车道经 `CompensateAllAsync → RunAsync` 也走该点，随后 `SagaProcessor.LogTerminalResult:545-548` 再计一次。`LogTerminalResult` 是超时处理器专用（`SagaProcessor.cs:420/509/524`），重试耗尽车道不走，故两条车道口径不同。

**修法**：

1. `SagaProcessor.cs:545-548`（`LogTerminalResult`）：移除 `else if (compensationSucceeded) { PalMetrics.SagaCompensated.Add(1); }`，保留 `saved == 0` 的 Warning。
   - 计数单一发射点留在 `SagaCompensation.RunAsync:160`。
2. `SagaCompensation.cs:154-159` 注释：补「超时车道经此计数，SagaProcessor 不再重复计」。

**验收**：

| 测试 | 类型 | 断言 |
|---|---|---|
| `SagaCompensatedMetricTests.TimeoutLaneWithExecutedSteps_CountsOnce` | 单元 | 超时车道 + 非空 `ExecutedStepKeys` → `SagaCompensated == 1` |
| `SagaCompensatedMetricTests.RetryExhaustedLane_CountsOnce` | 单元 | 重试耗尽车道 → `SagaCompensated == 1` |

变异验证：恢复 `LogTerminalResult` 计数，`TimeoutLaneWithExecutedSteps_CountsOnce` 必须转红。

### 1.5 P2-B Specification EF 翻译零回归网

**机制**：`SpecificationTests.cs:149` 小节标题写「EF Core 集成」，但 `:156/:168/:179` 三个用例都只 `expr.Compile()` 内存求值；`grep ToExpression test/` 只有这一个文件；`docs/conventions.md:823` 明确禁止 `Expression.Invoke`（EF 无法翻译），而内存 `Compile()` 对 `Expression.Invoke` 也能工作，故现有测试无法捕获该回归。

**修法**：

1. 在 `PalDDD.Repository.EFCore.Tests` 补 EF Provider 翻译测试：
   - `spec.ToExpression()` → `DbSet.Where(expr).ToListAsync()`
   - 覆盖 And/Or/Not 组合
   - 断言无 `Expression.Invoke`（或验证 EF 能翻译）
2. `SpecificationTests.cs:149` 标题改为「内存表达式可编译」（EF 测试在别处）。

**验收**：

| 测试 | 类型 | 断言 |
|---|---|---|
| `SpecificationEfTranslationTests.And/Or/Not_Translatable` | 集成（EF InMemory/SQLite） | `Where(spec.ToExpression())` 成功翻译为 SQL |
| `SpecificationEfTranslationTests.NoExpressionInvoke` | 单元 | 表达式树不含 `Expression.Invoke` |

变异验证：把 `ParameterReplacer` 改成 `Expression.Invoke`，翻译测试必须转红。

## 2. 任务清单（4 批，每批一提交）

| 批 | 主题 | 任务（可勾选） | 验收（可验证判据） |
|----|------|--------------|------------------|
| **1** | P1-A Kafka ack | ☐ `KafkaBroker.cs:883` 判据加 `!IsDeferred` ☐ `DeferredAckRegistry` 注释勘正 ☐ v4 方案/CHANGELOG 勘正 ☐ Kafka 批量集成测试 | 真容器：批量未结算时 offset 不前进；`Reject(requeue:true)` 真重投；直接订阅行为不变；变异转红；`dotnet build PalDDD.slnx` 0 警告 0 错误 |
| **2** | P1-B Retention 假 API | ☐ 三栈 `MarkFailedAsync` 写 `ProcessedAt` ☐ `InboxMarkFailed` SQL 加 `processed_at` ☐ `InboxMessage`/`RetentionOptions` 注释 ☐ ADR-039 勘正 ☐ 跨栈矩阵测试 | 三栈 Failed 行被清理；`false` 时保留；变异转红；build 0 警告 |
| **3** | P1-C README AOT | ☐ `README.md:65` 改措辞 ☐ AOT 表补 Native ❌ 行 ☐ `README.en.md` 同步 | grep 无矛盾；AOT 表完整 |
| **4** | P2-A + P2-B | ☐ `SagaProcessor` 移除重复计数 ☐ 补超时车道双计数测试 ☐ 补 EF Provider 翻译测试 ☐ `SpecificationTests` 标题修正 | 双计数转 1；EF 翻译测试通过；变异转红；build 0 警告 |

**批次间硬约束**：

- 批 1 不改公共 API（判据是内部逻辑，IVT 已有），但改行为，需快照 + CHANGELOG 同提交（G23）。
- 批 2 的 `MarkFailedAsync` 不改签名，只改行为（写 `ProcessedAt`），需快照 + CHANGELOG 同提交。
- 批 4 的 EF 测试放 `PalDDD.Repository.EFCore.Tests`，避免 `Core.Tests` 引入 EF 依赖；若新增测试文件，需确认 coverage 门禁分母（参考 `f3e0d0a` 的归属修复）。

## 3. 验证纪律

- 每批提交前：`dotnet build PalDDD.slnx` 0 警告 0 错误（前置门槛，不替代红→绿）。
- 每批红→绿锁定测试 + 变异反事实（删修复因子 → 转红 → 恢复）。
- 批 1/2 需真容器实跑（wslc kafka431 + 数据库），贴计数与输出。
- 全绿引用本轮实测，贴计数行，注明统计范围（src / src+test / 正则形态）。
- 批 1 的集成测试依赖 wslc 容器，本机实跑一次并记录结果。

## 4. 风险与回退

| 风险 | 概率 | 影响 | 处置 |
|------|------|------|------|
| Kafka 判据修改影响直接订阅 | 低 | 中 | 判据加 `!IsDeferred`，直接订阅无 `MarkDeferred`，行为不变；回归测试覆盖 |
| `MarkFailed` 写 `ProcessedAt` 改变 EF 映射 | 低 | 中 | EF 已有 `ProcessedAt` 列与更新逻辑（`:402`），只是语义扩展；跨栈测试验证 |
| EF 翻译测试引入 EF 依赖到 `Core.Tests` | 中 | 低 | 放 `Repository.EFCore.Tests`，避免 `Core.Tests` 引入 EF |
| 批 4 测试触发 coverage 门禁 | 低 | 低 | 参考 `f3e0d0a` 归属修复；必要时 `ALLOW_TEST_ONLY_CHANGE=1` 并写明理由 |
| `RetentionSql` 三栈方言差异 | 低 | 中 | 三栈 SQL 分别验证；跨栈矩阵测试 |

回退：4 批互相独立，任意一批可单独 `git revert`。批 1 的回退点是「删 `!IsDeferred` 判据」；批 2 的回退点是「恢复 `MarkFailedAsync` 不写 `ProcessedAt`」；批 3 是纯文档；批 4 的回退点是「恢复 `LogTerminalResult` 计数 + 删 EF 测试」。

## 5. 交付追踪

| 项 | 状态 |
|----|------|
| 方案 | 本文档（2026-11-07） |
| 实施 | 未启动 |
| 验证 | 未启动 |
| 待决 | 无（修法定案已完成，红队复核已收敛） |

## 6. 明确不做（红队复核后撤回或降级项）

| 项 | 结论 | 依据 |
|---|------|------|
| Prompts/Testing 打包 | 不做 | `README.md:479/487` 已披露 Prompts 非包；`usage.md:307` 是示例引用 |
| EF Idempotency 假终态回退 | 不做 | `IdempotencyProcessor` 不读 record 内存；v4 批 C 已裁决 |
| 压缩输出槽 Release | 不做 | `Rent` 有 256KB 超阈值兜底；`MemoryStream` 无非托管资源 |
| CodeFix 显式实现 | 不做 | `Identifier.Text` 对显式实现是属性名，过滤条件成立 |
| EventLogPositionReserver 倒挂 | 不做 | 已声明权衡（三十八轮 P2）；有 `ReadStreamAsync` 替代 |
| API 提案 3/4/6/8/10/13/14/17 | 不做 | 红队复核被推翻（见审计报告 §四） |
| mutation 门禁扩建 | 降级 P3 | `mutation.yml` 已自述放弃范围；报告在 artifact 可审计 |
| Skip.Test 门禁 | 降级 P3 | CI 有 Docker 不触发；门禁设计意图明确 |
| 两 Activity 工厂测试 | 降级 P3 | `perf-budgets.md:20` 通用条目覆盖；其他工厂同样无专门测试 |

---

## 实施映射（2026-11-07 批 1 落地时补）

§1.1 验收表的测试名与实际方法名的映射（方案原名 → 仓库命名规范实际名）：

| 方案验收名 | 实际方法名 | 理由 |
|-----------|-----------|------|
| `UncommittedOffsetDoesNotAdvance` | `UncommittedOffset_DoesNotAdvance` | `ArchitectureBoundaryTests.TestMethods_MustFollowUnderscorePattern` 要求 `Method_Scenario` 至少 1 个下划线 |
| `RequeueRedelivers` | `Requeue_Redelivers` | 同上 |
| `DirectSubscribeUnaffected` | `DirectSubscribe_Unaffected` | 同上 |

方案验收表原文保留（历史记录不改写）；实际锁定测试以上表为准。
