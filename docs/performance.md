# 性能实测记录

> 日期：2026-10-08（环境行 2026-10-18 校正：SDK rc.2 / BDN 0.16.0-preview.2；下方历史轮次保留当时环境）
> 环境：Windows 10 x64（AMD Ryzen 9 8945HX，32 逻辑核）；.NET SDK 11.0.100-rc.2.26504.105；BenchmarkDotNet 0.16.0-preview.2（InProcessEmitToolchain）
> 历史口径：2026-06-28 轮（SDK 11.0.100-preview.5）数字见下文「历史烟测归档」与「已有 BenchmarkDotNet 产物」

## 当前结论（2026-10-08 首轮；2026-10-18 口径校正见下）

**2026-10-21 当前口径**：**38 个基准类**（Framework 5 + App 2 + Feature 5 + Infra 7 + Persistence 4 + SagaLane 1 + Concurrency 2 + Remote 5 + Delivery 4 + Comparison 2 + Stream 1），入口以 `bench/PalDDD.Benchmarks/Program.cs` 为单一真源；`--smoke` 6/6 分配预算已接入 CI `perf-smoke`（每次提交）；BDN 0.16.0-preview.2 已恢复默认 Switcher 入口（2026-10-17），显式 InProcess 路径保留；EPSL v1.6 系统对账与修复见 `docs/review/review-2026-10-18-epsl-system.md`（当前系统版本 v1.7，2026-10-19 全局升位）。下面 2026-10-08 段保留首轮口径（22 类），不回溯改写。

2026-10-08 性能测试系统优化后，**22 个基准类（Framework 5 + App 2 + Infra 7 + Persistence 3 + SagaLane 1 + Concurrency 2 + Remote 2）全部具备正式报告入口**：`--framework` / `--app` / `--infra` / `--concurrency` / `--persist-pg` / `--persist-mysql` / `--persist` / `--persist-medium` / `--saga` / `--compression` 以模式级 ManualConfig（`Job.MediumRun` + InProcessEmitToolchain + MemoryDiagnoser）绕过 net11-rc moniker 限制（当时 BDN 0.15.8 默认 Switcher 入口不可用；2026-10-17 升 0.16.0-preview.2 后已恢复；根因与 jobs-union 双 job 坑详见 `bench/PalDDD.Benchmarks/Program.cs` 头注——类级 `[ShortRunJob]` 已全部移除，同 `--persist` 五十四轮先例）。`--smoke` 新增分配预算断言（Success/FromValue ≤1、Failed ≤50、RaiseEvent ≤140 B/op，超限退出码 1；变异验红实证：预算压至 1 时 RaiseEvent 判 ⛔ 退出码 1；R1 轮 2026-10-14 增 Start* 遥测入口 ≤1 B/op 两条——断言落 bench 进程：测试宿主订阅全局 ActivitySource 使 HasListeners 恒真，旧码 int tag 装箱 24.000/24.002 B/op 判红、守卫前置后 0 过）。

**同日缺口补齐轮**：新增 `--app` 车道（DispatcherBenchmarks + SerializationBenchmarks——CQRS 分发链与消息序列化此前是消息发布路径的两大缺位面：分发仅有 <500B 分配契约无计时，序列化零数据）；CompressionBenchmarks 补 LZ4/ZStandard 原生绑定四项（同一 Provider 五算法同负载可比，此前 README 宣传的原生绑定零基准）；`--concurrency` 车道（Outbox N worker 租约争用，InMemory 簿记层 + Dapper SQLite 真库层）把「多实例并发发布零丢失零重复」从正确性主张升级为性能数据——**不变量每 wave 成立（800/800 精确租走、零重复守卫全程未触发），性能形态两层分化**：InMemory 8 worker 退化 1.5×（锁竞争正常量级），Dapper SQLite 多连接并发退化 **25×**（158ms vs 串行 6.2ms——busy 等待升级延迟所致，单机 SQLite 单写者的真实最坏形态）。**后续追加**：Inbox 编排层入 `--infra`（首投 163ns/600B、重复拒绝 3-op 236ns，Error <2%）；`--persist-pg` / `--persist-mysql` 网络真库车道**建成并完成首轮真跑**（wslc 外部实例路径：`PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` 环境变量 + 库名 palddd_bench 白名单守卫，Testcontainers 路径保留给 Docker 环境）——多实例租约三形态落地：**PG 真并行（4W 中位 ~2.8×）/ MySQL Dapper 栈 N≥2 InnoDB 死锁（ADR-024 取舍的并发新实证）/ SQLite 25× busy 平台期**，详见 PG/MySQL 真库基线段。「22 类全覆盖」（2026-10-08 口径；2026-10-18 为 37 类，见上）指**运行入口覆盖**，特性面仍非全覆盖——剩余缺口见「未完成项」清单。

统计有效性自评（testing.md §五判据套用首轮实测）：绝大多数基准 Error/Mean < 5%（App 车道与框架微基准全部 <1%）；5~15% 可接受带若干项（Outbox.AddSingleMessage 12%、Configuration.Pg 11%、SmartEnum.AllValues 5%、并发 InMemory 4W 13.4% 等，报告标注环境敏感）；**>15% 统计无效 2 项**——SmartEnum.FromValue_Known 24.8% / TryFromValue_Valid 25.9%（ns 级零分配操作的固有抖动，判定以零分配事实为准，引用其计时时需先提升配置）；ValueObject 三项为 ZeroMeasurement（BDN 标注与空方法不可区分，值即 ~0）。Medium 口径跨跑稳定性：`--framework` 二跑关键值跨跑差 AddSingleEvent 0.7% / Create 1.6% / Success 3.5%（对照 ShortRun 历史 53% 跨轮差，medium 裁决实证成立）。

另：SmartEnum 三方法（FromValue/TryFromValue/AllValues）历史产物 NA 的根因已修——基类继承的静态方法调用不触发派生类静态构造器，注册未执行即调用；`[GlobalSetup]` 触碰静态字段强制注册先于全部基准（根因注释见 FrameworkBenchmarks.cs）。

## 烟测结果（2026-10-08，3 轮取中位；2026-10-14 增两条遥测预算后为 6 项）

命令：`dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke`（带分配预算断言，本轮 4/4 预算内、退出码 0；2026-10-14 起 6/6，2026-10-18 接入 CI `perf-smoke`）

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

## 优化轮四（2026-10-10）：拦截器异步路径批量化（IImmediateWriteOutboxStore 契约分派）

> 前三轮登记的「拦截器多事件批化缓做」本轮以契约分派形态安全落地。根因回顾：EF store 的
> `AddMessagesAsync` 内嵌自持 `SaveChangesAsync`，在 SavingChanges 相位调用构成 EF 重入——
> 但该陷阱只属于延迟写族。解法：新增公共接口 `IImmediateWriteOutboxStore`（重声明同名
> `AddMessagesAsync`，单一方法同时满足两接口，实现类零新增方法），Dapper/InMemory/PalORM
> 三栈标注（立即写语义），EF `OutboxDbContext` 刻意不标注。拦截器 `SavingChangesAsync`
> 据此分派：立即写族单次 `AddMessagesAsync` 批量（消息构造提取 `BuildOutboxMessage` 单一
> 来源，自清理域镜像 v34 P2 形态），EF store 与同步钩子维持逐条路径原语义。

收益（消费方可见的执行形态变化）：
1. **多事件保存批化**：立即写族 k 事件从 k 次单条写合并为 1 次批量（PG 单 COPY 往返），
   收益量级即上文 AddBatch5 基准（PG 3.5× / MySQL 3.8× / SQLite 3.1×）。
2. **异步路径解阻塞**：原 `SavingChangesAsync` 逐条调用同步 `AddMessage`，远程库上每事件
   对异步调用线程阻塞一个往返（PG ~609μs/条）；批量路径为真异步，消除线程池阻塞
   （吞吐影响为 [推断]，量级取决于并发与线程池压力，正确性由锁定测试保障）。

验证：锁定测试红→绿（`SavingChangesAsync_ImmediateStore_SingleBatchCallWithAllEvents`：
marker store 单次批量含全部事件、零逐条；同步钩子对 marker store 仍逐条）；**S3/红证明**
（翻掉分派因子后该测试转红、恢复后转绿，因子归因坐实）；非 marker 路径既有测试原样通过
（回归锁定）；16 面板全绿（快照 +2 行恰为新增接口）；公共 API 快照再生提交。

## v4.0 契约窗口实施（2026-10-10，ADR-026 ①②③④ 全部落地）

> 规划与设计裁决见 ADR-026；①scope 免除实测 167.8ns/160B → **108.1ns/32B**（分配 -80% 计时
> -36%，低于 MediatR 12.5.0 零行为口径的 117.6ns/224B）；②fencing 统一含实施期 refinement
> （Dapper MarkDead 守卫缺口 + EF 跟踪态守卫，详见 ADR-026）；③④接口异步化破坏面 129 调用
> 点全链转换（PalORM 7 处同步阻塞桥全部删除，sync-over-async 桥面收敛为拦截器同步钩子 1 处
> 文档化形态——G11 门禁以「ADR-026 标记门控豁免」放行该单点，意外桥仍拦）。实施期实录三则：
> ①G23 抓出快照/CHANGELOG 逐提交分离（①②重组为单提交修复，历史重组经推送状态核验为纯本地）；
> ②双公共构造在 MS DI 激活二义（IServiceScopeFactory/IServiceProvider 恒可解析，DI 测试实证）
> → 收敛单构造可选参 `Dispatcher(IServiceScopeFactory, IServiceProvider?)`；③批量重替换首轮
> 反引用语法错误污染 21 文件（serena `$!n` 语法），git 恢复后 dry-run 复核重做——批量机械
> 替换必须先 dry-run 的纪律再实证。全量验证：16 面板 15 过 + 1 全跳零失败、快照再生（差异恰为
> 异步签名面）、部署旋钮沉淀 [docs/deployment-performance.md](deployment-performance.md)。

## 测量面增强（2026-10-10 下午）：CAP 前置检查门 + strict 严格档

> 背景：上午全量复跑中 CAP 首跑以「连接重试风暴」形态产出 3s/op 静默垃圾且 exit 0（rabbit 凭据竞态：节点就绪前 add_user 被吞）。两项测量面增强根治该类问题并回应 Error 收窄需求。

**CAP 前置检查门**（`CapComparisonBenchmarks.GlobalSetup`）：真实 AMQP 连接预检 rabbit 认证，失败抛指向 runbook 的异常；配套 `--compare` 分支消费 BDN Summary（`Report.Success=false` 即 return 1——BenchmarkRunner 不因基准失败置非零退出码，曾使垃圾数字 exit 0 静默通过，这是重试风暴形态的另一半根因）。红证明：rabbit 不可达 + 错误凭据 → **exit 1、双基准行全 NA 零数字产出**、消息含既证 runbook（env 建用户 → 节点就绪后 change_password → authenticate_user 验证），快速失败替代 8 分钟垃圾。

**strict 严格档**（`--strict`，OutboxThroughput + SmartEnum 两类，迭代 15→40/预热 10→20/launch 2→3）：

| 基准 | medium（同日上午） | strict（同日下午） | 效果 |
|---|---:|---:|---|
| LeasePending_Batch100 | 94.7 μs（E13.2%） | **65.7 μs（E3.6%）** | Error 3.7×↓，均值回归账本真值 62.4μs |
| GetPending_Batch100 | 36.3 μs（E12.3%） | **22.8 μs（E0.5%）** | 24×↓，真值 22.4μs |
| LeaseAndMarkAll_Batch100 | 197.9 μs（E18.4%） | **124.6 μs（E0.6%）** | 30×↓，真值 126.6μs |
| AddSingleMessage | 4.30 μs（E16.2%） | **2.99 μs（E5.4%）** | 3×↓，真值 2.72μs |
| SmartEnum.FromValue_Known | 5.77 ns（E3.9%，历史带 11~25%） | **5.88 ns（E1.2%）** | 历史 25% 带问题在 strict 档终结 |

