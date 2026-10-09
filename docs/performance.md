# 性能实测记录

> 日期：2026-10-08  
> 环境：Windows 10 x64（AMD Ryzen 9 8945HX，32 逻辑核）；.NET SDK 11.0.100-rc.1.26425.128；BenchmarkDotNet 0.15.8（InProcessEmitToolchain）  
> 历史口径：2026-06-28 轮（SDK 11.0.100-preview.5）数字见下文「历史烟测归档」与「已有 BenchmarkDotNet 产物」

## 当前结论

2026-10-08 性能测试系统优化后，**22 个基准类（Framework 5 + App 2 + Infra 7 + Persistence 3 + SagaLane 1 + Concurrency 2 + Remote 2）全部具备正式报告入口**：`--framework` / `--app` / `--infra` / `--concurrency` / `--persist-pg` / `--persist-mysql` / `--persist` / `--persist-medium` / `--saga` / `--compression` 以模式级 ManualConfig（`Job.MediumRun` + InProcessEmitToolchain + MemoryDiagnoser）绕过 net11-rc moniker 限制（默认 Switcher 入口仍不可用；根因与 jobs-union 双 job 坑详见 `bench/PalDDD.Benchmarks/Program.cs` 头注——类级 `[ShortRunJob]` 已全部移除，同 `--persist` 五十四轮先例）。`--smoke` 新增分配预算断言（Success/FromValue ≤1、Failed ≤50、RaiseEvent ≤140 B/op，超限退出码 1；变异验红实证：预算压至 1 时 RaiseEvent 判 ⛔ 退出码 1）。

**同日缺口补齐轮**：新增 `--app` 车道（DispatcherBenchmarks + SerializationBenchmarks——CQRS 分发链与消息序列化此前是消息发布路径的两大缺位面：分发仅有 <500B 分配契约无计时，序列化零数据）；CompressionBenchmarks 补 LZ4/ZStandard 原生绑定四项（同一 Provider 五算法同负载可比，此前 README 宣传的原生绑定零基准）；`--concurrency` 车道（Outbox N worker 租约争用，InMemory 簿记层 + Dapper SQLite 真库层）把「多实例并发发布零丢失零重复」从正确性主张升级为性能数据——**不变量每 wave 成立（800/800 精确租走、零重复守卫全程未触发），性能形态两层分化**：InMemory 8 worker 退化 1.5×（锁竞争正常量级），Dapper SQLite 多连接并发退化 **25×**（158ms vs 串行 6.2ms——busy 等待升级延迟所致，单机 SQLite 单写者的真实最坏形态）。**后续追加**：Inbox 编排层入 `--infra`（首投 163ns/600B、重复拒绝 3-op 236ns，Error <2%）；`--persist-pg` / `--persist-mysql` 网络真库车道**建成并完成首轮真跑**（wslc 外部实例路径：`PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` 环境变量 + 库名 palddd_bench 白名单守卫，Testcontainers 路径保留给 Docker 环境）——多实例租约三形态落地：**PG 真并行（4W 中位 ~2.8×）/ MySQL Dapper 栈 N≥2 InnoDB 死锁（ADR-024 取舍的并发新实证）/ SQLite 25× busy 平台期**，详见 PG/MySQL 真库基线段。「22 类全覆盖」指**运行入口覆盖**，特性面仍非全覆盖——剩余缺口见「未完成项」清单。

统计有效性自评（testing.md §五判据套用首轮实测）：绝大多数基准 Error/Mean < 5%（App 车道与框架微基准全部 <1%）；5~15% 可接受带若干项（Outbox.AddSingleMessage 12%、Configuration.Pg 11%、SmartEnum.AllValues 5%、并发 InMemory 4W 13.4% 等，报告标注环境敏感）；**>15% 统计无效 2 项**——SmartEnum.FromValue_Known 24.8% / TryFromValue_Valid 25.9%（ns 级零分配操作的固有抖动，判定以零分配事实为准，引用其计时时需先提升配置）；ValueObject 三项为 ZeroMeasurement（BDN 标注与空方法不可区分，值即 ~0）。Medium 口径跨跑稳定性：`--framework` 二跑关键值跨跑差 AddSingleEvent 0.7% / Create 1.6% / Success 3.5%（对照 ShortRun 历史 53% 跨轮差，medium 裁决实证成立）。

另：SmartEnum 三方法（FromValue/TryFromValue/AllValues）历史产物 NA 的根因已修——基类继承的静态方法调用不触发派生类静态构造器，注册未执行即调用；`[GlobalSetup]` 触碰静态字段强制注册先于全部基准（根因注释见 FrameworkBenchmarks.cs）。

## 当前烟测结果（2026-10-08，3 轮取中位）

命令：`dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke`（带分配预算断言，本轮 4/4 预算内、退出码 0）

| 测量项 | 迭代次数 | 耗时（3 轮中位） | 当前线程分配 |
|---|---:|---:|---:|
| `PalValidationResult.Success` | 1,000,000 | 33.00 ms | 88 B（0.000 B/op） |
| `PalValidationResult.Failed` | 1,000,000 | 39.25 ms | 40,000,040 B（40.000 B/op） |
| `SmartEnum.FromValue` | 1,000,000 | 24.54 ms | 40 B（0.000 B/op） |
| `Entity.RaiseEvent` | 1,000,000 | 152.84 ms | 128,001,256 B（128.001 B/op） |

> 噪声实证（2026-06-28 vs 2026-10-08 两轮对照）：分配跨轮、跨 SDK 逐字节稳定（88 / 40,000,040 / 40,128B 级；RaiseEvent 总量 +1000B 属启动噪声 = 0.001 B/op），计时同日 3 轮极差 ±5%、跨 SDK 差 ±20%（Success 14.12→33 ms）。「分配可作判定、计时只作趋势」的分层再次实证。

## MediumRun InProcess 基线（2026-10-08 首轮，`--framework`/`--app`/`--infra`）

> 完整表（含 Ratio/Gen0/中位数/离群点提示）在 `bench/PalDDD.Benchmarks/bin/**/artifacts-medium/results/*-report-github.md`（构建产物不入库，可复跑再生）。与 2026-09-25 否决的 span decoder 改造留档对照：GZip 解压 33,704 B / Deflate 33,648 B 与回滚基线一致，回滚形态保持。
> 与历史 ShortRun 产物（`BenchmarkDotNet.Artifacts/results/`，preview.5 + CsProj 子进程工具链）**口径不同不可直接对比**：AddSingleEvent 91.04→116.8 ns 的差异含工具链与 SDK 因素，不作代码回归结论。

Framework（`--framework`）：

| 基准 | Mean | Error | Allocated |
|---|---:|---:|---:|
| EntityDomainEvent.AddSingleEvent_LinkedList | 116.8 ns | 1.21 ns | 72 B |
| EntityDomainEvent.AddMultipleEvents_LinkedList | 454.9 ns | 4.61 ns | 432 B |
| EntityDomainEvent.IterateEvents_RefStructEnumerator | 207.2 ns | 3.48 ns | 152 B |
| EntityCreation.Create | 3.7033 ns | 0.0912 ns | 56 B |
| SmartEnum.FromValue_Known（修复后） | 9.1616 ns | 2.2680 ns | - |
| SmartEnum.TryFromValue_Valid（修复后） | 6.6618 ns | 1.7285 ns | - |
| SmartEnum.AllValues（修复后） | 0.4228 ns | 0.0222 ns | - |
| Validation.Success | 0.2444 ns | 0.0149 ns | - |
| Validation.Failed_SingleError | 6.3368 ns | 0.1458 ns | 40 B |
| ValueObject.Create_Int | 0.0041 ns | 0.0045 ns | - |

