# 契约盘点复验（R2）后续优化方案（2026-10-21）

> 来源：第二轮契约盘点的第 7 章「未完成与不确定项」。本文件只处理“为什么没做完、风险在哪、怎么补、如何验收”，
> 不重复盘点结论。已闭环项见 CHANGELOG [Unreleased] 的 2026-10-21 两条记录。

## 一、本轮已闭环（作为后续工作的基线）

| 项 | 处理 | 验证 |
|---|---|---|
| slnx 批量测试文档失实 | 修正 conventions §10.5、development.md、testing.md；CI 保持逐项目循环 | `dotnet test PalDDD.slnx` 1828 例 exit 0 |
| runtime assembly scanning 无守卫 | SourceCodeGuardTests 守卫 7 + 5 个正反样本 | Core.Tests 全绿；矩阵样本可红 |
| AddPalFullStack 名不副实 | v5.0 窗口移除，统一 AddPalCoreStack | 快照 −1；usage/features 同步 |
| Dapper 三驱动传递足迹 | PrivateAssets="all" + 守卫改类型全名 + 静态缓存键去 provider 类型 | SQLite-only 与 PG-only 消费方各自只加载所用驱动 |
| RetentionDialect 公开面 | 定案为公开方言选择器，文档声明不携带 provider 类型 | 快照保持；A2 命中收窄为枚举值 |

## 二、未完成与不确定项逐条论证

**实施状态（2026-10-21 R3，R4 更新）**：2.1 部分落地（61 格清单 + Inbox 四栈矩阵；其余 4 组件矩阵待扩）；
2.2 已落地；2.3 已落地（三态汇总 + 配方 + pre-push 提示）；2.4 已验证（ASPNETCORE_URLS 复验通过）；
2.5 已由仓库测试覆盖（EF），PalORM 侧改为结构性论证。
R4 追加：Dapper 幂等 PG/MySQL 方言用例补齐，AcceptedGap 15→13；缺口集合改为精确冻结（换位即红）。

### 2.1 B2 对表只覆盖 61 格中的少数（R3：清单与 Inbox 矩阵已落地）

**事实**：本轮只对 Outbox 的终态守卫/重试自增与 Inbox 的认领/失败态做了 4 栈 × 4 组不变量比对，其余 45 格未对表。

**为什么没做完**：61 格是“组件 × 栈 × 方言”的笛卡尔积；每格 4 到 6 条不变量，全量约 300 条断言。现有
`CrossStackContractMatrixTests` 只覆盖 Outbox 的少数语义，且 PG/MySQL 格依赖 Testcontainers，本地无法跑。

**风险场景**：方言分支的差异（时间编码、唯一冲突分类、租约 SQL 形态）只在真库暴露；新增一个 store 实现时，
没有任何清单会提示“这个格子没有被对照过”。

**优化方案（按优先级）**：
1. **P1 格子清单测试**（新增 `test/PalDDD.Integration.Tests/ContractCellManifestTests.cs`）：
   用反射枚举 src 中实现六个抽象接口的公开类型，生成 (组件, 栈, 方言) 元组，与代码内的白名单常量逐项比对；
   新增实现而未登记 → 红。清单每行标注测试类名与覆盖状态（local / ci-only / uncovered）。
2. **P1 SQLite 全栈矩阵**：把 `CrossStackContractMatrixTests` 从 Outbox 扩到 Inbox/Idempotency/Saga/Projections/EventLog，
   四个栈全部用 SQLite（Dapper/PalORM/EF 均支持，无需 Docker）。目标 5 组件 × 4 栈 = 20 格，每格至少 3 条状态断言
   （返回 null/空集合、终态字段、令牌失配零命中）。
3. **P2 方言特异断言**：PG/MySQL 只补不能被 SQLite 代表的三类差异——时间列编码、唯一冲突分类、租约锁形态，
   每条落在现有 `DialectProbeTests` 的对应方言段。

**验收**：清单测试枚举 61 格且 0 未登记；SQLite 矩阵覆盖 ≥20 格；对任一实现删除一条守卫 SQL，对应矩阵用例转红。

### 2.2 B3 的「只跑通」判定依赖断言存在性（R3：规则已落地）

**事实**：本轮用“测试文件引用 + Assert.That 计数”分层，未逐个读断言内容。

**为什么没做完**：读断言内容是语义审查，成本与“逐文件扫缺陷”同量级，超出契约盘点预算。

**风险场景**：测试可能只断言 `Assert.That(result).IsNotNull()` 或只验证不抛异常，store 的终态字段写错仍然绿。

**优化方案**：
1. **P1 断言强度门禁扩展**：`AssertionStrengthGateTests` 增加 `*StoreTests` 规则——每个测试方法至少一条
   “状态/返回值断言”（`IsEqualTo`/`IsTypeOf`/`IsNull`/集合计数），纯 `IsNotNull` 计为弱；基线按现有计数设定棘轮。
2. **P1 清单联动**：2.1 的格子清单为每格指定“断言类型”（状态断言/仅调用），CI 拒绝 uncovered 与 call-only。
3. **P2 月度变异**：现有 `mutation.yml` 的 Stryker 范围加入 PalDDD.Dapper/PalORM 的 store 文件，
   用存活变异体反查“测试存在但断言不足”的格子。

