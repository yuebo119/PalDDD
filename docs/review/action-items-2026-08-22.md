# 行动项 · 2026-08-22 全仓地毯轮（REVIEW-2026-08-22）

> 来源：`docs/review/review-2026-08-22-full-carpet.md`（commit 070b42f 基线）。
> 分族派发纪律：族间文件零重叠；每族修复后自跑 build 0/0 + 受影响测试（MTP 逐项目）。
> 修复门两问：①故障类有传感器吗（无则先写复现测试再修）②改动外溢吗（查 sibling-map 联动面）。

## 族 A · PalORM Store 手动 reader 双缺陷（P1 ×2，探针已实证）

### [ ] ITM-242 · F1：三 Store GetDateTime→DateTimeOffset 物化本地偏移漂移（MySQL）
- **位点（9 处）**：`PalOrmSagaStateStore.cs（222-229 行）`（CreatedAt/CompletedAt/ErrorAt/LeasedUntil）、`PalOrmProjectionCheckpointStore.cs（44-45 行）`、`PalOrmIdempotencyStore.cs（46 行）`（locked_until/expires_at/updated_at）。
- **实证**：探针漂移 -8.000h + UPDATE 后 DB 回拨 8h（报告 F1 详情）。触发：MySQL + 非 UTC 进程。
- **修复**：物化统一 `DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))`；SQLite 路径（"O" 带偏移串）一并验证。
- **传感器先行**：多方言测试补时间戳往返断言（ITM-245 联动）+ dialect-probe 补 Saga 租约时间断言（下沉）。
- **验证**：Saga 探针红转绿 + 移除修复退回红（S3）；PG/SQLite 探针无回归。

### [ ] ITM-243 · F2：三 Store 5 处 raw command 未挂 cmd.Transaction（MySQL 事务路径不可达）
- **位点**：`PalOrmSagaStateStore.cs（63 行）/130/142`、`PalOrmProjectionCheckpointStore.cs（33 行）`、`PalOrmIdempotencyStore.cs（36 行）`。
- **实证**：事务内 SaveChanges 抛 `InvalidOperationException: The transaction associated with this command is not the connection's active transaction`（报告 F2 详情）。
- **修复**：raw 命令构造收敛私有 helper，统一挂接会话活动事务（先核 PalORM 5.3 公开 API：`GetActiveTransaction` 或等价物；无则评估 Session 级命令构造）。PG/SQLite 行为不得回归（连接级自动参与的方言挂接 null 安全）。
- **传感器先行**：多方言测试补"事务内 SaveChanges"用例（当前零覆盖——F15 关联）。
- **验证**：Saga 探针 [B] 转绿 + 三方言探针复跑。

## 族 B · 多方言测试基建（P1 ×1 + P2 ×2）

### [ ] ITM-244 · F3：TestSession 从不 dispose——容器泄漏
- **位点**：六个 `PalOrm*MultiDialectTests.cs` 全部工厂方法（模式 `=> await Helper(await CreateXxxAsync())`）。
- **修复**：Helper 形参改 `await using`；Saga 工厂返回复合 disposable。验证：docker ps 容器归零。

### [ ] ITM-245 · F13+F15：时间戳往返零断言 + 方言矩阵缺口
- 多方言测试补 DateTimeOffset 往返断言（类注释已宣称、断言缺失——ITM-242 的漏网原因）；Saga 乐观锁冲突补 MySQL 方言；Projection 过期抢占补 PG/MySQL。

## 族 C · 基准有效性（P1 ×1）

### [ ] ITM-246 · F4：LeasePending/LeaseAndMarkAll 迭代内耗尽
- **修复**：耗尽检测重灌 + 按次调用摊销属性（BenchmarkDotNet），或改非耗尽型操作。对照 PD29 验证器三问第二问。

## 族 D · src 语义修复（P2 ×5）

### [ ] ITM-247 · F5：EventLogDbContext 冲突分类只查 events[0]
- `EventLogDbContext.cs（260-284 行）`：批内任意 EventId 已存在均应归类数据错误（对齐注释意图），非首位误判并发 → 无限重试。姊妹 Dapper 版不同源无需同步（PD24 已核）。修复前补探针（批内第二事件与既有 EventId 冲突 → 断言异常类型）。

