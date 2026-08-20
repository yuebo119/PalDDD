# 全仓评审报告 · 第三十五轮（全量档 · 八片并行地毯）

> 基线：3dd3451（dev=main）｜日期：2026-08-20｜方式：8 片并行子代理真实逐行读取（无缓存、无抽样）+ 主线程机械轴/跨片横向/终审
> 覆盖：**513/513 文件，78,052 行 100% 逐行**（各片自报 9,400-10,000 行，含反证追加片外读取）
> 机械轴：gate 22/22 严格｜verify-ai 20/20｜test-gate 0 失败｜doc-consistency 10/10｜棘轮 172/173｜CI main 三 job 绿｜全量测试 15/16 项目 1,087 绿 + PalORM 41 fail-closed（无 Docker 预期，CI 实证绿）

## 段 1：发现总览

**P0=0｜P1=0｜P2=13（终审后，含 1 项由候选降级）｜P3≈108（归并为 12 类）**

八片全部零 P1——与近五轮 P1≤1 的收敛趋势一致。P2 呈四大根因族聚集，其中**姊妹分叉族（PD17）与门禁假绿族**正是 v2.0 新防线的目标类别。

## 段 2：P2 定稿清单（按根因族）

### 族 A：验证器/门禁假绿（防线自身之病，最高优先）
| # | 发现 | 证据 | 修法 |
|---|---|---|---|
| A1 | **D11 python3 缺失假绿**：`doc-consistency-check.sh:178` `warn_count_d11` 空串时 `[ "" -gt 0 ]` 走 else 打印"覆盖完整"——台账基线实为 45 处缺失。V16/#13 同族（工具缺失静默变 PASS） | 片8；逻辑直读成立 | `${warn_count_d11:-工具缺失}` 分支显式 WARN |
| A2 | **verify-conventions.sh 子串匹配**：`grep -qE "0 (个错误|Error)"`——"10 个错误"包含"0 个错误"子串，错误数以 0 结尾+0 警告时坏构建过门禁 | 片1；证据链完整（`\|\| true` 吞退出码后仅靠文本判定） | 改锚定 `^` 行首整行匹配，或直接用构建退出码 |

### 族 B：AOT 口径三重失实（合并 5 片交叉印证）
| # | 发现 | 证据 |
|---|---|---|
| B1 | PalORM 三方言包 `NoWarn …IL3058` 无理由注释，vs palorm-adapter.md"消除"、README.en"True AOT·验证通过"、CI aot-verify 仅验 Sqlite 样例——三重不符；方言包恰是引入 Npgsql/MySqlConnector 的层 | 片3/4/5 独立命中同一根因 |
| B2 | Dapper 系 NuGet Description 仍印"✅ AOT"（母项目已改"⚠️ AOT 假象"，Sqlite/PG/MySQL 姊妹未同步）——PD3 勘正的姊妹漏网 | 片6 |
| B3 | MemoryPack：usage.md/architecture.md 称"AOT 安全，零反射"vs csproj 显式 `IsAotCompatible=false`（ITM-158 同族，README 已修而 docs 未同步） | 片7 |

### 族 C：姊妹分叉（PD17 实锤 ×3——T1 防线目标类别首次地毯级现身）
| # | 发现 | 修法 |
|---|---|---|
| C1 | `PalOrmUnitOfWork.RollbackAsync` 无 try/finally：回滚抛出时 `_transaction` 未清、`UseTransaction(null)` 未解绑——Dapper 版 ITM-131 已修，PalORM 版漏网 | 对齐 Dapper 版（finally 清理 + 异常过滤） |
| C2 | `Repository.EFCore/UnitOfWork.DisposeAsync` 回滚异常未过滤（停机+事务悬挂+连接故障三重条件下从 Dispose 逃逸）——同为 ITM-131 姊妹 | 同型修复 |
| C3 | Obsolete 的 `AddPalPostgreSqlReadWriteRouter` reader 端口编码只做半（Port=5433+副本 5432 时读流量连错实例），且注释声称"姊妹统一：端口编码进 Host"与实现矛盾——ITM-132 修复的姊妹漏网 | 对齐 `PostgreSqlMultiHost.EncodeHostEntry` 全编码 |

### 族 D：公开示例/导入防护失效
| # | 发现 | 修法 |
|---|---|---|
| D1 | usage.md:477 + saga-orchestrator.prompt 示例调用 `When("Initial", typeof(OrderSubmitted), …)` 3 参重载是 **internal**（InternalsVisibleTo 仅 Tests）——用户照抄即 CS0122。ITM-159（README 同型）修复时未覆盖 usage/prompt | 示例改 `When<OrderSubmitted>("Initial", …)` |
| D2 | `EventStreamJsonLines` MaxLineChars 防 OOM 是**后置检查**：ReadLineAsync 已把数百 MB 无换行行完整分配后才判定——防护目标（限内存）未达成，注释口径失实 | 改带上限的缓冲循环读（超限即中止读取） |
| D3 | `install-ai-system.sh` 用法 B（INSTALL.md 承诺的等价路径）被自身幂等守卫永久短路且静默 exit 0——文档与实现自相矛盾 | 守卫加 source==target 例外，或删除用法 B |
| D4 | `SqliteJsonExtensions`：EscapeLiteral 不处理 `"`（病态键名静默错查）；`Extract` 的 XML doc 指向 `ExtractPath`"引号转义形式"——该能力不存在 | 补转义 + 修 doc |