**验收**：注入一个只含 `IsNotNull` 的 store 测试样例，新规则判红；清单中 call-only 格子数为 0 或显式豁免。

### 2.3 PG/MySQL 方言格在本地跳过（R3：三态汇总 + 配方 + pre-push 提示已落地）

**事实**：本地无 Docker 时 78 个用例跳过（PalORM 54、Integration 14、Messaging.Integration 10）；
CI 有 `dialect-probe` job 覆盖 PG/MySQL 的 Store/SQL/DDL 面。

**风险场景**：本地“全绿”不等于方言面绿；开发者在本地改方言 SQL 不会触发验证。

**优化方案**：
1. **P1 状态标注**：2.1 清单把每格标为 `verified-local` / `verified-ci-only` / `uncovered`，本地跑测试时打印三态汇总，
   避免“全绿”口径漂移。
2. **P2 本地真库配方**：把 wslc 容器 + `PALDDD_TEST_*` 环境变量的配方写入 `docs/development.md`（已有零散记录，
   收敛为一节），提供 `node`/`dotnet` 一键脚本入口。明确“本地不跑方言也能提交，但 CI 必跑 dialect-probe”。
3. **P2 提交提示**：pre-push 钩子在检测到 `src/**/Stores|SqlTemplates|Sql` 改动且未设置 `PALDDD_TEST_*` 时打印提示（不阻断）。

**验收**：清单三态可打印；方言 SQL 改动的 PR 在 CI 的 dialect-probe 必跑。

### 2.4 E1 的配置删除用例依赖本机端口（R3：已验证）

**事实**：E1-V1 删除 `UseUrls` 后通过（本机 5000 空闲）；上一轮同一用例因 5000 被占用而失败。

**风险场景**：消费侧冒烟结论随环境漂移，无法作为可复现证据。

**优化方案**：冒烟脚本固定 `ASPNETCORE_URLS=http://127.0.0.1:0`（或保留样例的 `UseUrls("...:0")`），
删除配置项时改用环境变量注入随机端口；报告中不再把“端口占用”计入框架结论。

**验收**：同一命令在两种端口占用状态下结果一致。

### 2.5 E4 的方言配错只覆盖 Dapper/SQLite 形态（R3：EF 已覆盖；PalORM 结构性无此问题）

**事实**：E4 用 SQLite 连接 + PostgreSql 方言验证 Dapper 守卫；EF/PalORM 栈未做同类消费侧实验。

**风险场景**：EF 的 provider 名判定与 PalORM 的 SqlDialect 映射若有错，错误仍可能落在首次查询。

**R3 实施**：EF 侧已在仓库内落地——三个方言 Outbox 上下文的租约入口增加 `EfCoreDialectGuard`，
provider 不匹配抛可行动 `InvalidOperationException`；`EfDialectMismatchTests` 用 SQLite provider +
PostgreSql 上下文验证消息含 PostgreSql/Sqlite，并有 SQLite 上下文正向用例。PalORM 侧不需要探针：
其方言由 `DataSession<TProvider>` 的 provider 类型决定，不存在“连接与方言参数分离”的错配面。

**验收**：EF 两条用例通过；PalORM 以类型系统保证方言一致（结构性论证）。

## 三、优先级与工作量

| 项 | 优先级 | 工作量 | 验收命令 |
|---|---|---|---|
| 2.1 格子清单（已完成）+ SQLite 全栈矩阵（Inbox 已完成，其余 4 组件待扩） | P1 | 清单 0.5 + 每组件 0.3 人日 | `dotnet test test/PalDDD.Integration.Tests -- --treenode-filter "/*/*/ContractCellManifestTests/*"`；`InboxCrossStackMatrixTests` |
| 2.2 断言强度扩展 | P1 | 0.5 人日 | `dotnet test test/PalDDD.DependencyInjection.Tests -- --treenode-filter "/*/*/AssertionStrengthGateTests/*"` |
| 2.3 本地真库配方与三态标注（已完成） | P1/P2 | 0.5 人日 | 清单测试打印三态汇总；development.md wslc 节 |
| 2.4 冒烟端口固定（已完成，ASPNETCORE_URLS 复验通过） | P2 | 0.1 人日 | 同一命令在端口占用下仍通过 |
| 2.5 EF 错误探针（已完成）／PalORM 结构性论证（无需探针） | P2 | 0.5 人日 | `EfDialectMismatchTests` 两条通过 |

## 四、明确不做与理由

- **不拆 PalDDD.Dapper 包图**：本轮已用 `PrivateAssets="all"` 阻断传递足迹；拆包（把 DapperBulkCopy 下沉到方言包）
  是结构性重构，收益只剩“包图更纯”，风险高于收益，留待 v5.0 之后有真实消费方诉求时再评估。
- **不做 Inbox 租约续约**：续约会在 handler 执行期间并发使用同一个 DbConnection/DbContext，引入新的正确性风险；
  当前以“超时达阈值告警 + 文档化配置契约”收口。
- **不改 Outbox DeliveryMode / Saga EnableDueTimeFilter 默认值**：二者是 ADR-033/034 的显式决策，
  默认值变更会改变新部署的并发与扫描语义，须按 ADR 流程单独裁决。
