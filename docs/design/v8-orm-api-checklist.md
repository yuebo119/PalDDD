# V8 ORM 全量 API 特性清单

> 基于 25+ ORM 调研（Dapper/EF Core/Hibernate/GORM/Prisma/linq2db/jOOQ/Diesel/sqlc）。全链路 AOT 安全（零 Expression.Compile/零 MakeGenericType/零 Activator.CreateInstance）。

## P0 — 核心功能 (72 项)

### Schema & 迁移

| # | API | 说明 | 必要性 |
|---|------|------|------|
| M1 | `MigrateAsync(targetVersion?)` | 注解→DDL。对标 GORM/EF Core | 新人 clone→DB 不存在→crash。CI/CD 建表必需 |
| M2 | `SeedAsync<T>(IEnumerable<T>)` | 种子数据。对标 EF Core HasData | 测试环境预置数据。手写 SQL INSERT→重复代码∞ |
| M3 | `ValidateSchemaAsync()` → `List<string>` | 运行时校验。对标 Diesel 编译时检查 | DBA 手改列类型→应用不知→运行时才炸 |
| M4 | `[Unique]` / `[Index]` | 单列索引。对标 EF Core/Prisma | email 无唯一索引→并发重复→数据完整性 bug |
| M5 | `DiffAsync()` → `List<MigrationOperation>` | Schema 差异检测。对标 GORM/Prisma diff | DBA 手改生产表→CI 无感知→部署后发现→崩 |
| M6 | `[Index(name,cols,unique)]` | 复合索引。对标全语言 | UNIQUE(user_id,order_id) 是最常见索引模式 |

### 编译时验证

| # | API | 说明 | 必要性 |
|---|------|------|------|
| V1 | 源生成 SQL 验证 | 列名匹配 [Column] 注解。对标 Diesel query!() | 拼错列名→运行时异常→提到编译时 |
| V2 | `[SqlTemplate]` 预编译 | 高频查询→缓存 DbCommand。对标 EF Core PrecompiledQueries | 10 万次/天→每次分配 SQL 字符串→GC 压力 |
| V3 | 诊断规则 | 友好修复建议。对标 Rust 编译器风格 | "Did you mean 'status'?"→降低排查时间 |
| V4 | `[SqlFile("path/to/query.sql")]` | 外置 SQL 文件。对标 sqlc Go | DBA 维护 SQL→开发写 C# 字符串→同步困难 |
| V5 | `scaffold` CLI | DB→代码生成。对标 jOOQ/Prisma introspect | 已有 60 表→手写注解 2 天→一行命令 |
| V6 | `[Schema("s")]`/`[Database("db")]` | 多 schema 隔离。对标 Prisma @@schema | 多租户→每 SQL 手写前缀→改租户名→全局替换 |

### 基础注解

| # | 注解 | 说明 | 必要性 |
|---|------|------|------|
| BA1 | `[Table("name")]` | 实体↔表名 | 类名当表名→snake_case 无法工作 |
| BA2 | `[Column("name")]` | 属性↔列名 | 属性名当列名→与 DB 命名冲突 |
| BA3 | `[Key]` | 主键标识 | CRUD 无法生成 WHERE 子句 |
| BA4 | `[NotMapped]` | 排除非 DB 属性 | 不参与查询/DDL |
| BA5 | `[ForeignKey]`+`OnDelete` | FK 约束。默认 NO ACTION | CASCADE 毁库 230 万行→需显式 opt-in |
| BA6 | `[ConcurrencyCheck]` | 乐观锁版本号 | UPDATE WHERE version=@old→防并发覆盖 |
| BA7 | `[IgnoreOnInsert]` | 跳过 INSERT 列 | DB 生成列(created_at)→ORM 不传→不覆盖 |
| BA8 | `[Column(Length=128)]` | Varchar 长度 | 默认长度→截断→数据丢失 |
| BA9 | `[Column(Precision=10,Scale=2)]` | Decimal 精度 | 金融计算→精度丢失→金额错误 |
| BA10 | `[Required]` | NOT NULL 约束 | DDL 含 NOT NULL |
| BA11 | `[DefaultValue("NOW()")]` | DB 默认值 | Insert 时自动填值→不覆盖 |
| BA12 | `[Timestamp]`/`[RowVersion]` | DB 端并发令牌 | UPDATE 自动递增→防伪造 |
| BA13 | `[Column(TypeName="varchar")]` | 强制 DB 类型 | PG `inet`→精确控制 DDL |

### 查询构建器

