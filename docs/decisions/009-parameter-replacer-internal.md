# ADR 009：`ParameterReplacer` 维持 internal 不下沉为公共工具

> 状态：已采纳  
> 日期：2026-06-29  

## 背景

`ISpecification<T>` 的组合（`And` / `Or` / `Not`）使用 `ParameterReplacer`（`src/PalDDD.Core/ISpecification.cs` 内的 `internal sealed class ParameterReplacer : ExpressionVisitor`）将子表达式参数统一替换为公共参数，避免 `Expression.Invoke`（EF Core 不支持）。评审提出：是否将 `ParameterReplacer` 提取到 `PalDDD.Core/Linq/ParameterReplacer.cs` 作为公共表达式工具类，方便第三方实现类似 DIM 桥接 / 表达式参数替换模式。

## 决策

维持 `internal sealed`，**不暴露**为 `PalDDD.Core.Linq.ParameterReplacer` 公共工具类。

## 取舍

- **优点**
  - 表面积最小：`ParameterReplacer` 仅 1 个 `ExpressionVisitor.VisitParameter` 重写，~10 行——暴露需配套 XML doc、稳定性承诺、单测公共化，收益无法覆盖成本（YAGNI）。
  - 当前调用点全部位于 `ISpecification.cs` 同文件内 (`AndSpecification` / `OrSpecification`)，内聚性高、无需跨文件复用。
  - 若第三方需要类似参数替换，主流 .NET 生态已有成熟方案（`System.Linq.Expressions.ExpressionVisitor` 子类化是教科书技巧），无需 Pal.DDD 提供公共封装。
- **代价**
  - 第三方实现类似 DIM 桥接需自行写一个 ~10 行的 `ExpressionVisitor`。代价低——这是 Expression API 标准用法，不构成 Pal.DDD 应当承担的 API 稳定性负担。

## 边界条件

后续若出现以下场景，需重新评估本决策：

1. 框架内出现 ≥ 2 个调用点需要 `ParameterReplacer`（当前仅 `AndSpecification` / `OrSpecification` 共 4 处即合并使用）；
2. 评审确认 ≥ 1 个第三方包愿意基于 Pal.DDD 的公共 `ParameterReplacer` 构建上层抽象；
3. EF Core 8/9/10 演进导致 `ExpressionVisitor` 替换范式需调整，集中维护降低漂移成本。

## 验证

- `PalDDD.Core.Tests` 通过 `AndSpecificationTests` / `OrSpecificationTests` / `NotSpecificationTests` 完整覆盖组合表达式的参数替换正确性。
- 源码内 `ParameterReplacer` 保持 `internal sealed` —— `grep -rn "class ParameterReplacer" src/PalDDD.Core/` 仅出现一次且为 internal。

## 关联

- 源码：`src/PalDDD.Core/ISpecification.cs`（`ParameterReplacer` + `AndSpecification` + `OrSpecification`）
- 性能契约条目：`AGENTS.md` 表达式树组合规约——禁止 `Expression.Invoke`，必须用 `ExpressionVisitor` 参数替换
- 评审来源：`docs/review/audit-2026-06-29-v2.md` ITM-006