# Pal.DDD 评审任务清单

> 来源：`review-2026-07-09-serena-comprehensive.md`（综合 8.6/10）
> 生成日期：2026-07-09 · 基于 Serena 综合评审的发现与建议
> 状态图例：✅ 已实施 · 🔍 已核实（非缺陷）· 📝 已记录理由（保持现状）

---

## 一、任务清单

| ID | 优先级 | 发现 | 处置 | 涉及文件 | 验证方式 |
|----|:--:|------|------|----------|----------|
| OBS-064 | P3 | HealthCheck `catch (Exception)` 未过滤 `OperationCanceledException` | ✅ 已实施 | `src/PalDDD.Hosting.AspNetCore/AspNetCore/HealthCheckExtensions.cs` | 代码审查 + 与 `ExceptionMiddleware` 一致性 |
| OBS-069 | P3 | `LoggingBehavior` Debug 分支用插值非 `LoggerMessage` | 📝 已记录理由 | `src/PalDDD.CQRS/PipelineBehaviors.cs` | 代码审查：门面隐藏 `ILogger` + `IsEnabled(Debug)` 门控，生产零分配 |
| OBS-070 | P3 | Outbox 原子性依赖同事务 `DbContext` | ✅ 已实施（文档化前置条件） | `src/PalDDD.Repository.EFCore/ServiceCollectionExtensions.cs` | 代码审查：前置条件已写入 XML doc |
| F-003 | P2 | README Metapackages 视角与 conventions 未区分 | ✅ 已实施 | `README.md` | 代码审查：Metapackages 行加注聚合/内容元包区分 |
| F-061 | P2 | conventions 测试数 14→15 | 🔍 已核实（非缺陷） | `docs/conventions.md:302` | `test/` 实际 15 `.csproj`（含 `PalDDD.Testing`）；conventions 的"14（不含 Testing）"与 README 的"15（含 Testing）"视角一致 |
| F-062 | P2 | NAMING 文件清单未含 7 月产出 | ✅ 已实施 | `docs/review/NAMING.md` | 代码审查：已补 7 月全部评审产出 |
| OBS-068 | P3 | 元包 `.csproj` 未入库（磁盘缺失、被忽略） | ✅ 已实施 | `src/PalDDD.Base/PalDDD.Base.csproj` · `src/PalDDD.Extension/PalDDD.Extension.csproj` | 从 `.nupkg` 的 `.nuspec` 还原依赖；`git add` 跟踪 |
| ITM-060 | P3 | net11.0 Preview 依赖 | 📝 已记录（跟踪项） | `global.json` | 注释标注 Preview 依赖与 GA 跟踪意图，无需代码修复 |

---

## 二、实施说明

### OBS-064（已实施）
`PalOutboxHealthCheck.CheckHealthAsync` 的 `catch (Exception ex)` 改为
`catch (Exception ex) when (ex is not OperationCanceledException)`，
与 `ExceptionMiddleware` / `LoggingBehavior` / `OutboxBatchProcessor` 的统一约定一致：
客户端断开导致的取消不再被误判为 `Unhealthy`。

### OBS-069（已记录理由，保持现状）
`LoggingBehavior` 经 `IPalLogger<T>` 门面发日志，门面刻意隐藏底层 `ILogger`
（见 `IPalLogger.cs` 设计原则），与 `LoggerMessage.Define` 所需的 `ILogger` 入参不兼容；
且插值仅在 `IsEnabled(LogLevel.Debug)` 门控后执行，生产路径零分配。
改为 `LoggerMessage` 会泄漏门面抽象并增加复杂度，违反 YAGNI，故保持现状。理由已写入 `PipelineBehaviors.cs` 注释。

### OBS-070（已实施）
`AddPalOutboxUnitOfWork<TContext>` 的 XML doc 新增"原子性前置条件"段落，
明确声明：Outbox 原子性依赖 EF Core 版 `IPalOutboxStore` 与业务 `DbContext` 处于同一事务；
接入异构存储需自行保证同事务/两阶段提交。

### F-003（已实施）
`README.md` 项目结构树的 `Metapackages` 行补充注释，区分
`Base`/`Extension`（聚合元包，仅 PackageReference、无源码）与 `Prompts`（内容元包）。

### F-061（已核实，非缺陷）
`test/` 目录实测 15 个 `.csproj`（含共享基础设施 `PalDDD.Testing`）。
`conventions.md:302` 的"14 个测试项目（不含共享基础设施 PalDDD.Testing）"与
`README` 的"15 测试项目（TUnit）"为同一事实的两种视角，数字均正确，**未做篡改**。

### F-062（已实施）
`NAMING.md` "文件清单"段补充 7 月全部产出
（`review-2026-07-09`、`review-2026-07-05`、`audit-2026-07-05`、`audit-2026-07-01` 系列、
`refine-*`、`architecture-refinement-analysis.md`、`action-items-2026-07-01.md`）。

### OBS-068（已实施）
`src/PalDDD.Base/` 与 `src/PalDDD.Extension/` 此前仅有 `bin/obj`（被 gitignore），
磁盘无 `.csproj`，导致 `git clone` 后无法从源码重建元包。
现依据 `nupkgs/PalDDD.{Base,Extension}.1.0.0-preview.1.nupkg` 内的 `.nuspec`
还原两个 `.csproj`（依赖版本与 `PrivateAssets="build;analyzers"` 一一对应 nuspec 的
`exclude="Build,Analyzers"`），并 `git add` 跟踪。
注：元包未加入 `PalDDD.slnx`，以避免改变主构建图；本地重建用
`dotnet pack src/PalDDD.{Base,Extension}/PalDDD.{Base,Extension}.csproj`。

### ITM-060（已记录，跟踪项）
`global.json` 新增 `_comment` 标注 .NET 11 SDK Preview 依赖与 GA 后切换意图。
属跟踪项，无需代码修复。

---

## 三、整体结论

8 项发现全部闭环：6 项直接实施（OBS-064/070、F-003、F-062、OBS-068），
1 项核实为非缺陷（F-061），1 项评估后保持现状并记录理由（OBS-069），
1 项跟踪项记录意图（ITM-060）。无 P0/P1，无新增风险。