App（`--app`，2026-10-08 缺口补齐轮新增；命令实例复用只测分发，响应对象分配计入）：

| 基准 | Mean | Error | Allocated |
|---|---:|---:|---:|
| Dispatcher.SendAsync_NoBehavior | 188.2 ns | 1.27 ns | 216 B |
| Dispatcher.SendAsync_OneBehavior | 280.8 ns | 1.63 ns | 432 B |
| Serialization.Serialize_TypedMessage | 67.940 ns | 0.4791 ns | 112 B |
| Serialization.Deserialize_TypedMessage | 295.469 ns | 1.5671 ns | 208 B |
| Serialization.Catalog_FindByName | 7.572 ns | 0.0511 ns | - |
| Serialization.Catalog_FindByType | 2.063 ns | 0.0319 ns | - |

> 分发口径注：216B/次含响应对象（BenchResult record）+ 每请求 scope + 快路径状态机，与 CqrsTests 的 <500B 分配契约一致且余量充足；单直通行为增量 +92.6 ns / +216B，与 Dispatcher.ExecutePipelineAsync remarks 声明的「每行为一次委托分配 + 状态机 ~40B」口径吻合。Catalog 查找零分配（FrozenDictionary 路径）由 MemoryDiagnoser 实证。

Outbox 并发车道（`--concurrency`，每 wave = 清场 + 种子 800 条 + N worker 并发抢租至清空；每 wave 断言零重复不变量）：

| 基准（InMemory 层） | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Drain_Sequential | 412.49 μs | 1.00 | 1,023.76 KB |
| Drain_2Workers | 446.26 μs | 1.08 | 1,071.46 KB |
| Drain_4Workers | 593.71 μs（Error 13.4%，环境敏感带） | 1.44 | 1,166.74 KB |
| Drain_8Workers | 619.51 μs | 1.50 | 1,357.32 KB |
| SeedOnly_800（减法锚） | 80.34 μs | 0.20 | 275.66 KB |

| 基准（Dapper SQLite 真库层） | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Drain_Sequential | 6.205 ms | 1.00 | 2.21 MB |
| Drain_2Workers | 158.389 ms | **25.57** | 2.21 MB |
| Drain_4Workers | 159.337 ms | **25.72** | 2.23 MB |
| Drain_8Workers | 159.530 ms | **25.75** | 2.25 MB |
| SeedOnly_800（减法锚） | 1.905 ms | 0.31 | 1.48 MB |

> 并发车道注（2026-10-08 缺口清单第 ① 项落地）：**正确性主张在并发实测下成立**——每 wave 断言 800/800 精确租走，零重复守卫全程未触发（重叠租约或漏租即抛）。性能形态两层分化：InMemory 层锁竞争退化有界（8W +50%，同步完成 ValueTask 下锁持有短）；**Dapper SQLite 多连接并发退化 25×**（~158ms 平台期，与 worker 数 2/4/8 无关）——根因是 SQLite busy 等待的升级延迟序列（1/2/5/10/15/20/25/…100ms 递增 sleep），共享缓存多写者互撞即进入该序列；这是单机 SQLite 单写者的真实最坏形态，**多实例部署的租约性能主张（FOR UPDATE SKIP LOCKED 类真并发）须由 `--persist-pg` / `--persist-mysql` 车道验证**（车道已建成：Testcontainers 一次性容器 + DDL 真源 docs/sql/{dialect}/000_schema.sql + 镜像真源 TestEnvironment，吞吐 3 项 + 并发 wave 5 项每方言；本机无 Docker/WSL，fail-fast 退出码 2 已验证，首轮数字待容器环境补记）。操作性结论：SQLite 栈多实例并发不可作为性能方案（单实例轮询即可），InMemory 层的多 worker 开销在有界范围内。

Inbox 口径注：三方法为 1-op / 3-op / 2-op 捆绑（重复拒绝必然含首投+标记，无法单独测拒绝对端），均含 per-invoke store 重建（ITM-246 教训口径）；600B/528B 含 store + InboxMessage + 记录分配。

## Coverage 五车道基线（2026-10-08 缺口清单 ③④⑤⑥⑧ 落地，`--coverage`，全零外部依赖）

| 基准 | Mean | Error | Allocated |
|---|---:|---:|---:|
| Projection.ProcessAsync_FullCycle（TryStart+handler+MarkCompleted） | 572.8 ns | 11.09 ns | 168 B |
| Projection.Checkpoint_TryStartComplete_Cycle（断点存储直测） | 484.5 ns | 16.06 ns | 144 B |
| Evolution.Upgrade_SingleHop_V1toV2 | 284.0 ns | 8.26 ns | 224 B |
| Evolution.ChainUpgrade_V1toV3（两跳链） | 292.1 ns | 2.59 ns | 288 B |
| Specification.IsSatisfiedBy_Match / NoMatch | 1.683 / 1.870 ns | ~0.03 ns | **0 B** |
| Specification.ToExpression_Access | 0.4662 ns | 0.0383 ns | - |
| EndToEnd.FullChain_NoBehavior（分发→序列化→Outbox 落入，InMemory） | 470.4 ns | 4.86 ns | 968 B |
| EndToEnd.FullChain_OneBehavior | 595.1 ns | 6.25 ns | 1,184 B |
| Hosting.Terminal_Only（DefaultHttpContext 终端基线） | 412.5 ns | 8.83 ns | 1.41 KB |
| Hosting.ExceptionMiddleware_Passthrough | 441.4 ns | 9.72 ns | 1.41 KB |
| Hosting.MapCommand_Endpoint_Invoke（JSON 反序列化+分发+响应序列化） | 2,543.3 ns | 11.56 ns | 3.51 KB |

> 五车道注（2026-10-08）：① Specification 内存路径**零分配 ~1.7ns**——Expression.Compile 在规约构造期一次完成，IsSatisfiedBy 走缓存委托调用；README「AOT 已知限制③」的内存路径由此量化为非热区。② 演化链式（V1→V3 两跳）仅比单跳 +3%/+64B——成本在反/序列化对而非转换器跳数，多级演化的边际开销可忽略。③ 端到端 InMemory 链 470ns/968B 给出「框架开销层」单命令全链路基线（分发+序列化+Outbox 落入；不含 DB I/O——真库形态见各持久化车道）。④ 异常中间件直通 +29ns（+7%）/请求；MapCommand 端点全周期 2.5μs/3.5KB（含 JSON 双向，Error 0.5%）。⑤ 演化契约实录：升级步必须同 wire name + 递增 schemaVersion（管线校验「stable wire name」——首跑用 .v1/.v2 命名被拒，属 API 契约的基准侧实录）。

## 横向对比与绝对 QPS（2026-10-09 新维度轮：`--compare` / `--qps`）

