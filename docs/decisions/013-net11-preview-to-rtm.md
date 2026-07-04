# ADR 013：.NET 11 Preview→RTM 迁移计划

> 状态：已采纳  
> 日期：2026-06-30  
> 关联：ITM-036、ITM-041

## 背景

`global.json` 锁定 `.NET 11 Preview 5` + `rollForward:latestMajor` + `allowPrerelease:true`。框架库依赖 .NET 11 Preview 特性：

- `JsonSerializerContext` 源生成增强
- 新 AOT 分析器（`VerifyReferenceAotCompatibility`）
- `runtime-async` 特性（`Directory.Build.props` `<Features>runtime-async=on</Features>`）
- `StackTraceLineNumberSupport`（.NET 11 Preview 4+）
- `Lock` 类型（C# 15）

Preview API 可能在 RTM 前发生破坏性变更，需有迁移预案。

## 决策

**RTM 发布后立即升级，不保留 Preview 兼容窗口。**

## 迁移策略

1. **时间线**：参考 .NET 官方发布节奏，.NET 11 预计 2025 年 11 月 GA。RTM 发布后 1 周内完成升级。
2. **升级步骤**：
   - 更新 `global.json`：`allowPrerelease:false`，SDK 版本升至 RTM
   - 更新 `Directory.Packages.props`：BenchmarkDotNet 升至支持 .NET 11 RTM 的版本（触发 ITM-041 执行）
   - 运行 `dotnet build` + `dotnet test` 全量验证
   - 运行 BenchmarkDotNet 全部基准，更新 README 性能表格（ITM-041）
3. **风险缓解**：CI 在升级前应在最新 .NET 11 Preview 版本上验证（`rollForward:latestMajor` 已支持）。
4. **破坏性变更预案**：若 RTM 有 Preview API 破坏性变更，按 ADR-005 单目标锁定原则，直接适配新 API，不做多目标兼容。

## 联动任务

- ITM-041（Benchmark GA 后运行）在 RTM 升级时一并执行
- 升级后更新本 ADR 状态为"已完成"

## 不动

当前维持 Preview 5 锁定，待 RTM 后执行升级。
