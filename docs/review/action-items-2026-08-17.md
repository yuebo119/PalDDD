# 行动项清单 2026-08-17（第二十九轮全量评审）

> 来源：`docs/review/audit-2026-08-17-full-review.md` · 基线 5d4d250
> 优先级 = 危害 × 复杂度（conventions §13）

---

### [ ] ITM-173 · PalOrmSagaStateStore PG 租约缺 SKIP LOCKED · ✅
- **维度**：并发正确性 · **优先级**：[P1] · 危害: 高（并发租约错分） · 复杂度: 易（<1h）
- **问题**：`LeaseActiveSagasAsync` PG 分支（↑88）`UPDATE ... IN (SELECT ... LIMIT n)` 缺 `FOR UPDATE SKIP LOCKED`。Dapper 版 `SagaLeaseActivePG`（SqlTemplates.cs:261）已有——跨实现不对称（PD17 修一半）。多 worker 并发租约互相阻塞而非跳过，且 ITM-076 实测后到者覆盖先到者 leased_by。
- **建议**：PG 分支子查询补 `FOR UPDATE SKIP LOCKED`，与 Dapper 版对齐。修复后跑 dialect-probe（SagaSmoke 断言）确认红转绿（S3 反向验证）。
- **修复前先补探针**：dialect-probe 的 SagaSmoke 已覆盖 LeaseActiveSagasAsync（PG 20 断言含 Saga Lease 归属），可直接复用。

### [ ] ITM-174 · InMemoryOutboxStore 租约所有权守卫缺失 · ✅
- **维度**：并发正确性 · **优先级**：[P1] · 危害: 高（InMemory 测试网掩盖真库语义） · 复杂度: 中（1-4h）
- **问题**：InMemory 三兄弟中 Inbox/Idempotency 均已有 successor 替换 + `IsCurrentLeaseHolder` 守卫（InMemoryInboxStore.cs:100/121/134），唯 Outbox（InMemoryOutboxStore.cs:49-138）原地改写同一实例、Mark 方法无归属校验。worker A 租约到期后 B 重租同一实例，A 的 MarkProcessed/MarkDead 无校验覆盖 B 的活跃租约。
- **建议**：对齐姊妹：Lease 时以 successor 替换字典持有者，Mark 前 `IsCurrentLeaseHolder` 校验（含 LockedUntil > now 或 owner 匹配）。附测试：双 worker 租约交错不互相覆盖。

### [ ] ITM-175 · IdempotencyProcessor 失败原因未截断 · ✅
- **维度**：错误处理 · **优先级**：[P1] · 危害: 中（失败记录滞留→重放二次执行） · 复杂度: 易（<1h）
- **问题**：`MarkFailedAsync(record, ex.Message, ...)`（↑70）不截断。error 列 HasMaxLength(2048)（IdempotencyDbContext.cs:111），超长 ex.Message 让终态保存本身抛截断异常 → 残留 Processing → 租约过期重放 → 副作用二次执行。OutboxBatchProcessor:22/InboxProcessor:28 均有 MaxFailureReasonLength=2000，PD24 失败标记族唯此漏。
- **建议**：补 `internal const int MaxFailureReasonLength = 2000`，MarkFailedAsync 前截断。与 Inbox/Outbox 对称。

### [ ] ITM-176 · ExponentialBackoffPolicy exponentCap 溢出 · ✅
- **维度**：健壮性 · **优先级**：[P1] · 危害: 中（配置不当即崩溃） · 复杂度: 易（<1h）
- **问题**：`ComputeDelay`（↑80）`Math.Pow(2, cappedExponent)` 在 maxDelay 封顶前求值，`TimeSpan.FromSeconds` 对 2^40 秒（~3.5 万年 > TimeSpan 上限 ~9.22e11 秒）抛 OverflowException。构造期无 exponentCap 上界校验（仅下限 ≥1）。
- **建议**：构造期校验幂底 2 的 exponentCap 次方可表示（上限约 39），或先 clamp 秒数到 maxDelay 再 FromSeconds。附测试：`new ExponentialBackoffPolicy(exponentCap: 60)` 构造抛清晰异常。

