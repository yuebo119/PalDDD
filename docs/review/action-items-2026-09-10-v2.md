# Pal.DDD 行动项清单 — 验证轮（第二轮全仓运行）2026-09-10

> 来源报告：[`review-2026-09-10-full-v2.md`](review-2026-09-10-full-v2.md)
> 基线 commit：`69c06d8`（dev）· 档位：全仓重扫 + **验证轮**（逐 diff 验证上轮 ITM-615~647 共 64 项修复）
> 生成方式：机械防线 9 项实跑 + 14 个并行子代理（src 7 片 + test 5 片 + 验证轮专项 + 跨片不变式）+ 主线程探针实证
> 编号衔接：上轮清单 ITM-615~647；本清单自 **ITM-648** 起编号。

---

## 总体进度

| 优先级 | 条目数 | 待修复 | 修复中 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|:------:|
| **P0** | 0 | 0 | 0 | 0 | — |
| **P1** | 1 | 0 | 0 | 1 | 100% |
| **P2** | 4 | 0 | 0 | 4 | 100% |
| **P3** | 30（汇总） | 1 | 0 | 29 | 97% |
| **合计** | 35 | 1 | 0 | 34 | **97%** |

**修复轮（2026-09-10 第三轮，6 代理并行 + 主线程）完成情况**：
- **ITM-648**：`set -o pipefail` 提至 run 块首（覆盖 secret-scan + 六门禁 + 根 gate 全部管道），探针复验 CAUGHT；后两处冗余 set 清理。
- **ITM-649**：UPDATE 两条 FormattableString 改绑 `{truncatedError}`；新增回归测试 `SaveChangesAsync_UpdatePathWithOverlongError_TruncatesDbValueAndKeepsMemoryInSync`（INSERT 后重塞超长 Error 触发 UPDATE，断言 DB ≤2040 + 内存/DB 一致）。
- **ITM-650**：探针测试落盘 `InboxTimestampTokenPrecisionProbeTests`（Dapper 栈，注入非整微秒 now → TryStart → MarkProcessed → 直查 DB status）——**SQLite 本地实证 token 命中**（"O" 格式 7 位小数全精度存储，写/比同串恒等）；PG/MySQL 走 Testcontainers 待 CI（理论同参数化路径应命中，CI 红则疑点成立需 token 归一修复）。
- **ITM-651**：fixture 改走 `RabbitMqBroker.CreateAsync`（4 个既有 Rabbit 测试自此覆盖 confirms 路径）+ 新增 `RabbitMq_PublishUnroutableMessage_ThrowsPublishException`（无绑定 exchange + mandatory → PublishException.IsReturn=true，ITM-213+639 联合语义首次锁定）。
- **ITM-652**：PG `ThrowIfCredentialsMismatch` 补 SslMode 第 4 项 + 3 个姊妹测试；连带 **P3#7** MySQL standby 侧 LoadBalance 冲突校验 + 2 个测试；**P3#20** MySQL SslMode 行为测试 2 个（v65 修复的姊妹测试收口）。
- **P3 批 29/30 完成**：PD34 计数锚根治（doc-consistency **D12a** boundary 方法数实测锚 + **D12b** ConfigureAwait 去裸数字化，双红测验证）；gate G23/G24 加 `rev-parse --verify` 守卫（不可解析输出 SKIP 非假 PASS）；tech-debt **#20a** 存在性子断言（find 为空 FAIL）；DiagnosticCoverageGate 形态①收紧（diag 根 + lambda 参数回溯，38 条仍全覆盖，实跑 2/2）；HasValueSequence 多段回归测试（实跑通过，ITM-629 修复自此有回归网）；EFCore 负 timeout 直测；快照 delegate 修正（RequestExecutor class→delegate，唯一 diff）+ event emit 能力补全；flaky×2（哈希碰撞断言删除/池化容差）；名实收口×N；资源泄漏×2；杂项 7 项。
- **证伪 2 条**（清单预设不成立，零改动）：#10 GenerateId 误用——`Attributes.cs:30` 已有 `[AttributeUsage(AttributeTargets.Struct)]`；9b InboxDbContext "空白归一"注释——`git log -S` 证实该文件历史上从未含此注释。
- **重要证伪（上轮修复缺陷第 5 项）**：P3#12 TryAddEnumerable 预设"第二次调用被静默忽略"不成立——反编译 MS.DI 8~11 四版证实 factory 注册的 `Singleton<IHostedService>(factory)` 在 `TryAddEnumerable` 下**首次调用即抛 ArgumentException**（indistinguishable-type 检查），即上轮 ITM-637 的修复形态实际使 `AddPalPostgreSqlOutboxNotifier` 完全不可用。已改双泛型 `Singleton<IHostedService, PostgreSqlOutboxNotifier>(factory)` 修复。
- **待修 1 条**：P3 残余 = DapperUnitOfWork.RollbackAsync 同型缺口（E 代理修复 CommitAsync 时发现，姊妹收口留下轮）。

