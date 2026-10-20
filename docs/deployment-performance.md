# 部署性能旋钮指南

> 定位：面向**应用部署决策**的性能旋钮清单——库代码不替应用做这些决策（GC 模式与服务端
> 配置属宿主主权），本文给出实测数字、启用条件与权衡，供选型引用。
> 数字来源：[docs/performance.md](performance.md)「旋钮实验三组」（2026-10-09/10 同晚容器
> A/B 实测，含对照组）；引用绝对数字时须带环境标签（跨环境须复测）。
> 本文档只收**部署面旋钮**；库内优化与基准口径见 performance.md 全文。

## 一、GC 模式选型（Server GC vs Workstation GC）

| 场景 | Workstation GC（默认） | Server GC（`DOTNET_gcServer=1` / `<ServerGarbageCollection>true</ServerGarbageCollection>`） |
|---|---|---|
| 消息链路类重分配负载（分发→序列化→Outbox 落入） | 8 线程聚合 1.79M ops/s（4 线程即平台期） | **8 线程 4.64M ops/s（+159%），平台期消失** |
| 分配轻负载（纯分发、零分配热路径） | **8 线程 28.3M ops/s** | 8 线程 19.9M ops/s（-30%） |

选型建议：以 Outbox/EventLog 消息管道为主的服务选 Server GC（逐 op 分配是常态，GC 并发
是聚合瓶颈）；分配极低的纯计算服务维持 Workstation。混合负载以自身压测为准（`--qps`
harness 形态可参考，见 performance.md）。

## 二、PostgreSQL

### 2.1 连接串 `Max Auto Prepare`（**strict A/B 定论：不推荐默认启用**）

- **strict A/B 实测（2026-10-10 下午，同日同机双容器轮，40 迭代/20 预热/3 launch 稳态）**：

| 指标（PalORM 栈） | 无旋钮 strict | 旋钮 strict | 差 |
|---|---:|---:|---|
| GetPending_Batch100 | 685μs（E4.5%） | 738μs（E2.2%） | 无收益（持平偏差带内） |
| Lease_Batch100 | 98.0ms | 177.1ms | **旋钮慢 1.8×** |
| EventLog_AppendSingle | 1,022μs | 1,694μs | **慢 1.7×** |
| Idempotency 全周期 | 1,425μs | 2,925μs | **慢 2.1×** |
| Dapper AddSingle | 630μs | 1,255μs | **慢 2.0×** |
| Dapper GetPending | 746μs | 491μs | 快 34%（唯一改善项） |

> ⚠️ 勘正脚注（2026-10-11 T0.1 判别，见 performance.md「T0.1 归因结论」）：Lease/EventLog/Idempotency
> 三行**无旋钮基线绝对值不可复现**（本尊提交重跑为 177ms/2,273μs/3,133μs，n=1 快偏读数）——本表
> 的有效引用面是**比值列**（同轮对内比较，旋钮使写路径变慢的方向性结论成立）；GetPending/Dapper
> 两行基线可复现（685/746/630μs 均与后续轮一致）。

- **结论修正史**：首轮 medium 档测得「PalORM 14.7→6.7ms（-55%）」→ 专项治理轮降级为「加速
  到达稳态」→ 本轮 strict A/B **定论：稳态下无读收益且写路径显著变慢**。历史「收益」全部是
  medium 15 迭代欠采样期的语句计划冷启动效应（Npgsql auto-prepare 阈值 5 次执行，把冷计划
  段压缩进了采样窗）。**默认不启用**；读极重写极轻的负载可试，但须以自身 strict A/B 验证
  （唯一改善形态：Dapper 同形 SELECT 重复查询 +34%）。

### 2.2 `synchronous_commit=off`（写延迟换持久性窗口，谨慎）

- **实测**：**本地容器环境零影响**（AddSingle 606→613μs 等全部吞吐项同值）——本地容器
  fsync 近零成本，该旋钮的收益**不可在本地环境证实/证伪**；真实磁盘部署下理论收益为每次
  提交省一次 fsync 等待。
- **权衡**：崩溃窗口内（~wal_writer_delay，默认 200ms 级）已提交事务可能丢失——事务性
  Outbox 场景**默认不启用**；仅观测/可重放类负载评估后启用。

## 三、MySQL

### 3.1 `innodb_flush_log_at_trx_commit=2`（写路径 -24~-31%，带对照）

