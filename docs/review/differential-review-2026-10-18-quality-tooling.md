# 差分安全评审试点（2026-10-18）

> 对象：质量工具收敛提交 `b027e0a`（相对 `5683f14`），65 文件。
> 方法：Trail of Bits differential-review 思路——按信任边界与数据流审查变更，重点看 SQL 构造、取消传播、反射与序列化。
> 目的：关闭评估文档中「ToB 技能只保留未试点」的遗留项，并验证质量工具改动没有引入安全回退。

## 一、范围

逐 hunk 审查以下安全敏感面：

1. SQL 语句构造与注入防护（`DapperBulkCopy`、`PostgreSqlOutboxNotifier`、`SqlitePerformanceOptimizer`、投影 checkpoint、EF Core `FromSqlRaw`）。
2. 取消与资源释放（`S8949` 相关改动、`Task.Run`、`CancellationTokenRegistration.DisposeAsync`、`CancelAsync`）。
3. 反射与 AOT 抑制（`Saga.ResolveChildSagaByType`、`S3011`/`S2743`）。
4. 序列化与 ThreadStatic 池（`JsonMessageSerializer`、`JsonLinesEventStream`）。
5. 时间语义（`MA0132` 显式 `DateTimeOffset` 修复）。

## 二、发现

### F1（高，已修复）：S2077 规则级关闭削弱 SQL 注入纵深

**证据**：`.editorconfig` 曾将 Sonar `S2077` 全局设为 `none`，理由是「Semgrep 覆盖」。但 Semgrep 的 6 条 findings 全是 `DapperBulkCopy`/`PostgreSqlOutboxNotifier`/`SqlitePerformanceOptimizer`，**没有覆盖** `PostgreSqlOutboxDbContext.FromSqlRaw` 与 `SqlServerOutboxDbContext.FromSqlRaw` 两处；这两处恰是 Sonar 唯一能看到的 SQL 构造形态。

**影响**：若未来有人在 `FromSqlRaw`/`CommandText` 引入未校验插值，Semgrep 与 Sonar 可能同时沉默，形成检测盲区。

**修复**：恢复 `S2077=warning`，8 个已验证站点逐处 `#pragma warning disable S2077` 并注明依据（白名单/编译期常量/方言常量/已参数化）。Semgrep 保留为独立防线。修复后分析器仍为 0 警告。

**独特发现判定**：通过。该缺口不是任一工具的输出，而是「规则级关闭 + 工具覆盖差集」的交叉推理结果。

### F2（验证通过）：6 处 Semgrep SQLi findings 均为误报

- `PostgreSqlOutboxNotifier`：`_channelName` 经 `ValidateChannelName` 白名单 `[A-Za-z0-9_]` 后双引号包裹。
- `SqlitePerformanceOptimizer`：`WalPragma`/`ProductionRestPragma`/`LightRestPragma` 均为 `const string`。
- `DapperBulkCopy`：`table`/`colList` 经 `ValidateIdentifier`/`ValidateColumns` 入口校验；测试 `DapperBulkCopyTests.BulkInsertAsync_RejectsUnsafeTableName/ColumnName` 覆盖 `;DROP TABLE`、数字开头、空段等形态；值侧全参数化。
- 投影 checkpoint：`castType` 是方言常量（`INTEGER`/`BIGINT`/`SIGNED`）。

### F3（验证通过）：`Task.Run` 不传 token 的三处不是取消泄漏

`PostgreSqlOutboxNotifier.FireBatchProcessAsync`、`StreamRunRegistry.PumpAsync`、宽限期清理任务均不向 `Task.Run` 传 token，但内部通过 `stoppingToken`/`_cts.Token`/`grace.Token` 观察取消；若向 `Task.Run` 传 token，调度前取消会让任务不执行，反而留下 gate/run 终态不收敛。站点级 `#pragma` 已写明该语义。

### F4（低，观察）：`FromSqlRaw` 的抑制不可复制

两处 EF Core `FromSqlRaw` 的 `EF1002` + `S2077` 抑制依赖 `nowSql = GetNowSql()` 为 provider 常量、其余参数均为 `{0}/{1}/{2}` 占位。后续若在同类语句加入新插值，必须重新验证并单独注释，不能复制现有 pragma。此条写入 triage 文档的站点级抑制原则。

### F5（验证通过）：`MA0132` 修复修正了 MySQL DATETIME 偏移语义

`DapperIdempotencyStore.ToUtcOrMin` 原实现 `DateTime.SpecifyKind(..., Utc)` 后隐式转 `DateTimeOffset`，对 `Unspecified` 会套本地时区偏移；改为 `new DateTimeOffset(..., TimeSpan.Zero)` 后语义与注释一致。全量测试通过。

## 三、验证

| 检查 | 结果 |
|---|---|
| 分析器（`EnableQualityAnalyzers=true`） | 0 警告（S2077 恢复后仍为 0，靠站点级抑制） |
| Semgrep（p/csharp + p/security-audit） | 0 findings |
| 16 个测试项目 | 全部通过（Transactions 229/229；Compression 46/46） |
| Stryker | CompressionProvider 100%；MySqlLeasePolicy 100% |
| AOT（win-x64 真发布） | AotSample 全检查通过；PalOrmSample PASSED |

## 四、结论

试点产生 1 条独特发现（F1）并已修复；其余为验证性结论。ToB differential-review 方法论在本仓可用，后续用于发布前的高风险 diff；不引入额外工具依赖。
