# Pal.DDD 评审报告（v93 · 质量系统运行轮）

> 报告编号：REVIEW-2026-10-19
> 评审基准：commit `30ff6e7`（本轮修复未提交，工作树含修复）· **标准档**（ADR-032 变更面）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD39）
> 触发：用户令「运行当前项目的 AI 质量系统，发现的问题都要充分修复」

---

## 执行摘要

**评审结论**：机械门禁（43 脚本 / 41 探针 / 8 守卫 / 17 测试项目）全绿；语义评审聚焦 v92 之后**唯一未覆盖的产品代码面**——ADR-032 SQLite FTS5/JSON1 双栈 API（`d6f0946` + `c4938ee`）与 `eps-run` 编排器（`30ff6e7`）。发现 **1 P0 + 5 P3**，全部同轮修复并以探针/锁定测试闭环。

| 指标 | 本轮 | 上轮（v92） | 趋势 |
|------|:--:|:--:|:----:|
| P0 | 1 | 0 | ↑（ADR-032 回归） |
| P1 | 0 | 0 | → |
| P2 / P3 | 0 / 5 | 0 / 0 | ↑（新覆盖面） |
| 逃逸（累计）/ 复发 | 0 / 0 | 0 / 0 | → |
| 证伪数（误报治理） | 2 | 0 | — |
| P0/P1 修复时延 | 同会话 | — | — |

### 与上轮对比

| 上轮发现 | 上轮 | 本轮 | 状态 |
|----------|:---:|:---:|:----:|
| v92 遗留（27 锚点 / 4 工具遗留 / 新控制探针） | 全部闭环 | — | ✅ 保持绿 |
| — | — | F1（JSON 路径单引号） | 🔴 新引入（ADR-032 重构丢失 v4 防线） |

---

## 第一部分：评审基础

### 1.1 覆盖度账本

**本轮为标准档（变更面）评审，不是 237 文件全量地毯**。ADR-032 变更面产品文件逐个全文读取：

| 文件 | 行数 | 状态 |
|------|-----:|:----:|
| `src/PalDDD.Shared/SqliteSqlCore.cs` | 268 | ☑ |
| `src/PalDDD.Shared/SqliteJsonCore.cs` | 388 | ☑ |
| `src/PalDDD.Shared/SqliteFtsCore.cs` | 594 | ☑ |
| `src/PalDDD.Dapper.Sqlite/SqliteFtsExtensions.cs` | 157 | ☑ |
| `src/PalDDD.Dapper.Sqlite/SqliteJsonExtensions.cs` | 114 | ☑ |
| `src/PalDDD.Dapper.Sqlite/SqliteFtsIndexDefinition.cs` | 88 | ☑ |
| `src/PalDDD.Dapper.Sqlite/SqliteFtsColumn.cs` | 11 | ☑ |
| `src/PalDDD.PalORM.Sqlite/SqliteFtsExtensions.cs` | 157 | ☑ |
| `src/PalDDD.PalORM.Sqlite/SqliteJsonExtensions.cs` | 114 | ☑ |
| `src/PalDDD.PalORM.Sqlite/SqliteFtsIndexDefinition.cs` | 88 | ☑ |
| `src/PalDDD.PalORM.Sqlite/SqliteFtsColumn.cs` | 11 | ☑ |
| `src/PalDDD.PalORM.Sqlite/SqliteFtsSessionExtensions.cs` | 144 | ☑ |
| `test/PalDDD.Integration.Tests/SqliteFtsTests.cs` | 280 | ☑ |
| `test/PalDDD.Integration.Tests/SqliteJsonTests.cs` | 310 | ☑ |
| `test/PalDDD.PalORM.Tests/SqliteFtsPalOrmTests.cs` | 137 | ☑ |
| `test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`（新增方法 + 助手） | 16 | ☑ |
| `scripts/eps-run.cs` | 630 | ☑ |
| `bench/PalDDD.Benchmarks/InfraBenchmarks.cs`（SqlGenBenchmarks） | 40 | ☑ |
| `docs/decisions/032` · `docs/migration/sqlite-fts-json-v5.md` · `docs/usage.md` 相关节 | — | ☑ |

七流覆盖度：架构/安全/错误/AOT/并发/资源/生成语义 = 变更面 **100%**；生成语义流以机械基线（快照 + 编译器/分析器 0 警告）为主。
三轴状态：**机械轴** verify-ai 30/30 · gate-audit 43 脚本（25 接线 / 41 探针）· guard 8/8 · 17 测试项目 0 失败；**实测轴** SQLite 真驱动执行级测试（Dapper 29 + PalORM 6）与 4 组独立探针。

### 1.2 评审基线

