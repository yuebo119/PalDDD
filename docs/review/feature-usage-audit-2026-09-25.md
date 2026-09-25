# 引用库最新特性使用审计 — 2026-09-25

> **触发**：「充分审计，当前项目是否使用的引用库的最新特性？充分利用所有引用库的新特性，充分优化当前项目」
> **范围**：55 个中央包（`Directory.Packages.props`）+ slnx 外 2 项目，按库族分 5 组并行审计
> **方法**：官方文档/上游 release notes/本地 NuGet 包 XML 与 DLL 字节探针三方查证特性存在性；每条"未使用"结论附 grep 命令与命中数；关键项另跑编译/运行探针与隔离变异
> **性质**：审计 + 有限实施（本文落盘时点已完成 A/C/E/F/G 五项）

---

## 一、结论先行

1. **版本维度已无空间**：10 个基础设施包经 NuGet flat-container 实查全部已是最新稳定版；微软 rc 线 `11.0.0-rc.1.26425.128` 为该线最新（rc.2 未发）。差距全在**特性采用率**。
2. **运行时级特性利用充分**：`runtime-async=on`、`GetTypeInfo<T>` 14 处、`EqualityComparer.Create` 3 处、`System.Threading.Lock` 9 处、`TimeProvider` 38 文件、`SearchValues`、C# 12/13 特性（主构造 49、集合表达式、`params ReadOnlySpan`、`OverloadResolutionPriority`）均有落地。
3. **结构性结论（决定多数"未使用"的性质）**：
   - **本仓 EF 用法是 raw SQL + 投影**，EF Core 11 头条特性零落点：`Include`=0、`AsSplitQuery`=0、`GroupBy`(EF)=0、`UseSqlServer`=0、`MigrateAsync`=0、complex types=0。
   - **本仓 PalORM 适配层几乎完全绕过实体 CRUD 与 QueryBuilder**：`Session.ExecuteAsync` 30 次 / `QueryAsync<T>` 5 / `ScalarAsync` 4 / `QueryAsyncEnumerable` 2 / `InsertAsync` 2 / `BulkInsertAsync` 1，而 `GetAsync`/`UpdateAsync`/`DeleteAsync`/`From<T>()` **全为 0**——所有挂在 QueryBuilder 上的能力（`WhereIn`/`ForEachAsync`/`WithCache`/`ToPageAsync`/`GridReader`/Auto Tagging）**结构性零接触面**。
4. **真正值得动手的是 5 处真实缺口**（已实施），而非"用上某个新 API"。多数未用项属"本仓无对应场景"或"触及既有声明/已否决决策"。
5. **本次首跑即抓到 3 个真实问题**：压缩侧失实声明（安全正确性）、`.gitignore` 缺 Verify 产物规则、`.editorconfig` 缺 Verify 段。

---

## 二、已实施（A/C/E/F/G）

