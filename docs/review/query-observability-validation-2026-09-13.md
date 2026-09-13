# PalORM 查询可观测性与 SQL 安全：同业文章主张覆盖验证

> 验证编号：QUERY-OBS-VALIDATION-2026-09-13
> 数据来源：PalORM 5.5.1 源码逐项审读（`C:\ai\claude\Pal.ORM`，HEAD `b2e5741`）+ 测试文件核实
> 验证对象：微信公众号文章《.NET EF Core 查询优化实战（拦截器篇）：原生 SQL 安全下推 + 慢查询拦截》（作者：朕在coding，2026-09-11）
> 结论用途：核实文章三大主张在 PalORM 的覆盖情况，为"PalORM 能力对标行业实践"提供代码级证据；同时明确 Pal.DDD（消费侧）与 PalORM（本体）的职责边界

---

## 核心结论（先说答案）

文章的三张"底牌"（看清 SQL / 安全下推 / 慢查询拦截）在 PalORM 均有对应实现，且多处比文章建议更严谨。文章唯一有而 PalORM 无的是"拦截器内置阈值告警"，属职责分层差异（PalORM 提供耗时数据，告警交监控侧），不是能力缺口。

| 文章主张 | PalORM 覆盖 | 关键证据 |
|---|:---:|---|
| `ToQueryString()` 审视 EF 生成的 SQL | ✅ 覆盖且更完整 | `AsDryRun()` 返回 SQL + 参数快照（ITM-511 防御性副本）；含 Set 子句时返回 UPDATE 预览（ITM-563） |
| `FromSqlInterpolated` 参数化下推原生 SQL | ✅ 架构等价 | PalORM 本体即参数化 SQL 生成器（`DbParameter` 绑定）；Pal.DDD 的 Dapper 栈是手写全参数化 SQL；EF Core Repository 栈不承载业务查询 |
| `IDbCommandInterceptor` 慢查询拦截 | ✅ 覆盖且更完整 | `IQueryInterceptor` 三段式（`OnBefore`/`OnAfter`/`OnError`），`OnAfter` 携带 `elapsed` 与 `rowCount`，按 `Priority` 排序执行 |
| SQL Tag 定位调用者（文章中段引用） | ✅ 覆盖且自动化 | `TagWith` + 源生成器 `AutoTaggingEmitter` 编译期自动注入 `relativePath:line member` 注释，用户零代码改动 |
| 指标按查询名区分 | ⚠️ 有意不采纳 | `WithMetrics` 的 name 参数"仅保留 API 兼容，不作为指标标签"（`QueryBuilder.cs:480`），采用有界标签防高基数 |
| 拦截器内置慢查询阈值告警 | ❌ 无（职责分层） | PalORM 提供 `palorm.query.duration` Histogram，阈值告警由 OpenTelemetry 监控侧配置；文章的内置告警是单体项目简化做法 |

---

## 逐项验证

### 1. ToQueryString 对应物：`AsDryRun()`

**文章主张**：用 `ToQueryString()` 把 EF 实际发往数据库的 SQL 打印出来，"你以为的优雅一行，往往是一坨 EF 生啃出来的怪物"。

**PalORM 实现**（`src/PalORM.Core/QueryBuilder.cs:498-506`）：

```csharp
public DryRunResult AsDryRun()
{
    bool isUpdate = HasClause(QueryClauseKind.Set);
    IReadOnlyList<DbParameter> live = isUpdate ? GetUpdateParameters() : GetQueryParameters();
    var snapshot = new DbParameter[live.Count];
    for (int i = 0; i < live.Count; i++)
        snapshot[i] = _paramFactory(live[i].ParameterName, live[i].Value);
    return new(isUpdate ? BuildUpdateSql() : BuildSql(), Array.AsReadOnly(snapshot));
}
```

返回类型 `DryRunResult(string Sql, IReadOnlyList<DbParameter> Parameters)`（`IQueryInterceptor.cs`），"调试辅助，不执行数据库查询"。

**比文章多出的两点**：

- **参数快照是防御性副本**（ITM-511）：修改快照参数不影响后续真实执行。文章示例只打印 SQL 文本，参数值需自行拼装。
- **UPDATE 预览正确性**（ITM-563）：含 `Set` 子句时返回真实 UPDATE 语句，而不是丢弃 Set 的误导性 SELECT 预览。这是"预览与实际执行一致"的契约级保证，文章未涉及。

**判定**：覆盖，且增加了快照隔离与预览保真两个契约。

### 2. 参数化下推：架构等价，且不存在"翻译惨案"场景

**文章主张**：EF 翻译不佳时用 `FromSqlInterpolated` 下推参数化原生 SQL，绝不字符串拼接。

