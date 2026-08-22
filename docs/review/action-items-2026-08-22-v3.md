# 行动项 · 2026-08-22 v3（第四十一轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v3）

> 来源：`docs/review/review-2026-08-22-full-carpet-v3.md`（commit 0b1b60f 基线）。
> 上轮 ITM-262..267 已勾销（action-items-2026-08-22-v2.md）；本轮验证轮确认主体通过，唯 ITM-265 的终验传感器恒真需返工。

## 族 A · P2（验证轮抓出的修复自身缺陷）

### [ ] ITM-268 · F1：AotSample 终验恒真——租约遮蔽使 GetPending==0 在 bug 复活时同样成立
- **位点**：`samples/PalDDD.AotSample/Program.cs` 约 61-63 行（R40 ITM-265 新增的 afterMark 终验）。
- **机理（片 8 三文件闭环推演）**：MarkProcessed 被引用守卫拒绝（no-op）时，successor 仍 Pending 但持未过期租约 → InMemory QueryPending 的 `LockedUntil <= now` 过滤把它排除 → afterMark 照样 0——终验对它声称要防的自欺无检出力。
- **修复**：终验改状态断言 `pending[0].Status == OutboxStatus.Processed`（成功路径原地改写入参，拒绝路径状态仍 Pending）；或 FakeTimeProvider 推进租约过期后再 GetPending。修复后做一次 mutation 自证（临时改回传 outboxMsg → 终验必须红）。
- **顺带（同点位 P3）**：①59 行 `pending[0]` 在 60 行 Check 之前——空租约时 IndexOutOfRange 绕过 failures 机制，调序；②58 行注释"对齐 ECommerce/PalOrmSample 的 pending[0] 写法"失实——PalOrmSample 走的是 PalORM 特有无租约直标路径，改准确。

### [ ] ITM-269 · F2：PalOrmSample 演示路径违反接口契约（GetPending 观测用途被当处理管线）
- **位点**：`samples/PalDDD.PalOrmSample/Program.cs` 约 39-44 行。接口 doc 明确 GetPendingMessagesAsync"只用于观测/健康检查，不获取租约"，样本却 GetPending→MarkProcessed 当管线；该路径仅 PalORM 放行（owner-null 分支），复制到 InMemory/Dapper 静默 no-op。
- **修复**：改走 Lease 路径（对齐 ECommerce），或注释显式声明"仅 PalORM 实现支持无租约直标"。
- **姊妹（转 src 裁决的 P3 一并做）**：`src/PalDDD.Transactions/OutboxStore.cs` 的 MarkProcessed XML doc 补前置条件声明——未租约消息直标在 InMemory（拒）/PalORM（放行）语义相反，接口层未声明。

## 族 B · P3 修复轮（姊妹漏网与三方一致）

### [ ] ITM-270 · PalOrmProjectionCheckpointStore lease_until 无 IsDBNull 容错（三十八轮只修了 Dapper 姊妹）
- **位点**：`src/PalDDD.PalORM/Stores/PalOrmProjectionCheckpointStore.cs` 约 57 行（GetUtc(reader, 5) 直读）。Dapper 版三十八轮已修 nullable 物化+兜底；手工运维插入 NULL lease_until 行时 PalORM 栈抛 SqlNullValueException。
- **修复**：`reader.IsDBNull(5) ? default : GetUtc(reader, 5)` 对齐姊妹。

### [ ] ITM-271 · 三方一致勘正批（5 处漏网）
- `OutboxSqliteConcurrencyTests.cs:79/166`："生产 Lease 不可翻译"旧措辞与类尾勘正矛盾（R40 勘正自身漏网）。
- `PalOrmSagaStateStoreTests.cs:114`："AsyncLocal 泄漏"措辞——src 已改 CWT。
- `PalOrmOutboxMultiDialectTests.cs:16`：类头"Base64 Payload"——070b42f 已原生化。
- SagaStateDbContext（约 131-135 行）与 DapperInboxStore（约 141-144 行）：注释机制描述失真（EF Entry() 语义/ProcessingStartedAt 实际抛点在 C# 层）。

## P3 池

本轮新增 ~10 项 P3 已登记 `.ai/review/action-items-p3-backlog.md` 四十一轮追加段（含 Dapper MarkProcessed Status 不同步跨栈分叉、PalOrmOutboxStore 截断族、ServiceRegistrationTests singleton 名实不符、Pooled 分配比较 tiered-JIT 假红窗、boundary obj 过滤不对称、mojibake 注释恢复等），30 天老化。

## 完成定义

1. ITM-268 修复后必须做 mutation 自证（改回 bug 形态终验红）。
2. 全部修复自带回归验证；合入后 gate 22/22 + dialect-probe 40/40 + 测试与基线 1043（998+45）一致 + 棘轮 ≤152。
3. 修复轮后跟验证轮。