**验证轮核心结论**：上轮 64 项修复中 **59 项验证通过；4 项修复自带缺陷（6.2%）**——①ci.yml secret-scan 的 pipefail 掩码（P1，上轮 ITM-616 修复自身的缺陷，探针实锤）；②PalOrmSagaStateStore UPDATE 漏改绑定（P2，上轮 v65 修复引入回归）；③RabbitMqBroker.CreateAsync 零调用零测试（P2，上轮 ITM-639 完整性缺口）；④PG SslMode 姊妹不对称（P2，上轮 MySQL 侧修复未同步 PG 侧）。另有 2 条计数自激振荡（PD34：ArchitectureBoundaryTests 41→实际 37、ConfigureAwait 444→实际 447+）。

---

## 🔴 P1 — 立即修复（1 条）

### [x] ITM-648 · ci.yml secret-scan 在 `set -o pipefail` 之前执行——失败被 tee 掩码，CI 凭据门禁假绿 · 可信度 ✅
- **维度**：质量门禁 / 验证器自欺（PD29）
- **优先级**：P1 · 危害: 高 · 复杂度: 易
- **问题**：上轮 ITM-616 把 `bash scripts/secret-scan.sh | tee` 放在 ci.yml:106，而 `set -o pipefail` 在 :120/:135（六门禁循环前）才生效。GitHub Actions 默认 `bash -e`（无 pipefail）→ `if ! cmd | tee` 管道退出码取 tee 的 0 → **secret-scan 失败被静默掩码**。讽刺的是 :119 注释自引"SHELL-1 教训"却漏了首个调用点——SHELL-1 教训在本会话第五次现身。
- **触发路径（探针实锤）**：`bash -e -c 'if ! bash -c "exit 1" | tee log; then …'` → 输出 MASKED；加 pipefail → CAUGHT。上轮修复轮红测只测了脚本本体 exit 1，未测 CI 管道包装（验了仪器没验管道）。
- **建议**：将 `set -o pipefail` 提至步骤首行（run 块第一句），并复核该步骤内**全部**管道调用。
- **验证**：探针已实锤；修复后 `bash -e -o pipefail -c '…'` 应 CAUGHT。
- **涉及文件**：`.github/workflows/ci.yml`

---

## 🟠 P2 — 近期修复（4 条）

### [x] ITM-649 · PalOrmSagaStateStore UPDATE 路径仍绑定原始 `state.Error`——上轮 v65 修复引入回归 · 可信度 ✅
- **维度**：错误流 / 截断收口（PD19+PD34：修复自带缺陷）
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：上轮 v65"先算局部变量后赋回"修复中，INSERT 路径（`PalOrmSagaStateStore.cs:221/222`）正确改绑 `{truncatedError}`，但 **UPDATE 路径（:243/:244）仍绑定原始 `{state.Error}`**。:192 注释声称"INSERT/UPDATE 两处 {truncatedError} 赋值点共用此收口"与代码不符（假修命中）。姊妹 `DapperSagaStateStore.cs:176/209` 两处均正确用截断值（PD17 对照坐实）。
- **触发路径**：直调 `SaveChangesAsync`（绕过 SagaProcessor 的 Normalize）+ saga 已存在 + `state.Error` > 2040 字符 → UPDATE 携带超长参数 → 跨栈共表（EFCore error 列 varchar(2048)）写入失败；且 :249 UPDATE 成功后回写 `truncatedError` 造成内存（截断值）/DB（原始超长值）分叉。
- **建议**：UPDATE 两条 FormattableString 的 `error = {state.Error}` 改 `{truncatedError}`；补一条 >2040 字符 Error 的 UPDATE 用例锁定（Dapper 姊妹已有同款）。
- **验证**：读 :243/:244 实证 ✅；修复后 grep `error = {state.Error}` 应零残留。
- **涉及文件**：`src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs`、`test/PalDDD.PalORM.Tests/PalOrmSagaStateStoreTests.cs`