| # | 项 | 依据 | 落地 |
|---|---|---|---|
| **A** | 原生压缩改**流式 decoder** | `NativeCompressions.LZ4.Core.dll` 实测含 `LZ4Decoder`/`LZ4Encoder`/`LZ4Stream`、`Zstandard.Core.dll` 含 `ZstandardDecoder`/`ZstandardEncoder`/`ZstandardStream`；而 `NativeCompressors.cs` 头部声明「此限制受外部库 API 设计约束，**无法在适配层修复**」**失实** | 三处 `Decompress` 改走 `LZ4Decoder`/`ZstandardDecoder` span 循环（形态对齐同库既有 `BrotliCompressor.Decompress`），输出累计每轮即校验 `MaxOutputBytes`，超限立即中止——**上限检查先于后续分配生效**，不再等全量解压完成；失实声明与测试侧旧口径注释同步修正 |
| **C** | **`VerifyChecks.Run()` 约定自检** | `VerifyTUnit.VerifyChecks.Run()` 反射确认存在；本仓此前 0 使用 | 新增 `VerifyChecksTests`；首跑即抓两缺口并修复：`.gitignore` 补 `*.received.*`、`.editorconfig` 补 `[*.{received,verified}.{txt}]` 段（**段头内层花括号是官方文案原样**，由其 `HasAllExtensions` 的 `line[24..^2]` 切片解析，"简化"为 `.txt` 即过不了） |
| **E** | RabbitMQ **`PublishReturnException`** 诊断 | 7.2.0 新增，反射确认含 `Exchange`/`RoutingKey`/`ReplyCode`/`ReplyText`；本仓 0 使用 | `PublishAsync` 包 catch：补 NO_ROUTE 日志后 `throw;` 原样重抛——重试与 ITM-213 `mandatory:true` 契约不变 |
| **F** | MySQL **`configure` 钩子** | MySqlConnector 2.6 的 `ConfigureTracing` 因无 builder 入口而**结构性不可达**；PG 姊妹已有 `Action<NpgsqlDataSourceBuilder>` | 新增带 `Action<MySqlDataSourceBuilder>?` 的重载（**用重载而非改签名**，避免二进制破坏），`Build()` 前调用 |
| **G** | **ITM-261 RC1 预检回填 + canary** | EF11 RC1 真实探针实测：`Where(LockedUntil <= now)` 抛 `InvalidOperationException`、`OrderBy(CreatedAt)` 抛 `NotSupportedException`、`==` 可译 → **限制仍成立**，raw SQL 形态继续正确 | `SqliteOutboxDbContext` 注释回填实测结论；`OutboxSqliteConcurrencyTests` 加 canary 锁住两条不可翻译性——**若上游修复翻译即转红，是 GA 复核信号而非 bug**（GA 计划 §三.1 提前完成） |

**A 项变异验证（验证验证者）**：把 LZ4 上限检查改为 `totalWritten > long.MaxValue`（恒不触发）→ `Decompress_OutputAboveLimit_Native_ThrowsInvalidData(LZ4)` 精确变红（+30/x1）→ 还原后 31/31 绿。证明新实现的上限检查被测试真实覆盖。

---

## 三、待用户裁决（架构级，不自行定案）

| 项 | 现状 | 为什么不自行做 |
|---|---|---|
| **PalORM `readFromReplica` 读副本路由** | 5.6.0 新增的第 2 位参数，本仓 11 个读点全走默认 `false`（`ReadConnectionString` 0 配置） | 11 点中 **4 点是 read-after-write 敏感**（`MAX(stream_version)` 并发预检 ×2、`SELECT id` 回查、租约回读），走副本会读陈旧版本致并发判定误译。属架构决策，影响全部消费者。报告建议仅 EventLog 纯读流（`:214/:251`）试点 |
| **PalORM `CircuitBreakerScope`** | 5.6.0 新增；本仓 `DataSession` 注册为 **Scoped**，正是上游文档点名的「一请求一会话下默认阈值 5 几乎不可能达到、熔断形同虚设」形态 | 本仓 `WithCircuitBreaker` 只有 3 处 XML 注释示例、无实调。若要启用熔断则必须同步设 `Scope=Process`，但"是否启用熔断"本身是宿主决策 |
| **.NET 11 `GZipEncoder`/`DeflateEncoder` span 编解码** | 本仓 `SystemCompressor` 的 GZip/Deflate 仍走 `MemoryStream`+`GZipStream`+`ToArray`（Brotli 已是 span 形态）；探针已实测新编码器双向互通 | 性能收益**本仓未测**（PERF 纪律：先录基线、改后 A/B、<5% 默认不做）；且 `quality(int)` 构造与 `CompressionLevel` 枚举非同一标度，映射值需按官方文档另定 |
| **RabbitMQ `RabbitMQActivitySource` 内建 OTel** | broker 发布/消费 span 目前完全缺失（`PalActivitySource` 19 处无一覆盖）；DLL 探针确认 API 存在 | 有**待实测的冲突点**：客户端 `ContextInjector` 自动注入的 traceparent（来自 `Activity.Current`）与本仓从 outbox 持久化还原的 `MessagePublishContext.TraceParent` 可能同键冲突，谁覆盖谁需先写集成测试 |
| **PalORM `PreWarmAsync` + `WithPool` 生产配置面** | 三方言现用 `DbOptions.Development(...)`，无预热/池参数入口 | 上游宣称远程建连 ~8.5ms/条（**上游数字，本仓未实测**）；`Development`→`Production` 改默认属对外契约变更，须裁决 |

