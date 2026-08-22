# 行动项 · 2026-08-22 v4（第四十二轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v4）

> 来源：`docs/review/review-2026-08-22-full-carpet-v4.md`（commit 934c0c6 基线）。
> 上轮 ITM-268..271 已勾销；本轮验证轮确认代码半面全部成立，但 doc 半面发现一处错误（F1）与一处同提交注释漏网。

## 族 A · P2（验证轮抓出的 R41 修复缺陷·doc 半面）

### [x] ITM-272 · F1：OutboxStore.MarkProcessed doc 把 Dapper 错归"拒绝"阵营——Dapper 实为放行
- **位点**：`src/PalDDD.Transactions/OutboxStore.cs` 约 44 行（R41 ITM-269 新增的前置条件声明）。
- **铁证（主线程亲验）**：`SqlTemplates.OutboxMarkProcessed` 的 WHERE 含 `(@owner IS NULL AND locked_by IS NULL)` 放行分支 + `DapperOutboxStore` 传 `owner = message.LockedBy`——未租约消息（LockedBy=null）直达放行分支，UPDATE 成功。Dapper 与 PalORM 同为 owner-null 放行语义（仅内存同步弱于 PalORM：无 affected 门控、无 Status 回写）；InMemory 才是拒绝方。
- **修复**：doc 改为"**PalORM/Dapper 栈放行**（owner-null 分支；Dapper 内存侧不回写 Status）、**InMemory 栈拒绝**（租约守卫静默 no-op）"。修复后以三栈行为对照表做一次自查（本条即为对照表产物）。
- **顺带（同 doc P3）**："GetPending 观测结果不构成标记资格"不完整——租约过期未重租的行（LockedBy 非 null）经 token 分支仍可标记成功（原 worker 过期后完成属合理语义）。补"租约过期窗口例外"限定语。

## 族 B · P3 勘正批（R41 同提交漏网 + 姊妹缺口）

### [x] ITM-273 · 第六处三方一致漏网（934c0c6 同提交内）
- `samples/PalDDD.AotSample/Program.cs` 约 58-60 行：注释仍称"PalOrmSample 走的是 PalORM 特有的无租约直标路径"——ITM-269 同提交已把 PalOrmSample 改为 Lease 路径。改为"ECommerce/PalOrmSample 均传 Lease 返回值（msgs[0]/leased[0]）；owner-null 直标分支仅 PalORM/Dapper 放行，样本不走该分支"。
- `test/PalDDD.PalORM.Tests/PalOrmSagaMultiDialectTests.cs:115`：注释"SagaId 是新 Ulid 与原始不同"失实——JSON 路径 SagaId 已恢复（同文件 L181 测试互证）。勘正并补 SagaId 断言。
- `OutboxSqliteConcurrencyTests` 三种 ITM-261 措辞变体统一；`PalOrmSagaStateStoreTests:114` 行尾英文句点。

### [x] ITM-274 · PalOrmInboxStore ProcessingStartedAt=null 静默（R41 勘正暴露的姊妹缺口）
- `src/PalDDD.PalORM/Stores/PalOrmInboxStore.cs` 约 159/184 行：null 插值参数化 → SQL `=NULL` 永假 → affected=0 静默零变更；Dapper 姊妹同场景 C# 层抛 InvalidOperationException（fail-fast）。对齐：入口显式守卫或注释声明静默语义。

## P3 池

本轮新增 ~8 项 P3 已登记 `.ai/review/action-items-p3-backlog.md` 四十二轮追加段（SagaManager 临时实例掩盖子 saga Interrupt 前提、Kafka SubscribeAsync 缺 _disposed 守卫、InfraBenchmarks Append 无界流增长、EventLog 分配测量跨线程漂移等），30 天老化。

## 完成定义

1. ITM-272 修复后 doc 声明与三栈实现逐一对齐（对照表归档注释或测试）。
2. 合入后 gate 22/22 + 测试与基线 1044（999+45）一致 + 棘轮 ≤152。
3. 修复轮后跟验证轮。

## 修复轮验证记录（2026-08-22 同日）

- **ITM-272**：doc 阵营勘正（PalORM/Dapper 放行 + fencing 强度差异声明 + InMemory 拒绝）+ 租约过期窗口例外限定语——与三栈行为对照表逐条对齐。
- **ITM-273**：第六处注释批全清（AotSample 描述同步 ITM-272 新口径、Saga 测试失实注释勘正并补 SagaId 断言、OutboxSqlite 措辞统一、英文句点）。
- **ITM-274**：两处 null ProcessingStartedAt 显式 fail-fast（`is null → throw ArgumentNullException`——CA1871 禁 ThrowIfNull 于可空结构）+ 传感器（ParamName 断言）绿。
- **终基线**：build 0/0；测试 1045 = 1000 通过 + 45 fail-closed + 0 代码失败（+1 传感器）；AotSample all passed；机械全绿（棘轮 152；gate 21/22 之 G22=待提交）。
