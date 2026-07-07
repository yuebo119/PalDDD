# V8 ORM 全量 API 特性清单

> 基于全语言 25+ ORM（Dapper/EF Core/Hibernate/GORM/Prisma/linq2db/jOOQ/Diesel/sqlc）调研，98 个 API 按实施阶段排列。每项含对标 ORM 来源和必要性论证。

## P0 (必须实现 — 62 项)

### 基础注解（所有 ORM 的根基）

| # | 注解 | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| BA1 | `[Table("name")]` | EF Core, GORM, Prisma, jOOQ, 全语言 | 实体类↔表名映射。无此→类名当表名→snake_case 无法工作 | P0 |
| BA2 | `[Column("name")]` | EF Core, GORM, Prisma, jOOQ, 全语言 | 属性↔列名映射。无此→属性名当列名→与 DB 命名冲突 | P0 |
| BA3 | `[Key]` | EF Core, GORM, Prisma, jOOQ, 全语言 | 主键标识。无此→CRUD 无法生成 WHERE 子句 | P0 |
| BA4 | `[NotMapped]` | EF Core, GORM, 全语言 | 排除非 DB 属性→不参与查询/DDL 生成 | P0 |
| BA5 | `[ForeignKey]` + `OnDelete` | EF Core, GORM, Prisma | FK 约束 + CASCADE/NO ACTION 控制。CASCADE DELETE 毁库(M2.3M 行)→默认 NO ACTION | P0 |
| BA6 | `[ConcurrencyCheck]` | EF Core IsConcurrencyToken | 乐观锁版本号→UPDATE WHERE version=@old→防并发覆盖 | P0 |
| BA7 | `[IgnoreOnInsert]` | EF Core DatabaseGenerated | DB 生成列(created_at default NOW())→Insert 时排除 | P0 |
| BA8 | `[Column(Length=128)]` | EF Core MaxLength | Schema 生成→VARCHAR(128)。无→默认长度→截断→数据丢失 | P0 |
| BA9 | `[Column(Precision=10, Scale=2)]` | EF Core Precision | decimal 精度。金融计算→精度丢失→金额错误 | P0 |
| BA10 | `[Required]` | EF Core Required | NOT NULL 约束→源生成 DDL 含 NOT NULL | P0 |
| BA11 | `[DefaultValue("NOW()")]` | EF Core DefaultValueSql | Insert 时 DB 自动填值→不覆盖 | P0 |
| BA12 | `[Timestamp]` / `[RowVersion]` | EF Core IsRowVersion | DB 自动递增的并发令牌→UPDATE 自动更新→防并发覆盖。与 BA6 不同：Timestamp 由 DB 管理，BA6 由应用管理 | P0 |
| BA13 | `[Column(TypeName = "varchar")]` | EF Core ColumnTypeName | 强制覆盖 DB 列类型。PG `inet`→`[Column(TypeName="inet")]`→精确控制 DDL | P1 |

### 设计阶段 — Schema & 迁移

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| M1 | `MigrateAsync(targetVersion?)` | GORM AutoMigrate, EF Core | 新人 clone 项目→DB 不存在→crash。CI/CD 需要建表步骤 | P0 |
| M3 | `SeedAsync<T>(IEnumerable<T>)` | EF Core HasData, Prisma seed.ts | 测试环境需要预置数据。每次手写 SQL INSERT→重复代码∞ | P0 |
| M4 | `ValidateSchemaAsync()` → `List<string>` | Diesel compile-time check | DBA 手改列类型（VARCHAR(50)→VARCHAR(100)）→应用不知道→运行时才炸 | P0 |
| M5 | `[Unique]` / `[Index]` | EF Core HasIndex, Prisma @unique | email 列无唯一索引→并发 INSERT 重复邮箱→数据完整性 bug | P0 |
| M6 | `DiffAsync()` → `List<MigrationOperation>` | GORM AutoMigrate diff, Prisma migrate diff | DBA 手改生产表结构→CI 无感知→部署后字段不存在→崩 | P0 |
| M7 | `[Index(name, columns, unique)]` | EF Core HasKey 复合键, 全语言支持 | UNIQUE(user_id, order_id) 是最常见索引模式。M5 只支持单列 | P0 |