**PalORM 的情形**：PalORM 不是 LINQ 翻译器，`QueryBuilder` 本身就是参数化 SQL 生成器（`DbParameter` 绑定，见 `AsDryRun` 的 `GetQueryParameters()`），不存在"EF 翻译出嵌套子查询"的问题，也无"下推"的必要。文章的核心安全约束（参数化、防注入、命中执行计划缓存）是 PalORM 与 Dapper 栈的生成方式本身。

**Pal.DDD 三栈的关系**（`docs/architecture.md`、`docs/review/parity-analysis-2026-09-13.md`）：

- **PalORM 栈**：源生成 + 参数化 SQL 生成，本仓库 `PalDDD.PalORM` 为适配层（6 Store + UnitOfWork）。
- **Dapper 栈**：手写全参数化 SQL（`DapperIdempotencyStore` 等 Store 内联 SQL + `CommandDefinition` 参数绑定）。
- **EF Core 栈**：`Repository.EFCore` 定位为 UnitOfWork + Outbox 事件拦截器桥接，全仓无 `FromSql`/`ToQueryString` 使用（本次 git grep 实证），不承载复杂查询，文章针对的"EF 翻译惨案"在此栈不成立。

**判定**：安全目标（参数化、防注入、计划缓存）全覆盖；"下推"动作在本架构中无对象。

### 3. 慢查询拦截：`IQueryInterceptor` 三段式

**文章主张**：`IDbCommandInterceptor` 在命令执行前后切入，记录耗时，超过阈值告警。

**PalORM 实现**（`src/PalORM.Core/IQueryInterceptor.cs`）：

```csharp
public interface IQueryInterceptor
{
    int Priority => 100;                                  // 数值越小越先执行
    void OnBefore(QueryContext context);
    void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount);
    void OnError(QueryContext context, Exception exception);
}
```

**注册方式**（双双支持）：

- 构造期：`DbOptions.Interceptors`（`DbOptions.cs:90`，`IReadOnlyList<IQueryInterceptor>?`）
- 运行期：`DataSession<TProvider>.AddInterceptor(IQueryInterceptor)`（`DataSession.cs:181`，"日志/缓存/审计"，按 Priority 执行）

**配套实现**：`AuditInterceptor`（`AuditInterceptor.cs`，转发到 `ILogger`，默认 Priority=200 让业务拦截器优先；参数值默认不写日志，`[SensitiveData]` 列经掩码替代）。

**比文章多出的三点**：

- **三段式含 OnError**：文章示例只有"执行后统计耗时"一段，异常路径不产生记录；PalORM 的 `OnError` 覆盖失败路径（"此时不调用 OnAfter"有明确契约）。
- **`rowCount`**：文章靠 SQL 文本猜结果集大小；PalORM 直接给执行后行数。
- **优先级排序**：多个拦截器的执行顺序由 `Priority` 显式定义，不依赖注册顺序（文章无此概念）。

**文章的踩坑章节 vs PalORM 的既有设计**（文章第 5 节"拦截器是全局副作用，注意线程安全与性能"）：

- 热路径日志开销：`AuditInterceptor` 文档明确"无日志订阅者时仍构造 QueryContext 字符串，不适用于超高频场景；超高频请用 WithTracing"（`AuditInterceptor.cs` 注释），**边界前置声明**。
- 参数安全：`QueryContext.Parameters` 注释明令"拦截器不得修改 `DbParameter.Value`（会直接改变随后执行的 SQL 语义），仅可读取"（ITM-535），并附 `[SensitiveData]` 脱敏机制。文章对此的提醒仅停留在"打印日志用异步/队列"。

**判定**：覆盖，且契约（错误路径、只读参数、优先级、覆盖边界）比文章示例严格。

### 4. SQL Tag：源生成器自动打标

**文章主张**（A11/A12 段）：SQL 注释 `/* GetPendingOrders */` 让慢查询日志"一眼定位"调用者，"排查 30 分钟变 0 秒"。

**PalORM 实现**：

- 手动 API：`TagWith`（`QueryBuilder.cs` / `QueryBuilderExtensions.cs`）。
- **自动打标（文章无对应物）**：`PalORM.SourceGen/AutoTaggingEmitter.cs`，源生成器在编译期检测 6 个终态方法调用（`ToListAsync`/`FirstAsync`/`FirstOrDefaultAsync`/`SingleAsync`/`SingleOrDefaultAsync`/`ExecuteNonQueryAsync`），为每个调用点生成 `[InterceptsLocation]` 拦截方法，注入 `builder.Tag("relativePath:line member")`。用户零代码改动，SQL 自动携带源码定位注释。opt-in 开关：消费侧 csproj 设 `<PalORMAutoTagging>true</PalORMAutoTagging>`。