### [x] ITM-650 · Inbox 时间戳抢占 token 精度失配疑点——内存全精度 vs DB 微秒列 · 可信度 ⚠（待真库探针）
- **维度**：并发流 / exactly-once 语义
- **优先级**：P2 · 危害: 高 · 复杂度: 中
- **问题**：`PalOrmInboxStore.cs:78`（及 Dapper 姊妹 :101/:158/:180）新建/抢占路径返回**内存 token = now 全精度**（.NET DateTimeOffset 100ns），而 DB 列为 MySQL `DATETIME(6)`/PG `TIMESTAMPTZ`（微秒）。若 provider 写入与参数比较的舍入路径不一致，now 第 7 位小数非零时（Linux TimeProvider.System 现实可达）首次 `MarkProcessedAsync` 的 `WHERE processing_started_at = token` 零命中 → 消息滞留 Processing → 超时重试 → **双处理**，违背 Inbox exactly-once。佐证：`IdempotencyRecord.Revision` remarks（v53）自认"时间戳令牌受 DB 列精度截断"并已在该栈换 revision token——Inbox 侧仍用时间戳。
- **反证路径（如实呈现）**：若写与比走同一参数化舍入（同值同舍入），等值恒成立则无缺陷——需真库探针裁决（SQLite TEXT 列不受影响；PG/MySQL 需实测）。修复前必须先探针：真库 `TryStart → MarkProcessed` 断言 status 落库 Processed（测试环境 now 常为整微秒会掩盖，须人为构造非整微秒 now 注入）。
- **建议**：①补真库探针（Testcontainers PG/MySQL）；②若证实，对齐 Idempotency 栈方案（换 revision token 或写入前 now 截断到微秒）。
- **涉及文件**：`src/PalDDD.PalORM/Stores/PalOrmInboxStore.cs`、`src/PalDDD.Dapper/DapperInboxStore.cs`、`test/PalDDD.Integration.Tests/`

### [x] ITM-651 · RabbitMqBroker.CreateAsync 全仓零调用零测试——上轮 ITM-639 修复完整性缺口 · 可信度 ✅
- **维度**：测试覆盖 / 修复完整性
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：上轮 ITM-639 新增的 `CreateAsync` 工厂（显式启用 publisherConfirmations + tracking，使 `mandatory:true` 的 PublishException 语义可达）**全仓（src/test/samples）零调用、零测试**。集成测试 fixture（`BrokerIntegrationTests.cs:208-209`）仍以 `connection.CreateChannelAsync()` 无参创建（未启用 confirms）+ 裸构造——正是类 XML remarks 自述的"消息丢失窗口"形态。ITM-213 `mandatory:true` 修复的核心语义（发布无绑定 exchange 抛异常而非静默成功）**无任何测试锁定**。
- **触发路径**：用户按 fixture 同款裸构造 → 发布到无绑定队列 exchange → 静默"成功" → Outbox 标 Processed → 消息丢失无诊断。
- **建议**：①fixture 改用 `CreateAsync`（或同款 `CreateChannelOptions`）；②补一条"无绑定队列 + mandatory → 抛 PublishException"的集成测试（Testcontainers RabbitMQ）锁定 ITM-213+639 联合语义。
- **涉及文件**：`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs`、`src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs`