> **重要勘正（入账）**：上午全量报告 infra 车道「94.7/36.3/197.9μs」读数被 strict 复测证明为**采样噪声膨胀而非真实性能**（medium 15 迭代在忙窗口采到慢样本拉高均值，40 迭代×3 launch 回归真值）——2026-10-09 晚记录的 infra「计时上移 ~1.65×」漂移属同族现象，该轮结论修正为「medium 采样噪声」，非机器状态系统性变化。引用级数字一律以 strict 档为准（带档位标签）；medium 档定位为趋势与分配判定。

## Error 专项治理轮（2026-10-10 下午二：普查→归因→治理→验证）

> 方法：单位感知解析器扫两日全部 31 份日志（515 行读数/83 基准），跨轮聚合 >5% 频率；
> 按频率归因分类，逐族治理并实测验证。

**四族分类（频率降序）**：

| 族 | 成员（跨轮 >5% 频次） | 根因 | 治理 | 验证结果 |
|---|---|---|---|---|
| A 网络方差族 | PG Dapper GetPending（42 轮 13 次）、PG Drain/Seed 波段（28 轮 9~12 次）、CAP 侧 PalDDD（3 轮 3 次） | 网络往返抖动 + **多峰分布**（strict 实证：Drain_Sequential 中位 151 vs 均值 139、StdDev 76ms，BDN MultimodalDistribution 警告四处） | strict 加密采样 + 中位数口径 | 吞吐类收窄：GetPending E21→5.1%、Lease/AddSingle/AddBatch5/EventLog/Idempotency 全部 E<2%；**Drain 波段不收窄（E17~21%）——多峰是真实形态不是采样不足，引用一律中位数** |
| B 采样不足族 | infra OutboxThroughput 四项（AddSingleMessage g15×2 等）、SmartEnum.FromValue（历史 25% 带） | medium 15 迭代在忙窗口欠采样 | strict（前轮已落地，本轮全局化） | 四项 E12~18%→0.5~5.4%、FromValue E1.2%，且均值回归真值（前轮已录） |
| C 量纲下界族 | ValueObject 四项（E60~150%） | Mean ~0.001ns 处时钟分辨率除近零均值——BDN ZeroMeasurement 标注项 | 口径声明（不适用加密） | 账本已标注「值即 ~0、判定以零分配为准」，维持 |
| D 低频带族 | DapperConfig_CreatePg/Sharding/ReadStream/Catalog_FindByName 等（各 1~2 次 5~15%） | 单次忙窗口，无跨轮复现 | 不专项治理（复现时 strict 复测） | 本轮 medium 大多回到 <5% |

**治理落地**：①`--strict` 全局化——任意模式可叠加（`--persist-pg --strict` 已验证、`--infra --strict` 等，Program.cs ModeConfig 单点分派 + 远程方言传参）；②QPS harness 多轮中位——单轮 3s 改 3 轮取中位并报区间（dispatch 4T 轮间 ±0.5% 稳定；chain 8T ±11% 波动从隐藏变为实测，GC 平台期的真实形态）；③CAP 前置检查门（前轮）。

**PG strict 附加发现**：PalORM GetPending 无旋钮 strict 实测 **685μs（E4.5%）**——四条件序列（14.7 无旋钮 medium → 6.7 旋钮 medium → 1.5 旋钮 medium → 685μs 无旋钮 strict）说明采样深度是更主导变量，MaxAutoPrepare 结论降级为「加速到达稳态」待 strict A/B 定论（deployment-performance.md §2.1 已同步修正）。

**残余（登记）**：Drain 多峰波段（结构性，中位数口径已纪律化）；ValueObject 量纲下界（口径声明）；PG strict A/B（旋钮定论）；MySQL 族近两轮全 <8% 未加密（频率驱动的优先级裁决）。

## Error 专项治理轮二（2026-10-10 下午三：折叠修复 + 全模式 strict 验证 + 旋钮 A/B 定论）

> 上轮残余四项全部处置：量纲下界族修复、D 族 strict 压缩、MySQL strict 补测、旋钮 strict A/B 定论。

**① ValueObject 族修复（根因修正：非 DCE，是「快于可测量」）**：三项基准返回值本就被 BDN 消费（无死代码消除），真根因是 readonly record struct 单 int 尺寸全程寄存器化（0.0002~0.0017ns < 计时器分辨率）。治法：单 invoke 折叠 10 万次放大可测信号（⚠️ BDN 0.15.8 无 OperationsPerInvoke——DLL/XML 双源查证，防捏造——方法内循环 + 注释声明口径，读数单位=每 10 万次）：

| 基准 | 修复前 | strict 修复后 | 附带收获 |
|---|---:|---:|---|
| Create_Int | 0.0009ns（E88.9%） | 20.18μs/10万次（**E0.12%**） | — |
| ImplicitConversion | 0.0002ns（E150%） | 20.21μs/10万（**E0.17%**） | — |
| Next_RowVersion | 0.0010ns（E60%） | 20.29μs/10万（**E0.10%**） | — |
| IsTransient_Check | 0.0017ns（E64.7%） | 273.2μs/10万=2.73ns/op（**E0.32%**） | **首次可见真实形态**：56B/op 分配（类实体），零测量期不可见 |

**② D 族 strict 压缩验证**：infra 七项 5~18%→0.8~4.5%（ReadStream E3.1%、Sharding E1.3/2.8%、DapperConfig E4.5%）；app 全项 <1.5%（Catalog_FindByName E23.5% 带压到 E0.4%）；唯 LeasePending 残 E8.2%（中位 86 vs 均值 100，63KB/op 的 Gen0 停顿多峰倾向，归中位数口径族）。

**③ MySQL strict（频率第二方言族）**：吞吐六项 E1.3~5.1%（medium 带 2~10%）、Drain 波段 E1.3~5.8%、PalORM 全项 <5%——MySQL 族在 strict 下整体进入可引用带。

**④ PG 旋钮 strict A/B 定论（deployment-performance.md §2.1 已同步改写）**：稳态下旋钮对 PalORM 读路径无收益（685 vs 738μs）、写路径全面变慢（Lease 1.8×、EventLog 1.7×、Idempotency 2.1×、Dapper AddSingle 2.0×）、唯一改善 Dapper GetPending +34%。**结论：默认不启用**；历史 medium 档「-55% 收益」全部是欠采样期的冷计划压缩效应。

**残余（终态登记）**：多峰族（PG Drain 波段/infra LeasePending——结构性，中位数口径纪律化，这是测量学边界非缺陷）；CAP 侧 E6~16% 带未 strict（需双容器，频率低未排期）。**83 基准终态：strict 档下除多峰族外全部 <5.2%。**

## Error 专项治理轮三（2026-10-10 傍晚：Drain 多峰根因修复 + CAP strict——「结构性」定性被推翻）

> 前两轮把 PG Drain 波段定性为「结构性多峰、真实系统行为、中位数口径即完备处置」。本轮对该定性做根因假设检验并**推翻它**：多峰的慢模式是**基准方法学伪影**——清场用 DELETE，每波产生约 3200 死元组（800 插入+800×2 更新+800 删除），40 迭代 × 3 launch 累积触发 autovacuum 间歇干扰。改 TRUNCATE（即时回收、每迭代起始状态恒定）后同日 strict A/B：

| Dapper 并发（PG strict） | DELETE 时代 | TRUNCATE 时代 | 变化 |
|---|---:|---:|---|
| Drain_Sequential | 138.60ms（E16.9%） | **25.20ms（E1.9%）** | 快 5.5×、Error 收 9× |
| Drain_2Workers | 110.51ms（E20.7%） | 22.53ms（E4.3%） | 快 4.9× |
| Drain_4Workers | 77.75ms（E24.4%） | 23.07ms（**E0.4%**） | 快 3.4× |
| Drain_8Workers | 63.52ms（E15.6%） | 21.54ms（**E0.8%**） | 快 2.9× |
| SeedOnly_800 | 36.93ms（E3.4%） | 9.55ms（E1.6%） | 快 3.9× |

> **三重意义**：①Error：最高频的 A 族（Drain 波段跨两日 >15% 计 30+ 次）从根上消失，全项进入引用带（<4.3%）；②**性能真相**：PG 并发清空的真实耗时是 21~25ms——DELETE 时代的 63~139ms 含累积死元组扫描与 autovacuum 干扰，那是基准伪影非产品行为；PG Dapper 栈并发真并行的加速比在清洁状态下为 8W ~1.17×（25.2→21.5ms），此前「~2.8×」的量级来自带伪影的旧方法学，引用并发加速比须以 TRUNCATE 时代为准；③吞吐五项无回归（GetPending 527μs/Lease 65.5ms 与 DELETE 时代同带）。PalORM 波段同收窄（Seq E3.7→1.8%、8W E8.2→3.7%）。
> **勘正链**：治理轮一录「Drain 多峰=真实形态」→ 本轮证伪为方法学伪影。账本勘正：凡引 DELETE 时代的 Drain/SeedOnly 数字（含 2026-10-08/09 全部并发波段与「三形态对照」段的 PG 列加速比）须标注「DELETE 方法学时代，含死元组伪影」。
> 方法学变更声明：Drain/SeedOnly 与 DELETE 时代数字不可直接比；MySQL 远程车道同文件同改（下轮生效同规则）。

**CAP strict（最后一族补测）**：PalDDD_SerializeAndOutboxAdd **1.859ms（E3.4%）**（medium 带 E6~16% 终结）、CAP 侧 4.763ms（E3.0%）——双侧进引用带，比值 2.56×/分配少 57% 与 medium 时代一致。

**Error 终态（83 基准）**：strict 档下唯一残项 infra LeasePending（E8.2%，InMemory 测试栈的 Gen0 停顿多峰，中位数口径）——**其余全部 <5%**，其中大多数 0.1~4%。

**LeasePending 专项取证（治理轮三补录，「真实多峰」定性再修正）**：逐迭代 WorkloadActual 取证否证了 GC 停顿噪声假说——同文件同分配量的 LeaseAndMarkAll 全程平稳 137~147μs（E0.8%）却唯 LeasePending 有形态切换，且三 launch 形态各异（L1 第 20 迭代快→慢阶跃持续到底、L2 全程快、L3 慢→快），排除固定调用计数机制（分层编译/PGO 阈值应每 launch 同位）与 CPU 频率机制（同窗的 LeaseAndMarkAll 应同步变慢而未变）。**判别实验（DOTNET_gcServer=1 strict）**：E 8.2→5.4%，形态未消——GC 模式非根因。**定性收口：进程级形态切换（快 78μs/慢 145μs 双稳态，切换时刻随 launch 随机）**，机制未隔离（候选：workstation GC 分配上下文与后台线程交互/OS 调度迁移；隔离需 dotnet-trace 级取证，对测试夹具层基准性价比不足）。**处置：终态登记，引用中位数（86μs）**；历史四轮读数 65.7/94.7/100/95μs 均值漂移与中位差同源。教训与 Drain 案同款：先排除方法学（本轮已排除清场伪影与 GC 模式），剩余形态切换证据在案，不称「已修复」。

## 全量复跑轮（2026-10-10 晚：12 车道 strict 全量真跑 + v4.0 发布对比总表）

> 背景：v4.0 契约窗口（ADR-026 四项 + 拦截器批化）落地后的首次全量 strict 真跑，双目标：全车道数字刷新 + 发布说明单表融合（CHANGELOG `[Unreleased]` Documentation 节）。环境：Win10 x64 / .NET 11.0.0-rc.1 / BDN 0.15.8 InProcess strict（40 迭代 20 预热 3 launch；coverage 例外见发现②）；wslc 一次性容器 postgres:18.6（15432）/ mysql:8.4.11（13306）/ CAP 专用 mysql 8.4.11（13307）/ rabbitmq:4.3.6-management（15732——5673/5675 撞 Windows TCP 保留段 5606-5705 被 WSAEACCES 吞掉，AMQP 口改 15732；rabbit env 凭据未生效按既证 runbook `change_password` 显式重置后 `authenticate_user` 通过）。12 条车道全部 exit 0（`--compression` 含于 `--infra` 车道；`--persist-medium` 锚点本轮未跑）；除下列发现/偏移外，全部读数与 strict 账本同带。