### [ ] ITM-248 · F6：ExtractJsonByPath 缺 fail-fast（姊妹已修）
- `PostgreSqlJsonbExtensions.cs（161-178 行）`：对齐 `ExtractTextByPath` 的逗号/花括号构建期拒绝（ITM-033 同型，PD24）。

### [ ] ITM-249 · F7：PostgreSqlReadWriteRouter.DisposeAsync 无异常隔离
- 对齐 `ShardedDataSourceManager` 逐资源隔离保首异常模式（三十七轮姊妹先例，PD24）。

### [ ] ITM-250 · F8：PalOrmInboxStore 回查 catch(InvalidOperationException) 过宽
- `PalOrmInboxStore.cs（110-115 行）`：改"查空"语义（FirstOrDefault+null 判）或精确判定无行异常；对照 DapperInboxStore 姊妹。

### [ ] ITM-251 · F9：DapperBulkCopy valueExtractor 调用次数契约失实
- `DapperBulkCopy.cs（96-98 行）` 注释与 215-243 实现不符：修正实现（推断循环缓存首行值）或修正契约注释。

## 族 E · 测试质量批（P2 ×10）

### [ ] ITM-252 · F10+F11：EF Lease 遮蔽修复
- `OutboxSqliteConcurrencyTests.cs（187-217 行）` 删本地重写直测生产 `SqliteOutboxDbContext`；`OutboxEfCoreTests.cs（185-211 行）` 删死 override 或补真实 Lease 测试。

### [ ] ITM-253 · F12：DapperStoreTests 补 `[NotInParallel]`
- 加 `[NotInParallel("dapper-global")]` 使隔离机制与类注释对齐。

### [ ] ITM-254 · F14：四类 EF store 补 SQLite 内存库变体（PD26）
- Inbox/Idempotency/Projection/Saga 按 OutboxEfCoreTests 先例补关系型覆盖（EnsureCreated + 写读 roundtrip）。

### [ ] ITM-255 · F17+F18+F23：断言真实性批修
- `CompensateAsync_NoCompensationHandlers_Succeeds` 补状态断言；AotContractTests 两测试显式设置反射禁用开关（.NET BCL 的 AppContext 开关 API）建立前提，或改名；`DimBridge`/`DotNet11MigrationTests` 名实对齐。

### [ ] ITM-256 · F19：ArchitectureBoundaryTests 命名正则假阴性
- 正则允许多连续特性行 + 泛型返回类型；补一个"带 [Arguments] 的坏命名"负向自证用例（防线 falsification）。

### [ ] ITM-257 · F20：Rabbit 轴四层防线补齐
- 两个收数测试接 CapturingLogger + warmup-ready 重发 + 超时诊断上下文（对齐 Kafka 轴）。

### [ ] ITM-258 · F22：RowVersion 属性测试边界排除
- 属性内排除 MaxValue 或白名单 OverflowException（⚠[推断]——修复前先跑属性测试 10 轮验证 flaky 真伪）。

### [ ] ITM-259 · F16：测试 SQLite DDL 四副本收敛
- 删 `CreateSchemaSql` 死常量或提取单一 DDL 真源（MultiDialectSchema）。

### [ ] ITM-260 · F21：release.md AotSample 声明修正
- 改为"手动验证示例"或在 ci.yml 补 AotSample publish 步骤（PD28 发布面失实）。

## P3 池

55 项 P3 已登记 `.ai/review/action-items-p3-backlog.md`（30 天老化→升 P2），本轮不展开。

## 完成定义（本轮行动项）

1. ITM-242/243 必须有 Saga 探针红转绿 + S3 反向验证证据。
2. 全部修复自带回归测试（修复门两问第①问）。
3. 修复合入后：gate 22/22 + tech-debt 全绿 + dialect-probe 40/40 + 逐项目测试与基线一致（997/956/41 环境性）。
4. 修复轮后必须跟验证轮（engine.md 评审-修复循环协议——历轮修复缺陷率 31%→8% 的教训）。