> 新维度背景：此前四轮评估的覆盖面结论不包含「跨框架对比」与「多线程聚合吞吐」——
> BDN 单线程 ns/op 不可直接外推多线程聚合（锁竞争/分配压力/GC 并发），跨框架亦无
> 同 harness 数据。本轮两个专门量具补齐。

### MediatR 对比（`--compare`，MediatR 12.5.0 vs PalDDD 3.3.0，同进程同 harness 四点）

> MediatR 锚定 12.5.0：13.0.0 起转商业许可，对齐本仓开源许可裁决。公平性：同 handler
> 形态（同步完成、每次 new 结果含 Guid）、同管道形态（零行为+单直通）、同 MediumRun
> InProcess 同轮执行。⚠️ 引用须带完整口径：MediatR 是纯进程内分发器（无 Outbox/Saga/
> EventLog），此对比只回答「分发层开销」——PalDDD 的增量价值在分发之外（见 README 对比表）。

| 基准 | PalDDD | MediatR 12.5.0 | 差距 |
|---|---:|---:|---|
| 零行为分发 | 183.9 ns / 216 B（E1.8%） | **125.9 ns** / 224 B（E2.6%） | MediatR 快 ~31% |
| 单直通行为 | 270.0 ns / 432 B（E0.5%） | **216.3 ns** / 528 B（E1.7%） | MediatR 快 ~20%，PalDDD 分配少 18% |

> MediatR 分发裸快 20~31%（同步完成的内联优化更深）；但 PalDDD 的 IsCompletedSuccessfully
> 零堆路径在**分配**上占优（单行为 432B vs 528B），且自带租约 Outbox/Saga/EventLog 等
> MediatR 没有的基础设施——「分发是起点不是终点」的 README 主张与实测一致。
> 引用边界：两轮 PalDDD 数字（183.9/270.0 vs 此前 188.2/280.8）为跨进程 JIT 状态波动，
> 同轮内比值可信。

### CAP 对比（`--compare` + `PALDDD_BENCH_CAP_*`，DotNetCore.CAP 8.4.1，MIT）

> 公平口径：两侧同为「同步写本地消息表 + 后台异步送 broker」——CAP.PublishAsync 内联
> 写 cap.published 行并入队后台 sender（RabbitMQ 传输）；PalDDD 侧为序列化+outbox_messages
> 写行（MySQL 真库同容器；broker 投递由 OutboxProcessor 后台负责，语义对位 CAP sender）。
> 一次性容器：rabbitmq 4.3.6-management（独立 bench 用户）+ mysql 8.4.11。

| 基准 | Mean | Allocated |
|---|---:|---:|
| CAP.PublishAsync（RabbitMQ 传输 + MySQL cap.published 表） | **5.054 ms**（E6.2% 环境敏感带） | 24.44 KB |
| PalDDD 序列化+OutboxAdd（MySQL outbox_messages 表） | **1.872 ms**（E7.6% 环境敏感带） | 10.36 KB |

> PalDDD 同语义路径快 **2.7×**（1.87ms vs 5.05ms）、分配少 **58%**（10.36KB vs 24.44KB）。
> 口径注：两侧各自的序列化器均在被测路径内（CAP 用自带 JSON 序列化，PalDDD 用源生成
> JSON——各自生态的真实成本，非第三方差异剔除后的纯 SQL 差）；CAP 的消息送 RabbitMQ 在
> 后台异步完成、PalDDD 的送 broker 同样在 OutboxProcessor 后台——「消息表写入+入队」
> 对位公平。Error 6~8% 属 5-15% 环境敏感带（真库网络往返主导）。

### 绝对 QPS（`--qps` harness，非 BDN：1/4/8 线程 × 3s 实测聚合）

| 场景 | 1 线程 | 4 线程 | 8 线程 |
|---|---:|---:|---:|
| dispatch（纯分发，零行为） | **4.87M ops/s**（205 ns/op） | **19.76M ops/s**（202 ns/op） | **33.15M ops/s**（241 ns/op） |
| chain（分发→序列化→Outbox 落入→SaveChanges，InMemory） | **1.06M ops/s**（941 ns/op） | 1.61M ops/s（2488 ns/op） | 1.55M ops/s（5152 ns/op） |

> QPS 注（趋势级，单轮实测）：① dispatch 多线程近线性扩展到 4 线程（19.8M=4×4.9M），
> 8 线程 +68%（33.2M）——分配压力与 GC 并发下仍保持高聚合。② **chain 场景 4 线程即到
> 平台期**（1.6M，4→8 线程不再增长，ns/op 退化 2488→5152）——逐 op 的 store 重建+序列化
> 分配使 Gen0 压力饱和，多线程下 GC 成为瓶颈。InMemory 框架层口径（无 DB I/O），真库
> 场景瓶颈在数据库不在框架（见并发车道）。③ 与单线程 BDN 数字互验：dispatch 205 ns/op
> ≈ BDN 184~196ns（harness 含循环与 Stopwatch 开销差，量级一致）；chain 941 ns/op ≈
> BDN 470ns 的 2×——ThreadLocal 访问+逐 op store 重建的并行形态差，口径各自注明。

## PG/MySQL 网络真库基线（2026-10-08 首轮，wslc 一次性容器 + `PALDDD_BENCH_*_CONN` 外部实例路径）

> 环境：postgres:18.6 / mysql:8.4.11（wslc 本地镜像，旁路端口经 WSL NAT 到 127.0.0.1）；AddSingle ~0.6-1.6ms/条 即网络往返量级，与 SQLite 共享内存库（μs 级）口径差三个数量级属预期。Lease_Batch100 口径含重灌 100 条单条插入（同 SQLite 车道口径），网络下由重灌主导。

吞吐（Dapper 栈）：

| 基准 | PG（postgres:18.6） | MySQL（8.4.11） |
|---|---:|---:|
| Outbox_GetPending_Batch100 | 665.4 μs（Error 21% ⚠统计无效） | 664.7 μs（Error 2.6% ✓） |
| Outbox_Lease_Batch100（含重灌 100） | 58.40 ms（Error 0.8% ✓） | 173.65 ms（Error 1.7% ✓） |
| Outbox_AddSingle | 641.3 μs（Error 7.8% 环境敏感带） | 1,609.7 μs（Error 3.9% ✓） |

并发 wave（每 wave 种子 800 + N worker 抢租 + 零重复不变量；MySQL 列为 ADR-025 前
无锁模板的历史基线，2026-10-09 修订后的双栈形态见下方复跑注）：

| 基准 | PG | MySQL（无锁模板，ADR-025 前史） | MySQL（ADR-025 后：双栈 SKIP LOCKED 双模板） |
|---|---:|---:|---:|
| Drain_Sequential | 128.80 ms（中位 146.70） | 63.62 ms（Error 1.1% ✓） | Dapper 61.3 ms / PalORM 92.3 ms ✓ |
| Drain_2Workers | 102.35 ms（中位 78.97） | **NA：InnoDB 死锁** | Dapper 44.2 ms（0.72）✓ |
| Drain_4Workers | 79.24 ms（中位 52.00） | **NA：InnoDB 死锁** | Dapper 54.8 ms / PalORM 51.9 ms ✓ |
| Drain_8Workers | 153.05 ms（中位 140.94） | **NA：InnoDB 死锁** | Dapper 51.3 ms（0.84）/ PalORM 44.9 ms（0.49）✓ |
| SeedOnly_800 | 26.59 ms（Error 2.3% ✓） | 18.71 ms（Error 3.2% ✓） | 量级不变 |

