# 三 ORM 深度优化分析——完整清单与充分论证

> 报告编号：ORM-OPT-DEEP-2026-09-13
> 方法：三栈热路径源码逐行审读(Outbox lease/mark/append、Idempotency 全链、连接生命周期)+ 前序四份报告收敛(aot-analysis/dapper-aot-probe/dapper-aot-experiment/efcore11-investigation)+ 三轮极致性分析迭代
> 论证纪律:每个优化点标注[实证](代码可查)/[推断](逻辑推导未基准化);收益量级未基准化的一律不标"显著";每个"不做"给对抗理由

---

## 零、先回答"是否可以优化性能"

**可以,但分三个层次**:

1. **已经拿走的**:Dapper 栈本轮拦截器化(JIT 省反射 emit + AOT 全链路)、三栈既有的 ExecuteUpdate/AsNoTracking/COPY 批写/const 模板+MaxAutoPrepare/驱动池化——热路径没有"显然错误"的选择。
2. **还能拿的(有代价)**:一项真机会(EFCore Pooling 解锁)+ 一项上游依赖(Dapper ct 入口)+ 两项 major 窗口项(接口异步化吸收 PalORM 同步阻塞)。共性:**都需要 API 变化或上游动作**,不是本地微调。
3. **最大的缺口不是优化点,是测量**:三栈至今**没有 BenchmarkDotNet 基准**——本清单所有"微/小/中"量级均为[推断]。充分论证的下一步是先建基准,让后续每笔优化有数字裁判。

## 一、Dapper 栈

### 已就位(盘点确认)
拦截器 34 调用点全量接管;SQL 模板全 const(Npgsql MaxAutoPrepare=20 自动预备,PostgreSqlMultiHost 强制注入);连接池驱动层管理;EnsureOpen 快速路径(`State != Open` 检查后池化 Open);声明式 TypeHandler;blob DynamicParameters 绕行;AsList() 避免二次拷贝;守卫族 fail-fast(纳秒级,不构成负担)。

### 清单

| # | 优化点 | 现状证据 | 分析 | 收益[推断] | 裁决 |
|---|--------|---------|------|-----------|------|
| D1 | **文档漂移修复**:GetPendingMessagesAsync 注释仍称"AOT 拦截未启用,走经典反射路径" | DapperOutboxStore.cs:110-111 | AOT 已启用,注释误导后续维护 | 无(卫生) | ✅ **立即修** |
| D2 | SQLite/MySQL 时间参数每次 `ToString("O")` 格式化(每次租约 2-3 次) | ToTimeParam → DapperAotInitializer.ToSqliteParameter/ToMySqlParameter | TEXT 存储模型的固有成本;"O"格式化百 ns 级;换 epoch-int 存储需 DDL+全栈迁移+跨栈契约变更 | 微 | ❌ 不做——代价远超收益 |
| D3 | ToTimeParam 方言 switch 每调用 | enum jump table | 纳秒级,委托缓存反而引入分配 | ≈0 | ❌ 不做 |
| D4 | AddMessage DynamicParameters 7 参数构造分配 | DapperOutboxStore.AddMessage | 一次小对象分配/调用;对象池引入复杂度 | 微 | ❌ 不做 |
| D5 | ct 恢复(SQL 执行层取消) | 直接重载无 ct;Command<T> 管道已支持 ct(1.1.0 源码实证),仅缺调用点语法入口 | 上游 issue → 落地后 34 调用点恢复 ct | 功能性(非性能) | ⏳ **起草上游 issue** |

## 二、PalORM 栈

### 已就位
BulkInsertAsync 方言最优(PG COPY/MySQL BulkCopy/SQLite 多值);源生成物化零反射;PG/SQLite 租约单语句 RETURNING(含 FOR UPDATE SKIP LOCKED,fencing 完整);Session scoped 复用。

### 清单