**发现① MySQL 多表 TRUNCATE 基准缺陷（本轮修复 cbf68c5）**：治理轮三迁移（61e3c79）把远程车道清场 DELETE→TRUNCATE 时注释称「PG/MySQL 均支持 TRUNCATE 多表语法」——**有误**：多表逗号形态是 PG 专有，MySQL 的 TRUNCATE 仅接受单表。本轮 `--persist-mysql --strict` 首跑实证：Dapper 吞吐（6 项）与 PalORM（8 项）两类 GlobalSetup 即抛 SQL 语法错（near ', events, idempotency_records'）全 NA（并发类与 SKIP LOCKED 类清场单表不受影响、正常出数）；PG 同语句正常。修复=逐表执行（`ClearAllAsync` 三表 / `ClearAuxTablesAsync` 两表改循环），修复后重跑 4 类零 NA exit 0。教训：方言能力假设须双方言真跑验证——「下轮生效」的变更首跑即验证点，昨日若同日跑 MySQL 就不会把错误注释带过夜。

**发现② coverage × strict 车道级不兼容**：`--coverage --strict` 首撞 InProcessEmitExecutor 单基准 5 分钟硬上限——Projection 类 ns 级 op 被 BDN 自动放大至 1,048,576 ops/迭代 ×（20 预热 + 40 迭代）× 3 launch 全在单进程内串行，超限抛「takes too long to run」整车道中止。处置：coverage 维持 medium 档（本轮 medium 5 类全过，数字与账本同带）。strict 全局化的适用边界自此明确：慢基准类（ms 级 op）不受影响，ns 级快类被自动放大迭代倍数时受限——`--strict` 全局化承诺不含 coverage 车道。

**发现③ MySQL TRUNCATE 时代首组并发数字（方法学时代注）**：TRUNCATE 在 MySQL 是 DDL（隐式提交 + 表重建），逐迭代清场成本远高于 PG——SeedOnly_800 从 DELETE 时代 18.7ms 升至 **110.8ms**（PG 同语义 9.4ms 与 DELETE 时代持平），Drain 波段绝对值同被抬高（串行 207.6ms vs DELETE 时代 61.3ms）。**两时代绝对值不可比**（PG 侧同款勘正纪律的 MySQL 延伸）；同轮内并发比值有效：8W/串行 = Dapper 0.85 / EF 形态 0.85 / PalORM 0.86，**死锁零复现**（ADR-025 SKIP LOCKED 双模板在 strict 40×3 采样深度下互斥不变量全波通过）。

**方向性偏移登记（待归因 [推断]）→ 已归因收口（2026-10-11 优化实施轮 M1，本段为登记原文，结论见下方「T0.1 归因结论」）**：PalORM 远程 EventLog/Idempotency 双方言一致约 2× 慢于 10-09 账本——EventLog PG 2,187.9μs（账本 1,129.7）/ MySQL 5,126.1μs（2,157.5）；Idempotency PG 3,210.1μs（1,371.2）/ MySQL 8,963.9μs（3,587.4）。同轮 Dapper 侧无此形态（EventLog PG +15% / MySQL +26% 属环境带），Dispatcher（188→155ns）与 InMemory EventLog（6.8→4.2μs）反向变快。v4.0 全链异步化后首次全量 strict，候选=异步化/拦截器契约分派对 PalORM 路径的副作用；**未做专项 A/B，不构成回归结论**，登记待单独立项归因。

**T0.1 归因结论（2026-10-11，三重判别实验，定性：参照系勘正，无回归）**：

| 判别 | 实验 | 结果 |
|---|---|---|
| ① 清场语句单变量 A/B | 同日同容器，TRUNCATE→DELETE 临时改（S3 后还原） | 只回收 15~20%（PalORM EventLog 2072→1723μs、Idempotency 3335→2876μs）——**否证 TRUNCATE 主因假说** |
| ② 参照提交本尊复跑 | worktree 检出 33f4dba（治轮二手记 1022/1425μs 的代码状态：DELETE 形态 + PalORM 6.3.1）当天 strict 重跑 | **2273/3133μs**——与本轮 dev 三次独立读数（2072/2188/2273）同带，**1022/1425μs 在其本尊提交上不可复现** |
| ③ 环境稳定性对照 | Dapper 锚点跨三天比对 | 逐字节稳定：EventLog DELETE 822.0μs ≈ 优化轮三 strict 820μs；AddSingle 630.4 ≈ 630μs——排除机器/容器环境漂移 |

- **定性收口：无产品回归、无上游包回归（PalORM 6.3.1↔6.4.0 同带）——「2× 偏移」是对不可复现参照系的误比**。治轮二旋钮表无旋基线（1022/1425μs，n=1，双容器轮）与 10-09 medium 原值（1129.7μs）均不可复现；可复现稳态 = PalORM EventLog ~1.7ms（DELETE 清场）/ ~2.1-2.3ms（TRUNCATE）、Idempotency ~2.9ms（DELETE）/ ~3.1-3.3ms（TRUNCATE）。
- **结构性结论转正**：PalORM EventLog/Idempotency 慢 Dapper 约 2×（同日 DELETE 对照 1723/2876 vs 822/1394μs）为真实差距，形态根因 = PalORM 追加仍是 MAX 预查+INSERT 两往返（`PalOrmEventLog.cs` 类头声明）vs Dapper PG 单语句快路径——**T2.2 移植单语句 Any 追加预期回收差距的大部，优先级上调**。
- **方法学登记（TRUNCATE 侧效应，引用须带清场形态标签）**：TRUNCATE 使 aux 表（events/idempotency_records）逐迭代页冷化，EventLog/Idempotency 较 DELETE 形态系统性 +8~28%（Dapper EventLog 822→1051μs +28%、PalORM EventLog 1723→2072 +20%、PalORM Idempotency 2876→3335 +16%）；Drain/SeedOnly 波段维持既有两时代声明。
- **勘正链**：全量复跑轮「偏移登记待归因」→ 本轮证伪为参照系问题。治轮二旋钮表（deployment-performance.md §2.1）的 EventLog 1,022μs / Idempotency 1,425μs / Lease 98.0ms 三格无旋基线为**不可复现读数**（2026-10-11 判别）——旋钮 A/B 属同轮对内比较，**方向性结论（写路径变慢 1.7~2.1×）不受影响**，绝对基线值不再引用。

**远程双栈吞吐（strict，本轮读数）**：

| 基准 | PG Dapper | PG PalORM | MySQL Dapper | MySQL PalORM |
|---|---:|---:|---:|---:|
| Outbox_GetPending_Batch100 | 481.4 μs（E2.5%） | 493.1 μs（E2.0%） | 658.3 μs（E2.2%） | 797.9 μs（E2.9%） |
| Outbox_Lease_Batch100 | 63.2 ms（E1.0%） | 174.7 ms（E0.7%） | 172.0 ms（E0.7%） | 534.0 ms（E1.0%） |
| Outbox_AddSingle | 630.4 μs（E1.6%） | — | 1,730.6 μs（E2.2%） | — |
| Outbox_AddBatch5 | 846.4 μs（E1.9%） | — | 2,073.8 μs（E2.2%） | — |
| EventLog_AppendSingle | 1,051.1 μs（E2.3%） | 2,187.9 μs（E6.5%） | 2,549.3 μs（E2.5%） | 5,126.1 μs（E7.2%） |
| Idempotency_TryStart_Completed | 1,522.6 μs（E1.5%） | 3,210.1 μs（E8.7%） | 3,871.8 μs（E1.9%） | 8,963.9 μs（E5.5%） |

（与 10-09 账本对照：Dapper 六项全部同带；PalORM Outbox 三项同带（GetPending 685→493μs 属跨日带）；PalORM EventLog/Idempotency 见偏移登记。AddBatch5 批化收益：PG 846.4μs vs 5×单条 3,152μs = 3.7×、MySQL 2,073.8μs vs 8,653μs = 4.2×，与优化轮 C2' 的 3.5×/3.8× 同带。）

**并发形态（strict，比值 = 相对各自串行；清场 PG/MySQL 均为 TRUNCATE 时代）**：

| 栈/库 | 串行 | 8W | 8W/串行 | 零重复不变量 |
|---|---:|---:|---:|---|
| PG Dapper（TRUNCATE 时代第二轮） | 23.24 ms | 20.71 ms | 0.89 | 全波通过 |
| PG PalORM | 32.11 ms | 20.36 ms | 0.63 | 全波通过 |
| MySQL Dapper（TRUNCATE 时代首组） | 207.6 ms | 174.1 ms | 0.85 | 全波通过、零死锁 |
| MySQL EF SKIP LOCKED 形态直跑 | 207.0 ms | 174.8 ms | 0.85 | 全波通过 |
| MySQL PalORM | 204.8 ms | 176.0 ms | 0.86 | 全波通过 |
| SQLite Dapper（本地车道） | 5.57 ms | 160.0 ms | 28.7 | 全波通过（busy 平台期） |
| InMemory 簿记层 | 391.5 μs | 602.0 μs | 1.54 | 全波通过 |

（PG 形态与治理轮三 TRUNCATE 时代结论一致：清洁态并发增益有界（~1.1×）；MySQL 首组 TRUNCATE 时代比值 0.85 与 PG 同款「种子/清场主导、并发增益有界」形态。）

**其余车道与账本核对结论**（关键项，全表见会话原始快照）：MediatR 四点同带（零行为 173.1ns/160B vs 134.5ns/224B——MediatR 计时快 22%、本仓分配少 29%；单行为 268.9/432 vs 224.3/528——快 17%/少 18%）；CAP 双点同带（2.035ms/10.38KB vs 4.788ms/25.02KB，快 2.35×/分配少 58%，与治理轮三 strict 1.859/4.763 同带，PalDDD 侧带 Multimodal 警告 mValue 2.9 引中位 1.920ms）；框架微基准同带（AddSingle 116.0ns/72B、Create 3.65ns/56B、SmartEnum.FromValue 6.17ns 零分配、折叠三项 20.06~20.14μs/10 万次 E≤0.2%、IsTransient 2.55ns/56B）；infra 30 项同带（GetPending 21.7μs E0.5%、AddSingle 1.90μs、LeasePending 中位 63.7μs、LeaseAndMarkAll 中位 123.9μs——两项多峰族中位口径维持；Inbox 155.9~221.6ns、EventLog 追加 4.17μs、压缩五算法 LZ4 5.78μs/376B、ZStd 6.26μs/304B 池化形态维持）；app 同带（Dispatcher 154.6/236.2/106.3ns——实例路径 106.3ns/32B 与 ADR-026 ① 实测 108.1ns/32B 同带；序列化 73.0/303.9ns、Catalog 7.77/2.17ns 零分配）；SQLite 三栈（Dapper AddSingle 41.6μs / Lease 1.93ms、PalORM 同带、EF 2.2~4.3× 慢——「SQLite 栈 PalORM≈Dapper 反超结论不外推网络库」维持）；coverage（medium）5 类同带（端到端 466.1/581.3ns、Hosting MapCommand 2,500.3ns/3.45KB、Specification 1.67ns 零分配）；Saga（Normal 168.7ns/368B、FanOut(4) 3.13μs/3,064B）；QPS（dispatch 1T 5.4M / 4T 20.3M / 8T 34.8M ops/s、chain 1.0M/1.6M/1.8M——**chain 4 线程平台期结论第三次复现**）；PG 方言特性（Pipeline 批插 6.18ms vs 逐条 139.7ms = **22.7×**，刷新历史区间 8.6~16.2× 的上界——Pipeline 侧 8.8→6.2ms 变快、对照侧持平；Router 42ns；LISTEN-NOTIFY 454μs 中位 413μs）；smoke 4/4 预算内退出码 0。

**发布对比总表**：单表融合版入 CHANGELOG `[Unreleased]` Documentation 节（v4.0 发布随 tag 转正为发布说明），口径注与本段互引，不在此重复。

