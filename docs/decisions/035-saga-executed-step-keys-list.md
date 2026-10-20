# ADR 035：SagaState.ExecutedStepKeys 改用 List<string>

> 状态：**已采纳并实施（2026-10-20）**，随 v5.0.0 发布（公共 API 属性类型破坏性变更）
> 日期：2026-10-20
> 关联：ADR-034（性能迁移）、`docs/review/perf-round-2026-10-20-data-plane.md`、
> EF PG 租约真库探针（Npgsql EF 11 preview 6 + PG 18.6）

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-10-20 |
| 评审状态 | 已采纳并实施：PG 真库探针 8/8，SQLite EF 测试 15/15，Integration 417 通过 / 14 跳过 |
| 兼容性 | 源破坏：显式赋值 `Collection<string>` 的消费方需改为 `List<string>`；常规 `Add`/`Contains`/`foreach`/索引器用法不变。二进制破坏：属性类型签名变化。JSON 线格式不变，无数据迁移 |
| 触发原因 | Npgsql EF 11 preview 6 对 `PrimitiveCollection` 的物化类型是 `List<string>`，原 `Collection<string>` 属性在 EF PG/MySQL 读取路径抛 `InvalidCastException` |

## 背景

`SagaState.ExecutedStepKeys` 承载 Saga 已执行步骤的执行序，是补偿顺序的唯一依据。它被配置为 EF Core 11 的 `PrimitiveCollection`（原生 JSON 列）。SQLite provider 的 EF 测试一直通过，但 PG/MySQL 路径没有 provider 覆盖。

2026-10-20 用 Npgsql EF 11 preview 6 在 PG 18.6 真库执行 EF Saga 租约探针时，首次读取 `SagaState` 即抛：

```
System.InvalidCastException: Unable to cast object of type
'System.Collections.Generic.List`1[System.String]' to type
'System.Collections.ObjectModel.Collection`1[System.String]'.
```

原因不在租约 SQL，而在实体物化：provider 对原生 JSON 数组返回 `List<string>`，EF 无法赋给 `Collection<string>` 属性。探针忽略该列后租约四形态全过，确认问题独立。

## 决策

把 `SagaState.ExecutedStepKeys` 的公共类型由 `Collection<string>` 改为 `List<string>`，保留 `PrimitiveCollection` 映射：

```csharp
public List<string> ExecutedStepKeys { get; init; } = [];
```

`CA1002`（公共 API 不暴露泛型列表）在属性级精确抑制，理由是本类型必须与 provider 的物化类型一致；用 `Collection<T>` 需要额外的模型转换或包装层。

## 被否方案

| 方案 | 否决理由 |
|---|---|
| 模型级 JSON 字符串转换，保留 `Collection<string>` | 列类型可能由原生 JSON/数组变为文本，需要 schema 迁移与三方言确认；收益只是避免一个属性类型变更，却引入更大的数据面变更 |
| 为集合属性挂自定义 `ValueConverter` | EF 对 primitive collection 的集合级转换支持不确定；需要在模型层做 provider 特判，复杂度高于类型对齐 |
| 保留 `Collection<string>`，要求 EF PG/MySQL 用户自行覆盖模型 | 默认路径仍然损坏，与框架“开箱可用”的契约冲突；问题会在每个消费方重复出现 |
| 继续使用 `Collection<string>` 并把 provider 返回值再包一层 | EF 在赋值前已失败，包装层不可达；除非改 provider 物化行为，超出框架控制 |

## 影响与迁移

- 消费方若显式写 `new Collection<string> { ... }` 赋给 `ExecutedStepKeys`，改为 `[...]` 或 `new List<string> { ... }`。
- 常规用法（`Add`、`Contains`、`foreach`、索引器、`Count`）在 `List<string>` 上全部可用。
- JSON 序列化/反序列化结果不变，仍是字符串数组；EF 列结构不变。
- 公共 API 快照中该属性一行由 `Collection<string>` 变为 `List<string>`。
- 二进制兼容性破坏，随 v5.0.0 发布。

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|---|---|---|
| 原类型在 Npgsql EF 11 preview 6 物化失败 | EF PG 租约探针首次运行：`InvalidCastException`，堆栈落在 `LeaseViaPostgreSqlAsync` 的回读物化 | ☑ |
| 新类型物化成功 | 去掉探针的 `Ignore(ExecutedStepKeys)` 后，PG 18.6 真库 8/8；`ExecutedStepKeys = ["Start","Step1"]` 往返断言通过 | ☑ |
| 租约四形态不受影响 | 首次租约 4 行、第二 owner 0 行、到期过滤 2 行、租约后跟踪保存与完成态持久化 | ☑ |
| SQLite EF 路径不回归 | `PalDDD.Repository.EFCore.Tests` 15/15；Integration 417 通过 / 14 跳过 | ☑ |
| 公共 API 快照同步 | `test/PalDDD.Integration.Tests/Snapshots/core-packages-public-api.txt` 属性行变更 | ☑ |

## 开放决策点

| # | 决策点 | 状态 | 触发条件 |
|---|---|---|---|
| 1 | EF MySQL 全栈真库验证 | 未执行：本机无与 EF 11 rc2 匹配的 MySQL EF provider；SQL 形态已在 MySQL 8.4.11 直接执行 4/0/2 | 有匹配 provider 的 CI 车道可用时补四形态探针 |
| 2 | 为需要 `Collection<T>` 语义的消费方提供适配属性 | 不做 | 出现明确消费方且 `List<string>` 无法满足时，另立增量 API，不回退本决策 |
