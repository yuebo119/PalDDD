# Dapper / EF Core / PalORM 接口一致性与功能完整性分析

> 分析编号：PARITY-ANALYSIS-2026-09-13
> 方法：六接口 × 三栈方法级全量对比 + 源码逐行审读 + 依赖矩阵 + DI 注册链追踪

---

## 核心结论

| 维度 | 状态 | 说明 |
|---|:---:|------|
| 接口一致性 | ✅ 一致 | 六接口方法签名在三栈间完全一致 |
| 实现逻辑一致性 | ✅ 语义一致 | 同一方法在不同栈中产出相同业务结果（五轮交叉审计验证）|
| 功能完整性 | ⚠️ **有一个缺口** | Dapper 栈**缺少 IIdempotencyStore 实现** |
| PalORM 功能全覆盖 | ⚠️ **各有独有功能** | 三栈不是"谁实现了谁的全部"，而是各有技术特有的独有功能 |

---

## 第一部分：六接口 × 三栈实现矩阵

| 接口 | Dapper | PalORM | EF Core | InMemory | DI 注册 |
|------|:------:|:------:|:-------:|:--------:|---------|
| `IPalOutboxStore` | ✅ `DapperOutboxStore` | ✅ `PalOrmOutboxStore<TProvider>` | ✅ `OutboxDbContext`（abstract + 4 方言） | ✅ | `AddPalDapperTransactions` / `AddPalOrm*` / `AddPalOutboxUnitOfWork` |
| `IInboxStore` | ✅ `DapperInboxStore` | ✅ `PalOrmInboxStore<TProvider>` | ✅ `InboxDbContext`（abstract） | ✅ | 同上 |
| `ISagaStateStore<T>` | ✅ `DapperSagaStateStore<TState>` | ✅ `PalOrmSagaStateStore<TProvider,TState>` | ✅ `SagaStateDbContext<T>`（abstract） | ✅ | 同上 |
| `IEventLog` | ✅ `DapperEventLog` | ✅ `PalOrmEventLog<TProvider>` | ✅ `EventLogDbContext`（abstract） | ✅ | 无 Dapper DI（直接构造） |
| `IProjectionCheckpointStore` | ✅ `DapperProjectionCheckpointStore` | ✅ `PalOrmProjectionCheckpointStore<TProvider>` | ✅ `ProjectionCheckpointDbContext`（abstract） | ✅ | 同上 |
| `IIdempotencyStore` | ❌ **无实现** | ✅ `PalOrmIdempotencyStore<TProvider>` | ✅ `IdempotencyDbContext`（abstract） | ✅ | Dapper DI 注册链注释仅提及 Outbox/Inbox/SagaState 三者 |

---

## 第二部分：接口一致性详细分析

### 2.1 方法签名一致性 — ✅ 完全一致

以 `IPalOutboxStore`（方法最多的接口，9 方法）为例：

| 方法 | Dapper | PalORM | EF Core | 签名一致 |
|------|:------:|:------:|:-------:|:-------:|
| `GetPendingMessagesAsync(batchSize, maxRetryCount, ct)` | ✅ | ✅ | ✅ (virtual) | ✅ |
| `LeasePendingMessagesAsync(batchSize, owner, leaseDuration, maxRetryCount, ct)` | ✅ | ✅ | ✅ (abstract→4 方言 override) | ✅ |
| `AddMessage(message)` | ✅ void | ✅ void | ⚠️ 见下 | ✅ |
| `AddMessagesAsync(messages)` | ✅ | ✅ | ⚠️ 见下 | ✅ |
| `MarkProcessed(message, processedAt)` | ✅ void | ✅ void | ✅ void | ✅ |
| `MarkDead(message, failureReason, deadAt)` | ✅ void | ✅ void | ✅ void | ✅ |
| `ReleaseForRetry(message, failureReason, nextAttemptAt)` | ✅ void | ✅ void | ✅ void | ✅ |
| `RequeueDeadAsync(messageId, nextAttemptAt, retriedBy, ct)` | ✅ PalUlid | ✅ Ulid | ✅ PalUlid | ✅ |
| `SaveChangesAsync(ct)` | ✅ 返回 0（立即执行模式） | ✅ | ✅（DbContext 继承） | ✅ |

> ⚠️ EF Core OutboxDbContext 的 `AddMessage`/`AddMessagesAsync` 通过 ChangeTracker 模式实现（`DbSet.Add` + 延迟到 SaveChangesAsync），与 Dapper 的"立即执行 SQL"语义不同但接口签名一致——**接口注释已显式声明此语义差异**。

### 2.2 实现逻辑一致性 — ✅ 语义一致（技术实现不同）