## 优化实施轮（2026-10-11：包升级全量 + T0.1 归因 + T0.3 纯租约量具 + T1.2 自适应排空）

> 背景：用户令「升级包至最新 + 按优化方案完整实施」。本段承接上表任务清单 M1/M2。包升级（PalORM ×5 → 6.4.0、CAP ×3 → 10.0.2、TUnit 1.73.19、Testcontainers 4.16.0、NativeCompressions 1.1.0；MediatR/Verify.TUnit 维持许可钉扎，f57076b）经 16 项目面板 0 失败验证，PalORM 三契约 6.4.0 XML 复核在案，全部车道数字与升级前同带（PalORM EventLog 2072μs ≈ 升级前 2188μs 同带）。

**T0.1 归因结论**：见上段（参照系勘正，无回归；PalORM 2× 为结构性差距 → T2.2 优先级上调）。

**T0.3 纯租约量具（LeaseOnly_100：IterationSetup 保证 100 行 pending，基准体只租不灌）**：

| 栈（PG strict，TRUNCATE 清场形态） | Lease_Batch100（含重灌，历史口径） | **LeaseOnly_100（纯租约）** | GetPending_100（同批读对照） |
|---|---:|---:|---:|
| Dapper | 66.0 ms | **1,687.5 μs（E1.8%）** | 532.1 μs |
| PalORM | 172.1 ms | **2,338.1 μs（E5.6%）** | 505.7 μs |

> 结论：①历史 Lease_Batch100 读数的 **97%+ 是体内重灌 100 单条的噪声**（Dapper 66.0ms 中租约仅占 1.7ms、PalORM 172.1ms 中占 2.3ms）——T2.1 批量标记的收益锚点应以 LeaseOnly 为基线；②纯租约双栈差 **1.39×**（远小于 Lease_Batch100 口径暗示的 2.6×——重灌路径放大了栈差）；③租约为同批读的 3.2~4.6×（100 行版本写入 + 全列 RETURNING 回读的真实成本）；④清场形态标签：IterationSetup 每迭代 TRUNCATE + 重灌，租约命中的是冷化页——DELETE 形态读数会更低（见 T0.1 段 TRUNCATE 侧效应）。

**T1.2 自适应排空（代码 + 锁定测试，吞吐量化待 T0.2 量具）**：`PeriodicBackgroundProcessor` 循环改为「上轮满批 → 1ms 让步续批；否则按间隔」，`OutboxOptions.PollInterval` 改每轮热读（冷快照声明废止，XML doc 同步）。锁定测试三件（满批 1ms 续批 / 未满批守间隔 / 热读生效）改前红证明：旧循环下满批件与热读件结构性红、不变量件绿；新码三连绿零抖动（206/206 ×3）。公共 API 快照 +1 行（`ProcessBatchWithBacklogAsync`）。**积压排空吞吐预期从 100/(tick+500ms) 升到 100/tick**——绝对数字待 T0.2 投递吞吐量具落成后同轮实测入账。

**T0.2 投递吞吐量具（`--delivery`，SQLite 真库标记 + 假 broker RTT 可配 + 真序列化/catalog，基准体 = 排空 1000 条积压；专用采样档 5 迭代/3 预热/1 launch，多秒级 op 撞 InProcess 5 分钟上限故 medium 不可用）——T1.2 收益定论 + T2.1/T3.1 基线**：

| 基准（口径见类头注） | Mean | 换算吞吐 | Error |
|---|---:|---:|---:|
| RTT=0 自适应排空（节奏类，批间 1ms） | **90.4 ms** | **≈11,000 msg/s** | E63%（首迭代 JIT 带，min 78ms） |
| RTT=0 旧固定间隔排空（节奏类，批间 500ms） | **5,071.7 ms** | ≈197 msg/s | E1.5% |
| RTT=1 自适应排空 | 10,012 ms | ≈100 msg/s | E67%（采样浅，趋势级） |
| RTT=20 自适应排空 | 25,525 ms | ≈39 msg/s | E3.0% ✓ |

> **T1.2 端到端定论：积压排空 56×（5,072ms → 90ms，RatioSD 8%）**——旧形态下 5.07s 中 4.5s 是纯批间睡眠，批处理净工作仅 ~80ms（9 批 × ~9ms）；自适应形态吞吐上限被真实工作接管。RTT=20 档的 25.5s 中 20s 是逐条发布确认的串行等待（1000 × 20ms）——**T3.1 流水线化的理论收益上限即此（~4.6×）**；RTT=0 档的 ~90μs/msg 净工作即 **T2.1 批量标记的收益锚**（现标记段为逐条 UPDATE）。首跑实录：medium 采样撞 InProcess 单基准 5 分钟上限（与 coverage×strict 同族的车道级不兼容，多秒级 op 专属），改专用采样档后双类 exit 0 零守卫违规。

> **2026-10-19 更新（ADR-033，自动投递策略）**：`OutboxOptions.DeliveryMode` 默认 `Auto`。broker 声明 `SupportsConcurrentPublish` 时窗口自动提升到至少 16；store 实现 `IBatchMarkOutboxStore` 时标记阈值至少 16，并由 `BatchMarkFlushInterval`（默认 1 秒）加时间上界；内置四栈的即时写库使 `SaveChangesAsync` 空调用被跳过。本节此前的“默认 1”数据仍作为串行基线；Auto 模式的组合收益需要在 `--delivery-pg` / `--delivery-mysql` 上重跑 A/B，未重跑前不把 13× 与 6.4× 直接相乘。`Serial` 可逐字复现旧行为。

**T3.1 发布确认流水线化（`OutboxOptions.MaxInFlightPublishes`，默认 1 = 串行逐字不变；>1 启用 FIFO 窗口，标记顺序保启动序，失败语义逐项镜像串行路径，锁定测试三件三连绿）——量具 A/B 定论（窗口 16）**：

| RTT（假 broker 确认延迟） | 窗口 1（串行基线） | 窗口 16（流水线） | 收益 |
|---|---:|---:|---:|
| 0ms | 98.2 ms | 105.8 ms | 无损无益（RTT 为零时窗口无意义；分配 4.24→4.35MB +2.6% 为窗口队列与任务包装） |
| 1ms | 10,052 ms | **776 ms** | **12.9×** |
| 20ms | 25,647 ms | **1,971 ms** | **13.0×** |

> **定论：broker 确认往返主导的投递形态下，流水线窗口收益 ~13×**（超过 T0.2 时估算的 4.6× 理论上限——窗口跨整批预发 + 标记与发布重叠的双重效应；RTT=1 档的串行基线 10s 系 Windows Task.Delay(1ms) 实际粒度 ~10ms 所致，相对收益不受影响）。同轮节奏对照复测：Adaptive1ms 88.75ms vs Fixed500 5,080ms（57.3×，与 T0.2 定论同带）。**推荐形态：远程 broker 部署开窗口 16~64（消费方具备 Inbox 幂等前提；Kafka 须 enable.idempotence 保序，语义声明见 OutboxOptions.MaxInFlightPublishes XML doc）**；本地/零延迟 broker 维持默认 1。

**CAP 10.0.2 换锚复测（2026-10-11，随 f57076b 升级轮）**：CAP 4.734ms（E2.7%）/ 25.21KB vs PalDDD 1.854ms（E3.4%）/ 10.05KB——**快 2.55×、分配少 60%**；与 8.4.1 锚（4.788ms/2.35×）同带，**结论跨 CAP 主版本 8→10 稳定**，引用基准自此换为 10.0.2。

**T2.2 移植否决（PalORM EventLog PG 单语句 Any 追加——零收益回退，防重复提议留档）**：照 Dapper 侧同款实现 INSERT..SELECT..RETURNING 快路径（Ulid 裸传 Npgsql 参数首跑即抛 InvalidCastException，修为字符串后快路径确认激活——分配形态变化 10.2→11.4KB 为证），PG strict 实测 **2,188μs（E10.8%）vs 两往返原路径 2,072~2,273μs 同带，零收益且分配略高 → 回退**。**附带勘正（T0.1 转正结论修正）**：单语句不降耗时直接证伪「PalORM 慢 Dapper 2× 的主因是两往返形态」——真因是 **PalORM 会话路径的每往返开销本身**（同库同 SQL：PalORM 单往返 ~2.2ms / 双往返 ~2.2ms vs Dapper 单往返 ~0.5ms、双往返 ~0.98ms），方向在 DataSession 层或基准夹具连接形态（LeaseOnly 同证：PalORM 单往返租约 2.3~2.6ms vs Dapper 1.6~1.7ms），未隔离——归 PalORM 上游适配层调查项，本仓库内无改靶（对齐 P7/P8 探针否决先例：否决即回退不留死代码）。Dapper 侧快路径维持不变（其 -15% 收益在往返主导形态成立）。附带沉淀：Any 版本跨方言行为等价测试三件 + EventId 重复分类测试入 PalORM.Tests（对原路径同样有效的回归网）。

## EPSL 修复轮（2026-10-11 晚：地毯审计 A/B/C 批 15 项落地，报告 docs/review/epsl-carpet-2026-10-11.md）

> 背景：同日地毯审计轮（222/222 深读、18 候选）后用户令「根据报告完整修复」。A 批 3 + B 批 6 + C 批 6 落地（B7 投影窗口化检查点移交——特性级变更需重建驱动配合；C 项 #12/#13/#16 登记不做带理由，见报告修复轮段）。全部行为等价变更配锁定测试（分配断言旧码红→新码绿，或行为锁定含启用态对照）。

**A3 Brotli 解压池化**（接入 DecompressBufferPool，池上移 PalDDD.Compression 单一事实源 + Native IVT 消费）：

| 基准（--compression，16KB 负载） | 改前（账本 strict 锚） | 改后 | 差 |
|---|---:|---:|---|
| Brotli_Decompress | 45,185 B（13.8~14.4 μs） | **16,440 B / 13.75 μs** | 分配 **-64%**，计时同带 |
| 对照：LZ4/ZStd_Decompress（既有池化形态） | 16,488 B | 同带 | 逐字节同量级——三算法解压分配形态归一 |

**B4 EventLog k>1 PG 批量化**（多行 VALUES+RETURNING 单往返；postgres:18.6 wslc 同容器同日配对 A/B，行为守卫：版本跨度==k-1 内嵌基准断言）：

| 基准（PG medium 配对） | 改前（逐条往返） | 改后（批路径） | 差 |
|---|---:|---:|---|
| EventLog_AppendBatch5（k=5） | 3,726.2 μs / 48,224 B | **1,345.2 μs / 42,216 B** | **快 2.77× / 分配 -12%** |
| 对照：EventLog_AppendSingle（k=1，未触碰路径） | 1,039.9 μs | 1,105.0 μs | 同带（日内噪声） |

> 与 Outbox AddBatch5 批化锚（PG 3.5×/MySQL 3.8×）同量级；k=5 批内 5 次往返收 1 次的机制印证。方言门控：k=1 与 MySQL/SQLite 维持原路径（EventLog Any 快路径三方言实测先例同款口径）；冲突分类等价（单语句原子 + MaxVersion 重查分类，方言探针补批内 EventId 重复断言，CI 双方言跑）。

**单元分配锁定（旧码红→新码绿，仪器含正例对照）**：FromHeaders(null) 0 B（旧 ~80B/次）；Brotli 解压预算 18KB/op（旧 45KB）；Guid ID JSON 读 D 格式 0 B（旧 ~90B/次，N/B/P 回退腿行为锁定）；DomainEventDispatcher 单事件 ≤700B/迭代（旧 851B——构成：宿主 ActivityListener 下真实 Activity ~440B + Debug async 状态机类 ~140B，生产无监听为零，bench app 车道 154.6ns/160B 佐证）；InboxProcessor 禁用态零 Information 调用（启用态双路径照常的对照锁定）。

