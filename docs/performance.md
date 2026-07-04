# 性能实测记录

> 日期：2026-06-28  
> 环境：Windows 10 x64；.NET SDK 11.0.100-preview.5.26302.115；BenchmarkDotNet 0.15.8

## 当前结论

批次 8 的性能入口已恢复：默认入口运行 BenchmarkDotNet，额外提供 `--smoke` 轻量烟测用于当前 .NET 11 Preview 工具链下的可复跑证据。BenchmarkDotNet 0.15.8 是 NuGet 当前可见最新版本，暂时没有可升级版本；本机最小 BDN 校验只输出 `Validating benchmarks:`，未生成正式报告，因此不能把烟测数据等同于 BDN 结论。

## 当前烟测结果

命令：`dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke`

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
| 替代性能烟测 | `dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke` | 通过；输出 4 组 Stopwatch + GC 分配数据 |
| Native AOT 发布 | `dotnet publish samples/PalDDD.AotSample/PalDDD.AotSample.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishAot=true` | 通过 |
| 全解决方案构建 | `dotnet build PalDDD.slnx --no-restore` | 通过；0 warning / 0 error |
| 全解决方案测试 | `dotnet test PalDDD.slnx --no-restore -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"` | 通过；745 passed / 2 failed / 6 skipped |

## 已有 BenchmarkDotNet 产物

现有 `BenchmarkDotNet.Artifacts/results/` 下保留了历史 ShortRun 结果，可作为旧基线，但不作为 .NET 11 Preview 当前实测结论。

| 基准类 | 结果文件 | 可引用数据 |
|---|---|---|
| `EntityDomainEventBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EntityDomainEventBenchmarks-report-github.md` | `AddSingleEvent_LinkedList`：91.04 ns / 72 B；`AddMultipleEvents_LinkedList`：797.87 ns / 376 B |
| `EntityCreationBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EntityCreationBenchmarks-report-github.md` | `Create`：3.8377 ns / 56 B；`GetHashCode_Compute`：17.6934 ns / 56 B |
| `ValidationBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.ValidationBenchmarks-report-github.md` | `Success`：0 ns / 0 B；`Failed_SingleError`：9.8009 ns / 32 B |
| `SmartEnumBenchmarks` | `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.SmartEnumBenchmarks-report-github.md` | 当前历史产物为 NA，不可作为性能结论 |

## 未完成项

1. 等 BenchmarkDotNet 支持当前 .NET 11 Preview 或发布兼容版本后，用默认入口重新生成全部 9 个 benchmark 类结果。
2. 用同一硬件、同一 SDK、同一 `ShortRunJob` 将新 BDN 报告与历史基线对比，明确标注回归、持平或改进。
3. 保留 `--smoke` 作为 BDN 不可用时的低成本趋势检查；该模式只用于工程烟测，不替代正式 BenchmarkDotNet 报告。

## 发布判断

可发布收尾版本不依赖新的 BenchmarkDotNet 数字：本轮已验证 AOT 发布、构建、测试和 Dapper Projection 持久化闭环。当前补充的 `--smoke` 数据提供了可复跑性能证据，但正式 BDN 数据仍属于批次 8 后续项，不能在当前工具链状态下伪造成已完成。