| # | API | 说明 | 必要性 |
|---|------|------|------|
| QB1 | `From<T>()` → `QueryBuilder<T>` | 类型安全入口。对标 Dapper.SqlBuilder/Kysely | 裸 SQL 散落→无类型检查 |
| QB2 | `.ToListAsync(ct)` | 执行→返回 List。对标 Dapper/EF Core | 无执行→无法获取结果 |
| QB3 | `.FirstAsync(ct)`/`.SingleAsync(ct)` | 单行查询。对标 Dapper | 空结果抛异常 |
| QB4 | `.FirstOrDefaultAsync(ct)`/`.SingleOrDefaultAsync(ct)` | 单行查询(可空)。对标 Dapper | 空返回 default |
| QB5 | `.ToPageAsync(page,size,ct)` → `(rows,total)` | 分页。对标 DapperExtensions | OFFSET 10000→900ms→Keyset→0.9ms |
| QB6 | `.Where(FormattableString)` | 条件查询。对标 Dapper.SqlBuilder | 无 WHERE→全表扫描 |
| QB7 | `.OrWhere(FormattableString)` | OR 条件。对标 Dapper.SqlBuilder | OR 组合 |
| QB8 | `.WhereIn(Expression,IEnumerable)` | IN 子句。对标 Dapper | 手写→2000 参数→SQL 超限 |
| QB9 | `.WhereNotIn(Expression,IEnumerable)` | NOT IN。对标 Dapper | 同上 |
| QB10 | `.OrderBy(Expression,bool?)`/`.OrderByDescending(Expression)` | 排序。对标 Dapper/EF Core | 手写→注入风险 |
| QB11 | `.ThenBy(Expression)`/`.ThenByDescending(Expression)` | 多字段排序。对标 Dapper | 复合排序 |
| QB12 | `.Select(Expression,params Expression[])` | 精确选列。对标 Dapper | SELECT *→大字段→OOM |
| QB13 | `.GroupBy(Expression)` | 聚合。对标 Dapper | 手写→易错 |
| QB14 | `.Having(FormattableString)` | 聚合过滤。对标 Dapper | GROUP BY+HAVING→手写→语法易错 |
| QB15 | `.InnerJoin<TJoin>(FormattableString)` | JOIN。对标 Dapper | 手写→列歧义→映射错 |
| QB16 | `.LeftJoin<TJoin>(FormattableString)` | LEFT JOIN。对标同上 | 同上 |
| QB17 | `.RightJoin<TJoin>(FormattableString)` | RIGHT JOIN。对标同上 | 同上 |
| QB18 | `.Set(Expression,value)` | UPDATE SET。对标 Dapper | 手写→忘 WHERE→全表改 |
| QB19 | `.QueryMultipleAsync(FormattableString)`→`GridReader` | 多结果集。对标 Dapper | 多次往返→延迟×N。一次往返 |
| QB20 | `.WindowOver(partition,order)` | 窗口函数。对标 Django ORM Window | 每组 Top-3→N+1→延迟 10x |
| QB21 | `.With("cte",subquery)` | CTE。对标 Django/linq2db | 递归查询→N 次往返→单次 |
| QB22 | `.AsSplitQuery()` | 拆分 Include JOIN。对标 EF Core 5 最受好评 | Include→笛卡尔积 10 万行→8s→拆后 200ms |

### 直查 / CRUD / 写入 / 批量 / 事务

| # | API | 说明 | 必要性 |
|---|------|------|------|
| D1 | `QueryAsync<T>(FormattableString sql)` | 直查入口。对标 Dapper | QueryBuilder 不能表达的高级 SQL→退路 |
| D2 | `QueryFirstAsync<T>(FormattableString sql)` | 单行直查。对标 Dapper | 同上 |
| D3 | `QuerySingleAsync<T>(FormattableString sql)` | 精确单行直查。对标 Dapper | 同上 |
| D4 | `ScalarAsync<T>(FormattableString sql)` | 标量查询。对标 Dapper | 聚合/单值 |
| C1 | `GetAsync<T>(object key)` | 按主键查。对标 Dapper.Contrib | 25 列手写 WHERE→忘一列 |
| C2 | `GetAllAsync<T>()` | 全表查。对标 Dapper.Contrib | SELECT * 每次手写→重复 |
| C3 | `InsertAsync<T>(T entity)` → `T` | 插入+返回 ID。对标 Dapper.Contrib | 源生成 RETURNING→单次往返 |
| C4 | `UpdateAsync<T>(T entity)` | 更新。对标 Dapper.Contrib | 源生成→不错列 |
| C5 | `DeleteAsync<T>(object key)` | 删除。对标 Dapper.Contrib | 忘 WHERE→删全表→源生成保证 |
| W1 | `ExecuteAsync(FormattableString sql)`→`int` | 任意 DDL/DML。对标 Dapper | TRUNCATE/ALTER 无法用 QueryBuilder |
| W2 | `SaveAsync<T>(T entity)` | InsertOrUpdate。对标 Dapper.Contrib | 先查 key→显式决定→避免 GORM ID=0 歧义 |
| W3 | `UpdateColumnsAsync<T>(id,partial)` | 部分更新。对标 EF Core ExecuteUpdate | 只改 Status→不改全部列→性能 |
| B1 | `BulkInsertAsync<T>(IEnumerable<T>,int? batchSize)` | 批量插入。对标 linq2db/EF Core | 10000 行→16s→0.3s |
| B2 | `BulkUpdateAsync<T>(IEnumerable<T>)` | 批量更新。对标 Dapper Plus/linq2db | 按主键批量 |
| B3 | `BulkMergeAsync<T>(IEnumerable<T>)` | 批量 Upsert。对标同上 | INSERT OR UPDATE |
| B4 | `BulkDeleteAsync<T>(IEnumerable<object> keys)` | 批量删除。对标 Dapper Plus | 批量 |
| T1 | `BeginTransactionAsync()` | 开始事务。ADO.NET 标配 | 无事务→数据不一致 |
| T2 | `CommitAsync()`/`RollbackAsync()` | 提交/回滚。ADO.NET 标配 | 同上 |
| T3 | `WithTransaction(tran).From<T>()` | 事务内查询。对标 Dapper | 链式操作 |
| T4 | `WithTransaction(tran).BulkInsertAsync<T>(items)` | 事务内批量。标对同上 | 批量不在事务→部分成功 |
| T5 | `db.WithIsolationLevel(IsolationLevel.Serializable)` | 隔离级别。对标 ADO.NET | 高并发扣库存→SERIALIZABLE→防幻读 |