> **ADR-025 落地复跑注（2026-10-09，MySQL 8.4.11 同容器同负载）**：版本探测双模板上线后
> **双栈死锁归零、互斥不变量全波通过**（死锁计数 5→2→0，中间 2 为 PalORM 探针行未过
> 源生成器的回退形态，已修）——Dapper 串行 61.3ms→8W 51.3ms、PalORM 串行 92.3ms→
> 8W 44.9ms（加速 1.2~2.1×），与 EF 形态直跑车道（1.75×）互相印证。**MySQL 并发车道
> 自此进入 PG 同款真并行形态**，ADR-024 记录的死锁/双租窗口在 ≥8.0.18 段消除；
> 旧版/MariaDB 回退路径仍为第二列的无锁形态（fail-safe，行为逐字不变）。

> **三形态对照（缺口⑦的核心产出，ADR-025 后更新）**：同一并发 wave 在三种库上的真实形态——
> **PG 三栈一致 SKIP LOCKED → 真并行**：中位数序 4W(52ms) < 2W(79ms) < 串行(147ms)，4 worker 中位加速 ~2.8×；但 PG 并发组 Error 25~33%（>15% 统计无效带），只可引中位数趋势、不可引点值，如需点值须升严格档复测。8W 回落（中位 141ms）疑连接数/种子争用，待复测。
> **MySQL ≥8.0.18（ADR-025 修订后）双栈真并行**：死锁归零、互斥成立（见复跑注）；**<8.0.18/MariaDB 回退路径**维持 ADR-024 现状——无锁 last-writer-wins 形态的并发后果是 InnoDB 死锁异常（3 处实录，ADR-024 补记实证一），消费方需幂等消费 + 死锁重试（处理器用户 tick 级自愈，见 ADR-024 运维语义勘正）。
> **SQLite 25× busy 平台期**（见上文并发车道注）。零重复不变量在 PG 全部 wave 与 MySQL 修订后全部 wave 成立。

PalORM 栈远程车道（含 EventLog/Idempotency 口袋补齐，2026-10-09 后段，同 wslc 路径）：

| 基准 | PG（Dapper / PalORM） | MySQL（Dapper / PalORM） |
|---|---:|---:|
| EventLog_AppendSingle | 917.5 μs（E 3.0%）/ 1,129.7 μs（E 11.5% 带） | 2,019.1 μs（E 7.1% 带）/ 2,157.5 μs（E 5.3% 带） |
| Idempotency_TryStart_Completed | 1,236.6 μs（E 1.9%）/ 1,371.2 μs（E 5.5% 带） | 3,661.8 μs（E 7.7% 带）/ 3,587.4 μs（E 4.3% 带） |

> EventLog/Idempotency 口袋注（2026-10-09）：口径同 SQLite 三栈车道（Append Any 版本 + TryStart 唯一键全周期）；两类远程吞吐类（原名 Outbox）自本轮起覆盖三 Store 能力，类名沿用不改（避免报告文件名/文档面漂移，范围已在类头注明）。**基准缺陷实录**：首版 `DapperEventLog` 漏传第三参 dbType（默认 Sqlite）——PG 报 42804（timestamptz/text）、MySQL 报 last_insert_rowid 不存在，两库同根因不同表象，修后复跑四格全出数；属基准侧缺陷非产品缺陷（产品 `ToTimeParam` PG 路径传原生 DateTimeOffset，正确）。PG 追加约为 MySQL 一半（往返次数同为 2，差在驱动/服务端写路径）。

PalORM 栈 Outbox（2026-10-08 ⑦ 残余补齐，PG/MySQL 同 wslc 路径）：

| 基准 | PG | MySQL |
|---|---:|---:|
| Outbox_GetPending_Batch100 | 16.485 ms（Error 1.4% ✓） | 744.8 μs（Error 6.6% 环境敏感带） |
| Outbox_Lease_Batch100（含重灌 100） | 113.265 ms（Error 2% ✓） | 520.898 ms（Error 2.4% ✓） |
| Drain_Sequential | 45.245 ms（Error 19.8% ⚠趋势） | 90.150 ms（Error 4.5% ✓） |
| Drain_4Workers | 22.521 ms（Error 6%，**加速 2.0×**） | **NA：InnoDB 死锁** |
| Drain_8Workers | 23.788 ms（Error 5.1%，加速 1.9×） | **NA：InnoDB 死锁** |
| SeedOnly_800 | 9.255 ms（Error 2.9% ✓） | 18.123 ms（Error 13.2% 环境敏感带） |

> PalORM 栈注：① **PG 真并行成立**（4W/8W 稳定 ~2×，不变量全 wave 通过）——与 Dapper PG 互相印证 SKIP LOCKED 形态。② **MySQL PalORM 与 Dapper 同形态死锁**（N≥2 全 NA）——ADR-024「Dapper/PalORM 双栈无锁子句」的并发后果在两栈双双实证，裁决面证据完整。③ 吞吐量级显著慢于 Dapper（GetPending 16.5ms vs 665μs）——PalORM 会话/源生成路径的每次操作开销更高，属栈间真实差异（编译期 SQL 换运行时开销的取舍面），单机 SQLite 车道的「PalORM 2.465ms vs Dapper 2.919ms 反超」结论**不外推**到网络库。④ **形态实录**：`DataSession<T>` 单活跃操作限制（非线程安全）——共享单会话多 worker 并发抛「already has an active database operation」，多实例形态 = 每实例独立会话（本车道实现形态，亦是部署结论）。

EF 栈租约 SQL 形态车道（`MySqlSkipLockedShapeBenchmarks`，2026-10-09 ⑦ 收尾；EF 栈级车道待上游 Provider——Npgsql.EF 11 线仅 preview 且钉死 EF preview 精确版本与仓 rc.1 冲突 NU1107、Pomelo 无 11 线 NU1102，即死条目清理注释「待出 RC 后跟进」的真因）：

| 基准（MySQL 8.4.11，JOIN 派生表 FOR UPDATE SKIP LOCKED 形态逐字直跑） | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Drain_Sequential | 92.44 ms（Error 1.7% ✓） | 1.00 | 915.73 KB |
| Drain_4Workers | 59.57 ms（Error 3.8% ✓） | 0.64 | 928.88 KB |
| Drain_8Workers | 52.61 ms（Error 2.1% ✓） | **0.57** | 947.18 KB |
| SeedOnly_800（减法锚） | 26.09 ms（Error 1.7% ✓） | 0.28 | 880.04 KB |

> **SKIP LOCKED 形态注（ADR-024 指引验证闭合）**：EF 栈步骤 1 租约 SQL 在 MySQL 8.4.11 上**零死锁、互斥不变量全波通过（UPDATE 受影响行数之和 == 种子 800）、串行→8W 加速 1.75×**——与 Dapper/PalORM 无锁形态的死锁（同轮复现 5 处）构成同库同负载的正面对照。「需要强互斥的 MySQL 场景选 EF 栈（或自加派生表锁）」的指引**成立且已量化**。实证已补记入 ADR-024（2026-10-09 补记段）。