文章建议"手动给关键查询打 Tag"，PalORM 做到了"编译期自动全覆盖"，这是文章思路的自动化升级。

**测试覆盖**：`test/PalORM.SourceGen.Tests/AutoTaggingTests.cs`（专项）。

### 5. 指标：有界标签优先于查询名区分

**文章主张**：拦截器"顺手统计命令次数"，发现 N+1 的影子；指标可按查询区分。

**PalORM 实现**：`PalORMMetrics`（`PalORM.Core/PalORMMetrics.cs`）：

- `ActivitySource "PalORM"` + `Meter "PalORM"`（instrumentation version 与包版本对齐，ITM-773）
- `palorm.query.executions`（Counter，`"Number of database commands executed"`）
- `palorm.query.duration`（Histogram，秒）
- 标签："仅包含有界的 Provider、操作和结果"（类文档原文）

**关键差异**：`WithMetrics(string name)` 的 name 参数文档明说"仅保留 API 兼容，不作为指标标签"（`QueryBuilder.cs:480`）。查询名/调用方维度的高基数落地在 SQL 注释（TagWith）而非指标标签。这与 Pal.DDD 的"20 Counter 零 tag 设计声明"（见 CHANGELOG 可观测性条目）同口径：**指标用于聚合告警，定位靠 SQL 注释与 trace**，两套通道各司其职。

文章未讨论指标基数问题，其"按查询名区分"若直接实施在生产（每查询一个标签值）会引发高基数问题，PalORM 的选择是行业共识做法。

### 6. 唯一未覆盖项：内置阈值告警（职责分层，非缺口）

**文章主张**：拦截器内置"超过 50ms 打印慢 SQL"。

**PalORM 现状**：提供耗时数据（`OnAfter.elapsed`、`palorm.query.duration` Histogram），无内置阈值判断。

**判定为合理分层**：

- 阈值是环境相关参数（开发机 50ms 已慢，批处理 50ms 正常），库内写死任何阈值都会过时；文章自己也承认"可按业务调整"。
- PalORM 的 Histogram 接入 OpenTelemetry 后，阈值告警是监控侧（Prometheus AlertManager 等）的标准能力，比应用内打印更可靠（不受日志采样影响）。
- 需要应用内告警的场景，用户可基于 `IQueryInterceptor.OnAfter` 自行实现（`elapsed` 已给），`readme`/文档无需框架预设。

---

## 边界声明（PalORM 覆盖的显式限制）

论证"覆盖"必须同时声明覆盖的边界，避免误用：

1. **`IQueryInterceptor` 不覆盖所有操作**：仅作用于实体 SELECT 执行管线与 QueryBuilder UPDATE；INSERT/DELETE/Bulk/存储过程/QueryMultiple 不经过拦截器。接口文档与 `AuditInterceptor` 均前置声明"完整审计请用数据库层审计或 OpenTelemetry"。文章示例同样只覆盖命令执行（其覆盖面由 EF 拦截器机制决定，未声明边界，属文章薄弱点）。
2. **`WithTracing` 不含 SQL/参数/调用方路径**（`QueryBuilder.cs:473`），敏感信息治理决定；需要 SQL 文本用 `AsDryRun` 或审计拦截器。
3. **自动打标需 opt-in**（`<PalORMAutoTagging>true</PalORMAutoTagging>`），默认关闭，不存在隐藏代码生成。
4. **Pal.DDD 消费侧的可达性**：Pal.DDD 适配层已注册 Scoped `DataSession<TProvider>`（`SqlitePalOrmExtensions.cs:68` 等三方言），用户经 DI 注入后可访问上述全部 PalORM API，无"适配层透传缺口"。Pal.DDD 自身不重复封装查询可观测性（职责边界：适配层只做 6 Store + UnitOfWork）。

## 结论

1. 文章的"三张底牌"在 PalORM 全部有对应实现，多项（参数快照、UPDATE 预览保真、错误路径钩子、优先级、自动打标、有界标签、脱敏）超出文章的示例深度。
2. 文章的价值定位修正为：**同业对标样本**（证明这些能力是行业公认的生产必需品），而非设计输入（无新增能力需求）。
3. 唯一差异项（内置阈值告警）判定为职责分层，维持现状；如未来有应用内告警需求，基于 `IQueryInterceptor` 自行实现即可，无需改动 PalORM。
4. 本验证不产生 Pal.DDD 代码变更需求；Pal.DDD 侧无适配层动作。
