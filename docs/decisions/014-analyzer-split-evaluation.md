# ADR 014：StrategicDddAnalyzer 文件拆分评估

> 状态：已采纳  
> 日期：2026-06-30  
> 关联：ITM-040

## 背景

`src/PalDDD.Analyzers/StrategicDddAnalyzer.cs` 单文件 597 行，包含 15 条诊断规则（PDDD001~015）注册 + 多个辅助方法（`TryGetProjectionName` / `TryGetStaticStringProperty` / `InheritsFrom` / `ImplementsInterface` / `MetadataNameEquals` 等）。评审提出拆分辅助方法为 `SyntaxHelper` / `SymbolHelper` 静态类，使主文件专注规则定义。

## 决策

**维持现状，不拆分。**

## 理由

1. **分析器代码内聚性高**：辅助方法与规则注册强耦合——`TryGetProjectionName` 等方法服务于特定规则的语法树遍历，拆分后需跨文件传递 `SyntaxNodeAnalysisContext`，增加参数传递开销而非降低复杂度。
2. **597 行在分析器领域可接受**：Roslyn 分析器单文件含完整规则集是常见模式（参考 `Microsoft.CodeAnalysis.NetAnalyzers` 多个分析器 500+ 行）。拆分的收益（导航便利）不抵成本（跨文件依赖）。
3. **规则数稳定**：15 条 PDDD 规则覆盖战略 DDD 全部约束点（BC/ProcessManager/Projection/Message 命名与形状），新增规则概率低，不会持续膨胀。
4. **合并冲突风险低**：分析器为单人维护的核心治理组件，非多人并发修改热点。

## 拆分方案（备选，若未来规则数增长至 25+ 再考虑）

- `SyntaxHelper.cs`：`TryGetProjectionName` / `TryGetStaticStringProperty`
- `SymbolHelper.cs`：`InheritsFrom` / `ImplementsInterface` / `MetadataNameEquals`
- `StrategicDddAnalyzer.cs`：仅保留规则注册与 `AnalyzeSyntaxNode` 主逻辑（目标 < 400 行）

## 不动

维持单文件 597 行，不拆分。