**B1/B3（Saga 存储往返结构，机制入账无计时 A/B）**：B1 批量释放把超时扫描非超时分支的 B 次「往返 + 全量快照序列化（Dapper 叠加存在性全行反序列化 / EF 叠加 DetectChanges 扫描）」收为单语句单往返（leased_by 守卫 + 换代；EF 栈 ExecuteUpdate + 全批 Detach——跟踪器幽灵写陷阱的行为锁定测试在案）；B3 把 Save 常见路径 2 往返收 1（UPDATE 先行 + 0 行 COUNT 探测，新行冷路径 2→3）。四栈 SQLite 真库行为测试 + 处理器接线红证明（旧处理器零调用能力接口）在案；EF 栈车道待上游 Provider（既有登记），SQL 计数探针属其解除后的补测项。

## 全量重读性能实施轮（2026-10-14：R1~R5 五批，含三项 P0 结构修复）

> 来源：全量重读性能审计（本人精读五扇区 + 四只读子代理扇区深读，热路径 100% 覆盖）。
> 产出方案四级分级后按序实施，本段只记实测与机制入账；细节见 CHANGELOG [Unreleased] 与 ADR-027。

**R1 快赢族九项**（9a3b59e）：Start* 遥测入口无 listener 零分配（smoke 新增两断言，**红证明 stash 循环在案：旧码 24.000/24.002 B/op 装箱判红、新码 0.002/0.000 过**）；状态机 next 委托每请求 1 笔（原每行为 1×~64B）；Dispatcher 行为物化数组协变直取（免 Select+ToImmutableArray 链）；LoggingBehavior Error 门；LZ4/ZStd options 三档缓存；单字节 IndexOf；Dapper MySQL 版本判定 CWT 缓存；InMemory Outbox Processed 终态清除（治无界增长）；EF Inbox AsNoTracking/EF Saga 字典哈希去排序/EF MySQL 回读 LIMIT。撤销留档：SQLite WAL 一次化（文件重建边界 + <5% 纪律）。

**R2 CQRS 管道分配族**（98e31e0）：内置行为显式非泛型实现（免 DIM 桥闭包+Func）+ Unit 单例箱（typeof JIT 折叠）。--app 实测（MediumRun，对照基线 2026-10-11 轮 268.9ns/432B）：**单行为 225.5ns/360B（计时 -16%、分配 -16.7%）**；零行为 160B 持平（该基准引用类型响应无装箱路径，符合预期）。

**R3 Saga 步骤分配族**（e23d565）：四车道计时静态化 + failures 惰性建 + TryAdd。--saga 实测：**Normal 368→296 B/op（-19.6%）、FanOut 3,064→2,991 B/op**（Stopwatch ~40B + 空 List ~32B 两笔预测恰好兑现）；计时 192.2ns 跨轮漂移带内。撤销留档：RunRetryLane 闭包重构与 MakeKey 缓存（DB 主导 <5% + 行为最密集文件风险）。

**R4 Outbox 终态批量标记**（ADR-027，P0-2/T2.1 锚立项）：`IBatchMarkOutboxStore` 能力接口 + `BatchMarkThreshold` opt-in（默认 1 逐条逐字不变）。机制：成功路径标记往返 N 次 → 组数次（按 RetryCount 分组单语句，fencing 守卫与逐条逐字等价零窄化）；fencing 部分拒绝行为锁定（Dapper/EF/PalORM SQLite 真库 raw 行断言）；处理器批序=租约序（T3.1 保序族）。量级锚：T0.2 已实测 RTT20ms 逐条确认串行化占 80%——批量启用后标记侧串行相乘收敛（2026-10-15 --delivery 车道 A/B 补测：本地 SQLite 端到端零显著差，标记段 μs 级占排空 ~8% 是成因；收益形态在远程库 RTT——见「R4 补测」段）。**PalORM 栈 2026-10-16 补齐**（ADR-027 延后段清算）：until 编码三方言探针（SQLite 本地 + PG/MySQL wslc 外部实例）实证库内形态 SQLite TEXT 100ns 全精度 / PG timestamptz μs / MySQL DATETIME(6) 无 offset 裸 UTC，回读值重编码均精确命中（正腿全批 + until±1s/换 owner/retry+1/已终态四负腿全拒）；同值以 DateTime 形态传递三方言全拒（编码形态敏感）。实现按已定案分组单语句形态，PalORM 每插值孔绑一 DbParameter 的约束经 `FormattableStringFactory.Create` + `{N}` 索引孔满足（PalOrmEventLog A11 先例）。

**R5 EF EventLog Hi/Lo 修复**（848e839，P0-1）：版本校验+位置预留前移出事务。机制入账：稳态追加 **4 次 DB 往返（原恒 6，-33%）**、1/N 追加付分配器 2 次、GlobalPosition 连续无稀疏（原按 chunk 稀疏 100×）。**红绿双向锁定**（SQLite 真库 ChunkPositionsConsecutiveWithinChunk：stash 旧码 +100 稀疏判红 Expected 1 found 100、新码 +1 连续绿）；ITM-226 场景 (a) 化正确性声明在案（事件事务回滚只留间隙，既定可接受）。

**移交后续轮**（本方案已定设计、未实施）：P0-3 回放高水位（per-position 检查点的连续前缀语义 + 四栈接口，需 ADR）；P0-4 EF Saga 租约方言下推（ExecuteUpdate 四方言）；P1-1 序列化 IBufferWriter 直写（公共 API 扩面 + 三 broker 联动）；P2-1/2/3 池驻留三项；微项打包（ISpanFormattable/Analyzer 零分配/五处 IsUniqueConstraintViolation 收口等）。

## 全量重读性能实施轮续（2026-10-14：R6/R8/R9）

**R6 投影增量回放续读**（8f4ba70，ADR-028，P0-3）：双能力接口（`IResumableProjectionCheckpointStore` + `IResumableEventReplaySource`）把 `ReplayAsync` 从 **O(全流长度) 降为 O(增量)**（已完成前缀零 payload 重读、零逐事件 checkpoint 探查）。语义三态=最小未完成洞位（保「仅处理缺失/失败检查点」契约逐字一致）/全完成 max+1/无行 null；EF 实现实测发现 SQLite provider 不翻译 `Convert.ToInt64(string)`（TRANSLATION-PROBE 判红），落最小投影两列客户端聚合回退。**变异红证明在案**（砍续读分支 RequestedFromPosition/OfferedCount 判红）。九件锁定（Integration 386/386）。

**R8 序列化直写**（9900fae，ADR-029，P1-1）：`IMessageSerializer` 加 `Serialize(object, descriptor, IBufferWriter<byte>)` DIM（自定义实现零迁移）+ Json 覆写真直写（池化 Utf8JsonWriter Reset(destination)，输出逐位一致）+ RabbitMqBroker TLS 直传 WrittenMemory——消除每消息全载荷 ToArray 分配+拷贝。**变异红证明**：覆写体替换回退 → 零分配断言判红（56B/op 载荷拷贝）恢复绿。Kafka 不改（byte[] 契约）；MemoryPack 暂不覆写（1.21.4 无 (writer, Type, object) 重载，本地编译实证）。调用形态约束：DIM 经具体类型不可见（CS1501 实证）。

**R9 池驻留 + 微项**：① TLS 缓冲阈值弃槽四处（JsonMessageSerializer 两路径/JsonLinesEventWriter/RabbitMq Broker body——历史峰值 >256KB 下次入口重建，驻留从「历史峰值」收敛为有界）；② 解压首轮 GetSpan 提示上限 64KB（`DecompressionGuard.InitialBlockHintBytes`，Brotli+LZ4/ZStd/OpenZL 四处——原 max(4096, len*2) 在 8MB 输入下首轮 16MB 过量提交固化进线程槽峰值；改后峰值容量=「≥ 实际输出的最近倍增」）。撤销两项留档：**D-F9 ISpanFormattable 生成 ID 实测证伪撤销**（net11-rc1 三形态对照：插值 111 / ToString 80 / 手动 TryFormat 80 B/op——DefaultInterpolatedStringHandler 对结构体经 ISpanFormattable 的装箱未消除，插值反向劣化 +31B；勘正注释留生成器 ToStringBody 处防重开）；RabbitMQ per-delivery CTS 池化（linked CTS 无法 TryReset 复用，语义保持需自定义 CTS 子类，~72B/消息不值）；五处 IsUniqueConstraintViolation 收口（跨 4 程序集 InternalsVisibleTo 链 + 冲突冷路径，收益不成立）。Analyzer 零分配化（D-F5）移交（IDE/CI 编译期面，独立轮次）。

## 全量重读性能实施轮续二（2026-10-15：R7 Saga 租约 SQLite 单语句下推）

**R7 EF Saga 租约单语句下推**（P0-4 移交项清算；决策论证 docs/review/decision-2026-10-15-saga-lease-sqlite-single-statement.md）：`SagaStateDbContext.LeaseActiveSagasAsync` 在 SQLite provider 下改走集合式单语句 UPDATE（资格谓词子查询 + ORDER BY + LIMIT + `version=version+1` 同句换代）+(owner, until) 等值守卫回读——**整批固定 2 条语句与批次大小无关**（原跟踪分页形态：一页全实体物化查询 + N 条逐行 UPDATE，头部活跃租约堆积时叠加 O(跳过页) 翻页往返；含 saga_data 大列的未选中行同样被物化）。**语句计数判别器红证明在案**（旧码批 5 → 5 条 UPDATE 判红、新码 1 条绿）；语义锁定三件（活跃租约跳过/过期接管换代/AWD 纳入）+ 饥饿消除（batchSize=1 头部占满仍取窗口外）+ 跟踪契约（补偿路径 `ISagaStateStore.SaveChangesAsync(state)` 直存端到端）；ITM-632 幽灵租约用例迁 InMemory 锁定回退路径保护。**范围收敛声明**：EF 栈 rc.2 线无 PG/MySQL provider 包（本地/CI 均无真库车道），方言下推留待车道（PG 形态=标识符引用差异、MySQL 需 JOIN 两步，SQL 形态已在案）；其余 provider 保底路径逐字不变。验证：Release 全量构建 0W0E + 16 项目面板 **1581 = 1509 过 + 72 跳 0 失败** + format-verify/encoding-gate 绿。

## 全量重读性能实施轮续三（2026-10-15：R10 Analyzer 零分配化 D-F5）

**R10 分析器符号比对族零分配**（D-F5 移交项清算，IDE 增量分析编译期面）：`StrategicDddAnalyzer` 热路径谓词去分配——**实测红绿双向**（旧形态等价体注入判红 → 新实现绿，B/op）：`MetadataNameEquals` **68.8 → 0.0012**（原每次比对经 GetFullMetadataName 构造全名串；改造中发现 Roslyn `MetadataName` 属性对泛型符号每次访问重建 arity 串——探针实测 40B/次，连结构比对自身最初也被该属性判红，改 `Name`+`Arity` 逐段匹配闭环）；显式接口实现按名查找 **80.0 → 0.0024**（闭包 Any → 手写循环）；`BelongsToBoundedContext` **40 → 0**；`HasVersionSuffix` **32 → 0**（拼接 → 字符级对照，int 全域等价）。每类型分析数十次此类比对（基类链/接口集/attribute 集逐候选），据此去分配面随编译期间类型数线性。语义矩阵由 `AnalyzerAllocationTests` 锁定（嵌套名/顶层碰撞/泛型元数/global 命名空间/前导零版本/负号腿）+ 既有 47 用例零回归；载具为 InternalsVisibleTo 测试程序集直调内部谓词（量具同 R9 口径：预热 + 2 万次操作均值 <1 B/op）。诊断上报路径（`ImmutableDictionary`/`Diagnostic.Create`）按需分配属冷路径（仅违规时），不纳入。验证：Release 全量构建 0W0E + 16 项目面板 **1585 = 1513 过 + 72 跳 0 失败**。

## R4 批量标记 --delivery 车道 A/B 补测（2026-10-15）