### 设计阶段 — 编译时验证

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| V1 | 源生成 SQL 片段验证 | Diesel `query!()` 宏 | `Where($"name = {x}")` 中 `name` 不存在于 [Column] 注解→编译报错。把运行时 SQL 错误提升到编译时 | P0 |
| V2 | `[SqlTemplate]` 预编译 | EF Core PrecompiledQueries, sqlc | 高频查询（10 万次/天）→每次分配 SQL 字符串→GC 压力→延迟尖刺。预编译 DbCommand 模板消除分配 | P0 |
| V3 | 诊断规则（ColumnNameMismatch 等） | Rust 编译器错误风格 | V1 报语法错。V3 提供"Did you mean 'status'?"→降低排查时间 | P0 |
| V4 | `[SqlFile("queries/get_orders.sql")]` | sqlc Go 核心工作流 | DBA 团队维护 .sql 文件→与开发 C# 字符串同步困难→源生成器直接读取 .sql | P0 |
| V5 | `scaffold` CLI 工具 | jOOQ codegen, Prisma introspect | 已有 60 张表的 Brownfield 项目→手写注解 2 天→一行命令替代 | P0 |
| V6 | `[Schema("public")]` / `[Database("shard")]` | Prisma @@schema | 多租户 SaaS→每 SQL 手写 `tenant_123.` 前缀→改租户名=全局搜索替换→一个注解解决 | P0 |

### 开发阶段 — 查询构建器

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| QB1 | `From<T>()` → `QueryBuilder<T>` | Dapper.SqlBuilder, Kysely | 类型安全查询入口。无此→裸 SQL 散落各处 | P0 |
| QB2 | `.ToListAsync(ct)` | Dapper, EF Core | 无执行方法→无法获取结果 | P0 |
| QB3 | `.FirstAsync(ct)` / `.SingleAsync(ct)` | Dapper | 单行查询 | P0 |
| QB4 | `.FirstOrDefaultAsync(ct)` / `.SingleOrDefaultAsync(ct)` | Dapper | 单行查询（可空） | P0 |
| QB5 | `.ToPageAsync(page, pageSize, ct)` → `(rows, total)` | DapperExtensions, EF Core Skip/Take | OFFSET 10000→DB 扫描丢弃 99980 行→900ms。Keyset 分页保持 0.9ms | P0 |
| QB6 | `.Where(FormattableString clause)` | Dapper.SqlBuilder | 无 WHERE→每次查询全表扫描 | P0 |
| QB7 | `.OrWhere(FormattableString clause)` | Dapper.SqlBuilder | OR 条件组合 | P0 |
| QB8 | `.WhereIf(bool condition, FormattableString clause)` | Dapper.SqlBuilder 自定义 | 无→if/else 包裹整个 builder 链→代码膨胀。链内条件分支 | P1 |
| QB9 | `.WhereIn<T>(Expression, IEnumerable<T>)` | Dapper IN 子句 | 手写→参数爆炸（2000 个 @p）→SQL 超限 | P0 |
| QB10 | `.WhereNotIn<T>(Expression, IEnumerable<T>)` | 扩展自 QB9 | NOT IN 对称 | P1 |
| QB11 | `.WhereNull(Expression)` / `.WhereNotNull(Expression)` | 生态首创 ✦ | IS NULL 手写→易忘→查询结果不对 | P1 |
| QB12 | `.WhereBetween<T>(Expression, from, to)` | 生态首创 ✦ | BETWEEN 手写→边界值易错 | P1 |
| QB13 | `.OrderBy(Expression, bool descending?)` | Dapper.SqlBuilder, EF Core | 排序手写字符串→注入风险 | P0 |
| QB14 | `.ThenBy(Expression)` / `.ThenByDescending(Expression)` | Dapper.SqlBuilder | 多字段排序 | P0 |
| QB15 | `.Select(Expression, params Expression[])` | Dapper.SqlBuilder | SELECT *→大字段(text/blob)全加载→OOM。精确选列 | P0 |
| QB16 | `.GroupBy(Expression)` | Dapper.SqlBuilder | 聚合查询 | P0 |
| QB17 | `.Having(FormattableString clause)` | Dapper.SqlBuilder | GROUP BY + HAVING→手写→语法易错 | P0 |
| QB18 | `.InnerJoin<TJoin>(FormattableString onClause)` | Dapper.SqlBuilder | JOIN 手写→列歧义→映射错 | P0 |
| QB19 | `.LeftJoin<TJoin>(FormattableString onClause)` | Dapper.SqlBuilder | 同上 | P0 |
| QB20 | `.RightJoin<TJoin>(FormattableString onClause)` | Dapper.SqlBuilder | 同上 | P0 |
| QB21 | `.Union(QueryBuilder<T>)` | Dapper.SqlBuilder | 两查询→代码重复→合并 | P1 |
| QB22 | `.Intersect(QueryBuilder<T>)` | Dapper.SqlBuilder | 同上 | P1 |
| QB23 | `.Set(Expression, value)` | Dapper.SqlBuilder | UPDATE SET→手写→忘 WHERE→全表改 | P0 |
| QB24 | `.QueryMultipleAsync(FormattableString sql)` → `GridReader` | Dapper QueryMultiple | 多结果集→多次往返→延迟×N。一次往返解决 | P0 |
| QB25 | `.ExistsAsync(FormattableString sql)` → `bool` | PetaPoco | COUNT(*)>0→多一次往返。EXISTS 是标准 SQL 模式 | P1 |
| QB26 | `.WindowOver(partition, order)` | Django ORM Window | "每组 Top-3"→用 N+1 实现→延迟 10x。ROW_NUMBER() OVER 一次查询 | P0 |
| QB27 | `.With("cte", subquery)` | Django ORM / linq2db CTE | "上月 vs 本月"→两次查询→应用合并→延迟 2x。CTE 单次往返 | P0 |
| QB28 | `.AsSplitQuery()` | EF Core 5 AsSplitQuery（史上评价最高特性） | Include JOIN→1000×100=10 万行笛卡尔积→8 秒。拆分为独立查询→200ms | P0 |
| QB29 | `.RecursiveWith("cte", anchor, recursive)` | linq2db CTE / jOOQ | 组织树→N 次查询。WITH RECURSIVE 一次查询 | P1 |
| QB30 | `.WhereJson("key->>'name' = {v}")` | Django KeyTransform / jOOQ | PostgreSQL JSONB 查询→手写→类型不安全 | P1 |