PG 方言特性车道（`PgDialectFeaturesBenchmarks`，仅 `--persist-pg`，2026-10-09 ⑦ 收尾）：

| 基准 | Mean | Error | Allocated |
|---|---:|---:|---:|
| Pipeline_BatchInsert100（100 条单往返批插） | 8.814 ms | 4.8%（环境敏感带） | 324,210 B |
| Sequential_Insert100_Control（同 100 条逐条往返对照） | 142.686 ms | 0.8% ✓ | 299,554 B |
| Router_GetWriter / GetReader（路由决策+池获取） | 44.50 / 44.11 ns | ~1% ✓ | 184 B |
| ListenNotify_Roundtrip（NOTIFY+冲刷读+事件到达） | 460.875 μs | 1.4% ✓ | 2,346 B |

> 方言特性注：① **Pipeline 批处理单往返收益实测**（100 条插入 8.8ms vs 逐条 142.7ms；上一轮跑出 8.6×、本轮 16.2×——逐条对照组受机器状态影响大，比值以两轮区间 8.6~16.2× 引用，Pipeline 侧 Error 4.8~9.8%）。② 读写分离路由决策 44ns/184B（获取即还口径）。③ LISTEN/NOTIFY 往返 461μs——PG 协议语义：空闲连接不推送，通知随下一次命令结果冲刷（基准即此口径；`PostgreSqlOutboxNotifier` 的服务化推送延迟属集成测试量纲）。④ COPY 批量已隐含在 Dapper `AddMessagesAsync` 种子路径（PG 侧经 DapperBulkCopy）。

Infra（`--infra`，全 InMemory fixture）：

| 基准 | Mean | Error | Allocated |
|---|---:|---:|---:|
| OutboxThroughput.LeasePending_Batch100 | 62.400 μs | 0.7193 μs | 63,360 B |
| OutboxThroughput.GetPending_Batch100 | 22.355 μs | 0.2131 μs | - |
| OutboxThroughput.LeaseAndMarkAll_Batch100 | 126.559 μs | 1.0741 μs | 68,408 B |
| OutboxThroughput.AddSingleMessage | 2.720 μs | 0.3298 μs | - |
| Inbox.TryStart_FirstDelivery（InMemory 编排层，2026-10-08 缺口清单第②项） | 163.0 ns | 1.17 ns | 600 B |
| Inbox.TryStart_DuplicateAfterProcessed（3-op 捆绑：首投+标记+重投拒绝） | 235.8 ns | 2.26 ns | 528 B |
| Inbox.TryStartAndMarkProcessed_Cycle（2-op：首投+标记） | 198.2 ns | 3.07 ns | 528 B |
| EventLog.Append_SingleEvent | 6.807 μs | 0.1469 μs | - |
| EventLog.ReadStream_Forward | 94.398 μs | 3.8230 μs | - |
| SagaState.GetById | 32.45 ns | 0.637 ns | - |
| SagaState.GetActiveSagas_Batch50 | 1,086.57 ns | 48.826 ns | 2,200 B |
| SqlGen 七项（const SQL 生成） | 1.59~8.63 μs | — | 全 0 |
| Configuration.DapperConfig_CreatePg | 62.82 ns | 6.789 ns | 184 B |
| Compression.GZip_Compress/Decompress_16KB | 15.423 / 3.633 μs | ~0.14 μs | 1,624 / 33,704 B |
| Compression.Deflate_Compress/Decompress_16KB | 15.729 / 3.430 μs | ~0.16 μs | 1,240 / 33,648 B |
| Compression.Brotli_Compress/Decompress_16KB | 22.717 / 14.393 μs | ~0.15 μs | 304 / 45,185 B |
| Compression.LZ4_Compress/Decompress_16KB | 6.768 / 2.564 μs | ~0.10 μs | 16,816 / 45,232 B |
| Compression.ZStandard_Compress/Decompress_16KB | 7.258 / 3.851 μs | ~0.13 μs | 16,832 / 45,232 B |

> 压缩车道注（2026-10-08 缺口补齐轮，五算法同一 Provider 同负载 16KB）：原生 LZ4/ZStandard 压缩比 GZip 快 ~2.2×、解压快 ~1.1~1.4×，但**分配高一个量级**（压缩 16.8KB vs GZip 1.6KB/op——原生绑定直接分配输出缓冲）。「LZ4 极速适合热路径」的快成立，分配敏感性场景需权衡。Brotli 计时跨轮漂移较大（22.7~27.1 μs，重 CPU 负载受机器状态影响），引用以 artifacts 报告为准；GZip/Deflate 解压分配 33,704/33,648 B 与 2026-09-25 回滚留档逐字节一致。

## 全模式复跑与环境漂移实证（2026-10-09 晚，91c7aa9 终态）

> 新维度轮落地后对 HEAD 做全入口串行复跑：13 个运行模式 15 次运行（`--compare` 两跑：MediatR-only 与 CAP opt-in）、161 项 BDN 基准（含 MediatR 四点同晚复跑）+ smoke 4 断言 + QPS 6 格，20:41~22:26 严格串行（模式间不并行，避免互相污染测量）。基准进程全部 exit 0、MySQL 三栈死锁计数 0（全日志 grep 零命中）、wslc 容器零残留、工作树零污染。两次运行未直接达 exit 0（`--persist-mysql` 首跑、`--compare`+CAP 首跑）均为容器环境态且发生在基准进程启动之前，诊断后复跑成功（实录见本节末）。日志与退出码台账：`%TEMP%/palddd-bench-20261009/`；正式报告同落 artifacts-medium（31 份 github.md）。

结构性结论全部复现，无回归信号（对照上文各段基线）：MySQL ADR-025 双栈死锁归零+真并行（Dapper 8W Ratio 0.64、PalORM 4W 1.6×、EF 形态 8W 1.8×，Error ≤4%）；PG PalORM 真并行 1.5×、Dapper 并发中位 8W 加速 ~2.3×（组内 Error >15% 按纪律只引中位）；SQLite 多连接 busy 平台期 28.3×（首轮 25.7×）；InMemory 并发退化有界 8W 1.50；Pipeline 批插 9.3×（历史区间 8.6~16.2× 内）；Specification 零分配 ~1.7ns；MediatR 两轮复跑比值稳定（零行为 0.68~0.70、单行为 1.16~1.19；PalDDD 单行为分配 432B vs 528B 少 18%）；CAP 同轮快 2.2×、分配少 61%（首轮 2.7×/58%，比值量级一致；PalDDD 侧本轮 Error 16.4% 略超无效线，点值谨慎引用，比值与分配可引）；QPS chain 4 线程平台期复现；GZip/Deflate 解压分配 33,704/33,648 B 与 2026-09-25 回滚基线逐字节一致；smoke 四分配值跨三轮（06-28/10-08/10-09）逐字节一致。