### 终审降级记录（反证成功）
- ~~EFCore fencing 测试恒真假绿~~（片4 P2）：实读 `OutboxDbContext.MarkProcessed` 走 `ExecuteUpdate` 同步落库——测试有效，降 P3（正向用例显式 SaveChanges 属冗余风格）。

## 段 3：P3 归类（约 108 项 → 12 类）

| 类别 | 计数 | 代表 |
|---|:--:|---|
| 文档口径漂移/互斥 | ~30 | lessons ADR 计数 16→17；tutorial 972 vs README 977；ADR-011 签名 Guid→PalUlid；v8 文档头"100 条"实 295 |
| AOT 注释纪律（NoWarn 无逐条理由） | ~10 | DependencyInjection 核心包 IL3058；SourceGen RS1035 笼统注释；MinimalApi 广撒网 |
| 模板/教程自相矛盾 | ~6 | value-object 模板 Money 注释 vs 代码；Quantity 构造校验违反自家工厂规则；domain-event 示例缺 [GenerateMessage] |
| 死代码/死配置/重复 | ~8 | InboxMessageRow.FromDomain 字段错配死代码；Core.Tests 快照 PalDDD.Core 段重复 dump 两遍；重复 ProjectReference ×2 处 |
| 脚本边界 | ~10 | flaky-gate --runs 尾参 set -u 崩；tech-debt-scan.template 引号吞 `\|\|`；refine-scan 仓外死循环；check-all.sh 死分支 |
| 测试强度/恒真断言 | ~5 | AggregateRootInvariant 恒真 is 断言；SerializationTests 严格不等式 GC 脆断 |
| 性能小项 | ~8 | ReportHelper 每行 `"\n"u8.ToArray()`；DapperEventLog 逐条 INSERT；PipelineStateMachine 方法组分配 |
| 已知声明张力（不算新发现） | ~15 | PalORM 鸭子反射已声明降级；DapperAot 禁用状态；InMemory 无界增长测试域 |
| mojibake | 1 | SagaProcessorTests 整文件 UTF-8→GBK 损坏被 CS1570 NoWarn 掩盖 |
| 文档示例反教学 | ~4 | tutorial 用 UtcNow 违反自家 TimeProvider 规范；FtsExtensions 头部示例即警告禁止用法 |
| 验证器口径 | ~4 | V8 阈值 ≥6 却称"七流完整"；V7 注释 37 vs 40；台账 V1-V17 滞后；误判库速版缺 PD30-32 |
| 其他（schema 注释枚举失实/双轨未声明等） | ~7 | sqlite 000_schema status TEXT 与 PalORM int 契约混排受众歧义 |

## 段 4：趋势与防线的首次实战检验

1. **P1 连续六轮 ≤1 且本轮全仓 78K 行零 P1**——高危面收敛结论再次强化。
2. **姊妹分叉族（C1/C2/C3）三处实锤**：全部为"某轮修了 Dapper/主路径，姊妹（PalORM/EFCore/Obsolete 路由）漏网"模式——**T1 姊妹防线的目标类别首次被地毯级全景证实**；联动清单轴 A/B 覆盖检查：三处均在现有清单可达面内（UnitOfWork 族与路由器需补入轴 B 种子表）。
3. **门禁假绿族（A1/A2）**：本会话 CI 六层洋葱刚清完 shell 层假绿，地毯又在脚本层挖出两处同族——假绿是该系统的持续性病原，台账定标制度（V19）是对的方向。
4. **AOT 口径族（B 族六片交叉命中）**：单一根因（文档先行、验证滞后）散布 5 片——修复应以 B1 行动组一次性收口。

## 段 5：局限声明

- 八片代理"宁多报"初判 + 主线程终审反证的分工下，P2 定稿 13 项中 12 项证据链完整、1 项（A1）为逻辑直读（工具缺失场景本机不可复现——python3 在场）。
- P3 中"已知声明张力"15 项属既往决策在案，不计新债。
- 历史评审报告（.ai/review/history/）作为过程文档只核内部自洽未逐条回溯其结论时效。

## 段 6：修复清单（行动项）

**立即（防线之病）**：A1 D11 工具缺失 WARN｜A2 verify-conventions 锚定匹配
**本迭代（P2 代码）**：C1/C2 UnitOfWork 姊妹对齐（ITM-131 模式复制）｜C3 路由器端口全编码｜D2 EventStreamJsonLines 带上限缓冲读｜D4 Sqlite 转义+doc
**本迭代（P2 文档/口径）**：B1 PalORM AOT 三重对齐（csproj 补 Justification 或移除实测 + palorm-adapter/README.en/aot.md 修正 + CI 方言 AOT 验证缺口声明）｜B2 Dapper Description 姊妹同步｜B3 MemoryPack 文档对齐｜D1 usage/prompt 示例改泛型重载｜D3 安装器守卫例外或删用法 B
**下迭代（P3）**：按段 3 十二类分批；优先 mojibake 修复（整文件损坏）与 Core.Tests 快照重复 dump（影响 PublicApiSnapshot 可信度）
**联动清单动作**：轴 B 种子表补 UnitOfWork 族（Dapper/PalORM/EFCore/Repository.EFCore 四实现）与 Obsolete 路由器条目
