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

### 2.1 连接串 `Max Auto Prepare=64`（PalORM 栈强烈建议）

- **实测**：PalORM 栈 GetPending 14.7→6.7ms（**-55%**，两侧 Error<1%）；Dapper 栈对照组不变
  （Dapper 已走即时计划）。「PalORM PG GetPending 慢 Dapper ~20×」的约一半由未启用预编译
  解释，其余为 PalORM 会话/物化固有开销。
- **启用**：PalORM 栈（`PalDDD.PalORM.PostgreSql`）的连接串追加 `Max Auto Prepare=64`。
- **权衡**：预编译语句缓存占用服务端 plan cache（64 条上限有界）；对 Dapper/EF 栈无收益
  也无害（Dapper 实测对照组不变）。

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