```
AOT：IsAotCompatible=true 显式 8 · false 14
异常过滤：catch(Exception) 100 · OperationCanceledException 引用 90
测试：16 个测试项目（PalDDD.Testing 为库）1720 用例 = 1644 通过 + 76 跳过 + 0 失败
构建：Release 0 警告 0 错误；分析器开启 0 警告；format-verify 0 差异
```

### 1.3 范围声明

- **必须检查**：上表全部 ADR-032 变更面 + `eps-run` 编排器 + 其测试与文档。
- **明确不检查**：src/ 其余 220 个文件的全量地毯（上一轮全量 v88/v89 为 2026-09-19，90 天债未到期）；PG/MySQL 真库方言探针（本机无 Docker）；CI `aot-verify` 真发布（本地无 RID 发布证据）；性能读数复测（`c4938ee` 的数字沿用其提交实测，本轮只做参考实现等价性探针）。
- **抽样策略**：范围外零抽样；本报告的结论不外推到未读文件。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:1]     [P1:0]     [P2:0]
中等     [P1:0]     [P2:0]     [P3:3]
难修复   [P2:0]     [P3:2]     评估:0
```

### 2.2 发现清单

#### 🔴 P0 — F1 JSON1 路径段单引号未按 SQL 字面量翻倍

- **位置**：`src/PalDDD.Shared/SqliteSqlCore.cs` 的 `PathLiteralLength` / `WritePathLiteral`（v5 新增共享核心）
- **危害×复杂度**：高危害（SQL 注入面 + 合法键不可用）× 易修复（< 1h）→ **P0**
- **证据（探针实证）**：
  - `SqliteJson.Path("a'b")` 生成 `'$."a'b"'`；真实 SQLite 执行 `SELECT json_extract("payload", '$."a'b"')` 报 `SQLite Error 1: near "b": syntax error`。
  - 注入形态：`SqliteJson.Path("x' OR '1'='1")` 生成 `'$."x' OR '1'='1"'`——路径字面量提前闭合，剩余部分成为可解析的布尔表达式。
  - 回归来源：v4 的 `EscapeJsonPathSegment` 明确执行 `s.Replace("'", "''")`；ADR-032 改为引号路径后只处理 `\` 与 `"`。被删除的 `SqliteDialectTests.Extract_KeyWithSingleQuote_IsEscaped` 正是该防线的锁定测试，重写时未迁移。
- **已对照模式**：PD29（验证验证者）、PD34（计数/口径自激）同族；SQL 注入按项目既有裁决（PostgreSqlAuditor，批次3）定为 P0 危害。
- **定稿门三问**：① 误判库无同型误报；② 反证搜索：FTS 标识符/字面量路径均走 `EscapeLiteral`，仅路径段缺失，非全局误判；③ 触发路径：调用方把含单引号的键（`O'Brien`）或外部键传入 `Path/Extract/ExtractText/ExtractJson/Type/ArrayLength/Set/Insert/Replace/Remove/Equals*` 任一路径参数即触发。
- **修复**：`PathLiteralLength` 对 `'` 计 2；`WritePathLiteral` 写两个单引号（SQL 字面量规则），反斜杠/双引号仍按 JSON 路径规则加反斜杠。
- **验证**：新增 `Path_QuotesSpecialKeys`（含 `j'k` 的精确输出）、`Path_SingleQuote_InjectionAttempt_StaysInsideLiteral`、`Extract_SpecialKeys_ExecutesOnRealSqlite`（真实 SQLite 取值 1..6）；另以参考实现对拍探针覆盖 11 个特殊键 × 10 个生成方法（70+ 用例）全部一致。

#### 🟡 P3 — 其余五项（均已同轮修复）

| ID | 发现 | 证据 | 修复 |
|----|------|------|------|
| F2 | `JoinClause` 对 external-content 也要求 `sourceAlias != mapAlias`，与 XML doc「external-content 忽略 mapAlias」矛盾 | `SqliteFtsCore.BuildJoinClause` 在 kind 分支前抛错；`JoinClause("s","s")` 本应合法 | 校验移入 standalone 分支；新增 `JoinClause_ExternalContent_IgnoresMapAlias` 与 `JoinClause_Standalone_RejectsDuplicateAliases` |
| F3 | `eps-run --dry-run --lane <未知>` 抛 `NullReferenceException`（`LaneByName(...)!`），非干净参数错误 | 探针：`--dry-run --lane nope` → NRE at `PrintDryRun` | `ParseArgs` 增加车道存在性校验；selftest 增加「未知车道必红（含 --dry-run）」 |
| F4 | `eps-run` 帮助与头注写「16 条本地车道」，代码与 `docs/performance.md` 均 17 条 | `SelectLanes(false).Length == 17`（selftest 自身断言） | 两处文案改为 17 |
| F5 | v5 迁移指南/usage 缺「standalone 索引不自动回填存量行」步骤——只建索引不 `Rebuild` 时旧事件/旧消息静默搜不到 | 迁移指南 §3 只删旧建新；`CreateEventLogIndex` 为 standalone | 迁移指南补 `RebuildStatements()` 步骤与 PalORM `RebuildIndexAsync`；usage 同步；`EnsureIndexAsync` XML doc 增加提示 |
| F6 | CHANGELOG 声称「Dapper FTS/JSON 27 件」，实测原为 26 件（本轮补 3 件后 29） | `grep -c '[Test]'` 于 `d6f0946`：FTS 12 + JSON 14 = 26 | CHANGELOG 改 29 并注明补测来源；`.ai/perf-ledger.md` 表 4 #30 的「27+6」同步为「29+6」 |

