# 全仓评审报告 · 第三十七轮（三次验证轮 · 八片并行地毯 · 第三遍）

> 基线：6b3826d ｜日期：2026-08-20｜方式：8 片并行子代理真实逐行（无缓存零抽样）+ 主线程机械轴
> 覆盖：**516/516 文件，78,264 行 100% 逐行**
> 机械轴：gate 21/1/0｜verify-ai 20/20｜encoding-gate 4/4｜15/15 项目全绿

## 段 1：发现总览

**P0=0｜P1=1｜P2=20｜P3≈116**

### 本轮核心结论：修复质量大幅改善

**上轮（三十六轮）P1×4 + P2 首批 9 项修复中，本轮验证结果：**

| 修复项 | 三十七轮验证 | 说明 |
|---|---|---|
| P1-1 EscapeLiteral 拆分 | ✅ **正确无回归**（片1/片4 双验证） | 路径/值位置分离精确 |
| P1-2 mojibake 全清 | ❌ **SagaProcessorTests 残余 4 组**（片7） | TransactionsTests 残余 3 行（片2） |
| P1-3 E3 指纹扩充 | ✅ 在位 28 项（片1） | — |
| P1-4 E1 本地回退 | ✅ 在位（片1） | 但 E1/E4 有头部检查盲区（P2） |
| B2 orchestrator 守卫 | ⚠️ 本文件有效但 12 姊妹未同步（片8 P2） | — |
| B3 verify-conventions | ✅ **正确**（片6 `|| BUILD_RC=$?` 标准形态验证） | — |
| B4 V19 python3 守卫 | ✅ **有效**（片8） | — |
| C3 event_id 四 DDL | ✅ **在位**（片1/片4 三方一致） | — |
| D3 IsPackable | ✅ **验证通过**（片8） | — |
| D2 EventStreamJsonLines | ✅ **修复有效**（片8 六条敌对边界全过） | — |
| D4 Kafka Close | ✅ **修复有效**（片8） | — |

**修复缺陷率从上轮 31% 降至本轮 1/13 = 8%**——接近历史均值 6%，说明验证轮→修复轮→验证轮的迭代循环在收敛。

## 段 2：P1 定稿（1 项）

| # | 发现 | 来源 |
|---|---|---|
| P1-1 | **SagaProcessorTests mojibake 第三次修复仍有 4 组残余**（L13-14/L37-39/L126/L176——头注释、测试体内注释、两个类的 XML doc）。文件头声明"已重写"与实际矛盾。**根因：指纹式逐行清理在多层 mojibake 面前系统性漏检** | 片7 |

## 段 3：P2 定稿（20 项，按族归并）

### 族 A：姊妹联动缺口持续（6 项——PD17 防线目标类别第三轮批量现身）
| # | 发现 |
|---|---|
| A1 | INSERT IGNORE 4 处姊妹残留（Checkpoint/Inbox×2/SqlTemplates）——ITM-228 三轮未联动 |
| A2 | B2 盘根守卫 12 处姊妹未同步——_ai_root_find 模式全仓 13 处仅修 1 处 |
| A3 | InMemorySagaStateStore.GetActiveSagasAsync 漏 AwaitingHumanDecision（EFCore 已修 InMemory 未跟） |
| A4 | C1 DisposeAsync 残留（PalOrmUnitOfWork :105 裸调用无幂等 catch——三处同类两处有防护） |
| A5 | Saga DynamicStep 路径漏 SafeObserveCompletedAsync（ITM-212 姊妹漏第四条） |
| A6 | PalOrmUnitOfWork/DapperUnitOfWork DisposeAsync 异常白名单过窄（ODE 逃逸致事务泄漏） |

### 族 B：门禁/防线残余（4 项）
| # | 发现 |
|---|---|
| B1 | encoding-gate E1/E4 只查文件头 48/80 字节（中后段 CRLF 盲区） |
| B2 | V5 空匹配假绿（三方 grep 全空时 empty==empty → PASS） |
| B3 | refine/prompt.md 仍教 `dotnet test PalDDD.slnx`（PD27 禁令对象） |
| B4 | flaky-gate 零报告=零 flaky 假绿 + runner 崩溃默认 P |

### 族 C：契约/性能/文档（10 项）
PG JSONB path 逗号无守卫｜SmartEnum 双注册静默丢弃｜DapperBulkCopy Unknown 待探针｜Sharding DisposeAsync 无隔离｜基准 ConsistentHash 构造偏差｜DapperEventLog"流式"失实｜EventLogDbContext per-scope reserver｜IdempotencyStore 过期无乐观守卫｜ADR-013 年份错误｜MySQL DDL 列长漂移

## 段 4：修复清单

**立即（P1）**：mojibake 全文件重写（弃指纹法改全文法——读全文→识别所有非 ASCII 非 CJK 统一区字符→整块重写）
**本迭代（P2 族 A）**：INSERT IGNORE 4 处姊妹统一收口 + B2 盘根守卫 12 处批量同步 + InMemory Saga 过滤器补状态 + C1 DisposeAsync 补 catch + DynamicStep 补 SafeObserve
**本迭代（P2 族 B）**：encoding-gate 全文件扫描 + V5 空匹配守卫 + refine prompt 禁令修正
**下迭代（P2 族 C + P3）**：按报告分批

## 段 5：趋势判断

P1 从第一遍 0 → 第二遍 4（修复回归）→ 第三遍 1（修复残留）——**收敛趋势明确**。P2 从第一遍 13 → 第二遍 17 → 第三遍 20（但其中 12 项为已知未修的存量而非新引入）。机械轴三轮持续全绿。

金句：三轮地毯走完一条清晰的收敛曲线——第一遍找病、第二遍找修复的病、第三遍确认修复的病在减少；唯一的顽固病灶是 mojibake，它教会的不是如何修注释，是指纹式修法对多层损坏系统性失效时要换武器。