**R4/P0-2 收益量化（ADR-027 移交项：--delivery 车道 threshold 参数化 A/B）**：`OutboxDeliveryDrainBenchmarks` 新增 `BatchMark` 参数（1 = 逐条默认基线 / 16 = 满阈值分组单语句），矩阵 RTT 0/1/20 × InFlight 1/16 × BatchMark 1/16，口径 = SQLite 真库 + 假 broker 排空 1000 条墙钟（medium 档 5 迭代/3 预热）。**实测结论：本地端到端零显著差**——RTT=0 四格 162.3~172.2 ms/op 全在噪声带内（BatchMark16/1 比值 0.97 / 1.04 / 1.00 / 0.99，StdDev 量级 ~5%）；RTT=1 四格由发布延迟主导（Windows `Task.Delay` 15.6ms 粒度，串行档 ~17s/op，标记段占比可忽略）。RTT=20 四格未跑完：该格本机 ~33s/op × 8 次撞 BDN InProcess 单基准 5 分钟硬上限（既有车道限制，非本轮引入；按已测标记段成本推算其占比 ~0.04%，零差为确定性结论）。

**解释性量具（--persist-mark，新增 `OutboxMarkPathBenchmarks`，20 轮 × 100 条/op 放大到 ms 级）**：标记段本身收益真实——逐条 `MarkProcessedAsync` vs 满 100 单语句组（SQLite 真库，medium 档）：**26.46ms → 14.49ms（Ratio 0.55，StdDev 1.10/0.26ms），即 13.2μs/条 → 7.25μs/条（1.83×）**；分配 7.81MB → 3.37MB/op（3.9KB → 1.7KB/条，Dapper 逐条 DynamicParameters 形态）。但标记段仅占本地排空 ~8%（13.2μs / ~165μs 每 msg），批量节省 5.98μs/条 ≈ 端到端 3.6%（低于噪声带）——端到端零差由此解释，非无收益。**收益形态在远程库**：PG/MySQL 每标记 = 网络 RTT（本地基线锚：PG 单插入 630μs / MySQL 1730μs 量级），1000 条标记往返从 1000 次收为 62 次分组语句。**该 Not-Tested 登记已于 2026-10-16 清算**（--delivery-pg / --delivery-mysql 实跑，见下段）：串行确认档端到端 6.2~6.4×、标记段占排空 84%；「流水线档部分隐藏于发布 RTT 之后」未在远程格单独测（远程车道 InFlight 固定 1，T3.1 交互已有 SQLite 车道 12.9~13.0× 数据）。

**远程库投递 A/B（--delivery-pg / --delivery-mysql，2026-10-16，B1 复活前提实测，新增车道）**：口径同 --delivery（排空 1000 条积压墙钟 + 发布计数守卫 + 假 broker RTT），store 走 wslc 外部实例（`PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` + palddd_bench 库名守卫；MySQL 种子走 LOAD DATA LOCAL 协议，连接串显式 `AllowLoadLocalInfile=True` + 服务端 `local_infile=1`——库级安全门由调用方显式决策）。矩阵 RttMs {0,1} × BatchMark {1,16}，InFlight 固定 1（B1 决策面向默认配置；InFlight=16 与批量标记的交互已由 SQLite 车道 T3.1 实测）。专用采样档 5 迭代/3 预热/1 launch（远程 op 秒级，medium 15×2 撞 InProcess 单基准 5 分钟硬上限）。

| 方言 | RttMs | 逐条(BatchMark=1) | 批量(BatchMark=16) | 标记段 | 占排空 | 加速比 |
|---|---:|---|---|---|---:|---:|
| PG | 0 | 742.1 ms（E 78.32 / SD 12.12） | 116.4 ms（E 65.40 / SD 16.99） | 625.7 ms | **84.3%** | **6.37×** |
| PG | 1 | 9,666.2 ms（E 1,299.10 / SD 337.37） | 9,438.9 ms（E 1,463.80 / SD 226.52） | 227.3 ms | 2.35% | 1.02× |
| MySQL | 0 | 2,221.7 ms（E 395.22 / SD 102.64） | 359.0 ms（E 73.63 / SD 19.12） | 1,862.7 ms | **83.8%** | **6.19×** |
| MySQL | 1 | 12,328.8 ms（E 16,355.96 / SD 2,531.10） | 13,479.4 ms（E 11,537.17 / SD 2,996.17） | −1,150.6 ms | 噪声带内 | 0.91× |

派生读数：单标记往返成本 **PG 0.67ms / MySQL 2.00ms**（625.7ms 与 1,862.7ms 除以约 930 条避免的逐条语句；与账本单语句锚 PG 630μs / MySQL 1730μs 同带——批量标记省下的正是这部分网络往返）；分配 PG 3.77→2.01MB/op（−47%）、MySQL 5.13→2.22MB/op（−57%，逐条 DynamicParameters 形态收敛）。

**RttMs=1 格读法（两处必须同时声明）**：①本机 `Task.Delay(1)` 有效值约 9~10ms/条（Windows 定时器粒度，非 1ms——PG 该格每消息总延迟 9.67ms 减去标记段 0.63ms 与基数后约 8.9ms 即发布延迟），标记段被发布延迟稀释；②该格 A/B 差值落在 Error 带内（PG 227ms vs ±1.3~1.5s；MySQL 差值符号为负且 Error 带 ±11.5~16.4s 完全淹没）——**两方言 RttMs=1 格均不具统计显著性，结论是「无可测差异」而非「差异小」**。

**B1 复活前提第一项判定**（决策文档开放决策点 1「远端 DB 标记往返实测占发布延迟 >5%」）：DB 主导形态（broker 确认延迟低于阈值）下前提成立且远超阈值，占比 84.3%（PG）/ 83.8%（MySQL），端到端 6.2~6.4×；阈值解析式 share(r) = M / (base + 1000r + M) > 5% 给出 **PG r < ~11.8ms/条、MySQL r < ~35.0ms/条**（[推断]：以实测 M 与 base 代入，非独立实测点）。broker 确认延迟超过阈值后标记段退化为二阶成本（本机最小非零 broker 延迟格实测无可测差异，与该解析一致）。前提另两项（消费侧 Inbox 幂等可审计 + 时间界冲刷而非纯计数界）未满足，且「重复窗口放大最高 99×」的反驳与延迟形态无关，**B1 维持不做**；新数据的价值是把收益从量级估算升级为分形态实测，并给出部署判据：broker 确认延迟与 DB 往返同量级或更低时（本地/同机房 broker、跨机房 DB），`BatchMarkThreshold` 16~64 的 opt-in 收益显著。

**ADR-033 Auto 组合 A/B（2026-10-19，`--delivery-auto-pg` / `--delivery-auto-mysql`）**：真库 + 假 broker，排空 1000 条积压；Serial = 窗口 1/逐条标记/对象发布，Auto = 窗口 16/批量标记 16（1 秒时间界）/raw publish。采样档 3 迭代/2 预热/1 launch（InProcess；RTT=20 的 Serial 约 27~29s/op，5 op 控制在 5 分钟上限内）。环境：.NET 11 rc.2、BDN 0.16 preview、Ryzen 9 8945HX、wslc pg186/mysql84。RttMs 为假 broker 确认延迟；本机 RTT=1 实际约 15.6ms，RTT=20 实际约 25~28ms/条（Windows 定时器粒度）。

| 方言 | RttMs | Serial | Auto | 均值加速比 | 中位加速比 | 分配 Serial→Auto |
|---|---:|---:|---:|---:|---:|---:|
| PG | 0 | 2,778.4 ms | 202.8 ms | **13.70×** | 11.99× | 3.77→1.94 MB（−48.5%） |
| PG | 1 | 7,117.0 ms | 765.7 ms | **9.29×** | **13.53×** | 3.98→2.39 MB（−40.0%） |
| PG | 20 | 24,639.3 ms | 1,733.1 ms | **14.22×** | **14.36×** | 3.98→2.39 MB（−40.0%） |
| MySQL | 0 | 6,448.0 ms | 587.6 ms | **10.97×** | 10.97× | 5.13→2.16 MB（−57.9%） |
| MySQL | 1 | 9,550.8 ms | 802.8 ms | **11.90×** | 11.43× | 5.33→2.61 MB（−51.0%） |
| MySQL | 20 | 28,061.3 ms | 1,857.5 ms | **15.11×** | 15.11× | 5.34→2.61 MB（−51.1%） |

读法：①**组合收益 9.3~15.1×，不是 13× × 6.4× ≈ 83×**——发布窗口、批量标记与 raw 在同一段排空里重叠，窗口主导 RTT>0 的形态，标记与 raw 主导 RTT=0 的形态；乘法叠加不成立。②RTT=0 时窗口没有可重叠的往返，13.7×（PG）/11.0×（MySQL）来自“1000 次标记往返收为约 62 次 + 跳过反序列化/再序列化”。③RTT=1 格 PG 的 Auto Error 34%，均值受一次 1.4s 的离群拉高；按中位数 13.53× 读，与 RTT=20 的 14.36× 同带；MySQL 该格 Error 4.9%，均值可用。④分配下降 40%~58%，与 raw 路径消除二次载荷分配、批量标记减少命令/参数对象的机理一致。⑤本表是单机同日口径，不与 2026-10-16 的跨日绝对计时混用；跨机部署只引用比值区间。


## 流式读链路首测（2026-10-18，ADR-030 Phase 2 收窄论证的 bench 证据轮，`--stream`）

> 口径（框架开销层，同 Hosting/EndToEnd lane）：DefaultHttpContext 直调 RequestDelegate + 内存 PipeWriter/丢弃流，无网络栈、无真实 DB I/O；medium 档（15 迭代 × 2 launch / 10 预热，InProcessEmit）。引用须带此口径——生产每帧成本由网络写与 provider 延迟主导。新增 `StreamReadBenchmarks`（11 lane：framing 微 lane ×5 + flush 全路径 ×2 + 端点全周期 ×3，16 异步项/请求）。

| 基准 | Mean | Error | StdDev | Allocated |
|---|---:|---:|---:|---:|
| Frame_Default（data-only 帧 span 直写，预分配替身） | **5.19 ns** | 0.12 ns | 0.18 ns | **0 B** |
| Frame_Enriched（+event/id 行 UTF-8 直写） | 28.50 ns | 0.49 ns | 0.72 ns | **0 B** |
| Frame_FormatFrame（formatFrame 委托字符串帧） | 15.71 ns | 0.31 ns | 0.45 ns | **0 B** |
| Serialize_PerItem（SerializeToUtf8Bytes，源生成） | 78.43 ns | 2.25 ns | 3.23 ns | 56 B |
| SerializeAndFrame（序列化 + 默认帧） | 85.89 ns | 2.37 ns | 3.39 ns | 56 B |
| ZeroCopyFrame（Utf8JsonWriter 直写 PipeWriter 对照形态） | 77.90 ns | 2.72 ns | 3.82 ns | **136 B** |
| PerFrame_Default_WithFlush（+ FlushAsync，丢弃流背） | 920.80 ns | 61.30 ns | 91.75 ns | 390 B |
| PerFrame_ZeroCopy_WithFlush（对照形态 + flush） | 967.50 ns | 52.41 ns | 76.83 ns | 470 B |
| Endpoint_Plain（全周期 16 异步项：绑定+验证+头+枚举写帧） | 15.87 μs | 1.41 μs | 2.12 μs | 6,848 B |
| Endpoint_Enriched（event+id 选择器） | 17.20 μs | 1.19 μs | 1.74 μs | 6,848 B |
| Endpoint_Heartbeat（keepAliveInterval 1s，竞速路径） | 22.54 μs | 1.89 μs | 2.82 μs | 10,186 B |

**结论一：ADR-030 复活触发器 3（PipeWriter 零拷贝化）实测不满足，收窄决策获 bench 证据。** 默认帧路径 5.19ns 只占「序列化 + flush」每帧 921ns 的 **0.6%**，占「序列化 + framing」对的 6%；每帧成本 dominator 是 flush 机制（~835ns，91%）与序列化（78ns），framing 可忽略。零拷贝对照形态（Utf8JsonWriter 直写 PipeWriter）**不赢反输**：CPU 持平（77.9 vs 78.4ns，差 0.6% 在 Error 带内）而分配 2.4×（136B vs 56B——writer 内部缓冲 churn 比序列化器的单次 byte[] 更重），带 flush 后慢 5%（+47ns）且 +80B。「默认帧路径成热点」的触发前提被证伪，维持收窄。

