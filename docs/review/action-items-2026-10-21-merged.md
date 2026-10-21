# 2026-10-21 双评审合并优化清单（实施记录）

> 来源 A：`docs/review/review-2026-10-21-architecture.md`（架构/DDD/Clean/C# 特性，编号 A-F1…A-F16）
> 来源 B：只读源码评审（98 条通过校验的发现，归并为 R-F1…R-F24；原始表格在评审临时目录）
> 合并原则：**正确性优先于结构，结构优先于注释**；与 ADR-020（Dapper 栈退役轨道）、
> ADR-035、`.ai/perf-ledger.md`、5% 性能纪律冲突的项不立项或只做声明修正。
> 本文只记录「做了什么、为什么、没做什么、如何验证」，不复述评审原文。

---

## 一、优先级与波次

| 波次 | 主题 | 判定依据 |
|---|---|---|
| W1 | 静默丢数据 / 静默跳过事件的装配与路径缺陷 | 不修则生产丢事件、丢 Saga、丢投影段 |
| W2 | 跨栈契约分叉、资源泄漏、输入校验、公共 API 收敛 | 同一库不同栈读出不同结果；错误装配延迟到运行期 |
| W3 | 注释与实现不一致（含 4 条误导 AOT/性能评审的声明、计数类） | 不影响行为，但会误导后续评审与调用方 |
| W4 | 架构建议：域事件桥、零反射表述、支持面声明、池化文档 | 降低新用户踩坑概率；不改现有语义 |

---

## 二、已实施

### W1 正确性

| 项 | 修复 | 锁定方式 |
|---|---|---|
| R-F1 | `EventLogReplaySource<TMessage>` 补 `IResumableEventReplaySource<TMessage>`，ADR-028 增量回放对唯一随包回放源生效 | 既有 `ProjectionResumeTests` 的续读路径（生产类声明由编译期接口约束） |
| R-F6 | EF SQLite 到期租约 `NextCheckAt=NULL` 视为到期，与其余方言一致 | 既有四栈租约测试口径 |
| R-F5 | EF Retention 改传 EF 模型表名（PascalCase）并按方言加引号；`RetentionSql` 保留 snake_case 双参重载（二进制兼容） | `RetentionProcessorTests`（SQLite 大小写不敏感 + 新增三参重载路径） |
| R-F7 | 投影批量组首锚点**在处理前**取得：崩溃/取消/组首标记失败时续读最小位置为组首；组末仅作诊断 | `ProjectionResumeTests.ReplayAsync_BatchFailure_ResumesFromGroupStartNotAfterGroup` |
| R-F8 | Inbox 回落路径逐条 try/catch，失败标 Failed 后继续，最后聚合抛出 | 现有 Inbox 失败路径测试 + 代码对齐批量路径 |
| R-F3 | Dapper 幂等：0 字节载荷按 Completed 回放；`MarkFailed`/`MarkCompleted` 时间参数统一 `ToTimeParam` | 现有幂等双栈测试口径 |
| R-F4 | Dapper `saga_data` 空串与 PalORM 的 `IsNullOrEmpty` 对齐 | 现有 Saga 物化测试 |
| R-F9/R-F11 | InMemory 批标记：null/租约校验前移；部分接受零回写 | `OutboxBatchMarkTests` 契约组 |
| R-F10 | Dapper/EF 批标记 null 元素校验前移；`SagaKey.Make` 补 null；PalORM `GetAsync` 补空白守卫 | 编译期 + 现有入口测试 |
| R-F12 | EF SQLite 租约等值回读补 `Take(batchSize)` | 与 PG/MySQL 回读同构 |
| R-F2 | `EventStreamJsonLines` 导入/导出共用 `MaxLineBytes` 字节上限；旧名保留 `[Obsolete]` 别名 | `Projections.EventLog.Tests` 既有往返用例 |
| A-F1/A-F2 | EF Outbox：`AddPalOutboxUnitOfWork<TContext>` 约束收紧为 `DbContext, IPalOutboxStore`；解析期校验同实例 + 拦截器已挂接；教程改为同上下文 `UsePalOutboxInterceptor` | 新增 `OutboxWiringEndToEndTests`（真实 `SqliteOutboxDbContext` + 真实 outbox 表 + 两条错误装配） |

