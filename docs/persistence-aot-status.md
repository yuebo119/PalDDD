# 三 ORM 持久化栈 NativeAOT 支持真实情况

> 文档定位：三栈 AOT 能力的**权威口径**(对外文档/选型决策引用本文)。
> 依据：aot-analysis-2026-09-13 / dapper-aot-probe-2026-09-13 / dapper-aot-experiment-2026-09-13(抑制声明审计)/ efcore11-investigation-2026-09-13 四份报告的收敛结论,全部为实跑实证,非文档转述。
> 更新:2026-09-13(Dapper.AOT 全量启用合并 bffd2c2 后)。

---

## 一页总表

| | **PalORM 栈** | **Dapper 栈** | **EF Core 栈** |
|---|---|---|---|
| **AOT 等级** | ✅ **库级真 AOT** | ✅ **调用点级 AOT**(封装 API 面实测) | ❌ **实验性基础设施** |
| 机制 | PalORM.SourceGen 编译期生成 Row DTO 物化 + 编译期 SQL,零运行时反射 | Dapper.AOT 1.1.0 拦截器:34 个调用点编译期重定向到生成代码 | precompiled queries + compiled model:发布期静态扫描 LINQ 生成拦截器(**微软自述高度实验**) |
| 生产就绪 | ✅ CI 每次 NativeAOT publish + 运行断言 | ✅ SQLite / PG 18.4 / MySQL 8.4.11 真库 NativeAOT 二进制实跑 13/13 ×3 | ❌ 官方警告"not yet suited for production";发布仍报大量 trim 警告 |
| 反射残留 | 零 | 库内经典路径保留但调用点不可达(46 条上游警告与生成代码正交,见下文边界) | 表达式树编译被拦截器消除,但零警告目标未达成(#33478) |
| 行为差异 | 无 | SQL 执行层 ct 不可中断(连接超时兜底);接口签名与连接层 ct 保留 | —(本仓未启用 AOT) |
| 本仓声明 | `IsAotCompatible=true` | `IsAotCompatible=true` + `[module:DapperAot]` 已启用 | `IsAotCompatible=false`(诚实) |

## PalORM 栈:库级真 AOT

- 源生成器从 `[Column]`/`[Key]`/`[ConcurrencyCheck]` 注解编译期生成全部 Row DTO 映射、SQL、并发谓词——运行时零反射、零 IL emit。
- 这是三栈中唯一**程序集级**干净的实现:不依赖任何"拦截器重定向"技巧,ILC 直接看到全部物化代码。
- 纯度排序:PalORM > Dapper(调用点级)> EFCore(实验)。

## Dapper 栈:调用点级 AOT(2026-09-13 全量启用)

**能做什么**:

- 34 个调用点(`QueryAsync`/`QueryFirstOrDefaultAsync`/`ExecuteAsync`/`Execute`/`QuerySingleAsync`)全量被拦截器接管,SQL const 直引 / 实例属性 / switch 方言分支三种形状均被编译期常量追踪。
- 声明式 `[Dapper.TypeHandler]` 消费经典 `SqlMapper.TypeHandler<T>`(shim),Ulid/Guid/DateTimeOffset 全链路无反射绑定。
- snake_case 列名由生成器 `NormalizedEquals` 规范化匹配,不依赖全局 `MatchNamesWithUnderscores`。
- byte[] blob 参数经 `DynamicParameters` + `DbType.Binary`(绕开上游 List expansion 对 byte[] 的误展开,见实验报告坑 3)。
- JIT 项目**零配置零感知**:拦截器生成的就是普通 C# 代码,JIT 下行为一致且更快(省首调 jitter 与策略缓存);"不需要 AOT 的项目"无需任何选择。

**边界(消费者必读)**:

1. **只在封装 API 面内有效**——六接口(IPalOutboxStore 等)下的 34 个调用点。绕过封装直接使用 Dapper 原生 API(`CommandDefinition`/`dynamic`/运行时 `AddTypeHandler`/未拦截重载)在 NativeAOT 下**不受支持且会运行时炸**(经典反射路径仍在 Dapper.dll 内,炸点 `SqlMapper.CreateParamInfoGenerator` 已实证)。
2. **库级不干净**:Dapper.dll 自带 46 条方法级 trim/AOT 警告(IL2104/IL3053,`--singlewarn` 展开实证),全部落在经典反射路径,包级以 NoWarn 抑制——这是上游程序集属性,不是本仓调用点问题。
3. **ct 收缩**:Dapper 经典直接重载无 ct 参数(CommandDefinition 被 DAP057 拒绝),SQL 执行层不可中断;上游 `Command<T>` 管道已支持 ct 只差调用点入口,未来上游补齐后可恢复。
4. 消费者**无法**运行时/项目级切换"经典 vs AOT"——拦截在包编译期定死;也不需要切换(语义一致)。

## EF Core 栈:实验性基础设施(本仓未启用)

- 机制与 Dapper.AOT 同构(拦截器 + 发布期生成),但多一层编译模型负担,生成物体积大、生成慢。
- 官方限制:动态查询不支持(跨语句/条件拼接 LINQ 无法静态分析)、LINQ 查询表达式语法不支持、provider 需自建支持、捕获状态的值转换器不支持。
- 跳票记录:EF 9 承诺 EF 10 稳定化 → EF 10 未兑现 → EF 11(2026-09 RC)依然 experimental;零警告目标未达成。
- 本仓 `IsAotCompatible=false` 是诚实声明。若上游未来转正(EF 12 预期),需重估:provider 支持 + 本仓查询形状静态性审计。

## 选型指引

- 需要 AOT 部署 + 追求极致纯度/性能 → **PalORM**(库级源生成)。
- 需要 AOT 部署 + 手写 SQL 控制力 → **Dapper**(封装 API 面已实测)。
- 需要 EF 生态(Interceptor/迁移/LINQ/ChangeTracker)→ **EF Core**(JIT 部署;AOT 待上游转正)。
- JIT 部署 → 三栈任意,行为一致,按生态偏好选。