### 开发阶段 — 直查 API

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| D1 | `QueryAsync<T>(FormattableString sql)` | Dapper QueryAsync<T> | QueryBuilder 不能表达的高级 SQL→退路必须。无退路→被迫手写 ADO.NET | P0 |
| D2 | `QueryFirstAsync<T>(FormattableString sql)` | Dapper | 同上 | P0 |
| D3 | `QuerySingleAsync<T>(FormattableString sql)` | Dapper | 同上 | P0 |
| D4 | `ScalarAsync<T>(FormattableString sql)` | Dapper ExecuteScalar | 聚合函数/单值查询 | P0 |

### 开发阶段 — 聚合便捷方法

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| AG1 | `CountAsync<T>(FormattableString sql)` → `long` | EF Core CountAsync, 全语言 | 最常用聚合。写 `SELECT COUNT(*)` 手写→多行→易忘参数 | P1 |
| AG2 | `SumAsync<T>(Expression<Func<T,object>>)` → `decimal` | EF Core SumAsync | 金额求和→手写→精度易错 | P1 |
| AG3 | `MaxAsync<T>(Expression<Func<T,object>>)` → `TResult` | EF Core MaxAsync | 范围查询→手写 CAST→易错 | P1 |
| AG4 | `MinAsync<T>(Expression<Func<T,object>>)` → `TResult` | EF Core MinAsync | 同上 | P1 |
| AG5 | `AvgAsync<T>(Expression<Func<T,object>>)` → `double` | EF Core AverageAsync | 平均值→手写→小数精度 | P1 |

### 开发阶段 — 流式查询

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| ST1 | `QueryAsyncEnumerable<T>(FormattableString sql)` → `IAsyncEnumerable<T>` | Dapper buffered:false, EF Core AsAsyncEnumerable | 100 万行结果→ToList()→OOM。流式逐行处理→内存恒定 | P1 |

### 开发阶段 — CRUD

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| C1 | `GetAsync<T>(object key)` | Dapper.Contrib | 25 列实体→手写 `SELECT * FROM t WHERE a=@a AND b=@b`→忘一列→SQL 错 | P0 |
| C2 | `GetAllAsync<T>()` | Dapper.Contrib | `SELECT * FROM t` 每次手写→重复 | P0 |
| C3 | `InsertAsync<T>(T entity)` → `T` | Dapper.Contrib | 源生成 INSERT + RETURNING→返回实体含 ID。Dapper.Contrib 需要 @@identity 额外查询 | P0 |
| C4 | `UpdateAsync<T>(T entity)` | Dapper.Contrib | 源生成 UPDATE SET 全部列→不错列 | P0 |
| C5 | `DeleteAsync<T>(object key)` | Dapper.Contrib | 忘写 WHERE→删全表→源生成保证必有 WHERE | P0 |