| 语义维度 | Dapper | PalORM | EF Core | 一致？ |
|---------|--------|--------|---------|:-----:|
| 租约获取 | `FOR UPDATE SKIP LOCKED`(PG) / `FOR UPDATE`(MySQL) | 源生成 SQL + `FOR UPDATE` | `ExecuteUpdate` / SQL 翻译 | ✅ 同语义 |
| 乐观并发 | `WHERE version = @expected` 手写 | `ConcurrencyCheckAttribute` 源生成 | EF Core 并发 token | ✅ 同语义 |
| 冲突检测 | `IsUniqueConstraintViolation` 按方言 | `SqlErrorClassifier.IsUniqueKeyViolation` | EF Core `DbUpdateException` | ✅ 同语义 |
| 截断策略 | Outbox/Saga=Truncate(2040), Inbox/Checkpoint=Normalize(2000) | 同左（v3 轮 ITM-638 统一） | 同左 | ✅ 一致 |
| OCE 处理 | `catch { Detach; throw; }` / `when(not OCE)` | 同款 | 同款 | ✅ 一致 |
| 错误信息 | `ex.Data["MarkError"]` / `Data["MarkFailedError"]` | 同款 | 同款 | ✅ 一致 |

> **交叉审计验证**：五轮全仓循环中，v2/v3/v4 三轮的跨栈对比（PD17 姊妹核查 + PD24 管线孪生）已验证上述语义一致性。发现的差异（ITM-638 截断常量分叉、ITM-653 OCE 过滤遗漏）**全部已在修复轮清偿**。

### 2.3 实现技术差异（刻意设计，非不一致）

| 维度 | Dapper | PalORM | EF Core |
|---|---|---|---|
| SQL 来源 | `SqlTemplates.cs` 手写 const | PalORM.SourceGen 编译期生成 | EF Core 表达式树翻译 |
| 执行模式 | 立即执行（每次调用发 SQL） | Session 立即执行 | ChangeTracker 延迟 + SaveChangesAsync |
| 事务管理 | `DbTransaction` 构造注入 | `PalOrmAmbientTransaction` | `Database.BeginTransactionAsync` |
| 事务边界 | `UnitOfWork.BeginTransaction/Commit` | 同左 | 同左 |
| 类型物化 | 运行时 Reflection.Emit | 编译时源生成 | 运行时表达式编译 |
| AOT 兼容 | ⚠️ 编译不报错但运行时退化 | ✅ 真支持 | ❌ 显式不支持 |

---

## 第三部分：功能完整性对比（PalORM 是否被全部实现？）

**这个问题的准确表述应该是：三栈各自实现了接口的全部方法吗？各自有哪些接口之外的技术独有功能？**

### 3.1 接口方法覆盖度 — 全部 100%

| 接口 | 方法数 | Dapper 覆盖 | PalORM 覆盖 | EF Core 覆盖 |
|------|:-----:|:---------:|:---------:|:-----------:|
| IPalOutboxStore | 9 | 9/9 ✅ | 9/9 ✅ | 9/9 ✅（部分为 abstract/虚方法） |
| IInboxStore | 3 | 3/3 ✅ | 3/3 ✅ | 3/3 ✅ |
| ISagaStateStore | 6 | 6/6 ✅ | 6/6 ✅ | 6/6 ✅ |
| IEventLog | 3 | 3/3 ✅ | 3/3 ✅ | 3/3 ✅ |
| IProjectionCheckpointStore | 6 | 6/6 ✅ | 6/6 ✅ | 6/6 ✅ |
| IIdempotencyStore | 5 | **0/5 ❌** | 5/5 ✅ | 5/5 ✅ |

> **IIdempotencyStore 在 Dapper 栈中完全缺失** — `AddPalDapperTransactions` DI 注册注释明确仅涉及 Outbox/Inbox/SagaState 三者，不含 Idempotency。这意味着选择 Dapper 栈的用户无法使用幂等消费模式。

### 3.2 各栈技术独有功能（接口之外的增值能力）

**Dapper 独有（PalORM/EFCore 不具备）：**
| 功能 | 说明 |
|------|------|
| `DapperBulkCopy` | 数据库最优批量导入（PG COPY / MySQL BulkCopy / SQLite transaction batch） |
| 多主机支持 | `MySqlMultiHost` / `PostgreSqlMultiHost`（读写分离/故障转移/分片） |
| `PostgreSqlJsonbExtensions` | JSONB 操作（Extract/ExtractPath/HasKey/Contain） |
| `PostgreSqlSharding` | 分库分表路由（取模 + 一致性哈希） |
| `PostgreSqlOutboxNotifier` | LISTEN/NOTIFY 实时通知 |
| `SqliteFtsExtensions` | FTS5 全文搜索 |
| `SqlitePerformanceOptimizer` | WAL/PRAGMA 优化 |
| `SqlTemplates.cs` | 全部 SQL 编译时常量（`public const string`）|

