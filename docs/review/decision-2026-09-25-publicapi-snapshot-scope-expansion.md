# 决策：PublicApiSnapshot 覆盖范围扩展至数据面适配层程序集

> 关联：特性审计 `feature-usage-audit-2026-09-25.md` 结论 6（二轮对抗复核发现的保护盲区）；用户裁决「快照覆盖扩展：把数据面程序集纳入 PublicApiSnapshot」。

---

## 消费状态

**已实施（本决策同提交落地）。**

原范围决策（`PublicApiSnapshotTests.cs` 头注释，本决策回应而不删除）：

> 范围决策（刻意）：核心 11 程序集——适配层（Dapper/PalORM/EFCore 族）公共面由其各自集成测试与编译消费锁定，不纳入本快照；快照范围扩大需评审 PublicApiSnapshot 基线成本（基线文件体积与每次公共面变更的更新负担）。

**回应**：原决策的两个前提已被推翻——①「适配层公共面由集成测试锁定」：对抗复核实证
G23（快照↔CHANGELOG 逐提交校验）只作用于快照内程序集，PalDDD.Dapper.MySql 等 20 个
数据面程序集的 public API 变更**无任何机械防线**（本轮新增的 MySQL `configure` 重载正落
在盲区内，靠 CHANGELOG 自觉补记）；②「基线成本」：实测扩量后基线新增行数有限（适配层
为薄封装，见证据锚点表），且 G23 每次公共面变更本就要求动 CHANGELOG——增量负担是
「重生成一行命令」而非人工维护。

**扩展范围**：全部公开发布（docs/release.md ✅）包的程序集，共 +20；编译时组件
（Analyzers / Analyzers.CodeFixes / Core.SourceGen）与模板探针项目不纳入——它们不是
运行时公共 API 面。

## 证据锚点表

| # | 证据 | 出处 |
|---|------|------|
| 1 | G23 只校验快照内程序集的公共面变更 | scripts/gate.cs:1（PDDD-G23 快照↔CHANGELOG 逐提交耦合，见 docs/conventions.md:1 门禁表） |
| 2 | 盲区实例：MySQL `AddPalOrmDataSource` 新重载未进快照 | docs/review/feature-usage-audit-2026-09-25.md:19；CHANGELOG.md:18 |
| 3 | 原决策注释完整文本 | PublicApiSnapshotTests.cs:19（HEAD 8d1dcc0 时点，`git show 8d1dcc0:...`） |
| 4 | CI 拒绝快照自更新的护栏不受影响 | PublicApiSnapshotTests.cs:49（`PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS` CI 检测） |
| 5 | Hosting.AspNetCore 等全部数据面包为普通 `Microsoft.NET.Sdk`（无 AspNet 框架引用障碍） | src/PalDDD.Hosting.AspNetCore/ExceptionMiddleware.cs:1（包为普通 Sdk，测试项目可直接引用） |
| 6 | 基线增量实测 | test/PalDDD.Integration.Tests/PublicApiSnapshotTests.cs:71（快照路径常量；基线 1613 行含 +652 扩展） |

## 开放决策点

- **Analyzers 族是否纳入**：编译时组件（netstandard2.0）的公共面（诊断 ID / CodeFix）
  已有独立的 DiagnosticCoverage 门禁（38 条断言级覆盖）——本决策不纳入，若未来需要
  统一应另立决策。
- **基线体积进一步增长**：方言包后续若膨胀（如加大量 extension），可重新评估按「接口 +
  显式公开类型」裁剪快照输出（现机制为全量 dump，未做裁剪）。
