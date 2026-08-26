# 全量评审报告 v8（AI 质量系统 v2.1 首跑）

> 基线：8e435dd · 日期：2026-08-26 · 类型：全量档（五片并行地毯）
> 系统：.ai v2.1（本轮为 G23/G24/T20/三道复核注入后的首次全量运行）

## 1. 覆盖度声明

- **逐行覆盖**：176 手写 .cs 文件 ~26,400 行（片 A 32/事务域 + 片 B 38/Dapper 栈 + 片 C 46/PalORM+EFCore + 片 D 60/核心+工具链 + 片 E 46/应用层），五片各自声明零跳读；obj/ 生成物按规则仅验语义。
- **主线程跨片不变式**：四项全部成立（FailureReason.Normalize 六调用点、Dapper Tx 零残留、Ambient 四边界对称、Obsolete 零残留）。
- **机械轴**：gate-check 24/24、test-gate 0 失败、doc-consistency 10/10、弱断言 150<基线 173、ArchitectureBoundary+DI 94、Core 240（快照/AOT/分配/性能契约）全绿。
- **方言实测轴**：SKIP——PG TCP 可达但握手超时（192.168.200.120 环境故障）；附带发现探针工具对单方言连接失败无优雅降级（RunPg 崩溃连带 MySQL 未跑，见修复清单 T-3）。

## 2. 发现汇总（主线程终审定稿）

**P0=0 · P1=0 · P2×3（全部主线程亲验）· P3×30**

### P2 定稿（含修复方向）

| # | 位置 | 缺陷 | 修复方向 |
|---|------|------|---------|
| P2-1 | PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs:25 | 触发器示例 `WHEN (NEW.status = 'Pending')` 与状态列 int 化契约冲突——PG 中 `integer = text` 无运算符，用户照抄即建坏触发器、INSERT 失败 | 示例改 `NEW.status = 0`；修复后 dialect-probe PG 路径验证 NOTIFY 往返 |
| P2-2 | PalDDD.PalORM/Stores/PalOrmIdempotencyStore.cs:124 | 过期回收 `AND status <> Completed` 使"过期 Completed"记录 affected=0 → return null → 该幂等 key 永久拒绝；EFCore（ExpiresAt<=now 即回收）与 InMemory（过期 Remove 重建）均可重新执行——三栈分叉，PalORM 独异。三十七轮 P2-5 修复过度收窄（expires_at<=now 已含过期语义，status 守卫防的应是"未过期 Completed"） | 去 status 守卫或改 `AND (status <> Completed OR expires_at <= now)`——以 EFCore 行为为准统一；补双栈对照测试（Completed+过期同 key：PalORM 断言非 null） |
| P2-3 | PalDDD.Analyzers.CodeFixes/PalDDD.Analyzers.CodeFixes.csproj:13 | `SuppressDependenciesWhenPacking=true` 且零依赖声明——独立安装 CodeFixes 包（NuGet 静默允许）时 Roslyn 缺 Analyzers 程序集，5 个 fix provider 全部失效；1.1.0 nupkg 实证 nuspec 零 dependencies | 删 SuppressDependenciesWhenPacking 声明 Analyzers 依赖（最低版本锚定）；重打包 2.0.0 解包验 nuspec + 最小工程只装 CodeFixes 编译验证 |

### P3 摘要（30 项，按族归类）

- **int 化契约同步残留族**（3）：SqlTemplates remarks "字符串状态"失实；DapperBulkCopy IL2062 Justification 与 csproj 矛盾；DapperAmbientTransaction "五个 Store DI 解析"注释对 EventLog/Checkpoint 两 Store 失实（两者无 DI 注册）
- **注释-事实失实族**（4）：LeaseOwnerFactory 注释指向错误管线（Outbox+Saga 非 Outbox+Inbox）；PalDiagnostics "零 GC"过度声明（int tag 装箱）；SagaProcessor 补偿异常路径告警弱于注释声明；EventStreamConcurrencyException (string) 构造 ExpectedVersion=Any 歧义
- **文档缺口族**（3）：Saga 重试整步重放语义未成文（FanOut/ChildSaga 正向动作重执行风险）；PALID002 未提 readonly 前置要求；IdentityGenerator IsNumeric extern alias 防御不一致
- **行为微瑕族**（12）：MarkDead 构造串未过 Normalize（实害低）、EnsureOpenAsync 漏传 ct、DapperEventLog 无 EnsureOpen 风格分叉、MarkCompleted 无 status 守卫（姊妹对齐）、Kafka DisposeAsync 无超时、DomainEventDispatcher 不可达 throw、Rabbit exclusive 队列滞留、畸形 JSON 裸 400、开放/闭合 behavior 互斥限制、OpenZL 标识前向兼容、ConvertForNpgsql naive DateTime 潜伏、FailureReason 代理对截断
- **其他**（8）：dialect-probe 单方言崩溃无降级（工具）、SqliteRowFactory 保留合规、DI csproj NoWarn 备注、双时钟源漂移、EventLogPositionReserver 慢路径无 double-check、Inbox 截断跨栈分叉（各有列契约）、CAS null 手工脏数据、CodeFix 派生类链式不对称

### 零发现流证据（七流逐片附证，摘）

架构流五片全绿（引用矩阵合规）；安全流零 SQL 拼接/零 PII；资源流释放路径完整；并发流 lock 块全读+ITM-105/174 隔离在位；错误流 PD4/PD5 全合规；AOT 三态口径与 csproj 一致；生成语义流快照+键集锁定。

## 3. 本轮重点区核销（2026-08-26 当日变更全部复核）

Analyzers 拆分逐段对照等价（72 测试锁定）✅；CodeFixes 拆分 using 完整 ✅；FailureReason 五处收敛实证（四路径行为正确）✅；[Obsolete] 语法/零消费/快照 ✅；AddPalLogging 双重载无递归 ✅；AddPalDDD 自动补齐幂等 ✅；Dapper Tx 35 处零残留 + UoW 四边界对称 ✅；SqlErrorClassifier 收敛等价（含反射缓存正确性）✅；SQLite CAS null/IS NULL 语义 ✅；MessageVersionKey 提取 ✅。

## 4. 趋势

缺陷密度延续收敛（P1 连续 4 轮为零；P2 稳定在文档/契约同步残留与打包面）；本会话 4 次结构性修改（拆分/收敛/重组）全部通过地毯复核——修复质量门（验证轮协议）有效。