### 2.3 观察项（非问题）

| ID | 观察 | 说明 |
|----|------|------|
| O1 | `refine-scan` 的 M3（92 处 `public {get;}`）/ O3（73 处 `ToArray/ToList`）候选 | 属于 ORM 映射类与热路径分类的已知取舍，`.ai/perf-ledger.md` 与 ADR 已登记；本轮不按「发现问题」处理，避免把优化候选当缺陷批量改动 |
| O2 | `CreateStandalone` 的 `last_insert_rowid()` 依赖 | 触发器体内紧邻 map INSERT，其他触发器不能在单条触发器体中间插队；探针验证增删改/键变更/重建全通过，非缺陷 |
| O3 | 删除的 `SqliteDialectTests` 头注声称覆盖 `SqliteRowFactory` | grep 全仓无该测试，属**头注失实而非覆盖回归**（证伪 1 项） |

**证伪（2 项）**：① 「SqliteRowFactory 测试随文件删除而丢失」——原文件从未包含该测试（头注失实）；② 「`last_insert_rowid()` 在并发触发器下可能取错」——触发器执行模型不允许跨触发器体插队，实测键变更/重建全部正确。

---

## 第三部分：架构合规与收束判定

### 3.1 DDD / Clean Architecture 合规

| 原则 | 状态 | 证据 |
|------|:----:|------|
| 领域层零基础设施依赖 | ✅ | `ArchitectureBoundaryTests` 8 守卫全 GREEN |
| 依赖方向外→内单向 | ✅ | 同上 |
| 跨 BC 仅通过领域事件 | ✅ | G13 红线扫描 GREEN |
| 无 `IRepository<T>` 等反模式 | ✅ | 同上 |
| DIM 桥接替代反射 | ✅ | SourceCodeGuard GREEN |
| 聚合根保护不变量 | ✅ | AggregateRootInvariantTests GREEN |
| PalORM 方言包双栈平级 | ✅ | 新增 `PalOrmSqlite_DoesNotReferenceDapperOrForeignDrivers` |

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | verify-ai 30/30 · gate-audit 43 脚本 / 41 探针 · guard 8/8 · 17 项目 0 失败 |
| 静态轴 | ✅ | 变更面逐文件读完；修复后 0 P0/P1/P2 未闭环 |
| 实测轴 | ✅ | SQLite 真驱动 35 件（Dapper 29 + PalORM 6）+ 4 组独立探针；PG/MySQL 真库 SKIP（无 Docker） |

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖 | 17 产品/测试文件 + `eps-run` + 3 份文档（约 3,600 行） |
| 探针数 | 4 组（路径注入 / FTS 键变更与重建 / 奇数标识符 DDL / 70+ 参考实现对拍）+ 真实 SQLite 执行级测试 35 件 |
| 证伪数 | 2（见 2.3） |
| 下沉数 | 0（新发现均为一次性语义/合同缺陷，无可机械化的重复模式；F3/F4 的回归由 `eps-run --selftest` 锁定） |

### 4.2 自我局限声明

- 未做 237 文件全量地毯（上一轮全量 2026-09-19，90 天债未到期）。
- PG/MySQL 真库方言轴未实测（本机无 Docker）；本轮变更仅 SQLite 方言。
- 性能数字沿用 `c4938ee` 的实测，本轮只验证生成结果等价性，不重测分配。
- AOT 仅本地构建 0 警告；`PublishAot` 真发布由 CI `aot-verify` 承担。

### 4.3 元审计自检

```
☑ Z0 覆盖度：变更面文件全勾销；范围外零抽样并显式声明
☑ S1 论证链：P0 含探针证据 + 定稿门三问 + 触发路径
☑ S2 合规：3.1 七项原则全部检查
☑ S3 指标：4.1 与 metrics.md v93 行一致
☑ S4 反模式：未把 refine 候选当缺陷批量改动
☑ 可信度：P0 无 [推断] 定稿；P3 均有代码/探针锚点
☑ 一致性：摘要 ↔ 发现清单 ↔ 收束判定一致
☑ 对比：与 v92 无退化遗漏
违反项：无
```
