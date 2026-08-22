# 行动项 · 2026-08-22 v6（第四十四轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v6）

> 来源：`docs/review/review-2026-08-22-full-carpet-v6.md`（commit 5ec02bb 基线）。
> 本轮为 P3 存量清偿（~60 文件）的强制验证轮——代码半面全部通过，缺陷集中在断言/注释半面。

## 族 A · P2（验证轮抓出的清偿缺陷）

### [x] ITM-279 · F1：Rabbit 非 OCE 用例的 handlerEntered CAS 断言对目标回归失明
- **位点**：`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs` 约 559/583 行（族 5 TST-121 新写用例）。
- **机理（片 7 源码级推演）**：`Interlocked.CompareExchange(ref handlerEntered, 1, 0) == 0` 才抛——requeue:true 回归时第二次进入 CAS 失败后**直接 return 并被 ACK，不计数**，ErrorCount==1 与 handlerEntered==1 双断言照绿。该用例的"不重投"守护在其目标回归场景下完全失明。
- **修复**：`Interlocked.Increment(ref handlerEntered) == 1` 时抛（首次进入抛、后续计入），末尾断言 `handlerEntered == 1`。修复后推演回归形态必红。

## 族 B · P3 修复批（清偿残留 + 姊妹波及）

### [x] ITM-280 · 清偿注释动机失实两处 + Builder 异常分叉（"doc 半面"模式第六轮重现）
- `DapperBulkCopy.cs` SRC-209 注释：原 Array.IndexOf"重复列名静默错列"不可达（DataTable.Columns.Add 重复名先抛 DuplicateNameException）——真实收益仅 O(n²)→O(n)。勘正注释。
- `InMemoryOutboxStore.cs` SRC-105 注释：O(1) 短路命中的是终态重复 Mark/未租约引用，非"僵尸引用"（后者字段冻结在租约快照仍需 Contains）。勘正注释。
- `MessageEvolutionBuilder.Add` 重复分支改抛 `MessageEvolutionException`（SRC-304 目标在 Builder 主入口未达成——现抛 InvalidOperationException 与 Pipeline 分叉）。

### [x] ITM-281 · 姊妹波及与守卫对齐批
- `PalOrmUnitOfWork.cs:35` 构造补 `ArgumentNullException.ThrowIfNull(session)`（三栈 Dapper/EFCore 均有，PalORM 独缺——SRC-108 修复的姊妹漏网）。
- `IdempotencyTests.cs` L132-205：4 处指标 `Contains(1)` + 无 NotInParallel——族 3 TST-109 改造的同类波及遗漏（RecordingMeterListener 进程级广播互染下恒过）。改 `Count().IsEqualTo(1)` + `[NotInParallel]`。
- `PalOrmConcurrencyTests.cs`：busy_timeout PRAGMA 只设在 setup session，10-20 个并发 worker 连接未获——移入 `CreateSharedFileSessionAsync`（真正竞争的连接）。

### [x] ITM-282 · 测试锁定补缺批
- `FakeTimeProviderTimerTests` 补 `Change_Throws_NotSupported`（公共契约从恒 false 改抛——行为变更零锁定）。
- `SagaTests` 补 Forward 多已执行步骤场景（现全部 ≤1 已执行步，Forward 正序回归不可见——Backward 有 3 步锁定，不对称）。
- `TestEnvironment` RabbitMqPort fail-closed 行为补负向用例（非法端口→InvalidOperationException）。

## P3 池

本轮新增 ~8 项 P3 已登记 `.ai/review/action-items-p3-backlog.md` 四十四轮追加段（obj 排除剩余 ~9 处巧合式安全、Kafka 守卫 TOCTOU 残窗、Saga 观察者 matchedKey 归因声明、Npgsql fallback 文档修正等），30 天老化。

## 完成定义

1. ITM-279 修复后做推演自证（回归形态必红）。
2. 合入后 gate 22/22 + 测试与基线 1047（1002+45）一致 + 棘轮 ≤145。

## 修复轮验证记录（2026-08-22 同日）

- **ITM-279**：CAS 改 `Interlocked.Increment(ref handlerEntered) == 1` 时抛——检出力由构造给出：
  任何第二次进入（requeue 回归）必使计数 >1 → `handlerEntered == 1` 断言必红（原 CompareExchange
  形态下第二次进入直接 return 被 ACK 不计数——失明根因消除）。
- **ITM-280**：SRC-209 注释勘正（重复列名场景不可达——DataTable 先抛 DuplicateNameException，
  真实收益 O(n²)→O(n)）；SRC-105 注释勘正（O(1) 短路命中终态/未租约，僵尸引用仍需 Contains）；
  Builder 重复分支改抛 MessageEvolutionException（单点 catch 目标在主入口达成；异常类派生自
  InvalidOperationException 向后兼容）。
- **ITM-281**：PalOrmUnitOfWork 守卫（三栈对齐）；IdempotencyTests 4 处 Count==1 + 类级
  NotInParallel（TST-109 同款根治）；busy_timeout 移入 CreateSharedFileSessionAsync（全部
  连接获得——原仅 setup）。
- **ITM-282**：Change_Throws_NotSupported / ForwardPolicy_MultipleExecutedSteps（3 步正序锁定，
  补齐与 Backward 的对称）/ RabbitMqPort_InvalidEnvironmentVariable 三测试锁定。
- **终基线**：build 0/0；测试 1050 = 1005 通过 + 45 fail-closed + 0 代码失败（+3 锁定测试）；
  dialect 40/40；机械全绿（棘轮 145；gate 21/22 之 G22=待提交）。