### [ ] ITM-177 · AddPalMySqlSagaSnapshot 注册顺序敏感 · ✅
- **维度**：DI 配置 · **优先级**：[P2] · 危害: 高（配置顺序错则重启丢字段） · 复杂度: 中（1-4h）
- **问题**：`AddPalMySqlSagaSnapshot<TState>`（闭合泛型，传 jsonTypeInfo）若在 `AddPalMySqlWithStores`（↑179 开放泛型注册）**之前**调用，MS DI 逆序匹配描述符时开放泛型后注册胜出 → 解析到 jsonTypeInfo=null 的 DapperSagaStateStore → saga_data 写 NULL 重启丢业务字段。方法自述"覆盖开放泛型"的前提被调用顺序打碎。
- **建议**：开放泛型注册改 TryAdd（TryAdd 后注册的闭合泛型总是胜出），或文档声明调用顺序硬约束。推荐前者——消除顺序敏感性。

### [ ] ITM-178 · OutboxDomainEventInterceptor SaveChanges 失败双写 · ✅
- **维度**：错误处理 · **优先级**：[P2] · 危害: 中（重复消费） · 复杂度: 中（1-4h）
- **问题**：`AddMessage`（↑143）注入的 OutboxMessage 留在 ChangeTracker Added 状态；`SaveChangesFailedAsync`（↑98）只清 `_pending` 不清已注入实体。EF 失败不自动回滚 ChangeTracker → 调用方重试 SaveChanges 时旧消息+新消息一起落库 → 同事件 outbox 双写（下游重复消费，幂等消费兜底）。
- **建议**：失败路径按本轮消息 ID 从 `ChangeTracker.Entries<OutboxMessage>()` 移除（Detach 状态），或文档显式声明"SaveChanges 失败后必须丢弃 DbContext"。附测试：失败重试不产生重复 outbox 行。

### [ ] ITM-179 · MessageBrokerBase 泛型 PublishAsync null 放行 · ✅
- **维度**：健壮性 · **优先级**：[P2] · 危害: 中（null 负载发布→消费端 NRE） · 复杂度: 易（<1h）
- **问题**：泛型 `PublishAsync<TMessage>`（↑38-44）对引用类型 null 仅 `message!` 空断言放行，null 序列化为 "null" 负载发布到 broker，消费端反序列化 null 后 handler NRE 远离入口。非泛型核心重载（KafkaBroker:60 等）已有 ThrowIfNull。
- **建议**：入口补 `ArgumentNullException.ThrowIfNull(message)`（值类型零开销）。

### [ ] ITM-180 · InboxProcessor 成功但标记失败不可区分 · ✅
- **维度**：错误语义 · **优先级**：[P2] · 危害: 中（监控误判+重放） · 复杂度: 中（1-4h）
- **问题**：handler 成功（副作用已发生）但 `MarkProcessedAsync` 失败（DB 故障）落入通用 catch 被记为 Failed（↑112-135）——已完成副作用的消息被持久化为 Failed，监控误判 + 重试重放二次执行。at-least-once 已声明，但"副作用已发生"与"未发生"不可区分。
- **建议**：MarkProcessed 失败单独捕获，Error 消息注明"handler 已执行、状态待确认"，不按通用 handler 失败处理。

### [ ] ITM-181 · PostgreSqlReadWriteRouter reader 主库并入负载均衡 · ✅
- **维度**：架构 · **优先级**：[P2] · 危害: 中（读写分离稀释） · 复杂度: 中（1-4h）
- **问题**：reader 连接串 `psb.Host = 主库 + "," + 副本`（↑134-142），LoadBalanceHosts=true + TargetSessionAttributes=any → 读流量负载均衡到写主库，读写分离稀释、主库连接池承压。
- **建议**：确认意图——读优先副本、主库仅 fallback（排除主库 Host 或改 read-write 排序）；如允许读命主库则补文档声明。