**环境漂移实证（同晚计时层整体上移，非代码回归）**：受影响面三处——infra OutboxThroughput（较 10-08 基线上移 ~1.6~1.7× 且 Error 进 8~16% 带：LeasePending 62.4→115.6μs、GetPending 22.4→37.6μs、LeaseAndMarkAll 126.6→212.1μs）；CAP 对比绝对值（CAP 5.05→10.63ms、PalDDD 1.87→4.81ms，~2×）；QPS 绝对值（-7%~-15%）。判定证据链三层：① 受影响车道跨模式同向漂移，同晚其余车道（framework/app/coverage/saga 等）计时与基线偏差 ≤6%；② 全部车道分配列逐字节稳定（分配不受机器负载影响，「分配可判定、计时只作趋势」分层第四次实证）；③ 同轮比值全部与基线一致，且构成本文各基线段（10-08/10-09 日间）与本次复跑（10-09 晚）的不同日同代码对照。引用边界：当晚绝对计时引用须带环境标签，跨日计时对比不作回归结论；比值与分配列可跨轮引用。

复跑环境实录两则（容器环境态，非基准缺陷）：① MySQL 就绪探针竞态——`mysqladmin ping` 不校验凭据，首启 init 窗口内即回 alive，随后认证 SQL 报 Access denied（`&&` 链断在基准启动前）；探针必须带认证（`mysql -uroot -p… -e "SELECT 1"`）。② wslc rabbit 容器偶发首启即死（WSLC_E_CONTAINER_NOT_RUNNING，重起即好）；RABBITMQ_DEFAULT_USER/PASSWORD env 凭据未生效坑再次复现（用户已建但 authenticate_user 报 invalid credentials，change_password 显式重置后 Success）。

## 优化轮（2026-10-09 深夜）：C5/C6/C2' 三项落地 + 旋钮实验三组

> 前置：全模式复跑（上节）定位的靶点经探针核实后实施。探针裁决四条：处理器轮询已是单跳原子租约（无合并空间）；EF 租约路径已是 FromSqlRaw + AsNoTracking + FencedTarget ExecuteUpdate 形态（无 ExecuteUpdate 化空间）；序列化器已有 ThreadStatic writer/buffer 池（buffer 池化无空间）；拦截器多事件批化评估后**缓做**（EF store 的 `AddMessagesAsync` 内嵌 `SaveChangesAsync`，SavingChanges 相位调用构成 EF 重入陷阱，且 k=1 主路径零收益——登记 v4.0 契约窗口候选），Dispatcher scope 免除需新增公共 API 且收益 ~50ns（不做）。实施三项：C5 分发快路径 / C6 压缩缓冲池化 / C2' MySQL 小批量 VALUES 单往返。每项验证三段：改后同轮 A/B → **S3 反向验证**（stash 回 HEAD 同晚重跑，全部列逐字节退回基线——机器漂移排除，因子归因闭合）。

C5 零行为分发快路径（`Dispatcher.ExecutePipelineAsync`：空管道直呼 handler，省状态机实例与 ExecuteNextAsync 方法组委托两笔分配；语义与状态机空路径逐字一致）：

| 基准 | 改前（S3-HEAD 同晚） | 改后 | 差 |
|---|---:|---:|---|
| SendAsync_NoBehavior | 171.8 ns / 216 B | 159.4 ns / **160 B** | 计时 -7% / 分配 -26% |
| SendAsync_OneBehavior（对照组） | 250.6 ns / 432 B | 229.3 ns / 432 B | 分配逐字节不变 |

> MediatR 对照组同轮不变——零行为分配 160 B 已低于 MediatR 12.5.0 同口径的 224 B（裸分发计时仍 MediatR 快 ~40%，引用口径不变：MediatR 无基础设施层）。`--compare` 同轮四点：零行为 168.4 ns/160 B vs 117.6 ns/224 B；单行为 249.9/432 vs 211.1/528。连带：`--qps` dispatch 8 线程 28.3M→38.6M ops/s（跨晚口径，趋势级）。

C6 压缩缓冲池化（LZ4/ZStandard：Compress scratch 改 `ArrayPool` 租借、Decompress 增长缓冲改共用 ThreadStatic 槽——镜像 JsonMessageSerializer 池先例含峰值驻留权衡；v13 裁剪拷贝决策保留，返回值仍为精确尺寸独立数组）：

| 基准（16KB 负载） | 改前（S3-HEAD 同晚） | 改后 | 差 |
|---|---:|---:|---|
| LZ4_Compress | 5.98 μs / 16,816 B | 5.49 μs / **376 B** | 分配 -97.8% |
| ZStandard_Compress | 6.53 μs / 16,832 B | 5.90 μs / **304 B** | 分配 -98.2% |
| LZ4_Decompress | 2.31 μs / 45,232 B | 1.52 μs / **16,488 B** | 分配 -64% / 计时 -34% |
| ZStandard_Decompress | 3.56 μs / 45,232 B | 2.30 μs / **16,488 B** | 分配 -64% / 计时 -35% |
| Brotli 两项（对照组，不在改动面） | 25.6 μs/304 B、13.8 μs/45,185 B | 同值 | 逐字节不变 |

C2' MySQL 小批量 VALUES 单往返（`DapperBulkCopy`：≤16 行路由参数化多行 VALUES 单语句，>16 行维持 MySqlBulkCopy/LOAD DATA；小批量不再要求 `AllowLoadLocalInfile`——部署要求放宽，消费方可见变更见 CHANGELOG；值转换镜像 ConvertForMySql、事务 fail-fast 与行数守卫对齐既有路径）。新增 AddBatch5 基准项（k=5 多事件保存典型形态）：

| 车道 | AddBatch5（k 条单往返） | 5×AddSingle 对照 | 批化收益 |
|---|---:|---:|---:|
| PG（COPY，postgres:18.6） | 863.0 μs（E3.8%） | 5×609.3 = 3,046 μs | **3.5×** |
| MySQL（VALUES 新路径，8.4.11） | 2,072.4 μs（E4.3%） | 5×1,593.7 = 7,969 μs | **3.8×** |
| SQLite（事务内批，共享内存库） | 84.0 μs（E2.0%） | 5×51.7 = 258 μs | 3.1× |

> VALUES 路径真库实证：行为校验内联（返回行数必须 ==5，两项基准各跑 30 次迭代全过）、死锁 0、`DialectProbeTests` 补 3 条批量写入覆盖（CI Testcontainers 双方言跑）；两条远程车道其余 21 项与同晚 stock 复跑逐项同值（回归面干净）。

旋钮实验三组（同晚五连跑 A/B，容器组间同晚对照）：

1. **Server GC（`DOTNET_gcServer=1`，--qps harness）**：chain 场景 8 线程 1.79M→**4.64M ops/s（+159%）**、4 线程 +80%——4 线程平台期在 Server GC 下消失；代价：dispatch 场景 8 线程 28.3M→19.9M（-30%，分配轻负载下 Server GC 反而吃亏）。**GC 模式按负载选型**：消息链路类重分配负载选 Server GC；库不替应用决策，属部署文档级结论。
2. **PG 旋钮**：`synchronous_commit=off` 对吞吐五项零影响（AddSingle 606→613μs 等，本地容器 fsync 近零成本环境测不出该旋钮——生产磁盘环境收益不可本地证伪，负结果如实登记）。**连接串 `Max Auto Prepare=64`：PalORM GetPending 14.7→6.7 ms（-55%，两侧 Error<1%）**、Dapper 对照不变——「PalORM PG GetPending 慢 Dapper 20×」的一半由未启用预编译解释（部署建议：PalORM PG 栈连接串加 `Max Auto Prepare=64`；Drain 波段该轮反向变慢属单轮观察，标记待复测）。
3. **MySQL `innodb_flush_log_at_trx_commit=2`（吞吐五项）**：写路径 **-24~-31%**（AddSingle 1,527→1,148μs、Lease 170.4→118.0ms、Idempotency 3,513→2,498μs），读路径 GetPending 对照不变（归因方向正确）；崩窗丢最近 ~1s 事务的持久性权衡须部署侧决策后启用。