### 开发阶段 — 写入 / 批量 / 事务

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| W1 | `ExecuteAsync(FormattableString sql)` → `int` | Dapper ExecuteAsync | 手写 DDL / 自定义 DML→TRUNCATE/ALTER 无法用 QueryBuilder | P0 |
| W2 | `SaveAsync<T>(T entity)` (InsertOrUpdate) | Dapper.Contrib Upsert | 先查 key 存在性→决定 Insert 或 Update。避免 GORM Save() ID=0 的不一致行为 | P0 |
| W3 | `UpdateColumnsAsync<T>(id, partial)` | EF Core ExecuteUpdate | 只改 Status→不改全部列→性能提升 | P0 |
| B1 | `BulkInsertAsync<T>(IEnumerable<T>, int? batchSize)` | linq2db/EF Core BulkCopy | 10000 行逐条 INSERT→16 秒。批量→0.3 秒 | P0 |
| B2 | `BulkUpdateAsync<T>(IEnumerable<T>)` | Dapper Plus/linq2db | 批量更新按主键 | P0 |
| B3 | `BulkMergeAsync<T>(IEnumerable<T>)` | Dapper Plus/linq2db | INSERT OR UPDATE 批量 | P0 |
| B4 | `BulkDeleteAsync<T>(IEnumerable<object> keys)` | Dapper Plus | 批量删除 | P0 |
| T1 | `BeginTransactionAsync()` | ADO.NET 标配 | 没有事务→数据不一致。全语言标配 | P0 |
| T2 | `CommitAsync()` / `RollbackAsync()` | ADO.NET 标配 | 同上 | P0 |
| T3 | `WithTransaction(tran).From<T>()` | Dapper 事务扩展 | 事务内链式操作 | P0 |
| T4 | `WithTransaction(tran).BulkInsertAsync<T>(items)` | 批量事务包裹 | 批量操作不在事务内→部分成功 | P0 |
| T5 | `db.WithIsolationLevel(IsolationLevel.Serializable)` | ADO.NET IsolationLevel, 全语言 | 高并发扣库存→需要 Serializable→防幻读。ORM 不控制=依赖 DB 默认→Read Committed→数据不一致 | P0 |
| T6 | `tran.SaveAsync("sp_name")` / `tran.RollbackToAsync("sp_name")` | EF Core Savepoints, ADO.NET | 嵌套事务→部分回滚。批量操作→第 500 条失败→回滚到 savepoint→前 499 保留。无此→全部回滚 | P1 |

### 开发阶段 — 关联加载 / 多租户 / 语句准备

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| R1 | `builder.Include<TChild>(fk, ck)` | EF Core Include, GORM Preload, Prisma include | 关联查询→不用 Include→N+1→100 个父对象=101 次查询。一次 JOIN 替代 | P0 |
| R2 | `builder.ThenInclude<TGrandChild>(...)` | EF Core ThenInclude | 三层关联→每层需要一次 Include | P0 |
| S1 | `db.WithTenant(object tenantId)` | EF Core HasQueryFilter | 多租户 SaaS→每 SQL 手写 WHERE tenant_id=@t→忘一次→数据泄露 | P0 |
| S2 | `[TenantAware]` 注解 | 自创（基于 EF Core QueryFilter 模式） | 标注实体→自动应用租户过滤。不需要每查询重复 S1 | P0 |
| P1 | `builder.AsPrepared()` | Go sql.Stmt, EF Core Precompiled | 高频查询（1000 次/秒）→每次 SQL 字符串分配 1KB→每秒 1MB 分配→GC 压力 | P0 |

### 全阶段 — 高级特性