| # | 优化点 | 现状证据 | 分析 | 收益[推断] | 裁决 |
|---|--------|---------|------|-----------|------|
| P1 | FormattableString 每调用分配:每条 SQL 一个包装对象 + object[] 参数装箱(owner/until/now/batchSize...) | PalOrmOutboxStore.cs:110-115 租约 SQL `$"..."` | PalORM API 设计使然(编译期参数化);格式串本身 interned,分配面是包装对象+参数数组,每次租约 ~6-8 小对象,Gen0 压力 | 微-小(高吞吐可测) | ❌ 上游 API 层面问题,本仓不优化;基准若显示 Gen0 突出再议 |
| P2 | `rows.Select(r => r.ToDomain()).ToList()` LINQ 中间分配 | 租约路径三分支同型 | foreach 循环省一个枚举器+委托分配;可读性等价 | 微 | ❌ YAGNI——profile 显示热点才做 |
| P3 | 同步 DIM `GetAwaiter().GetResult()` 线程池阻塞 | AddMessage/MarkProcessed 同步方法 | 接口只有同步签名,PalORM 无同步 API 被迫阻塞;解药是 IPalOutboxStore 异步化 | 中(高并发线程池压力场景) | ⏳ major 窗口(ADR-020 已排) |
| P4 | MySQL 两步租回(UPDATE+SELECT 双往返) | SupportsOutboxReturning=false 分支 | MySQL 方言限制;候选 id 预取需 IN 参数化(上游不支持,八轮已声明) | 一往返/租约(MySQL only) | ❌ 已声明限制,等上游 IN 参数化 |
| P5 | 上游 `configureResilience` 未接 | 适配层零使用 | 外层 OutboxBatchProcessor 已有业务重试,两层叠加=重复投递风险 | — | ❌ 反风险不做 |

## 三、EF Core 栈

### 已就位
写路径全 ExecuteUpdate(单语句绕 ChangeTracker);读路径全 AsNoTracking(6 处);PG 租约 FromSqlRaw 单语句原子;EventLogPositionReserver CAS 探测已 AsNoTracking。

### 清单

| # | 优化点 | 现状证据 | 分析 | 收益[推断] | 裁决 |
|---|--------|---------|------|-----------|------|
| E1 | **DbContext Pooling 解锁**(ITM-640) | OutboxDomainEventInterceptor._pending/_injectedOutboxIds 实例状态 vs AddDbContextPool options 烘焙 | 状态迁 DbContext 派生类字段(拦截器经 eventData.Context 取用)——池化 ResetState 天然安全,EF 官方 per-context 拦截器模式。代价:消费者 TContext 须继承基类(API 变化,major 窗口) | 中:省每请求 DbContext 构造+模型检查;查询计划缓存跨请求共享(现在每新 context 首查询仍触发计划缓存,App 级共享已缓解一部分) | ⏳ **登记 ITM,排 major 窗口**——本清单唯一真性能机会 |
| E2 | Compiled Models / precompiled queries | 未用 | 只省启动;无冷启动瓶颈证据;上游 experimental | 微(JIT 下) | ❌ 不做 |
| E3 | EF 11 to-one join/CAST 优化 | 本仓 store 零导航(实证) | 收益属消费者项目 | 0(本仓) | —(GA 随版本跟进) |
| E4 | SaveChanges 批处理 | EF 原生自动批 | 已最优 | — | ❌ 无动作 |

## 四、跨栈

| # | 优化点 | 分析 | 裁决 |
|---|--------|------|------|
| X1 | **建立三栈 BenchmarkDotNet 基准** | 全清单量级均为[推断]的根因;基准矩阵:Outbox lease→mark 往返、EventLog append→read、Idempotency TryStart→MarkCompleted,三栈×三方言×{JIT}。有数字后:①验证/证伪本清单所有"微/小"标注 ②为 E1/P3 的 major 窗口裁决供弹药 ③防止未来"优化"回退(基准即回归门禁) | ✅ **建议立项——清单中行动价值最高项** |
| X2 | Ulid.ToString() Crockford 编码分配 | 每消息 ID 参数化一次,固有 | ❌ 不做 |
| X3 | 序列化(MemoryPack)/TimeProvider/IPalLogger guard | 已就位 | ❌ 无空间 |

## 五、裁决汇总(执行面)

| 优先级 | 项 | 性质 |
|:--:|---|---|
| **现在** | D1 文档漂移修复 | 卫生,零风险 |
| **现在建议立项** | X1 三栈基准建立 | 让所有后续优化有数字裁判 |
| **major 窗口** | E1 EFCore Pooling 解锁(登记 ITM) | 唯一真性能机会,API 代价 |
| **major 窗口** | P3 接口异步化(ADR-020 已排,吸收 PalORM 同步阻塞) | 功能+性能双收 |
| **上游跟踪** | D5 Dapper.AOT ct 入口 issue | 功能恢复 |
| **不做** | D2/D3/D4、P1/P2/P4/P5、E2/E4、X2/X3 | 反过度优化,理由如上 |

**底线结论**:三栈当前不存在"改一行就显著变快"的点——热路径的形状选择(单语句原子租约/COPY 批写/拦截器化/ExecuteUpdate)都已在位。剩余空间全部锁在 API 变化窗口(E1/P3)或上游(D5)后面;而**解锁"是否值得"判断的钥匙是 X1 基准**——没有它,任何进一步的"极致"都是猜测驱动。