- **实测**：写路径 AddSingle 1,527→1,148μs（-25%）、Lease 170.4→118.0ms（-31%）、
  Idempotency 3,513→2,498μs（-29%）；读路径 GetPending 对照不变（归因方向正确）。
- **权衡**：`trx_commit=2` 下 redo 刷盘改为每秒一次——**mysqld 崩溃不丢**（OS 缓冲仍在），
  **OS/主机崩溃丢最近 ~1s 已提交事务**。事务性 Outbox 场景须评估消息丢失窗口是否可接受
  （配broker 重投/幂等消费可部分兜底）；默认值 1（每次提交 fsync）是最安全形态。
- 同族：`sync_binlog=2`（若开 binlog）与主从复制语义联动，评估时一并看。

### 3.2 大批量写入的 `AllowLoadLocalInfile`（部署要求，>16 行批量路径）

- Dapper 栈 `AddMessagesAsync` 大批量（>16 行）走 MySqlBulkCopy（LOAD DATA LOCAL INFILE），
  要求连接串 `AllowLoadLocalInfile=True` + 服务端 `local_infile=1`（该选项扩大服务端可访问
  文件面，须由部署方显式决策——库不自动开启，缺失时 fail-fast 报错指引）。
- ≤16 行的小批量自 2026-10-10 起走参数化多行 VALUES（单往返，无此要求）。
- 启用条件：使用 Dapper 栈且有大批量写路径（如批量种子/回填）时开启；纯小批量负载不需要。

### 3.3 租约并发与版本矩阵（ADR-025）

- MySQL **≥8.0.18 且非 MariaDB**：Outbox/Saga 租约自动走 `FOR UPDATE SKIP LOCKED`——
  多实例并发零死锁、互斥成立、真并行（实测串行→8 worker 加速 1.2~2.1×）。
- MySQL **<8.0.18 / MariaDB / 版本探测失败**：回退无锁模板——并发后果是 InnoDB 死锁异常
  + last-writer-wins 双租窗口（ADR-024），消费方需幂等消费 + 死锁重试（处理器用户侧
  tick 级自愈；直连 Store 调用方需自行重试 `MySqlException 0x80004005`）。
- 生产部署建议 **MySQL ≥8.0.18**（8.4 LTS 线为当前推荐）。

## 四、跨方言通用

- **SQLite**：单机单写者形态，多实例/多连接并发租约呈 busy 等待平台期（实测 ~25-28×
  退化）——**多实例部署不选 SQLite 栈**，单实例轮询即可。
- **旁路端口/容器形态**：本文档不含基准容器 runbook（那是性能测试侧，见
  performance.md 已运行验证表）；生产部署按各云商/自管标准配置。
- 所有数字的复现口径与原始报告位置见 performance.md 相应段落；引用时带日期与环境标签。

## 五、连接池预热与下限（冷启动尾延迟）

- **PalORM 栈**：`AddPalOrmSqlite/PostgreSql/MySql` 的可选参数 `prewarmConnectionCount`
  （默认 0 = 不预热）注册 `PalOrmPreWarmHostedService<TProvider>`——启动期逐条灌暖物理连接
  （上游 XML：远程建连实测 ~8.5 ms/条），连接进 ADO.NET **进程级共享池**（按连接串键控，
  预热 options 与 DI 工厂共用同池）。失败降级启动（记 Warning 不炸 host）。
- **关键配套——`MinPoolSize`**：只预热不设池下限，空闲修剪器会把暖连接清空（上游 6.6.0
  XML 明示契约），预热与 `DbOptions.MinPoolSize`（或连接串 `Min Pool Size`）必须成对配置。
- **会话池（PalORM 6.6.0，默认关闭）**：`AddPalOrm*` 的 `useSessionPool: true` 让 Scoped
  `DataSession` 从 `IDataSessionFactory<TProvider>` 租出/归还，省下每请求会话构造税并让
  复用槽、读连接跨请求存活；它池化的是会话包装，物理连接仍由 ADO.NET 驱动池管理，
  与上文的连接预热互补，不替代 `MinPoolSize`。
- **Dapper 栈**：无库内预热入口，直接用驱动连接串（Npgsql / MySqlConnector 的
  `Min Pool Size` + `Max Pool Size`）达成同款效果；进程级池语义同上。
- **适用边界**：收益集中在**冷启动首请求尾延迟**（免首批建连串行化）；常驻进程稳态无收益
  （池已暖），重启频繁或弹性伸缩（缩容到零再拉起）的部署收益最大。预热条数建议 = 预期
  稳态并发数的下界，而非峰值。