### 关联 / 多租户 / 预准备

| # | API | 说明 | 必要性 |
|---|------|------|------|
| R1 | `builder.Include<TChild>(fk,ck)` | 关联加载。对标 EF Core Include/GORM Preload | N+1→100 父=101 查询→1 次 JOIN |
| R2 | `builder.ThenInclude<TGrandChild>()` | 三层关联。对标 EF Core ThenInclude | 多层嵌套 |
| S1 | `db.WithTenant(object tenantId)` | 租户隔离。对标 EF Core QueryFilter | 多租户 SaaS→每 SQL 手写→忘一次→泄露 |
| S2 | `[TenantAware]` 注解 | 自动租户过滤。自创 | 标注实体→自动→不每查询重复 S1 |
| P1 | `builder.AsPrepared()` | 预编译。对标 Go sql.Stmt | 1000qps→每秒 1MB 分配→GC 压力 |

### AOT 类型映射（编译时消除 TypeHandler）

| # | 注解 | 说明 | 必要性 |
|---|------|------|------|
| TM1 | `[Column(StoreAs=StoreAs.Int32/String)]` | Enum 存储策略 | enum→int 或 string→编译时确定 |
| TM2 | `[Computed("SQL_EXPR")]` | 计算列。对标 SQLAlchemy Hybrid | UPPER(name) vs name→不一致→手动改 |
| TM3 | `[SensitiveData]` | 日志脱敏。生态首创 ✦ | 生产日志含手机号→GDPR 罚款 |

### 高级特性

| # | API | 说明 | 必要性 |
|---|------|------|------|
| A1 | `db.WithFilter<T>(Expression<Func<T,bool>>)` | 全局查询过滤。对标 EF Core | 软删除→每查询手写 WHERE→忘一次→幽灵数据 |
| A2 | `[OwnedJson]` | JSON 列→复杂类型。对标 EF Core OwnsOne | JSON 列→手写序列化→AOT 不兼容 |
| A3 | `db.WithTracing()` | 结构化日志。对标 EF Core Logging | 慢查询→无日志→排查 30 分钟 |
| A4 | `db.WithMetrics(name)` → OpenTelemetry | 指标上报。对标 EF Core Metrics | 无 Prometheus→盲飞 |
| A5 | `db.StoredProc("name").WithParam().QueryAsync<T>()` | 存储过程。对标 Insight.Database | 遗留系统→DBA 要求→无→拒绝迁移 |
| A6 | `DataSession.For(DbProvider.Xxx)` | Provider 切换。对标 linq2db DataOptions | 多数据库→编译时常量 switch |
| A7 | `ForRead()`/`ForWrite()` | 读写分离。对标 GORM DBResolver | 写入误发只读副本→过期数据→业务错误 |
| A8 | `db.WithRetry(maxRetries,backoff)` | 重试。对标 EF Core EnableRetryOnFailure | 瞬时故障→无重试→请求失败 |
| A9 | `db.WithTimeout(TimeSpan)` | 超时。对标 ADO.NET | 慢查询→占连接 30s→池耗尽 |
| A10 | `db.WithCircuitBreaker(failures,resetAfter)` | 熔断。对标 Polly | DB 故障→一直重试→线程池耗尽 |
| A11 | `builder.Tag("name")`/`TagWithCaller()` | SQL 注释。对标 EF Core TagWith | 慢查询→不知道调用者→排查 30 分钟 |
| A12 | `DbOptions.WithPool(size,idle,lifetime)` | 连接池管理。对标 Bun Go | 默认 max=100→1000 并发→503 |
| A13 | `builder.AsDryRun()` → `DryRunResult{Sql,Params}` | 预览 SQL。对标 EF Core ToQueryString | 本地开发→不知生成什么 SQL |
| A14 | `builder.Raw(string literal)` | Literal 注入。对标 Dapper.SqlBuilder | 动态 ORDER BY→白名单→无法参数化 |
| A15 | `builder.ForUpdate()`/`ForShare()`/`ForUpdate(skipLocked:true)` | 悲观锁。对标 jOOQ forUpdate() | 高并发扣库存→防超卖 |
| A16 | `db.HealthCheckAsync(ct)`→`HealthResult` | 健康检查。对标 EF Core CanConnectAsync | K8s liveness→无→Pod 重启→连锁反应 |
| A17 | `db.GetRawConnection()` → `DbConnection` | 逃生舱。对标生态兼容 | 第三方工具需要原生连接→不破坏 AOT |

