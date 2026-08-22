# 行动项 · 2026-08-22 v7（第四十五轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v7）

> 来源：`docs/review/review-2026-08-22-full-carpet-v7.md`（commit 1e1a4db 基线）。
> 本轮为零新发现 P0-P2 的收束轮；R44 修复经敌对核全部通过且零缺陷。

## 族 A · P3 修复批（本轮新发现，全部低危害）

### [ ] ITM-283 · 语义对齐批（三处）
- `InMemoryInboxStore.cs` timeout=0 + 同时刻重入语义与 Dapper 栈分叉（`0 < 0` false 允许 vs `< now` false 拒绝）——对齐或声明边界语义。
- `EndpointExtensions.cs` 反序列化段 PalValidationException catch 返回裸 400 无 body——统一走 `ValidationProblemResponseFactory`。
- `OutboxDbContext.cs` ExecuteUpdate affected=0 后的兜底查询对 token 拒绝是冗余 DB 往返——提前 return（仅 provider 不支持路径走兜底）。

### [ ] ITM-284 · 守卫与转义补缺批
- `SqliteJsonExtensions.EscapeJsonPathSegment` 违禁集漏 `[` `]`（数组索引语法静默错查）——补入。
- `EFCore UnitOfWork` Begin/Commit/Rollback 补 `ObjectDisposedException.ThrowIf`（三栈 3/3、1/3、0/3 分叉）。
- `PalLogger.cs:18` 构造补 null 守卫。

### [ ] ITM-285 · 测试断言强化批
- `ProjectionTests` L114/L201 两处 `Contains` → `Count==1`（ITM-281 同款残留）。
- `SerializationTests` 2 处 + `SystemCompressionTests` 8 处字节断言 `IsEquivalentTo` → `IsEqualTo`（无序等价对置换损坏漏检）。
- `SendAsync_PassesCancellationToken` 断言与名不符——重写（记录 token 探针）或删。
- `PalOrmIdempotencyStoreTests` L109 TryStart 返回值丢弃——补 IsNotNull 前置。
- `FailingSaga` 补 `MaxRetries=0`（消 3 秒真实等待）。

### [ ] ITM-286 · 可选观察批（随下次触碰处理）
- `BrokerIntegrationTests` 非 OCE 测试 entered 后 Dispose 前加稳定窗口（检出力从大概率到确定）。
- `TimestampDefaultsTests` Clock 名字匹配升级形状匹配（TimeProvider 类型+非 AsyncLocal）。
- bench 注释 Inbox 基准声称失实（两处）；`Measure/MeasureAction` 合并；`DapperStoreTests` 文件头 Collection 残留。

## P3 池

本轮新增 P3/P4 已登记 `.ai/review/action-items-p3-backlog.md` 四十五轮追加段，30 天老化。

## 完成定义

1. 合入后 gate 22/22 + 测试与基线 1050（1005+45）一致 + 棘轮 ≤145。