## 优化轮三（2026-10-10 凌晨）：I/D 两项落地，P7/P8 探针否决，P8 实证回退

> 前两轮后剩余靶点的再裁决。探针否决两条：P7 序列化零拷贝直通——`Serialize+ToArray` 与 `SerializeToUtf8Bytes` 分配结构相同（池内写 + 一次终数组），无收益，避免一次无意义的 API 变更；P8 空行为数组直判——实施后实测零行为分配 160B 纹丝不动（MS DI 空 IEnumerable 走缓存数组，靶分配本不存在），**无收益即回退**，Dispatcher 留勘误注防重提。落地两项均过 S3（stash 回 HEAD 同晚复测 PG：EventLog 退回 967μs、AddSingle 退回 617μs，因子归因坐实；对照组 AddBatch5 同带不变）。

I：EventLog 单事件 Any 版本单往返（仅 PG 门控）——`INSERT..SELECT` 子查询服务端原子计算 `COALESCE(MAX(stream_version),-1)+1` 并 `RETURNING` 双列，读+插单往返并消除该子路径 TOCTOU 窗口；k>1 与非 Any 版本走原路径，冲突分类（EventId 重复原样上抛 / 版本位竞争转并发异常 / PG aborted 事务保守上抛）对齐既有 catch 意图。**方言门控依据（三方言同轮实测）**：

| 方言 | 原路径（S3-HEAD） | 快路径 | 差 | 裁决 |
|---|---:|---:|---:|---|
| PG（网络往返主导） | 967.2 μs（E2.9%） | **820.3 μs**（E2.6%） | **-15%** | 启用 |
| MySQL | 2,020 μs（E6.1% 带） | 1,912 μs（E6.8% 带） | -5.3%（噪声带内） | 不启用（原路径） |
| SQLite（本地往返≈0） | 111.8~123.7 μs | 161.1 μs | **+44% 劣化** | 不启用（原路径） |

> 教训入账：单往返优化只在往返主导的形态下成立——SQLite 本地「往返免费」，子查询复杂度反超省下的往返；MySQL 省一往返被写路径成本稀释。锁定测试：DapperStoreTests +2（Any 单事件慢路径行为 + EventId 重复冲突分类），DialectProbeTests EventLog 段 +Any 断言（CI 双方言，路径无关）。

D：Outbox 单条插入执行层去 Dapper 化（`AddMessage` 改原生 ADO 命令直发，参数值与方言转换逐一对齐原 DynamicParameters 形态，SQL 模板不变）：

| 基准 | 改前（昨夜 after） | 改后 | 差 |
|---|---:|---:|---|
| SQLite Outbox_AddSingle | 51.67 μs / 10.53 KB | **42.69~45.50 μs / 8.13~8.46 KB** | 计时 -12~17% / 分配 **-19.7%** |
| PG Outbox_AddSingle | 609.3 μs | 578.9 μs | -5%（RTT 稀释，S3 退化回 617 佐证） |
| 对照：PalORM 栈 AddSingle（不在改动面） | 10.65 KB | 10.65 KB | 逐字节不变 |

> 分配勘误：原推断「10.5KB 主要来自 Dapper 机械层」仅部分成立——去 Dapper 后仍余 ~8.4KB（SQLite 驱动/命令内部 + 基准侧 NewOutboxMessage），D 的真实贡献为 -2.07KB；以分配列实证裁决而非推断，保留。

## 历史烟测归档（2026-06-28，SDK preview.5）

| 测量项 | 迭代次数 | 耗时 | 当前线程分配 |
|---|---:|---:|---:|
| `PalValidationResult.Success` | 1,000,000 | 14.12 ms | 88 B |
| `PalValidationResult.Failed` | 1,000,000 | 41.10 ms | 40,000,040 B |
| `SmartEnum.FromValue` | 1,000,000 | 18.78 ms | 40 B |
| `Entity.RaiseEvent` | 1,000,000 | 124.80 ms | 128,000,256 B |

## 已运行验证