**结论二：心跳竞速每异步项 +417ns / +209B，绝对值可忽略、形态需知情。** 端点 lane 对比（同 16 异步项 handler）：22.54 vs 15.87 μs（+42%）、10,186 vs 6,848 B（+49%）——差额 6.67μs / 3,338B 摊到 16 项即每异步项 +417ns / +209B（`AsTask` + `Task.WhenAny` 竞速机械；共享延时修复后 Task.Delay 不再每项新建）。放到生产延迟地板上：LLM 流 10~50ms/token 时占 0.001~0.004%，1000 项/s 的高速流也仅 ~0.04% CPU。同步完成项走 `IsCompletedSuccessfully` 快路径零额外开销（微 lane 的 Frame_Default 0B/5.2ns 即该路径形状）。**opt-in 语义与代价写入 usage.md/参数文档。**

**结论三：富化帧每帧 +23.3ns CPU、零额外分配。** event/id 行的 UTF-8 直写 span 不进堆（Frame_Enriched 0B）；端点请求级 +1.33μs（+8.4%，16 项摊每项 ~83ns）。分型事件流的框架成本确认可忽略。

**测量形态声明**：①端点 lane 双峰分布（StdDev 12~13%，`Task.Yield` 线程池抖动，与既有 EndToEnd/Hosting 异步 lane 同形态）；plain vs heartbeat 差 6.67μs 远大于 Error 带（±1.4~1.9μs），结论有效。②预分配 PipeWriter 替身把 BCL `StreamPipeWriter.FlushAsync` 的每帧 ~131B 内部分配从 framing 断言目标隔离（pitfalls A9 同族实测口径）；flush lane 用丢弃流避免 MemoryStream 无界增长。③BDN LaunchCount=2 的两次启动若绑固定端口必 AddressInUse（Launch 1 套接字释放时序），端点 setup 用 `127.0.0.1:0` 随机端口（AotSample §7 同款）。

### P1 落地复测（2026-10-18 晚：帧载荷序列化池化）

`StreamFrameSerializer`（ThreadStatic 池化 `Utf8JsonWriter` + `ArrayBufferWriter`，复用 JsonMessageSerializer R8/ADR-029 形态）替换默认/富化两分支的 `SerializeToUtf8Bytes`。复测口径同首测（同机、同 medium 档、`--stream` 全 lane）：

| 基准 | P1 前 | P1 后 | 分配差 | 备注 |
|---|---:|---:|---:|---|
| SerializeAndFrame（→ Pooled） | 85.89 ns / 56 B | **59.68 ns / 0 B** | −56 B | 同 run 对照：快于 Serialize_PerItem 的 byte[] 单独序列化（76.21 ns） |
| PerFrame_Default_WithFlush | 920.80 ns / 390 B | **845.09 ns / 336 B** | −54 B | flush 仍占 ~93% |
| Endpoint_Plain（16 异步项） | 15.87 μs / 6,848 B | **12.62 μs / 5,952 B** | −896 B（−13.1%） | 每项 −56 B，与池化预测逐项吻合 |
| Endpoint_Enriched | 17.20 μs / 6,848 B | **13.66 μs / 5,952 B** | −896 B | 富化分支同享池化 |
| Endpoint_Heartbeat | 22.54 μs / 10,186 B | **16.32 μs / 9,304 B** | −882 B | 心跳差额 3.70μs/3,352B 为竞速机械（+231ns/+209B 每异步项；较 P1 前 +417ns 降低、分配持平，竞速机械本身不受池化影响） |
| ZeroCopyFrame（收窄对照） | 77.90 ns / 136 B | 75.58 ns / 136 B | 0 | CPU 与默认路径持平、分配仍 2.4×，PipeWriter 化收窄结论加强 |

**读法**：①分配差为确定性结论（每项 −56B，端到端与预测逐项吻合，零分配锁定测试三件 + 变异红证明在案）；②CPU 收益同 run 内可判定（池化 serialize+frame 59.68ns 快于 byte[] 单独序列化 76.21ns），跨 run 的端点时间差（−20~−28%）方向与同 run 微观 lane 一致，量级供参考；③首测结论二的心跳开销重标为每异步项 +231ns/+209B（P1 后口径，生产占比仍 0.001% 量级）；④默认路径至此 framing 与序列化双零分配，ADR-030 D4 声明获完整实证。

### 收敛读数与残余归属（2026-10-18，P1 后评估轮，无代码变更）

P1 后框架可控成本占比与残余分配归属各做一次专门测量，用于判定是否还有值得做的优化项。

**每请求成本分解**（`Endpoint_Plain` 12.62 μs / 5,952 B，16 异步项/请求）：

| 段 | 每帧 | 每请求 | 归属 |
|---|---:|---:|---|
| framing | 5.2 ns / 0 B | 0.08 μs | 框架，已最优 |
| 池化序列化（P1） | 59.7 ns / 0 B | 0.96 μs | 框架，已最优 |
| flush 与管道机制 | ~780 ns | ~12.5 μs | BCL 管道与真实 I/O（生产态为网络写） |
| 端点固定段（验证/DI/响应头/日志） | 无 | <1 μs | 框架，无单点热点 |

框架可控合计约 1.04 μs，占端点 **8.2%**；分配 0 B（框架可控段）。

**残余 336 B/帧的归属探针**（TEMP 临时探针，同机，100k 迭代）：同步 sink 替身下三种形态的每 op 分配与耗时——空 `async Task` 0.0 B / 37.7 ns；仅 `FlushAsync` 0.0 B / 53.1 ns；写 30B + `FlushAsync` 0.0 B / 227.2 ns。结论：同步完成路径下「写帧 + flush」本身零分配，bench lane 的 336 B/帧来自基准侧管道替身（`DiscardStream` 背 `StreamPipeWriter`）与 BDN 引擎的异步状态机机械，非框架代码 [推断，依据为该对照的比例关系]。框架侧无可消分配项。

**Kestrel 静默流限速疑点验证**（2026-10-18，外部验证）：曾怀疑 Kestrel 默认 `MinResponseDataRate`（240 B/s，宽限 5s）会掐断长静默 SSE 流并与 15~30s 心跳建议冲突。三路对照实测（12s 静默）：默认限速、2s 心跳、禁用限速三种配置下第二帧均完整到达（`second=YES`，两轮复跑一致）。**疑点不成立**，现行心跳建议无需因 Kestrel 默认值调整；反向场景（12s 静默连接提前结束但首帧完整）未复现，不计入结论。

**判定**：框架侧 CPU 占比 8.2%、可控分配 0 B，剩余 92% 属传输层；P2/P3 候选全部低于「<5% 默认不做」线并已入 `.ai/perf-ledger.md` 表 2。**流式读链路性能轮收敛**，复活条件：框架占比回升到 20% 以上、消费方实测持续超过 5k 项/秒或 10k 并发流、生产实测 flush 占端到端不足 50%、或出现流级横切需求（ADR-030 复活触发器 1）。

## SQLite FTS5/JSON1 双栈 API 基准（2026-10-19，`--sqlgen`）

新增 `--sqlgen` 聚焦车道（`Program.cs`），只跑 `SqlGenBenchmarks`，避免每次复测都启动整个 `--infra`。两轮均为同机 MediumRun + InProcess + MemoryDiagnoser，单轮 11 个基准，约 23 秒。第一轮是首版实现，第二轮是 2026-10-19 优化轮；两轮之差就是本轮优化的 A/B，不是与 v4 旧 API 的对照（旧入口已在破坏式清理中删除）。

| 方法 | 优化前 Mean / Allocated | 优化后 Mean / Allocated | 分配变化 |
|---|---:|---:|---:|
| `Sqlite_JsonExtract`（`Extract("payload","Type")`） | 1,888 ns / 344 B | 1,278 ns / 128 B | −63% |
| `Sqlite_JsonPath`（三段特殊键路径） | 1,052 ns / 248 B | 1,060 ns / 120 B | −52% |
| `Sqlite_JsonObjectParameters`（两组键值参数） | 1,829 ns / 440 B | 1,312 ns / 176 B | −60% |
| `Sqlite_FtsMatchParameter`（`MATCH @q` 片段） | 1,244 ns / 208 B | 1,021 ns / 64 B | −69% |
| `Sqlite_FtsCreateIndex`（Outbox standalone 完整定义 + `ToSql`） | 7,218 ns / 14,568 B | 6,857 ns / 9,960 B | −32% |
| `Sqlite_FtsCreateEventLogIndex`（EventLog standalone 完整定义 + `ToSql`） | 7,779 ns / 14,104 B | 6,289 ns / 9,632 B | −32% |

优化内容：

- `EscapeIdentifier` / `EscapeLiteral` / JSON 路径 / 函数调用 / 数组 / 对象 / 等值谓词改为先算精确长度、再用单次 `string.Create` 写出，去掉 `Replace` 中间字符串与 `StringBuilder` 缓冲。
- `NormalizeParameterName` 对已带 `@` 的参数名返回原实例，不再 `name[1..]` 切片。
- FTS 列名只转义一次，`NEW.` / `OLD.` / 重建 SELECT 列表用预转义数组直接拼接，去掉重复 LINQ `Select` 枚举。
- `MatchLiteral` / `MatchParameter` 改为单次分配；`SqliteFtsIndexDefinition` 在构造期缓存默认 `JoinClause("s","m")`，重复查询零重复拼接。

口径与归属：

- 分配是判定层，计时只作趋势。优化后部分基准的 Error/Mean 仍偏高（PostgreSql 基线漂移、MediumRun 单轮），时间变化按指示性解读；分配数字每轮确定，是主要证据。
- `JsonExtract` 剩余的 128 B 含调用方 `params` 数组（约 32 B）与结果字符串；再降需要单段路径重载，属于公开 API 扩张，本轮刻意不做。
- FTS 定义约 9.6~10 KB 分配来自映射表、FTS5 虚表与三个触发器的 DDL 文本加外层定义对象；这是 schema 初始化操作，不属于请求热路径。把它做成硬预算会奖励缓存而非正确性，故不设 B/op 门禁。
- 引用计时数字前按 `perf-budgets.md` §2 的 <5%/>15% 自评规则判断。

## EPSL 全量运行经验与编排器（2026-10-19）

本轮把 EPSL 从“检查件 + 手工逐条命令”推进到可重复编排，并记录一条重要环境边界。

**编排器**：`scripts/eps-run.cs`

```bash
dotnet run scripts/eps-run.cs -- --quick        # eps-perf-check + --smoke
dotnet run scripts/eps-run.cs -- --full         # 预检 + 构建 + 17 条本地车道 + 汇总
dotnet run scripts/eps-run.cs -- --lane infra   # 单车道
dotnet run scripts/eps-run.cs -- --list
```

- 全量要求工作树干净（脏则拒绝，`--allow-dirty` 可显式放行），预检打印 HEAD / 分支 / dotnet 版本 / 其他 dotnet 进程数，并在 `bench/logs/.eps-run.lock` 上加独占锁。
- 每条车道独立进程运行，日志写 `bench/logs/eps-run-<timestamp>/<lane>.log`；`summary.md` 记录结果、耗时、尝试次数；日志保留最近 20 份。
- 车道失败默认重试 1 次（30 秒间隔）；仍红才判失败。重试是经验性补偿，不隐藏第一次失败，summary 记录 attempts。
- 默认跳过 PG/MySQL 四条真库车道；`--include-remote` 显式纳入，无 Docker 且无 `PALDDD_BENCH_*_CONN` 时记 SKIP，不算失败。

**并发干扰的实证**：2026-10-19 全量运行期间有另一会话同时构建/测试 Pal.DDD。`--infra` 三次分别死在 SagaState / Compression / Configuration，日志无异常、失败类随机漂移；全部单类独立运行通过，干净环境（无并发构建）复跑 7/7 通过。结论：BDN `InProcessEmitToolchain` 对并发负载敏感，失败先按环境态复测，不得直接改预算或判代码回归。编排器的预检/独占/重试就是这条经验的机械化载体。

