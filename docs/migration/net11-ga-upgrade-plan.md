# .NET 11 GA 升级预案（ITM-806）

> 状态：预案（GA 日执行）· 制定：2026-09-19 · 依据：首席审计 D-1 + ITM-806 论证
>（audit-2026-09-19-findings-confirmation.md）

## 一、为什么需要预案（风险陈述）

全家桶锁定 `11.0.0-rc.1.26425.128`（`Directory.Packages.props:5-15`，9+ 包），`Directory.Build.props:42` 的 NU5104 抑制自认「GA 后统一升级并移除此抑制」。rc→GA 是日历确定的硬迁移窗口；仓内已有三例 rc 特有行为证明 preview 间行为会变（见 §三），每例都是 GA 日的潜在回归点。无预案 = 35 包被动全量回归；有预案 = 清单驱动主动迁移。

## 二、升级清单（CPM 单点改）

| 组 | 包 | rc.1 版本 | GA 动作 |
|---|---|---|---|
| 运行时扩展 | Microsoft.Extensions.{DependencyInjection,Logging,Hosting.Abstractions}(.Abstractions) | 11.0.0-rc.1.* | 升稳定版 |
| EF Core | Microsoft.EntityFrameworkCore{,.Relational,.Sqlite,.InMemory} | 11.0.0-rc.1.* | 升稳定版（重点回归组） |
| 数据驱动 | Microsoft.Data.Sqlite | 11.0.0-rc.1.* | 升稳定版（时间列 TEXT 格式复核） |
| SDK | global.json 锁 11.0.100-rc.1 | — | 升 GA SDK |
| 抑制清理 | Directory.Build.props NU5104 | — | **移除**（其存在前提消失） |

非 rc 依赖不动：Dapper 2.1.86 / PalORM 5.5.1 / MemoryPack 1.21.4 / ZLogger 2.5.10 / Confluent.Kafka 等。

## 三、rc 特有行为表（GA 日逐项复核）

| # | 行为 | 位置 | GA 复核动作 |
|---|------|------|------------|
| 1 | **ITM-261**：EF SQLite provider 不能翻译 DateTimeOffset 有序比较/排序（== 可译）——GetPending 谓词下推与 Lease 单语句都建立在此约束上 | `SqliteOutboxDbContext.cs:33-37` 注释 + raw SQL 实现 | GA 后跑单测 `OutboxSqliteConcurrencyTests`；若翻译已修复，评估 GetPending 还原 LINQ 形态的可行性（下推仍更快，可保留；但 ITM-261 注释需更新） |
| 2 | **ExecuteSql API 演进**：`ExecuteSqlInterpolatedAsync` 在 rc.1 已标 Obsolete → `ExecuteSqlAsync`（本会话实证） | `SqliteOutboxDbContext.cs:114-115` | GA 后确认 ExecuteSqlAsync 形态稳定；检查是否又有新演进 |
| 3 | **BDN moniker**：net11-rc runtime moniker 不被 BDN 0.15.8 识别，Switcher 全程序集验证崩溃 → --persist 走 Run<T> 绕行 | `bench/PalDDD.Benchmarks/Program.cs:12-16` | GA 后试 Switcher 路径恢复（删绕行注释前先实测）；BDN 若发适配版可一并升级 |
| 4 | **runtime-async**（OPP-B2）：全层 ~210 处 ConfigureAwait(false) 稀释 runtime-async 收益[推断·幅度未测] | `Directory.Build.props:50-54` | GA 后跑 `bench/OutboxThroughputBench` 对照，裁决 ConfigureAwait 去留 |
| 5 | **时间列 TEXT 格式**：provider 写空格分隔格式（本会话 spike 实证 rc.1 行为） | `SqliteOutboxDbContext.cs` UTC 前提 + spike 记录 | GA 后复跑 format spike（Microsoft.Data.Sqlite 换稳定版后确认格式未变——变了则是正确性级） |

## 四、升级顺序与回归范围

```
第 1 步  SDK（global.json）+ Extensions 组      → 全量 build + 单测（低风险先行）
第 2 步  Microsoft.Data.Sqlite                  → §三.5 格式 spike + OutboxSqliteConcurrencyTests 全套
第 3 步  EF Core 组                              → §三.1/2 + Integration 300 + Repository.EFCore +
                                                PalDDD.EventLog.EFCore/Projections.EFCore 全量
第 4 步  NU5104 移除 + vuln-scan                 → 确认零预览依赖残留
第 5 步  CI 全套（含 aot-verify 真发布）          → 五 job 绿
第 6 步  版本号 2.2.0 → 2.3.0（或 3.0.0 视是否捆绑 B1/B2）+ 35 包发布
```

每步独立提交，失败可二分定位到组。**预发布窗口内（GA 前）不再升级 rc.2**（中间版本两次迁移成本，收益低——审计权衡在案）。

## 五、验收定义

GA 日按本预案执行后：NU5104 抑制删除、§三 表五项全部复核有记录、CI 五 job 全绿、35 包发布成功。