| 验证项 | 命令 | 结果 |
|---|---|---|
| BDN 版本可升级性 | `dotnet package search BenchmarkDotNet --exact-match --take 10` | 当前最高可见版本为 `0.15.8`，无可升级版本 |
| Benchmark 入口状态 | `dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj` | 已恢复为 `BenchmarkSwitcher` 默认入口；本机最小 BDN 校验未生成正式报告 |
| 替代性能烟测 | `dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke` | 通过（2026-10-08：4 组 Stopwatch + GC 分配数据 + 分配预算 4/4 断言，退出码 0；变异验红：RaiseEvent 预算临时压至 1 → ⛔ 退出码 1 后恢复） |
| `--framework` 正式基线（2026-10-08） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --framework` | 通过（4:15，5 类 18 项基准；二跑复验 4:12，关键值跨跑差 ≤3.5%）；SmartEnum 三方法 NA→有效数字（GlobalSetup 修复同批验证） |
| `--infra` 正式基线（2026-10-08） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --infra` | 通过（4:32，6 类 23 项基准全 InMemory fixture，零 NA）；缺口补齐轮复跑（7:02，压缩类扩至五算法 10 项）通过 |
| `--app` 正式基线（2026-10-08 缺口补齐轮） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --app` | 通过（DispatcherBenchmarks 2 项 + SerializationBenchmarks 4 项，全部 Error/Mean <1%；分发 216B/次与 <500B 契约一致、单行为增量 +216B 与 PipelineStateMachine remarks 口径吻合） |
| `--concurrency` 正式基线（2026-10-08 缺口清单第①项） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --concurrency` | 通过（两类 10 项；零重复不变量每 wave 成立 800/800；发现 SQLite 多连接并发 25× 退化平台期与 InMemory 有界退化 1.5×，结论见并发车道注） |
| `--infra` 三跑含 Inbox 车道（2026-10-08 缺口清单第②项） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --infra` | 通过（7:28，7 类 30 项；Inbox 三项 Error/Mean <2%，数字入基线表） |
| `--persist-pg` fail-fast 路径（2026-10-08 缺口清单第⑦项） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --persist-pg` | 本机（无 Docker/WSL）按设计报错退出码 2（「容器不可达」+ Docker endpoint 明细）——TOOL 人工语义不静默跳过；容器环境下的真跑数字待补记 |
| `--persist-pg` / `--persist-mysql` 真跑（2026-10-08，wslc 外部实例路径） | wslc 起一次性容器（postgres:18.6 / mysql:8.4.11，旁路端口 15432/13306，库名 palddd_bench_local 前缀守卫命中）+ 环境变量 `PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` | **首轮真库数字落地**：PG 8 项 3:24 exit 0；MySQL 复跑 8 项中 5 项有效 + 并发 N≥2 三项 InnoDB 死锁实录（3 处 0x80004005，见 PG/MySQL 基线注）。MySQL 首跑全红属库的显式安全门（批量导入要求 `AllowLoadLocalInfile=True` 由调用方显式决策 + 服务端 local_infile=1）——一次性容器上合规开启后复跑；修一处 bench 缺陷（OpenKeeper 重构后 OpenWorker 双重 Open） |
| Native AOT 发布 | `dotnet publish samples/PalDDD.AotSample/PalDDD.AotSample.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishAot=true` | 通过 |
| 全解决方案构建 | `dotnet build PalDDD.slnx --no-restore` | 通过；0 warning / 0 error |
| 全测试项目测试 | `for p in $(find test -name '*.Tests.csproj' ! -path '*/obj/*' ! -path '*/bin/*' \| sort); do dotnet test "$p" --no-restore; done`（MTP 禁用 slnx 批量——握手 → exit 5） | 通过；869 passed / 2 failed / 6 skipped（历史快照：2026-06-28；2026-09-25 实测：16 项目 1502 用例 = 本机 1434 通过 + 68 跳过——60 项 Docker 依赖（PalORM 多方言 46 + Integration 14）由 CI Testcontainers 执行，另 8 项本机 RabbitMQ 预检不可达） |
| 压缩解压 span decoder A/B（`--compression` 基准，InProcess，16KB 负载） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --compression`（改造前/后各两轮 + 真回滚验证一轮） | **改造否决，已回滚**（2026-09-25 特性审计三轮）：分配确定性劣化——GZip 解压 33,704→45,296 B（+34%）、Deflate 33,648→45,264 B（+35%），改造后两轮逐字节一致、回滚后逐字节还原（ArrayBufferWriter 增长序列超省掉的 MemoryStream）；耗时在噪声带内无改善（改造前自身跨轮 3.120→3.805(Median)/4.464 us，对照组 Brotli/GZip-Compress ±3% 稳定证可比）。**留档防重复提议**：`.NET 11 GZipDecoder/DeflateDecoder` 相对 `GZipStream+CopyWithLimit` 在本负载形态下无收益——未来 .NET decoder 优化或大负载场景可凭本基准复测（量具已入 bench） |
| 全模式串行复跑（2026-10-09 晚，91c7aa9） | 13 模式 15 次运行全入口：smoke/qps/compare×2(MediatR-only+CAP opt-in)/framework/app/infra/concurrency/coverage/persist/saga/compression/persist-medium/persist-pg/persist-mysql | 通过：基准进程 15/15 exit 0（2 次容器环境态失败诊断后复跑达标）、161 项基准无 NA、MySQL 三栈死锁 0、wslc 容器零残留；结构性结论全部复现无回归；同晚计时层漂移实证与引用边界见「全模式复跑与环境漂移实证」段 |

## 已有 BenchmarkDotNet 产物

现有 `BenchmarkDotNet.Artifacts/results/` 下保留了历史 ShortRun 结果，可作为旧基线，但不作为 .NET 11 Preview 当前实测结论。

| 基准类 | 结果文件 | 可引用数据 |
|---|---|---|
| `EntityDomainEventBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EntityDomainEventBenchmarks-report-github.md` | `AddSingleEvent_LinkedList`：91.04 ns / 72 B；`AddMultipleEvents_LinkedList`：797.87 ns / 376 B |
| `EntityCreationBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EntityCreationBenchmarks-report-github.md` | `Create`：3.8377 ns / 56 B；`GetHashCode_Compute`：17.6934 ns / 56 B |
| `ValidationBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.ValidationBenchmarks-report-github.md` | `Success`：0 ns / 0 B；`Failed_SingleError`：9.8009 ns / 32 B |
| `SmartEnumBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.SmartEnumBenchmarks-report-github.md` | 当前历史产物为 NA，不可作为性能结论（根因 2026-10-08 已修——继承静态方法不触发派生 cctor，见上文；现行数字见 MediumRun InProcess 基线表） |

## 未完成项

1. ~~等 BenchmarkDotNet 支持当前 .NET 11 后用默认入口重新生成全部 benchmark 类结果~~（2026-10-08 大部分消解：17 类经 InProcess 显式模式全部可出正式报告；残余 = 默认 Switcher 入口与类级 job 注解的恢复，待 BDN 上游补 net11 moniker 或 .NET 11 GA 后实测，见 `docs/migration/net11-ga-upgrade-plan.md` 步骤 3）。
2. SmartEnum.FromValue/TryFromValue 两项 Error/Mean >15%（ns 级固有抖动）：如需正式引用其计时，提升配置（严格档 5/10/15）复测；当前以零分配事实为准。
3. 保留 `--smoke` 作为秒级趋势检查 + 分配预算判定（2026-10-08 起带断言）；计时为单轮无统计效力，不替代正式报告。
4. **特性面缺口登记（2026-10-09 收尾轮，防「30 类全覆盖」被误读为特性面全覆盖）**：~~①~⑤⑧~~（全部落地）；⑦ 四栈/方言面收尾——Dapper+PalORM 双栈 PG/MySQL 真跑 ✓ + **EventLog/Idempotency 真库层补齐 ✓**（2026-10-09 后段：双栈四格全出数，见 EventLog/Idempotency 口袋注）、EF 栈**租约 SQL 形态**验证 ✓（`MySqlSkipLockedShapeBenchmarks`：零死锁+互斥+1.75× 加速）、PG 方言特性 ✓（Pipeline 单往返/读写分离路由/LISTEN-NOTIFY 往返，COPY 隐含在种子路径）。**死锁已裁决并落地（ADR-025）**：版本探测双模板使 Dapper/PalORM MySQL ≥8.0.18 进入 SKIP LOCKED 形态（复跑双栈死锁归零，见 ADR-025 落地复跑注），ADR-024 对 <8.0.18/MariaDB 回退路径继续有效。**登记残余（四项，均有上游/量纲理由）**：① EF 栈级车道待 Npgsql.EF/Pomelo 出 EF 11 rc 配套版（2026-10-09 实证 NU1107/NU1102，SQL 形态车道先行为其等价物）；② `PostgreSqlOutboxNotifier` 服务化推送延迟（LISTEN-NOTIFY 底层能力已测 461μs；服务化形态属异步服务量纲，归集成测试）；③ **mysql:8.0.17 旧版镜像回归**（ADR-025 后续动作 2：验证回退模板行为与现状逐字一致，需拉取旧版镜像）；④ broker 发布路径（Kafka/RabbitMQ 需 broker 环境，正确性已由 Messaging.Integration 覆盖，性能归有 broker 环境时补）。另：PG Dapper 并发组 Error >15% 点值待严格档复测、8W 回落形态待复测；READ COMMITTED 对照实验留档于 ADR-025 被否决方案。

## 发布判断

可发布收尾版本不依赖新的 BenchmarkDotNet 数字：本轮已验证 AOT 发布、构建、测试和 Dapper Projection 持久化闭环。当前补充的 `--smoke` 数据提供了可复跑性能证据，但正式 BDN 数据仍属于批次 8 后续项，不能在当前工具链状态下伪造成已完成。（2026-06-28 轮判断，留档）

2026-10-08 更新：上段「正式 BDN 数据未完成」的前提已被 InProcess 模式消解——15 类正式 medium 报告已产出并归档（artifacts-medium），statistical 判据自评见「当前结论」；SmartEnum 两项高抖动计时按 §五规则显式登记为统计无效而非引用。