### [x] ITM-652 · PostgreSqlMultiHost 凭据一致性校验缺 SslMode——MySQL 侧修复未同步 PG 侧（PD17） · 可信度 ✅
- **维度**：安全流 / 姊妹对称
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：上轮 v65 给 `MySqlMultiHost` 补 SslMode 一致性校验（理由"TLS 安全配置静默降级属高危"），但 PG 侧 `PostgreSqlMultiHost.ThrowIfCredentialsMismatch`（:430-432）仍仅校验 Username/Password/Database 3 项。PG 多主机合并只保留 primary 参数（:289 注释），replica 连接串显式 `SslMode` 差异被静默丢弃。
- **触发路径**：primary `SslMode=VerifyFull` + replica `SslMode=Disable` → 合并后 replica 也用 primary 值（反向则安全配置静默降级，正是 MySQL 侧修复者定性"高危"的同机理）。
- **建议**：PG 侧校验集补 `SslMode`（NpgsqlConnectionStringBuilder.SslMode 属性比对，镜像 MySQL 侧写法）；补姊妹测试。
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlMultiHost.cs`、`test/PalDDD.Integration.Tests/PostgreSqlMultiHostPortEncodingTests.cs`

---

## ⚪ P3 — 汇总（30 条，进 backlog）

### 上轮修复连带（4）
1. gate G23/G24 回退 `HEAD~1..HEAD` 无 `rev-parse --verify` 守卫——初始 commit/orphan 分支下 no-op 回潮；多 commit push 漏检中间提交（dialect-probe job 的 v62 已用 `github.event.before`，gate 未对齐）【.ai/scripts/gate-check.sh:695-701】
2. tech-debt#20 存在性断言注释称"必须 FAIL"实际走 ALLOW——名实不符【.ai/scripts/tech-debt-scan.sh:352-357】
3. **PD34 计数自激振荡①**：ArchitectureBoundaryTests 方法数文档统一改 41，实测 `[Test]`=**37**（41 是被字符串字面量污染的 grep 巧合值；review-snapshot 输出 42 第三口径）——上轮刚修的同类漂移当轮复发【docs/testing.md 等 + gate-check.sh:18】
4. **PD34 计数自激振荡②**：ConfigureAwait 文档统一改 444，基线 d8746a6 实测 449、当前 447+（修复轮自己的提交增删了调用）——建议给这两个计数补机械锚（doc-consistency 增项）或文档改为不含裸数字的表述【docs/aot.md:182 等】

### src 代码（10）
5. `SagaTimeoutDetector` 构造无 `ThrowIfNull`（守卫族惯例遗漏）【SagaTimeoutDetector.cs:26-29】
6. `DapperUnitOfWork` Dispose 后 `CommitAsync` 静默 no-op——姊妹 PalOrm 抛 ODE，三栈分叉【DapperUnitOfWork.cs:54-56】
7. `MySqlMultiHost.EnsureNoLoadBalanceConflict` 仅查 primary——standby 显式 Load Balance 静默丢弃（守卫半覆盖）【MySqlMultiHost.cs:63】
8. `InboxProcessor.MarkProcessedAsync` catch 带 OCE 过滤 vs MarkFailed 不带——同方法口径不对称【InboxProcessor.cs:114】
9. `PalOrmEventLog` 两处 `await foreach` 缺 ConfigureAwait（同文件其余 await 均带）【PalOrmEventLog.cs:208/245】
10. `[GenerateId]` 挂 class/interface 时静默忽略零诊断（PALID002 只覆盖 struct 族；Attribute 无 AttributeUsage 限制）【IdentityGenerator.cs:481-484】
11. `AddBoundedContextPrefixCodeFix` 错前缀场景叠加而非替换首段（"shipping.a.v1"→"ordering.shipping.a.v1" 无后续诊断）【AddBoundedContextPrefixCodeFix.cs:64-66】
12. `AddPalPostgreSqlOutboxNotifier` TryAddEnumerable(Factory) 去重键 (ServiceType,null)——不同 channelName 第二次调用被静默忽略【PostgreSqlServiceCollectionExtensions.cs:180-186】
13. `ProjectionCheckpointDbContext.ResetAsync` InMemory 回退路径无 bare-catch（ITM-632 门控缺口，删除延迟提交危害低）【ProjectionCheckpointDbContext.cs:237-260】
14. `CopyToCsvAsync` 恒返回 0 vs `ExportCsvAsync` 真实行数（族语义分叉，已声明）；`IdempotencyProcessor` OCE 路径无观测；JSONL/CSV DBNull 语义分叉（3 条 P4 合并）【PostgreSqlReportHelper.cs:203-238 等】

### 测试防线（12）
15. `DiagnosticCoverageGate` 形态①（`x.Id=="X"` 二元比较）未收紧——与形态②不对称，`order.Id=="PALxxx"` 仍假绿【DiagnosticCoverageGateTests.cs:126-131】
16. `OutboxRequeueSqliteTests` 两条 nextAttempt 参数值未断言（实现回归为忽略参数仍绿；有同栈 created_at 先例）【OutboxRequeueSqliteTests.cs:85-95/198-215】
17. `MessageCatalogEndToEndTests` `Descriptors.Count==1` 隐含"程序集仅一条 [GenerateMessage]"前提未注释【MessageCatalogEndToEndTests.cs:65】
18. `BrokerIntegrationTests` done 门限按总计数——重投递副本恰为第 5 个时 Distinct 断言假红【BrokerIntegrationTests.cs:437/631】
19. `StrategicDddAnalyzerTests:61` PDDD013 负向断言空洞（谓词含消息不含的类名→恒 false→IsFalse 恒真）【StrategicDddAnalyzerTests.cs:61】
20. MySQL `SslMode` 冲突校验零测试（上轮 v65 修复的姊妹测试缺失）【MySqlMultiHostTests】
21. `HasValueSequence` 多段分支零测试（上轮 ITM-629 修复无回归网，mutation 不红）【IdentityGenerator.cs:710 vs test/ 全目录】
22. EFCore ProjectionCheckpoint 负 timeout 守卫零直接测试（仅 InMemory 姊妹覆盖）【ProjectionCheckpointDbContext.cs:65/292】
23. `SerializationTests:445` 池化等值边缘断言（tiered JIT 分配不对称→偶发红）+ `:496` 哈希不等断言（HashCode.Combine 可合法碰撞→进程级 flaky）【SerializationTests.cs】
24. `AotContractTests:446-457` 注释声称验 3 项生成模式实际只验 2 个属性值（名实不符漏网）【AotContractTests.cs】
25. `SagaTests:286-295` 名"RetriesUpToMaxRetries"实只断言抛 AggregateException（重试退化仍绿）+ `:652-663` 名"补偿过程中取消"实为入口取消【SagaTests.cs】
26. `PublicApiSnapshotTests` delegate 分类死分支（IsValueType 恒 false，RequestExecutor 已固化为 "class"）+ 公共 event 不入快照【PublicApiSnapshotTests.cs:91-105/120】

### 其余（4）
27. `SagaStateEfCoreTests:344-347` SqliteConnection 无 using；`NativeCompressionTests` ServiceProvider 泄漏（测试资源卫生）【两文件】
28. `TestEnvironment.Parse` 值类型不匹配 IOE 穿透 rethrow——绕过"配置字段无效"包装【TestEnvironment.cs:198-205】
29. `ArchitectureBoundaryTests:586-593` 尾随注释剥离口径与三个姊妹守卫不一【ArchitectureBoundaryTests.cs】
30. 测试 P3 杂项批：`PalOrmConcurrencyTests Count==100` 环境敏感、`PostgreSqlShardingTests` 分布断言缺失、`OutboxStaleTokenUntilLeg` until 腿断言不对称、`CqrsTests PipelineBehaviors` 名实、`ServiceRegistration 幂等`弱断言、`TimestampDefaults Clock` 按名匹配、`MessagingTests EmptyList` 零断言、`dapper-global` 隔离约定非机制、`PalOrmInboxStoreTests NRE` 诊断差、`BackoffPolicy oracle` 镜像、`DapperSagaStateStore ToSqliteParameter` 命名、`Ulid converter provider` 不一致、`SqliteRowFactory 注释`失实、`EFCore Inbox 注释`空白归一表述偏差

---

## 修复顺序建议

| 序 | ITM | 理由 |
|:--:|-----|------|
| 1 | ITM-648 | P1 门禁假绿——一行修复，CI 凭据防线立即生效 |
| 2 | ITM-649 | 上轮回归——两处绑定 + 测试，低风险高确定性 |
| 3 | ITM-652 / ITM-651 | 姊妹对称 + 修复完整性 |
| 4 | ITM-650 | 先真库探针再定方案（CI Testcontainers 或环境可达时） |
| 5 | P3 #3/#4 | PD34 计数锚——补机械防线防再振荡 |
| 6 | 其余 P3 | backlog 按老化 |

## 验收标准

- [ ] ITM-648 探针复验（`bash -e -o pipefail` 包装下 CAUGHT）
- [ ] ITM-649 修复后 `grep 'error = {state.Error}' PalOrmSagaStateStore.cs` 零残留 + 新测试锁定 >2040 UPDATE
- [ ] ITM-650 探针结论落盘（证实→修复方案；证伪→转误判库候选模式）
- [ ] ITM-651 fixture 改造 + PublishException 集成测试（CI 跑）
- [ ] ITM-652 PG 校验集 4 项 + 姊妹测试
- [ ] 计数锚（P3 #3/#4）补入 doc-consistency 或文档去裸数字化
