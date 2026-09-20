# Pal.DDD 评审报告（第五十二轮 · 全量四片）

> 报告编号：REVIEW-2026-09-19-FULL
> 评审基准：commit `d973ead` · 全量档（四片并行子代理真实读取 + 主线程复核）
> 方法：快照锚定 → 机械轴（build/15 测试项目/gate）→ 四片子代理逐文件 Read 全文（片1 变更面 / 片2 Transactions 33 文件 / 片3 三栈 Store 61 文件 / 片4 Core 支撑域 87 文件，合计 **184 源文件全文读取，零缓存零抽样**）→ 主线程独立预核高危发现 → 修复

---

## 执行摘要

**评审结论**：机械轴全绿（build 0W/0E、测试除 PalORM D1 环境性外全过、gate-audit 17 接线 0 缺口）；四车道骨架收敛经逐 diff 比对保真确认；三栈 Lease/GetPending 谓词逐列一致。**0 P1 / 5 P2 / 22 P3（新）+ 7 项已声明确认在位**。发现主体为三方一致失同步（注释/文档与本轮及会话外变更脱节），非行为缺陷。

| 指标 | 本轮 |
|------|:--:|
| P0 / P1 | 0 / 0 |
| P2 | 5（全部已修或登记） |
| P3 新增 | 22（8 项本轮修毕，余登记） |
| P3 已声明确认 | 7（声明在位） |
| 源文件全文读取 | 184（四片勾销清单见各片产出） |

## P2 发现与处置

| # | 发现 | 处置 |
|---|------|------|
| 片1-F1 | Saga.cs:423 骨架 XML doc 观察点顺序声明错误（声称 Started 在 ITM-069 检查后，实际在前，v43 注释自证）——f5947f5 引入的笔误 | ✅ 本轮修正 |
| 片1-F2 | AGENTS.md §2 CI 步骤链缺 format-verify（8e97e9c 加步骤未同步，违反 AGENTS §6 自身规则） | ✅ 本轮修正 |
| 片3-P2-1 | `[module:DapperAot]` 已启用（experiment/dapper-aot-full）与 5 处注释"未启用"矛盾——三方一致红线 | ✅ 本轮修正（5 处） |
| 片3-P2-2 | MySQL 三栈租约互斥分叉：EF 有派生表 SKIP LOCKED，Dapper/PalORM last-writer-wins（PalORM 注释自认双执行窗口）；两侧有分叉声明 + at-least-once 幂等兜底 | 📋 ITM-794（行为级需专项决策） |
| 片3-P2-3 | SQLite 时间列 TEXT 跨栈格式互不兼容（Dapper "O" vs EF/PalORM 空格式），DI "编码已兼容"声明缺此维度 | ✅ 本轮修正（2 处 DI 注释补维度） |

## 本轮修复清单（全部完成）

1. ✅ F1：骨架 XML doc 观察点边界改为「Route/未知 key 之后、ITM-069 之前」
2. ✅ F2：AGENTS.md CI 链补 format-verify
3. ✅ P2-1：DapperBulkCopy IL2062 依据 / SqliteRowFactory / SqliteTypeHandlers / DapperSagaStateStore / DapperServiceCollectionExtensions 五处 AOT 状态注释同步为「已启用」+ 历史脉络保留
4. ✅ P2-3：Dapper/PalORM.Sqlite 两处 DI 注释补时间列 TEXT 跨栈不兼容维度（含 "O" 恒真实证引用）
5. ✅ 片2-P3-2：CreateChildState 补 RequiresDynamicCode（ChildSaga 反射面 RUC+RDC 双声明补全）
6. ✅ 片1-F5：lease-index 迁移文档谓词勘正（`locked_until <` → 等值守卫族）
7. ✅ 片1-F4：leaseDuration 上界守卫注释理由更新（秒数转换已随批量化消除，跨方言防御保留）
8. ✅ 主线程-P3：SQLite override 守卫专测（2 参数化用例，ITM-659 覆盖迁移不丢失）

验证：build 0W/0E · Integration 300 全过 · Transactions 194 全过 ·（PalORM 46 失败为 D1 环境性在案）

## 行动项登记（ITM-794 起，接维护者已用编号）

| ITM | 来源 | 项 | 窗口 |
|-----|------|-----|------|
| ITM-794 | 片3-P2-2 | MySQL 三栈租约互斥统一（Dapper/PalORM 的 JOIN 加 SKIP LOCKED 等价形态）——行为级变更需专项决策 | 专项决策 |
| ITM-795 | 片2-P3-3 | OutboxBatchProcessor :103/:112 MarkDead 裸调防护（复合故障下重试分类替换业务根因）——修复形态需设计 | 下次触碰处理器 |
| ITM-796 | 片3-P3-5 | Dapper 侧 5 份唯一约束分类器收口（对齐 PalORM SqlErrorClassifier） | 卫生批 |
| ITM-797 | 片3-P3-2 | EF FencedTarget 持租分支补 Status 守卫（三方法口径统一） | 下次触碰 EFCore |
| ITM-798 | 片4-N1/N2/N3 | 分析器符号匹配缓存 + GenerateMessage 门控扩面 + DIM 默认实现 context 丢失防护 | 增强批 |

低优先记录（下次触碰时清理，不开 ITM）：片2-P3-1 死参数 failedStepKey、片2-P3-4 DefaultSagaManager remarks 程序集限制注记、片2-P3-5 P3-SRC-401 注释机制复核、片2-P3-6 同实例多 key 补偿重复（幂等契约覆盖）、片1-F3 ITM-763 归属三处不一致、片3-P3-1/3/4/6/7 已声明形态分叉、片4-N4/N5 微点。

## 特别核查结论（正面）

- **四车道骨架收敛保真**：f5947f5 逐 diff 比对——catch 顺序/OCE 透传/补偿聚合文案/补偿目标参数化/每 attempt 重建全部一致（片1）；SagaCompensation 与 TimeoutDetector 的耦合假设四条核查通过（片2）
- **三栈谓词族一致**：资格谓词四元组 + ORDER BY created_at + LIMIT 在 5 条路径词序一致（片3 对照表）
- **PalMetrics 线程安全 / SmartEnum 并发初始化 / Core 零依赖 / 7 个 AOT 项目零反射**：全部实证通过（片4）
- **注入面**：三栈全参数化，零字符串拼接（三片独立确认）
- **3 笔会话外提交**（6553c7a/3336aae/d973ead）与本轮改动域无冲突，HasIndex(LockedBy, LockedUntil) 与 shape 2 回读谓词精确匹配
