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

PalORM 栈（2026-10-08 ⑦ 残余补齐，PG/MySQL 同 wslc 路径）：

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
4. **特性面缺口登记（2026-10-09 收尾轮，防「30 类全覆盖」被误读为特性面全覆盖）**：~~①~⑤⑧~~（全部落地）；⑦ 四栈/方言面收尾——Dapper+PalORM 双栈 PG/MySQL 真跑 ✓、EF 栈**租约 SQL 形态**验证 ✓（`MySqlSkipLockedShapeBenchmarks`：零死锁+互斥+1.75× 加速）、PG 方言特性 ✓（Pipeline 单往返/读写分离路由/LISTEN-NOTIFY 往返，COPY 隐含在种子路径）。**死锁已裁决并落地（ADR-025）**：版本探测双模板使 Dapper/PalORM MySQL ≥8.0.18 进入 SKIP LOCKED 形态（复跑双栈死锁归零，见 ADR-025 落地复跑注），ADR-024 对 <8.0.18/MariaDB 回退路径继续有效。**登记残余（三项，均有上游/量纲理由）**：① EF 栈级车道待 Npgsql.EF/Pomelo 出 EF 11 rc 配套版（2026-10-09 实证 NU1107/NU1102，SQL 形态车道先行为其等价物）；② `PostgreSqlOutboxNotifier` 服务化推送延迟（LISTEN-NOTIFY 底层能力已测 461μs；服务化形态属异步服务量纲，归集成测试）；③ **mysql:8.0.17 旧版镜像回归**（ADR-025 后续动作 2：验证回退模板行为与现状逐字一致，需拉取旧版镜像）。另：PG Dapper 并发组 Error >15% 点值待严格档复测、8W 回落形态待复测；READ COMMITTED 对照实验留档于 ADR-025 被否决方案。

## 发布判断

可发布收尾版本不依赖新的 BenchmarkDotNet 数字：本轮已验证 AOT 发布、构建、测试和 Dapper Projection 持久化闭环。当前补充的 `--smoke` 数据提供了可复跑性能证据，但正式 BDN 数据仍属于批次 8 后续项，不能在当前工具链状态下伪造成已完成。（2026-06-28 轮判断，留档）

2026-10-08 更新：上段「正式 BDN 数据未完成」的前提已被 InProcess 模式消解——15 类正式 medium 报告已产出并归档（artifacts-medium），statistical 判据自评见「当前结论」；SmartEnum 两项高抖动计时按 §五规则显式登记为统计无效而非引用。
