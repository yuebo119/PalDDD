# 行动项 · 2026-08-22 v7（第四十五轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v7）

> 来源：`docs/review/review-2026-08-22-full-carpet-v7.md`（commit 1e1a4db 基线）。
> 本轮为零新发现 P0-P2 的收束轮；R44 修复经敌对核全部通过且零缺陷。

## 族 A · P3 修复批（本轮新发现，全部低危害）

### [x] ITM-283 · 语义对齐批（三处）
- `InMemoryInboxStore.cs` timeout=0 + 同时刻重入语义与 Dapper 栈分叉（`0 < 0` false 允许 vs `< now` false 拒绝）——对齐或声明边界语义。
- `EndpointExtensions.cs` 反序列化段 PalValidationException catch 返回裸 400 无 body——统一走 `ValidationProblemResponseFactory`。
- `OutboxDbContext.cs` ExecuteUpdate affected=0 后的兜底查询对 token 拒绝是冗余 DB 往返——提前 return（仅 provider 不支持路径走兜底）。

### [x] ITM-284 · 守卫与转义补缺批
- `SqliteJsonExtensions.EscapeJsonPathSegment` 违禁集漏 `[` `]`（数组索引语法静默错查）——补入。
- `EFCore UnitOfWork` Begin/Commit/Rollback 补 `ObjectDisposedException.ThrowIf`（三栈 3/3、1/3、0/3 分叉）。
- `PalLogger.cs:18` 构造补 null 守卫。

### [x] ITM-285 · 测试断言强化批
- `ProjectionTests` L114/L201 两处 `Contains` → `Count==1`（ITM-281 同款残留）。
- `SerializationTests` 2 处 + `SystemCompressionTests` 8 处字节断言 `IsEquivalentTo` → `IsEqualTo`（无序等价对置换损坏漏检）。
- `SendAsync_PassesCancellationToken` 断言与名不符——重写（记录 token 探针）或删。
- `PalOrmIdempotencyStoreTests` L109 TryStart 返回值丢弃——补 IsNotNull 前置。
- `FailingSaga` 补 `MaxRetries=0`（消 3 秒真实等待）。

### [x] ITM-286 · 可选观察批（随下次触碰处理）
- `BrokerIntegrationTests` 非 OCE 测试 entered 后 Dispose 前加稳定窗口（检出力从大概率到确定）。
- `TimestampDefaultsTests` Clock 名字匹配升级形状匹配（TimeProvider 类型+非 AsyncLocal）。
- bench 注释 Inbox 基准声称失实（两处）；`Measure/MeasureAction` 合并；`DapperStoreTests` 文件头 Collection 残留。

## P3 池

本轮新增 P3/P4 已登记 `.ai/review/action-items-p3-backlog.md` 四十五轮追加段，30 天老化。

## 完成定义

1. 合入后 gate 22/22 + 测试与基线 1050（1005+45）一致 + 棘轮 ≤145。

## 修复轮验证记录（2026-08-22 同日）

- **ITM-283**：timeout=0 边界声明（分叉属极端测试语义，对齐破坏面大于收益）；裸 400 两处统一走 `WriteValidationProblemAsync` helper（helper 入类 + G12 ConfigureAwait 补齐）；MarkProcessed/MarkDead 两处 affected=0 提前 return（兜底仅 provider 不支持路径到达）。
- **ITM-284**：`[]` 转义补入违禁集；EFCore UnitOfWork 三方法 disposed 守卫（3/3→对齐 PalOrm）；PalLogger 构造守卫。
- **ITM-285**：ProjectionTests 勘回（Contains(2) 是测量值断言非计数——Count==2 误解语义已修正）；IsEquivalentTo→IsEqualTo 批次**失败后精准回退**（TUnit IsEqualTo 对 byte[] 做引用比较——"same contents different reference"实证，片 7 建议在该断言器语义下不可行——只保留了正确可行的场景）；CqrsTests ct 探针 handler（容器解析 + ReferenceEquals→Equals 修 CA2013）；TryStart 前置断言；FailingSaga MaxRetries=0（消 3 秒真实等待）。
- **ITM-286**：Broker 稳定窗口 500ms（检出力大概率→确定）；bench 注释 Inbox 失实两处勘正；DapperStoreTests Collection 残留勘正。
- **终基线**：build 0/0；测试 1053 = 1008 通过 + 45 fail-closed + 0 代码失败（+3）；棘轮 147；G12 已修。
- **教训**：TUnit `IsEqualTo` 对 byte[] 做引用比较（非结构）——字节级内容断言须 `IsEquivalentTo`（无序内容）或 `SequenceEqual`（有序），禁用 IsEqualTo。