---

## 四、高价值但本轮未实施（改动面 / 成本说明）

| 项 | 价值 | 成本 | 说明 |
|---|---|---|---|
| **`Assert.That(x.Count).IsEqualTo(n)` → `HasCount(n)`** | 失败消息从"期望 1 实际 2"升级为集合内容+计数，断言强度不降反升；`HasCount` 0 使用 vs 候选 **80 处** | **33 个文件**（最多 5 处/文件），逐文件 Read+Edit | 已抽验主体全部为集合类型（`events`/`leased`/`listener.Measurements`/`catalog.Descriptors` 等），无标量误伤；过 `AssertionStrengthGateTests`（棘轮只统计 `IsNotNull` 与零断言方法） |
| **FsCheck 接入 `[FsCheckProperty]`** | `TUnit.FsCheck` 被 2 个 csproj 引用却 **0 消费**；换来 seed 一键重放 + TUnit 统一失败报告 | 半天（10 处 `Prop.ForAll(...).QuickCheckThrowOnFailure()` 改写 + `Replay`/`MaxTest` 配置） | 现属性测试无确定性重放能力（`Config.Default`/`WithReplay`/`WithMaxTest` 均 0 使用） |
| **并发测试加 `[Repeat(n)]`** | 把偶发竞态变必现；`[Repeat]` 0 使用 | 10 分钟 | 首选 `SmartEnumTests.ConcurrentReads_*`、`OutboxSqliteConcurrencyTests.LeasePending_SequentialWorkers_*`；纯内存测试成本近零 |
| **`Skip.Unless` + 自定义 `[RequiresDocker]`** | 12 处 `if (!x) Skip.Test(...)` 收敛为声明式；`Skip.Unless` 仅 2 处 | 1-2 小时 | 消除 4 个文件重复的环境守卫模板 |
| **BDN `[StatisticalTestColumn]`** | 8 处 baseline 比对现靠人眼读 `github.md`，与 PERF 纪律的"噪声地板/显著性"要求不对齐 | 半天（含 docs/performance.md 口径同步） | 需同步列数说明 |

---

## 五、明确不建议（含已否决，勿再提）

**已否决（历史裁决）**：Kafka Produce 批量回调（第 28 轮）、ZLogger 结构化门面改造（第 28 轮）、Dapper.AOT 启用（已完成）、Saga freeze 机制（ADR-015）、非 JSON 序列化全面铺开（ADR-002 采纳 MemoryPack 但暂不实施）。

**本轮判定不建议**：