### W2 契约、资源与 API

| 项 | 修复 |
|---|---|
| R-F13 | PG 数据源 4+2 个入口改工厂注册，容器接管释放；`DbDataSource` 委托同一注册 |
| R-F14 | RabbitMQ 声明段失败释放 consumer channel；清理顺序改为「删队列 → 释放 channel」 |
| R-F21 | RabbitMQ 取消回调改观察包装；`DetachedStreamOptions.Validate` 加 `Task.Delay` 上界；宽限计时任务 catch-all 记录 |
| R-F16/R-F23 | FTS DDL 单事务 + 单条多语句命令；复用 PalORM 环境事务 |
| R-F17 | `SmartEnum.All` 与 `AllShards` 返回 `Array.AsReadOnly` 包装 |
| R-F24 | `DapperConfiguration.Create` / `DapperSqlDialect.For` / `DapperRetentionStore.Map` 统一 `Enum.IsDefined`；断线续跑 400 响应收敛到 `CreateInvalidBody` |
| R-F22 | `PendingEvents` 降 `internal` + `InternalsVisibleTo`（公共 API 面收窄） |
| R-F1 附属 | 删除 `VersionProbeRow`（0 引用）与 Dapper `EventLogRow`/`ToRecordedEvent`（0 引用） |
| R-F15 局部 | `IsWithinGenericContainingType` 三副本收敛为 `GeneratorSharedHelpers`；`CompressionLevel` 映射收敛为 `CompressionLevelMap`；`RetentionSql` 去掉静默兜底；生成器重复 hint 字典加内容比较器 |
| R-F（PalORM） | PG 单事件唯一冲突重查失败时保留原始冲突异常（不再被 25P02 替换） |

### W3 注释纠偏

按 R-F18 / R-F19 / R-F20 清单逐条改写：Dapper 四条会误导 AOT/性能评审的声明（真流式、无反射物化、UPDATE 先行、MySQL 两步）；Obsolete/软删除/读写路由示例等会让调用方写出必错代码的文案；`VersionProbeRow`、`PalDiagnostics`、`PalOrmSqlite` 等计数；`Saga`/`SagaManager`/`ChildSagaStep` 的 null-manager 语义。计数类只改注释，不建第二台账。

### W4 架构建议

| 项 | 处理 |
|---|---|
| A-F4 | 新增 `DomainEventOutboxBridge`（序列化 + 追踪字段 + 批量写 + 成功后清事件），EF 拦截器改为调用同一构造；Dapper/PalORM 两行接法写入 `docs/features.md` |
| A-F3 | 删除「禁止 `AddDbContextPool`」的过期禁令，按 CWT 状态模型改写注册文档 |
| A-F5 | README 与 `docs/conventions.md` 改为「默认零反射，反射路径必须标注」，列出两处显式豁免 |
| A-F9 | README 首屏声明仅支持 .NET 11（单目标） |
| A-F12 | `docs/features.md` 增加「方言不对称」声明（PG 单方言特性不等于三方言对称） |
| A-F16 | 保留 `AddPalFullStack` 现名，`docs/usage.md`/`docs/features.md` 已声明其等价核心栈、更名留 v5.0 |

---

## 三、明确不做 / 延后（含复活条件）