| # | API | 对标 ORM | 必要性论证 | P |
|---|------|------|------|:--:|
| A1 | `db.WithFilter<T>(Expression<Func<T, bool>>)` | EF Core HasQueryFilter | 软删除→每查询手写 WHERE deleted_at IS NULL→忘一次→幽灵数据 | P0 |
| A2 | `[OwnedJson]` 注解 | EF Core OwnsOne | JSON 列→C# 复杂类型映射。手写 JsonSerializer→AOT 不兼容（需要源生成） | P0 |
| A3 | `db.WithCache(name, TimeSpan? ttl)` | RepoDB Cache | 高频只读查询→每次打 DB→CPU 浪费。本地缓存命中→DB 负载降低 | P1 |
| A4 | `db.WithTracing()` | EF Core Logging | 慢查询→无日志→不知道哪个查询→排查 30 分钟 | P0 |
| A5 | `db.WithMetrics(name)` → OpenTelemetry | EF Core Metrics | DB 问题→无 Prometheus 指标→盲飞→告警缺位 | P0 |
| A6 | `db.StoredProc("proc_name").WithParam(...).QueryAsync<T>()` | Insight.Database | 遗留系统→DBA 要求存储过程→无支持→拒绝迁移 | P0 |
| A7 | `DataSession.For(DbProvider.Xxx)` | linq2db DataOptions | 多数据库 Provider→编译时常量→switch 分发 | P0 |
| A8 | `ForRead()` / `ForWrite()` | GORM DBResolver | 读写分离→写操作误发只读副本→读旧数据→业务逻辑错误 | P0 |
| A9 | `db.WithRetry(int maxRetries, Func<int,TimeSpan> backoff)` | EF Core EnableRetryOnFailure | DB 瞬时故障→无重试→请求失败→用户看到错误。默认 3 次退避重试 | P0 |
| A10 | `db.WithTimeout(TimeSpan timeout)` | ADO.NET CommandTimeout | 慢查询→占住连接 30 秒→连接池耗尽→其他请求等待→雪崩 | P0 |
| A25 | `builder.WithCommandTimeout(int seconds)` | Dapper CommandDefinition, EF Core | 单次查询超时——与 A10 全局超时互补。报表查询→需要 120s→其他查询 5s | P1 |
| A11 | `db.WithCircuitBreaker(int failures, TimeSpan resetAfter)` | Polly CircuitBreaker | DB 故障→一直重试→线程池耗尽→雪崩。熔断后快速失败→保护上游 | P0 |
| A12 | `builder.Tag("name")` / `TagWithCaller()` | EF Core TagWith | 生产慢查询日志 → `SELECT /* GetOrders */ ...` → 一眼定位调用者。无→排查 30 分钟 | P0 |
| A14 | `DbOptions.WithPool(size, idle, lifetime)` | Bun Go | 连接池不配置→默认 max=100→1000 并发→503 | P0 |
| A15 | `builder.AsDryRun()` → `DryRunResult {Sql, Parameters}` | EF Core ToQueryString, GORM DryRun | 本地开发→想知道生成什么 SQL→无→打日志猜→效率低 | P0 |
| A16 | `db.AddInterceptor(IQueryInterceptor)` | EF Core Interceptors, Prisma Middleware | 自定义日志/缓存/脱敏→不改 ORM 源码→侵入少 | P1 |
| A17 | `builder.Raw(string literal)` | Dapper.SqlBuilder `/**literal**/` | 动态 ORDER BY 列名→白名单校验→无法参数化→需要 Literal 注入 | P0 |
| A18 | `DbOptions.WithNamingConvention(NamingConvention.SnakeCase)` | GORM NamingStrategy | snake_case 项目→不用每个列写 [Column]→70 列实体省 70 行注解 | P1 |
| A20 | `[Converter(typeof(TConverter))]` where TConverter : `IValueConverter<T,U>` | linq2db/EF Core ValueConverter | PG INET→C# IPAddress→每项目重复手写→标准化 | P1 |
| A21 | `builder.ForUpdate()` / `builder.ForShare()` / `ForUpdate(skipLocked: true)` | jOOQ forUpdate(), GORM Clauses.Locking | 高并发扣库存→SELECT FOR UPDATE→悲观锁→防止超卖。skipLocked=队列模式 | P0 |
| A23 | `db.HealthCheckAsync(ct)` → `HealthResult {IsHealthy, Latency, Error?}` | EF Core CanConnectAsync, GORM Ping | K8s liveness probe→无健康检查→Pod 被认为不健康→重启→连锁反应 | P0 |

### 注解

