# 行动项 · 2026-08-22 v5（第四十三轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v5）

> 来源：`docs/review/review-2026-08-22-full-carpet-v5.md`（commit 92a8f95 基线）。
> 上轮 ITM-272..274 已勾销；本轮验证轮确认修复本体全部成立，唯二残留均为注释级姊妹漏网。

## 族 A · P3 注释勘正批（"doc 半面"模式的最后两处残留）

### [x] ITM-275 · R42 注释一词错 + PalOrmSample 旧口径（Dapper 阵营第三次同模式）
- `src/PalDDD.PalORM/Stores/PalOrmInboxStore.cs` 约 154-156 行：注释声称"显式抛 InvalidOperationException"——代码实际抛 `ArgumentNullException`（R42 CA1871 修复改型未同步注释；与 Dapper 姊妹的类型差异属设计守卫 vs 附带抛出，fail-fast 语义一致）。
- `samples/PalDDD.PalOrmSample/Program.cs` 约 39-41 行：注释残留 ITM-269 旧口径"复制到 InMemory/Dapper 会静默 no-op"——Dapper 实际放行（ITM-272 已勘正 OutboxStore doc 与 AotSample，本文件漏同步）。
- 修复后全仓 grep"Dapper"+“no-op/拒绝”组合确认零残留（此为该模式第三轮追杀，必须终局）。

## 族 B · P3 修复轮（新发现）

### [x] ITM-276 · Legacy AddPalMySql 的 applyOptimization 虚假承诺
- `src/PalDDD.Dapper.MySql/MySqlServiceCollectionExtensions.cs` 约 124-144 行：优化打在临时连接上、Dispose 归池即被 ResetConnections 清除——`applyOptimization:true` 实际失效。Obsolete 消息或 remarks 补"该参数在 Legacy 路径不生效"声明（或删参）。

### [x] ITM-277 · PalOrmIdempotencyStore 空响应往返不保真
- `src/PalDDD.PalORM/Stores/PalOrmIdempotencyStore.cs` 约 58-66 行：Completed + 空 bytea 落库读回 `ResponsePayload == null`（与"无响应"不可区分）——幂等命中方以 null 判"无可复用响应"会重放副作用。修复：回填条件放宽为 `!IsDBNull(6) && Completed`（或注释声明二义性 + 消费方契约）。

### [x] ITM-278 · 测试可靠性批（三小项）
- `EventLogEfCoreTests.Append_AllocationPerEvent`：GC 计量跨 await 线程漂移（同仓正确范式 `.GetAwaiter().GetResult()` 已存在）——改同步阻塞或进程级计量。
- `BrokerIntegrationTests` 两个 `MultipleMessages_AllReceived`：断言读 `List` 无锁 vs handler 线程持锁写——读侧同 lock 取快照。
- `OutboxProcessorTests.ExecuteAsync_PollsAtConfiguredInterval`：50ms/250ms/>=3 未同步 SagaProcessorTests 的放宽标准——对齐。

## P3 池

本轮新增 ~6 项 P3 已登记 `.ai/review/action-items-p3-backlog.md` 四十三轮追加段（含 MarkFailedAsync 传感器姊妹缺口、OpenZL 往返缺口、SQLITE_BUSY 参数查证、Analyzer stub 真实接口引用等），30 天老化。

## 完成定义

1. ITM-275 修复后 grep 终局声明（零残留证据入提交信息）。
2. 合入后 gate 22/22 + 测试与基线 1045（1000+45）一致 + 棘轮 ≤152。

## 修复轮验证记录（2026-08-22 同日）

- **ITM-275**：双注释勘正 + **grep 终局声明**——"Dapper"+no-op/拒绝 组合全仓 5 处命中均为无关语境
  （Dapper 自身 no-op 方法/参数校验对齐/ReleaseForRetry 语义），**Dapper 阵营误分类零残留，
  "doc 半面"五轮模式正式关闭**。
- **ITM-276**：Obsolete 消息 + remarks 双声明 applyOptimization 不生效机理（ResetConnections 归池清除）。
- **ITM-277**：`Length > 0` 守卫移除（空 bytea 同样回放 MarkCompleted）+ 传感器（空响应落库读回
  非 null 空序列）绿；棘轮纪律自纠（新传感器冗余 IsNotNull 即时移除，152 维持）。
- **ITM-278**：GC 计量同步化（GetAwaiter().GetResult() 范式）+ MultipleMessages 读侧 lock 快照×2 +
  轮询阈值对齐×2（>=3→>=2，150ms/20ms 余量充足处保留）。
- **终基线**：build 0/0；测试 1046 = 1001 通过 + 45 fail-closed + 0 代码失败（+1 传感器）；
  dialect-probe 40/40；机械全绿（棘轮 152；gate 21/22 之 G22=待提交）。
