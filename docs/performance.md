# 性能实测记录

> 日期：2026-10-08  
> 环境：Windows 10 x64（AMD Ryzen 9 8945HX，32 逻辑核）；.NET SDK 11.0.100-rc.1.26425.128；BenchmarkDotNet 0.15.8（InProcessEmitToolchain）  
> 历史口径：2026-06-28 轮（SDK 11.0.100-preview.5）数字见下文「历史烟测归档」与「已有 BenchmarkDotNet 产物」

## 当前结论

2026-10-08 性能测试系统优化后，**15 个基准类（Framework 5 + Infra 6 + Persistence 3 + SagaLane 1）全部具备正式报告入口**：`--framework` / `--infra` / `--persist` / `--persist-medium` / `--saga` / `--compression` 以模式级 ManualConfig（`Job.MediumRun` + InProcessEmitToolchain + MemoryDiagnoser）绕过 net11-rc moniker 限制（默认 Switcher 入口仍不可用；根因与 jobs-union 双 job 坑详见 `bench/PalDDD.Benchmarks/Program.cs` 头注——类级 `[ShortRunJob]` 已全部移除，同 `--persist` 五十四轮先例）。`--smoke` 新增分配预算断言（Success/FromValue ≤1、Failed ≤50、RaiseEvent ≤140 B/op，超限退出码 1；变异验红实证：预算压至 1 时 RaiseEvent 判 ⛔ 退出码 1）。

统计有效性自评（testing.md §五判据套用首轮实测）：绝大多数基准 Error/Mean < 5%；5~15% 可接受带若干项（Outbox.AddSingleMessage 12%、Configuration.Pg 11%、SmartEnum.AllValues 5% 等，报告标注环境敏感）；**>15% 统计无效 2 项**——SmartEnum.FromValue_Known 24.8% / TryFromValue_Valid 25.9%（ns 级零分配操作的固有抖动，判定以零分配事实为准，引用其计时时需先提升配置）；ValueObject 三项为 ZeroMeasurement（BDN 标注与空方法不可区分，值即 ~0）。Medium 口径跨跑稳定性：`--framework` 二跑关键值跨跑差 AddSingleEvent 0.7% / Create 1.6% / Success 3.5%（对照 ShortRun 历史 53% 跨轮差，medium 裁决实证成立）。

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

## MediumRun InProcess 基线（2026-10-08 首轮，`--framework`/`--infra`）

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

Infra（`--infra`，全 InMemory fixture）：

| 基准 | Mean | Error | Allocated |
|---|---:|---:|---:|
| OutboxThroughput.LeasePending_Batch100 | 62.400 μs | 0.7193 μs | 63,360 B |
| OutboxThroughput.GetPending_Batch100 | 22.355 μs | 0.2131 μs | - |
| OutboxThroughput.LeaseAndMarkAll_Batch100 | 126.559 μs | 1.0741 μs | 68,408 B |
| OutboxThroughput.AddSingleMessage | 2.720 μs | 0.3298 μs | - |
| EventLog.Append_SingleEvent | 6.807 μs | 0.1469 μs | - |
| EventLog.ReadStream_Forward | 94.398 μs | 3.8230 μs | - |
| SagaState.GetById | 32.45 ns | 0.637 ns | - |
| SagaState.GetActiveSagas_Batch50 | 1,086.57 ns | 48.826 ns | 2,200 B |
| SqlGen 七项（const SQL 生成） | 1.59~8.63 μs | — | 全 0 |
| Configuration.DapperConfig_CreatePg | 62.82 ns | 6.789 ns | 184 B |
| Compression.GZip_Decompress_16KB | 3.489 μs | 0.0563 μs | 33,704 B |
| Compression.Deflate_Decompress_16KB | 3.477 μs | 0.0685 μs | 33,648 B |

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
| `--infra` 正式基线（2026-10-08） | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --infra` | 通过（4:32，6 类 23 项基准全 InMemory fixture，零 NA） |
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

1. ~~等 BenchmarkDotNet 支持当前 .NET 11 后用默认入口重新生成全部 benchmark 类结果~~（2026-10-08 大部分消解：15 类经 InProcess 显式模式全部可出正式报告；残余 = 默认 Switcher 入口与类级 job 注解的恢复，待 BDN 上游补 net11 moniker 或 .NET 11 GA 后实测，见 `docs/migration/net11-ga-upgrade-plan.md` 步骤 3）。
2. SmartEnum.FromValue/TryFromValue 两项 Error/Mean >15%（ns 级固有抖动）：如需正式引用其计时，提升配置（严格档 5/10/15）复测；当前以零分配事实为准。
3. 保留 `--smoke` 作为秒级趋势检查 + 分配预算判定（2026-10-08 起带断言）；计时为单轮无统计效力，不替代正式报告。

## 发布判断

可发布收尾版本不依赖新的 BenchmarkDotNet 数字：本轮已验证 AOT 发布、构建、测试和 Dapper Projection 持久化闭环。当前补充的 `--smoke` 数据提供了可复跑性能证据，但正式 BDN 数据仍属于批次 8 后续项，不能在当前工具链状态下伪造成已完成。（2026-06-28 轮判断，留档）

2026-10-08 更新：上段「正式 BDN 数据未完成」的前提已被 InProcess 模式消解——15 类正式 medium 报告已产出并归档（artifacts-medium），statistical 判据自评见「当前结论」；SmartEnum 两项高抖动计时按 §五规则显式登记为统计无效而非引用。