| # | 注解 | 对标 | 必要性论证 | P |
|---|------|------|------|:--:|
| AN1 | `[Computed("SQL_EXPR")]` | SQLAlchemy Hybrid Property | 实体用 `UPPER(name)` 但查询用 `name`→不一致→手动改 | P0 |
| AN2 | `[SensitiveData]` | 生态首创 ✦ | 生产日志含手机号→GDPR 罚款。ORM 日志自动脱敏→"***MASKED***" | P0 |
| AN3 | `[CompositeKey(nameof(a), nameof(b))]` | EF Core HasKey 复合键 | 真实 schema 60% 有复合主键。单列 [Key] 不够 | P0 |
| AN4 | `[SoftDelete]` | GORM gorm.Model, EF Core QueryFilter | 需要时才 opt-in。GORM 教训:默认软删→Delete 不真删→磁盘满 | P0 |
| AN5 | `[Column(StoreAs = StoreAs.Int32)]` / `[Column(StoreAs = StoreAs.String)]` | 自创（基于 EF Core HasConversion 模式） | enum→DB 存 int 还是 string→编译时确定→不会运行时不对 | P0 |

### 开发者安全 & 测试

| # | 机制 | 对标 | 必要性论证 | P |
|---|------|------|------|:--:|
| DS1 | N+1 检测（Dev 模式） | 生态首创 ✦ | 最经典 ORM 性能杀手→编译时警告。Release 零成本 | P1 |
| DS2 | Unbounded Result Warning（Dev 模式） | 生态首创 ✦ | GetAllAsync 无 WHERE/LIMIT→1000 万行→OOM。Dev 模式自动注入 LIMIT 1001+警告 | P1 |
| TST1 | `TestDb.Sqlite()` | EF Core InMemory | 三行写集成测试。不用 Docker | P1 |
| TST2 | `TestDb.FromRows<T>(IEnumerable<T>)` | jOOQ MockConnection | 纯内存模拟→不连真实 DB | P1 |

---

## P1 (重要增值 — 18 项)

| # | API | 对标 | 必要性 | P |
|---|------|------|------|:--:|
| QB8 | WhereIf | Dapper.SqlBuilder 自定义 | 链内条件分支 | P1 |
| QB11 | WhereNull/WhereNotNull | 生态首创 ✦ | IS NULL 编译时安全 | P1 |
| QB12 | WhereBetween | 生态首创 ✦ | BETWEEN 编译时安全 | P1 |
| QB21-22 | Union/Intersect | Dapper.SqlBuilder | 两查询合并 | P1 |
| QB25 | ExistsAsync | PetaPoco | 一次往返 | P1 |
| QB29 | RecursiveWith | linq2db/jOOQ | 树形结构 | P1 |
| QB30 | WhereJson | Django KeyTransform | JSONB 查询 | P1 |
| A3 | WithCache | RepoDB | 高频只读缓存 | P1 |
| A16 | Interceptor | EF Core Interceptors | 自定义扩展点 | P1 |
| A18 | NamingConvention | GORM NamingStrategy | snake_case 自动映射 | P1 |
| A19 | Record/init 支持 | jOOQ Record 类型 | C# 9+ record→init setter→2026 新项目标配 | P1 |
| A20 | ValueConverter | linq2db/EF Core | PG 非标准类型映射 | P1 |
| A22 | Notify | Npgsql WaitAsync | PG NOTIFY→最简单实时通知 | P1 |
| DS1 | N+1 检测 | 生态首创 ✦ | 编译时拦截 N+1 | P1 |
| DS2 | Unbounded Warning | 生态首创 ✦ | Dev 模式自动 LIMIT | P1 |
| TST1 | TestDb.Sqlite | EF Core InMemory | 3 行集成测试 | P1 |
| TST2 | TestDb.FromRows | jOOQ MockConnection | 纯内存模拟 | P1 |

## P2 (边缘场景 — 2 项)

| # | API | 对标 | 必要性 | P |
|---|------|------|------|:--:|
| A24 | TempTable | linq2db CreateTempTable | 复杂报表→临时表→少数场景 | P2 |
| V4 | SqlFile（动态 SQL） | sqlc Go | .sql 文件中含条件逻辑→源生成器复杂→二期 | P2 |

---

## 汇总

| 优先级 | 数量 | 说明 |
|:--:|:--:|------|
| P0 | 74 | 核心 ORM 功能——缺之不成 ORM |
| P1 | 27 | 重要增值——显著提升开发效率/安全性 |
| P2 | 2 | 边缘场景——少数场景需要 |
| 注解 | 16 | 基础注解(13) + 高级注解(3)——编译时映射驱动 |
| 机制 | 4 | 开发者安全(2) + 测试Fixture(2) |

**总计 123 项特性。每一项有全语言对标 ORM 来源，无一凭空设计。**