### [ ] ITM-182 · Dispatcher Freeze 竞态守卫退化 · ⚠
- **维度**：并发边界 · **优先级**：[P3] · 危害: 低（违反已文档化契约） · 复杂度: 易
- **问题**：Register 守卫只查 `_frozen`，Freeze 后 `_entries=null!`——并发 Register 在守卫通过后写入 null 字典抛 NRE。违反"启动期单线程"文档契约（Dispatcher.cs:74-77 已声明）。
- **建议**：守卫补 `_entries is null` 检查抛同一 ObjectDisposedException（一行防御）。

### [ ] ITM-183 · SmartEnum 二次注册静默丢弃 · ⚠
- **维度**：生成语义 · **优先级**：[P3] · 危害: 低（多模块分批注册场景） · 复杂度: 中
- **问题**：`Interlocked.CompareExchange` 首写获胜——同 TSelf 值分两批注册（多模块初始化器）时后批值静默丢弃无告警。
- **建议**：补注释声明"后注册者被丢弃"；或锁内合并注册。

### [ ] ITM-184 · MySQL INSERT IGNORE 吞噬非唯一错误 · ⚠
- **维度**：数据一致性 · **优先级**：[P3] · 危害: 低（键长由调用方约束） · 复杂度: 难
- **问题**：PalOrmInboxStore:68 / PalOrmIdempotencyStore:79 / PalOrmProjectionCheckpointStore:67 MySQL 分支 INSERT IGNORE 无差别吞噬非唯一冲突（列宽截断/NOT NULL 静默降级）。MySQL 原子幂等语义的刻意选择。
- **建议**：维持现状（已文档化权衡）；如需防键错位，调用方约束 messageId/consumerName 长度。

### [ ] ITM-185 · EFCore 族 Ulid Parse 无容错 · ⚠
- **维度**：数据容错 · **优先级**：[P3] · 危害: 低（脏数据面） · 复杂度: 中
- **问题**：EventLogDbContext:182-184 等 EFCore 族 HasConversion 用 `PalUlid.Parse`（无容错），Dapper/PalORM 用 TryParse 降级 null——族内一致但跨族差异，脏 Ulid 读行崩溃。
- **建议**：维持族内一致（已核 6 处统一 Parse）；如需容错全族一起改。

### [ ] ITM-186 · Saga.MaxRetries setter 无下限校验 · ⚠
- **维度**：健壮性 · **优先级**：[P3] · 危害: 低（负值静默零次执行） · 复杂度: 易
- **问题**：`MaxRetries = -1` 时 for 循环零次执行，ProcessEventAsync 静默返回。
- **建议**：setter 加 ThrowIfNegative。

### [ ] ITM-187 · Saga observer 异常触发已成功步骤补偿 · ⚠
- **维度**：错误语义 · **优先级**：[P3] · 危害: 低（observer 为框架扩展点） · 复杂度: 中
- **问题**：RecordExecutedStep（↑284）先于 observer.OnStepCompleted（↑287），observer 回调抛异常 → catch 计入失败 → 已成功步骤被补偿回滚。
- **建议**：observer 异常与步骤执行失败分离（observer 异常不触发补偿）。

### [ ] ITM-188 · DapperEventLog SQLite 唯一约束判定无类型限定 · ⚠
- **维度**：错误分类 · **优先级**：[P3] · 危害: 低 · 复杂度: 易
- **问题**：`IsUniqueConstraintViolation` SQLite 分支无 SqliteException 类型限定（EFCore/PalORM 已限定）——任意 DbException 消息恰含 "UNIQUE constraint" 被误分类。
- **建议**：镜像姊妹补类型限定。

### [ ] ITM-189 · PostgreSqlReportHelper JSON 数值类型失真 · ⚠
- **维度**：类型正确性 · **优先级**：[P3] · 危害: 低 · 复杂度: 易
- **问题**：uint/ulong/short/ushort 落 default 分支被 Convert.ToString 写成 JSON 字符串（非数值）。
- **建议**：补数值分支。

### [ ] ITM-190 · KafkaBroker ConsumeException 无限刷屏 · ⚠
- **维度**：可观测性 · **优先级**：[P3] · 危害: 低 · 复杂度: 中
- **问题**：持续故障下每秒 Error 日志一次无限重试（退避防空转但刷屏）。
- **建议**：最大连续错误计数/指数退避 + 重试上限。

---

## 下沉审查（P0/P1 收口）