**本次复跑读数**：17 条本地车道全部通过（含 `--sqlgen` 11 基准 23 秒）；16 个测试项目 1717 用例 0 失败 76 跳过；分析器开启构建 0 警告 0 错误；`eps-perf-check` 37/37 类且全部接入 `Program.cs`。真库车道未跑（本机无 Docker）。

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
| 全量 strict 复跑（2026-10-10 晚，cbf68c5 同轮） | 12 条车道：persist-pg / persist-mysql（TRUNCATE 修复后重跑）/ compare（MediatR+CAP 双容器）/ framework / infra / app / concurrency 均 strict + coverage medium + persist / saga / qps / smoke；wslc 四容器（PG/双 MySQL/rabbit）跑后零残留 | 通过：全部 exit 0；MySQL 重跑后零 NA；死锁零复现；数字入「全量复跑轮」段，发布单表入 CHANGELOG `[Unreleased]`；两项新登记见「未完成项」5 |

## 已有 BenchmarkDotNet 产物

现有 `BenchmarkDotNet.Artifacts/results/` 下保留了历史 ShortRun 结果，可作为旧基线，但不作为 .NET 11 Preview 当前实测结论。

| 基准类 | 结果文件 | 可引用数据 |
|---|---|---|
| `EntityDomainEventBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EntityDomainEventBenchmarks-report-github.md` | `AddSingleEvent_LinkedList`：91.04 ns / 72 B；`AddMultipleEvents_LinkedList`：797.87 ns / 376 B |
| `EntityCreationBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EntityCreationBenchmarks-report-github.md` | `Create`：3.8377 ns / 56 B；`GetHashCode_Compute`：17.6934 ns / 56 B |
| `ValidationBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.ValidationBenchmarks-report-github.md` | `Success`：0 ns / 0 B；`Failed_SingleError`：9.8009 ns / 32 B |
| `SmartEnumBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.SmartEnumBenchmarks-report-github.md` | 当前历史产物为 NA，不可作为性能结论（根因 2026-10-08 已修——继承静态方法不触发派生 cctor，见上文；现行数字见 MediumRun InProcess 基线表） |

## 2026-10-22 性能轮：往返账本驱动的四项优化

**口径声明**：往返数与回放吞吐来自代码推导（逐条链路数出实际语句数 × RTT）；出站唤醒延迟为实测
（`--wakeup-probe`，见下文）；分配类结论引用既有实测（`perf-budgets.md` 与各轮 BDN 报告）。
按 EPSL 纪律，未实测的派生量标「推导」，不构成性能承诺。

### 往返账本（推导，除唤醒延迟为实测）

| 链路 | 优化前 | 优化后 |
| --- | --- | --- |
| 出站投递（PG，批 100） | 2 次往返/批 + 0–500ms 唤醒延迟 | 2 次往返/批 + 唤醒/调度 p50 0.013ms（实测，另加租约与发布网络） |
| 入站单条（PG，重复消息） | 2 次往返 | 1 次 |
| 入站单条（PG，可重试失败） | 3 次往返 | 1 次 |
| 入站批量（PG，400 条） | 2 次往返/批 | 2 次往返/批（不变） |
| 投影回放（批 100 / 批 1000，10ms RTT） | 约 600μs/事件 | 约 60μs/事件 |
| EventLog 单条追加（PG） | 1 次往返 | 1 次往返（不变） |

### 落地项

1. **Outbox 唤醒信号**（公共接口 `IOutboxWakeUpSignal`）：四栈 Store 写入成功后通知，
   `OutboxProcessor` 的空闲等待改为「信号或轮询间隔先到者」，被唤醒但租到 0 条时 20ms
   自愈重试；未注册信号时退化为原 `Task.Delay` 形态。
2. **Dapper Inbox 单条认领单语句化**（PG/SQLite）：与批量路径共用 UPSERT + RETURNING，
   重复与可重试失败由 2/3 次往返收敛为 1 次，并消掉回读与 CAS 之间的 TOCTOU 窗口。
3. **回放批宽与续读裁剪**：`CheckpointBatchSize` 默认 100 → 1000；续读命中时跳过组首
   `GetAsync` 探测；组首裁剪改「先量前缀再一次 RemoveRange」（避免批宽放大后的 O(n²) 搬移）。
4. **消费上下文去闭包**：`FromHeaders`/RabbitMQ 路径直接持有头序列本体，`Headers` 惰性解码；
   实例尺寸不变（`FromHeaders_NullHeadersWithFallback_NoDictionaryAllocation` 预算保持）。

### 实测：出站唤醒信号（2026-10-22，`--wakeup-probe`）

量具：`bench/PalDDD.Benchmarks/OutboxWakeUpProbe.cs`。InMemory store + `NullMessageBroker` +
真实 `OutboxProcessor`/`OutboxBatchProcessor` 全链路，`PollInterval` 设 5 分钟使信号成为唯一
唤醒源；500 次「写入 → 被标记已发布」采样，另测 100 条突发写入的 tick 合并。

| 指标 | 实测（同机，两次运行区间） |
|---|---|
| 最小 / p50 / p95 / p99 / 均值 | 0.004–0.006 / **0.006–0.013** / 0.012–0.036 / 0.035–0.067 / 0.053–0.084 ms |
| 100 条突发写入的 tick 数 | **2–3**（逐条重构形态为 100） |

口径说明：本数字是框架内的唤醒与调度下限，不含数据库租约/标记往返与 broker 网络（那两项属
部署量纲，见 `--delivery-pg`/`--delivery-mysql` 车道）。μs 级指标受进程调度抖动影响，
按量级引用而不是按小数点后三位引用。旧形态（纯 `Task.Delay(500ms)` 轮询）的期望等待是 250ms、
最坏 500ms，属结构性事实而非估计。突发测试同时证明信号没有把批处理打散：写入合并后一批仍由
少量 tick 排空，吞吐不会因唤醒而退化。

复现：

```bash
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --wakeup-probe
# WAKEUP sample=500 min=0.006ms p50=0.013ms p95=0.036ms p99=0.067ms mean=0.084ms
# WAKEUP burst=100 ticks=2
# WAKEUP PASS
```

**信号覆盖边界**（不要假定更强）：Dapper/PalORM/InMemory 三栈的 `AddMessageAsync`/
`AddMessagesAsync` 写入成功后通知；EF 栈由 `OutboxDomainEventInterceptor` 在
`SavedChanges(Async)` 通知（覆盖领域事件路径）。以下路径仍走轮询：EF 上下文上直接
`OutboxMessages.Add` 且未挂拦截器、运维 `RequeueDeadAsync`（各栈均不通知，重投后最多等一个
`PollInterval`）、以及其它进程的写入。信号是加速通道，不是投递保证。

### 未落地（带理由）

- **MySQL 批量重认领 2N → 2 次往返**：同刻身份需要可空认领令牌列（DDL）才能不重蹈
  「时间戳做身份」的覆辙，属 schema 变更，本轮不做。
- **EventLog 单条追加参数对象池化**（约 2–4KB/次推导）：需先补该路径的 `[MemoryDiagnoser]`
  基线证明其占端到端 1% 以上，按「先量具后改动」推迟。
- **RabbitMQ 每投递 linked CTS**（约 200–300B/条推导）：与取消/关停语义耦合，收益风险比不成立。
- **出站完成管道去 `ContinueWith`**（约 64B/条推导）：需先证明其占端到端分配 1% 以上，同样等基线。
- **Saga 新建行批量插入**（同一批新 Saga 由 2N 次往返收敛为 1 次）：MySQL 的 affected-rows 与
  版本守卫组合易错且缺真库矩阵，未入场。
- **`NullInboxStore`（at-most-once 消费者的去重开关）**：属语义选择而非热点优化，且与「集成幂等
  在应用层」的既有文档取向冲突，未入场。
- **Saga 快照增量编码**：仅步骤数 >32 的长流程有感，属设计变更，未入场。
- **回放心跳惰性化**：每批约 3 笔分配加一次计时器注册，占批成本不足 0.1%，不做。

## 未完成项

1. ~~等 BenchmarkDotNet 支持当前 .NET 11 后用默认入口重新生成全部 benchmark 类结果~~（2026-10-08 大部分消解：17 类经 InProcess 显式模式全部可出正式报告；**2026-10-17 BDN 0.16.0-preview.2 已恢复默认 Switcher 入口**（`--list` 实测 exit 0），显式 InProcess 路径保留为口径配置；类级 job 注解仍按 Medium 显式 config 承载，不再依赖 attribute）。
2. SmartEnum.FromValue/TryFromValue 两项 Error/Mean >15%（ns 级固有抖动）：如需正式引用其计时，提升配置（严格档 5/10/15）复测；当前以零分配事实为准。
3. 保留 `--smoke` 作为秒级趋势检查 + 分配预算判定（2026-10-08 起带断言）；计时为单轮无统计效力，不替代正式报告。
4. **特性面缺口登记（2026-10-09 收尾轮，防「30 类全覆盖」被误读为特性面全覆盖；2026-10-18 口径为 37 类）**：~~①~⑤⑧~~（全部落地）；⑦ 四栈/方言面收尾——Dapper+PalORM 双栈 PG/MySQL 真跑 ✓ + **EventLog/Idempotency 真库层补齐 ✓**（2026-10-09 后段：双栈四格全出数，见 EventLog/Idempotency 口袋注）、EF 栈**租约 SQL 形态**验证 ✓（`MySqlSkipLockedShapeBenchmarks`：零死锁+互斥+1.75× 加速）、PG 方言特性 ✓（Pipeline 单往返/读写分离路由/LISTEN-NOTIFY 往返，COPY 隐含在种子路径）。**死锁已裁决并落地（ADR-025）**：版本探测双模板使 Dapper/PalORM MySQL ≥8.0.18 进入 SKIP LOCKED 形态（复跑双栈死锁归零，见 ADR-025 落地复跑注），ADR-024 对 <8.0.18/MariaDB 回退路径继续有效。**登记残余（四项，均有上游/量纲理由）**：① EF 栈级车道待 Npgsql.EF/Pomelo 出 EF 11 rc 配套版（2026-10-09 实证 NU1107/NU1102，SQL 形态车道先行为其等价物）；② `PostgreSqlOutboxNotifier` 服务化推送延迟（LISTEN-NOTIFY 底层能力已测 461μs；服务化形态属异步服务量纲，归集成测试）；③ **mysql:8.0.17 旧版镜像回归**（ADR-025 后续动作 2：验证回退模板行为与现状逐字一致，需拉取旧版镜像）；④ broker 发布路径（Kafka/RabbitMQ 需 broker 环境，正确性已由 Messaging.Integration 覆盖，性能归有 broker 环境时补）。另：PG Dapper 并发组 Error >15% 点值待严格档复测、8W 回落形态待复测；READ COMMITTED 对照实验留档于 ADR-025 被否决方案。
5. **全量复跑轮新登记（2026-10-10）**：① coverage × strict 车道级不兼容——InProcess 单基准 5 分钟硬上限对 ns 级快类（自动放大至百万 ops/迭代）不适用，处置 = coverage 恒 medium 档（发现②，本轮 medium 实测通过）；~~② PalORM 远程 EventLog/Idempotency 约 2× 偏移待专项 A/B 归因~~（**2026-10-11 T0.1 归因收口：参照系勘正非回归**，见「T0.1 归因结论」段；结构性 2× 差距转正为 T2.2 优化靶点——PalORM 单语句 Any 追加移植）。

## 发布判断

可发布收尾版本不依赖新的 BenchmarkDotNet 数字：本轮已验证 AOT 发布、构建、测试和 Dapper Projection 持久化闭环。当前补充的 `--smoke` 数据提供了可复跑性能证据，但正式 BDN 数据仍属于批次 8 后续项，不能在当前工具链状态下伪造成已完成。（2026-06-28 轮判断，留档）

2026-10-08 更新：上段「正式 BDN 数据未完成」的前提已被 InProcess 模式消解——15 类正式 medium 报告已产出并归档（artifacts-medium），statistical 判据自评见「当前结论」；SmartEnum 两项高抖动计时按 §五规则显式登记为统计无效而非引用。