**PalORM 独有（Dapper/EFCore 不具备）：**
| 功能 | 说明 |
|------|------|
| AOT 全链路 | PalORM.SourceGen 编译时生成 → NativeAOT publish 实测通过 |
| `ConcurrencyCheckAttribute` | 声明式乐观并发（替代 Dapper 手写 WHERE version=@v）|
| `ColumnAttribute` | 编译期列名映射（替代 Dapper 的 MatchNamesWithUnderscores 全局状态）|
| Provider 泛型参数化 | `PalOrmOutboxStore<TProvider>` — 方言在编译期特化（非运行时分支）|

**EF Core 独有（Dapper/PalORM 不具备）：**
| 功能 | 说明 |
|------|------|
| ChangeTracker | 变更追踪 + 延迟提交（UnitOfWork 模式）|
| LINQ 查询 | 类型安全查询（编译期检查）|
| Migration | 数据库 schema 迁移 |
| InMemory Provider | 无数据库测试（虽然项目用 InMemory 实现替代）|

### 3.3 功能差异对消费者的影响

| 场景 | Dapper 栈 | PalORM 栈 | EF Core 栈 |
|---|---|---|---|
| Outbox 模式 | ✅ | ✅ | ✅ |
| Inbox 幂等消费 | ✅ | ✅ | ✅ |
| Saga 编排 | ✅ | ✅ | ✅ |
| 事件溯源 | ✅ | ✅ | ✅ |
| 投影检查点 | ✅ | ✅ | ✅ |
| **幂等存储** | **❌ 需自行实现或混合栈** | ✅ | ✅ |
| **NativeAOT 发布** | ⚠️ 编译通过但运行时退化 | ✅ | ❌ |
| **批量导入** | ✅ BulkCopy | ✅ 方言 BulkCopy | ⚠️ 无专门 BulkCopy |
| **多主机/读写分离** | ✅ | ❌ | ❌ |
| **全文搜索** | ✅ SQLite FTS5 | ❌ | ❌ |
| **分库分表** | ✅ PostgreSqlSharding | ❌ | ❌ |
| **实时通知** | ✅ PG LISTEN/NOTIFY | ❌ | ❌ |
| **JSONB 操作** | ✅ PG JSONB 扩展 | ❌ | ⚠️ EF Core JSON 列 |

---

## 第四部分：结论

### 接口一致性 — ✅ 通过
六接口方法签名在三栈间完全一致。唯一例外：Dapper 栈**完全没有** IIdempotencyStore 实现——这不是签名不一致，是整个接口缺失。

### 实现逻辑一致性 — ✅ 通过
同一方法在不同栈中产出相同的业务结果。五轮交叉审计（PD17/PD24）验证了语义一致性。发现的差异全部已清偿。

### 功能完整性 — ⚠️ 三栈各有独有能力，无一覆盖全部

**Dapper 栈没有实现 PalORM 的全部功能**——反之亦然。这不是"谁没实现谁"，而是**三栈是平级的可组合持久化适配器，各有所长**：
- Dapper 强在：批量导入（BulkCopy）、多主机（读写分离/故障转移）、PostgreSQL 高级功能（JSONB/Sharding/NOTIFY/FTS5）
- PalORM 强在：AOT（唯一真支持）、声明式并发（ConcurrencyCheck）、编译期映射
- EF Core 强在：ChangeTracker、LINQ、Migration、生态兼容

**唯一的实际功能缺口**是 Dapper 栈缺少 IIdempotencyStore——选择 Dapper 栈的用户需要：①自行实现（复制 PalORM 模式）②混合使用 Dapper + EFCore 栈（Dapper 做 Outbox/Inbox/Saga，EFCore 做 Idempotency）③等待 Dapper 栈退役后统一到双栈。

### 对 ADR-020 的影响

| 发现 | 对退役策略的影响 |
|---|---|
| Dapper 缺 IIdempotencyStore | **强化退役合理性**——Dapper 栈功能不完整，Idempotency 消费者被迫混合栈 |
| Dapper 独有功能（BulkCopy/多主机/Sharding/NOTIFY/FTS5） | **退役前需评估**——这些功能在 PalORM/EFCore 栈中无对应物，Dapper 退役意味着功能损失 |
| Dapper `IsAotCompatible=true` 是编译期口径 | **强化退役合理性**——Dapper "AOT" 是假支持，PalORM 才是真 AOT |

**最终建议**：Dapper 栈退役前，需要为 5 个独有功能（BulkCopy/多主机/Sharding/NOTIFY/FTS5）在 PalORM 或 EFCore 栈中提供等价物，否则这些功能的消费者会被破坏。这不是代码 bug，是产品决策。