| 项 | 结论 | 理由 | 复活条件 |
|---|---|---|---|
| A-F6 四套 AsyncLocal 环境态收敛 | 不做 | 四个作用域（DomainEvent/Outbox/Saga/Observer）flush/restore 时机不同；合并改变热路径语义，收益未量测 | 出现跨作用域时钟不一致的真实缺陷，或性能轮给出量测 |
| A-F7 四栈能力契约矩阵 | 部分（本轮只加装配与投影锁定） | 完整矩阵需要真库车道与自定义 store 夹具，属独立工程 | 下一次持久化语义变更前立项 |
| A-F10 结构化日志 | 不做 | 需要新增门面契约 + 适配层实现 + 全部后台处理器迁移，属特性而非缺陷 | 有按字段检索日志的明确运维需求 |
| A-F11 Hosting 拆依赖 | 不做 | 包边界拆分是 v5.0 级破坏性变更 | v5.0 发布规划 |
| A-F13 注释轮次编号治理 | 只做局部 | 全仓规模替换无法机械判定「哪个编号有外部索引」；本轮只删失实/漂移声明 | 先建立编号到 ADR 的映射表 |
| A-F14 域事件链表基准 | 不做 | 本仓 5% 纪律：未见端到端收益证据 | 聚合写路径成为可测瓶颈 |
| A-F15 大文件拆分 | 不做 | 公共 API 快照零变化前提下的大规模搬移，风险高于收益 | 单文件成为变更冲突热点 |
| R-F15 方言策略表 / 8 文件收敛 | 不做结构迁移 | 与 ADR-020 Dapper 退役轨道冲突；只做「去静默兜底 + 声明」 | ADR-020 撤销或新方言需求出现 |
| R-F（MySqlMultiHost 内联校验） | 不做 | 4 处循环形态稳定，抽 helper 的收益低于回归风险 | 第 5 个入口出现 |
| R-F（唯一约束识别 3 副本） | 不做 | 跨三个适配程序集，需共享源码链接；当前三份测试各自锁定 | 出现一次漏改导致语义反转 |

---

## 四、验证（本轮实测）

- 编译：`dotnet build --nologo`，0 警告 0 错误（TreatWarningsAsErrors 开启）。
- 测试：16 个测试项目共 1806 个用例，**1728 通过、78 跳过、0 失败**。
  跳过集中在 PalORM（54，Testcontainers）与 Integration/Messaging.Integration 的方言探针（14/10）。
- 公共 API 快照：`+8/−5`（新增 `DomainEventOutboxBridge`、`MaxLineBytes`、`EfCoreRetentionStore`
  两个构造器；删除 `VersionProbeRow` 与 `PendingEvents`；`AddPalOutboxUnitOfWork` 约束收紧
  不在快照文本中，但属源破坏，见风险 1）。
- 门禁：`encoding-gate`、`count-audit`、`doc-consistency`、`verify-conventions --quick`、
  `license-policy`、`xml-guard` 全部通过。
- 真库项：F5 的物理名解析由 EF 模型驱动，SQLite 真库用例覆盖表名与列名；PG/MySQL 的
  运行期行为沿用既有 dialect-probe 车道（本机无 Docker，按仓库口径跳过并在测试计数中披露）。

## 五、未决风险

1. `AddPalOutboxUnitOfWork` 的约束收紧是**源破坏**：未实现 `IPalOutboxStore` 的既有调用方
   需要在升级时改为「业务上下文继承方言 Outbox 基类」。这是有意为之（错误装配此前静默丢事件）。
2. 投影批量检查点每组多一次 TryStart + MarkCompleted（100 条一组时检查点调用约 2 倍，
   仍比逐事件低一个数量级以上）；`--m2-probe` 阈值已按新形态调整。
3. `EfCoreRetentionStore` 的表名与列名改为按 EF 模型解析（默认 PascalCase）。直接用
   `DbConnection` 且数据库由非 EF 工具建表（snake_case 列名）的调用方，五参构造器只覆盖
   表名、列名仍按 EF 默认——这类场景应改为构造一个映射该库的 `DbContext` 再走
   `EfCoreRetentionStore(DbContext, dialect)`。
4. 注释纠偏不改变行为，但无法机械保证「所有失实声明」已清零；本轮覆盖评审列出的条目。
