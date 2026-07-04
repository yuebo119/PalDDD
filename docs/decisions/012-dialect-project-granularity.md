# ADR 012：方言增强项目粒度评估

> 状态：已采纳  
> 日期：2026-06-30  
> 关联：ITM-035

## 背景

`PalDDD.Dapper.PostgreSql` / `MySql` / `Sqlite` 三个方言增强项目各仅含 1-2 个文件（主要是 `*OutboxNotifier` 或方言 SQL 工具类），合计约 17 个文件。评审提出"是否合并入 `PalDDD.Dapper` 或按方言分组"以降低解决方案项目数（当前 31 个源项目）。

## 决策

**维持现状，不合并。**

## 理由

1. **按需引用是框架库的核心价值**：消费方按目标数据库单独引用方言包，避免引入无关方言的依赖。合并后 `PalDDD.Dapper` 将同时携带三方言代码，违反"按需付费"原则。
2. **AOT 配置差异**：三方言项目均为 `IsAotCompatible=true`（Dapper 路径），与 `PalDDD.Transactions.EFCore`（`false`）的 AOT 豁免策略一致隔离。合并会混淆 AOT 边界。
3. **slnx 分层清晰**：解决方案按 `Infra-Dapper/PostgreSql`、`Infra-Dapper/MySql`、`Infra-Dapper/Sqlite` 分组，项目数虽多但导航清晰。
4. **合并收益有限**：项目数从 31→28 仅降 3，但损失按需引用能力与 AOT 边界清晰度，收益不抵成本。

## 不动

三方言增强项目维持独立，不合并。