| ITM | 可下沉为 |
|-----|---------|
| ITM-173 | PalORM 版 SKIP LOCKED 对称——dialect-probe SagaSmoke 断言已覆盖（探针即回归网），无需新防线 |
| ITM-174 | InMemory 租约守卫——可在 ArchitectureBoundaryTests 或 InMemory 族测试固化，暂以单元测试覆盖 |
| ITM-175 | 失败截断对称——tech-debt-scan #18 已有截断守卫对称性检查，本项落后者修复即闭合 |
| ITM-176 | exponentCap 上界——构造校验属语义判断，保留为测试覆盖 |

## 完成回填（修复轮 2026-08-17 · 全量验证中）

- [x] ITM-173 · PalOrmSaga PG SKIP LOCKED — 修复轮 `PalDDD.PalORM` 分支构造；PalORM.Tests 99/99（含 `Saga_PostgreSql/Sqlite_LeaseActiveSagas` 双方言真库）+ 方言探针全 PASS；教训：PalORM `{lockClause}` 插值被参数化生成 `LIMIT @p5 @p6` 语法错误（PD18 再证），需按方言分支整句构造完整 FormattableString
- [x] ITM-174 · InMemoryOutbox 租约守卫 — successor 替换 + `IsCurrentLeaseHolder`（对齐 Inbox/Idempotency，ITM-105 模式）；Transactions.Tests 148/148（4 测试修正 + 1 新回归 `StaleReferenceAfterReLease_MarkIgnored`）
- [x] ITM-175 · IdempotencyProcessor 截断 — 补 `MaxFailureReasonLength=2000` 截断（PD24 失败标记族对称）；Idempotency 由 Integration.Tests 覆盖
- [x] ITM-176 · Backoff exponentCap 上限 — 构造校验 `>39 抛` + ComputeDelay 内 clamp（双保险）
- [x] ITM-177 · MySqlSagaSnapshot 注册顺序 — **探针证伪**（MS DI 闭合泛型恒优先，A/B 双向实测 CLOSED 胜出）；文档澄清调用顺序无关；伪证计入 metrics 证伪数
- [x] ITM-178 · OutboxInterceptor 失败 Detach — 失败路径 Detach Added OutboxMessage（EF 不自动回滚）；验证：代码审查 + 集成测试网回归（fake store 不触 ChangeTracker，无法单元验证 Detach，如实标注）
- [x] ITM-179 · MessageBrokerBase null 校验 — 泛型入口补 `ArgumentNullException.ThrowIfNull`
- [x] ITM-180 · InboxProcessor 标记失败区分 — MarkProcessedAsync 失败单独捕获，Error 日志标注 "handler SUCCEEDED" 按成功返回（at-least-once 状态待确认）
- [x] ITM-181 · ReadWriteRouter 读均衡 — `TargetSessionAttributes` any→`read-only`（Npgsql 10.0.3 实证合法值须连字符；readonly/read_only 抛 ArgumentException）
- [x] ITM-182 · Dispatcher 守卫 — 补 `_entries is null` 检查抛 ObjectDisposedException（一行防御）
- [ ] ITM-187 · observer 异常分离 — **记录为设计权衡**（P3）：observer 为框架扩展点，异常导致已成功步骤被补偿属可接受语义；如需分离投入产出不成比例，暂缓
- [x] ITM-186 · Saga.MaxRetries 下限 — setter `ThrowIfNegative`
- [x] ITM-188 · DapperEventLog SQLite 限定 — 补 `SqliteException` 类型前置（镜像 EFCore/PalORM 姊妹）
- [x] ITM-189 · PostgreSqlReportHelper JSON 数值 — 补 uint/ulong/short/ushort/byte/sbyte 数值分支
- [ ] ITM-190 · KafkaBroker ConsumeException 刷屏 — 记录（P3，行为已文档化：退避防空转）；持续故障日志刷屏属可观测性权衡，暂缓

构建验证：全解决方案 build 0/0 · Transactions 148 · PalORM 99 · DI 82 · Core 241 · CQRS 25 · Messaging 25 · Repository.EFCore 8 · 方言探针全 PASS