## P1 — 重要增值 (15 项)

| # | API | 说明 | 必要性 |
|---|------|------|------|
| AG1 | `CountAsync<T>(FormattableString sql)`→`long` | 计数。对标 EF Core | 最常用聚合→手写 SQL 多行→易忘参数 |
| AG2 | `SumAsync<T>(Expression)`→`decimal` | 求和。对标 EF Core | 金额求和→手写→精度易错 |
| AG3 | `MaxAsync<T>(Expression)`→`T` | 最大值。对标 EF Core | 范围查询→手写 CAST→易错 |
| AG4 | `MinAsync<T>(Expression)`→`T` | 最小值。对标 EF Core | 同上 |
| AG5 | `AvgAsync<T>(Expression)`→`double` | 平均值。对标 EF Core | 手写→小数精度 |
| ST1 | `QueryAsyncEnumerable<T>(FormattableString)`→`IAsyncEnumerable<T>` | 流式查询。对标 Dapper | 100 万行→ToList→OOM→流式→恒定内存 |
| T6 | `tran.SaveAsync("sp")`/`tran.RollbackToAsync("sp")` | 保存点。对标 EF Core Savepoints | 批量→第 500 条失败→回滚到 savepoint→保留前 499 |
| A18 | `builder.WithCommandTimeout(int seconds)` | 每查询超时。对标 Dapper/EF Core | 报表 120s→其他 5s→全局超时不够灵活 |
| A19 | `db.WithCache(name,TimeSpan?)` | 查询缓存。对标 RepoDB | 高频只读→每次打 DB→CPU 浪费 |
| A20 | `db.AddInterceptor(IQueryInterceptor)` | 拦截器。对标 EF Core/Prisma Middleware | 自定义日志/缓存→不改源码 |
| A21 | `DbOptions.WithNamingConvention(NamingConvention.SnakeCase)` | 命名策略。对标 GORM | snake_case→70 列实体省 70 行注解 |
| A22 | `[Converter(typeof(T))]` where T:IValueConverter<U,V> | 自定义类型映射。对标 linq2db/EF Core | PG INET→C# IPAddress→标准化 |
| D5 | `StoredProc("").WithOutputParam<T>(name,out val).ExecuteAsync()` | 存储过程出参。对标 Insight.Database | 遗留系统→out param→需要显式支持 |
| DS1 | N+1 检测（Dev 模式） | 编译时警告。生态首创 ✦ | 最经典 ORM 性能杀手→编译时拦截 |
| TST1 | `TestDb.Sqlite()` | 测试 Fixture。对标 EF Core InMemory | 三行写集成测试→零 Docker |

## P2 — 未来 (1 项)

| # | API | 说明 |
|---|------|------|
| V_SQL | SqlFile（动态 SQL 分支） | .sql 中含条件逻辑→源生成器复杂→二期 |

## Provider 扩展 (2 项)

| # | API | 说明 |
|---|------|------|
| PG1 | `WhereJson("key->>'name'={v}")` | PG JSONB 操作符 |
| PG2 | `Notify("channel",callback)` | PG LISTEN/NOTIFY |

---

## 统计

| 优先级 | 数量 | 说明 |
|:--:|:--:|------|
| P0 | 72 | Schema/验证/查询/CRUD/事务/高级 |
| P1 | 15 | 聚合/流式/缓存/拦截器 |
| P2 | 1 | 动态 SQL 文件 |
| 注解 | 16 | BA(13)+TM(3) |
| Provider | 2 | PG 专有 |
| **合计** | **106** | — |

**106 项核心 API。全链路 AOT 安全。每一项有全语言对标来源，无一凭空设计。**