| 项 | 理由 |
|---|---|
| C# 15 `union` | 官方明示「部分提案特性尚未实现」；引入属架构级隐形决策（Result/消息形态重做 + 序列化联动） |
| C# 15 `closed` 闭合层级 | `DomainEvent`/`Entity`/`SmartEnum` 是面向外部派生的框架库公共抽象类，`closed` 直接破坏扩展点 |
| C# 15 Memory safety | 需 `LangVersion=preview`（本仓 `latest`），且全仓 `unsafe` = 0 |
| `Decimal32/64/128` 替代 `decimal` | 仓内 0 处 decimal 强转/解析；十进制浮点引入 NaN/Inf 语义属无需求的风险引入 |
| `MessageCatalog` 用 `TryAdd(out index)` 机械替换 | 命中 **v29 P3 声明注释**（双键原子性），`TryAdd` 首键即提交会重现声明所修的中间态 |
| 49 处 DI 扩展方法迁 `extension` 块、11 处 `new List(n)` 改 `[with(capacity:)]` | 纯语法糖/零收益，违反"不重构没坏的代码" |
| EF SQL Server 全族（vector/全文/JSON_CONTAINS/temporal/DateTrunc） | 无 SqlServer provider（`UseSqlServer`=0）；`SqlServerOutboxDbContext` 已 `[Obsolete]` 待 v4.0 移除 |
| EF 迁移族（`--add`/`ExcludeForeignKeyFromMigrations` 等） | 本仓无 EF migrations（`EnsureCreated`=40 + `docs/sql` 手写 DDL）；引入=第二真源 |
| `LoggerMessageAttribute` 泛型源生成 | 已有反向裁决（`PalLogger` CA1848 + `PipelineBehaviors` YAGNI） |
| PalORM `CreateBatch`/`SessionBatch` | 本仓全是单语句且需逐条 `affected`/`RETURNING`，压批只返回累计行数 |
| PalORM `ForParallelReads` | 并行已有更优解：`OutboxBatchProcessor` 用 `Parallel.ForEachAsync` + per-worker `CreateScope()` |
| PalORM `WithTransactionRetry` | 与 `PalOrmUnitOfWork` 手工事务模型（ADR-023 嵌套 fail-fast）冲突；at-least-once 重放需业务级裁决 |
| PalORM `InsertNoReturning`/`InsertReturningKeyOnly` "改实体以适配" | 判定要求**无任何转换器列**，而上游明文规定「Ulid 必须配置编译期值转换器」→ `OutboxMessageRow` 结构性不可行；`InboxMessageRow` 虽判真但 Store 走手写 `ON CONFLICT...RETURNING`，不经 `InsertAsync` |
| `MigrateAsync` | 复合主键表被 PALORM019 拒绝注册 + Saga 开放泛型不可注册 → 覆盖不了全 schema |
| Auto Tagging `<PalORMAutoTagging>` | 生成器只检测 QueryBuilderExtensions 6 个终态方法，本仓 `From<T>()` = 0 → 开了也零生成 |
| Testcontainers `WithReuse(true)` | 关闭资源回收器，与本仓 ownership marker + 会话末 DROP 安全设计冲突 |
| TUnit `[Retry]` | 把真失败洗成绿，与"假失败必须查因"纪律冲突 |
| BDN `[Benchmark(OperationsPerInvoke)]` | 会把含 100 次重灌的成本除以 100，改变历史口径且失真 |
| LZ4 `trustedData=true` | 信任攻击者可控的帧头 ContentSize，削弱炸弹防护（与安全方向相反） |
| 真 OpenZL | 官方实验包，自评高层 API「只是包了 Zstandard，没有太大意义」 |
| `FromSqlRaw` → `FromSql` | `FromSqlRaw` **未** obsolete（元数据实证），主动改属重构没坏的代码 |

---

## 六、局限与标注

- **性能数字全部是上游 CHANGELOG 宣称值，本仓一次未测**（PalORM 5.6.0 的 −53%/−41%/−76% 等）[事实]。
- A 项收益是**安全时序改善**（上限检查前移），非性能优化；未做性能 A/B（契约不变、无回归即可）。
- `CircuitBreakerScope` 写入 API 形态未在 `DbOptions` member 列表中找到直接 setter → 实施前需再查 [推断]。
- `[CacheCommand]`/`[IncludeLocation]` 带 `[Conditional("DEBUG")]`，Release 下分析器是否仍读到语法需实测 [推断]。
- 5 组报告的 grep 数字已由主线程**逐项复验**：测试栈 100% 吻合、EF 15/16（`HasConversion` 差 1 属边界口径）、平台层 11/12、三栈 25/26（`QueryAsync` 差异经查是我方 grep 把 `QueryAsyncEnumerable` 前缀误并）、基础设施全部吻合。
- 本轮**未改** `scripts/*.cs`（曾有并行进程编辑，现已由其归属方处理）。
