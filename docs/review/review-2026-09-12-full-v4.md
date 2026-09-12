# Pal.DDD 评审报告 — 第四轮全仓运行（脚本 C# 化后验证轮）

> 报告编号：REVIEW-2026-09-12-V4
> 评审基准：commit `7eca6be`（dev）· 全仓验证轮（MIG-011/012 脚本 C# 化后首次全量审计）
> 行动项清单：[`action-items-2026-09-12-v4.md`](action-items-2026-09-12-v4.md)（ITM-660 起）
> 前序：[v3](review-2026-09-11-full-v3.md) → v3 修复（129bfe5/5cb3d78）→ MIG-011 Python 清零（9d8e851/a1dfa3d）→ MIG-012 bash 清零（7eca6be/d33c9fc）
> 执行方式：子代理配额耗尽（9-17 重置）→ **主线程直接执行**（27 工具全量实跑 + 静态全量扫 + 三方闭环 + 抽样敌对复查 8 文件）

---

## 执行摘要

**评审结论**：脚本 C# 化（MIG-011/012，27 个 file-based app 替代全部 python/判定层 bash）**经受住验证**——全量实跑无功能失败、Process 使用 32 处无死锁面、三方闭环成立（CI 真跑背书）、抽样敌对复查 7/8 通过。发现 **1 个 P2**（PalORM 过期注释声称姊妹未收口——实际已收口，反向失实）+ 4 个 P3 文档过期。四轮收敛轨迹延续：v2 抓 5 语义错误 → v3 抓 0 语义 + 7 同步遗漏 → v4 抓 0 语义 + **1 注释过期 + 4 文档过期**——错误等级持续降维，无行为缺陷。

| 指标 | 本轮(v4) | 上轮(v3) | 趋势 |
|------|:--:|:--:|:----:|
| P0 / P1 | 0 / 0 | 0 / 0 | → |
| P2 / P3 | 1 / 4 | 7 / 30 | ↓↓ |
| 改动自带缺陷 | 0 语义 + 1 注释过期 | 0 语义 + 7 同步遗漏 | ↓ 收敛延续 |

## 四轮收敛轨迹（总）

| 轮次 | 审计对象 | 抓出问题的性质 |
|------|---------|--------------|
| v2 | 第一轮 64 项修复 | 5 项**语义错误** |
| v3 | v2 修复 + MIG 迁移 | 0 语义 + 7 项**同步遗漏** |
| v4 | MIG-011/012 脚本化 + v3 修复 | 0 语义 + 1 项**注释过期** + 4 项**文档过期** |

错误载体一路降维：代码行为 → 代码同步 → 注释/文档。质量体系（红测纪律 + 姊妹核查④段 + verify-ai V5 保留集断言）对高阶错误的拦截全部生效——v4 轮的 5 项发现全部是**机械可检的文档面问题**，无一项需要语义推理。

---

## 第一部分：评审基础（基线实测）

```
基线：7eca6be · 工作树清洁 · 构建 0W/0E · 16 项目 1365 用例（51 失败全环境性：46 Testcontainers + 5 Broker）
机械防线（C# 形态）：gate 3/3 · verify-ai 23/23 · encoding 4/4 · tech-debt 0F · test-gate 0F
  · doc-consistency 1/1 · secret-scan PASS · template-gate PASS · gate-lite PASS
CI 真跑：run 34661346188 三 job 全绿（build-and-test + dialect-probe + aot-verify）
```

## 第二部分：发现与证据

### 2.1 工具专项（27 个 .cs 全量）

| 检查轴 | 结果 |
|--------|------|
| 全量实跑 | 22 个 exit 0；5 个非零全部定位为正常语义（changelog-facts 需 tag/fix-completeness+sister-axis 需参/probe-template 未知模块拒/check-all 既有 format 违规） |
| Process 32 处静态扫 | 全部有 Dispose/using；**4 个文件无 Error 流读取**（实为 2 处真——另 2 处 grep 词面误报；stderr 继承=bash 同语义降 P3） |
| 仓库根定位 | CWD 向上找（BaseDirectory 不可用已实证）——docs/ 子目录实跑 gate.cs 正常 |
| --selftest 覆盖 | 4/27 有（ci-coverage/osc-check/flaky-parse/test-gate）；23 缺——verify-ai 优先（ITM-664） |
| 扫描面自指 | encoding-gate E2/E3 扫 .cs——全绿含工具自身，无自指命中 |

### 2.2 三方一致性闭环

| 核对项 | 结果 |
|--------|------|
| ci.yml 调用的 .cs 全存在 | ✅ 6/6（含循环变量动态名）——CI 真跑背书 |
| 已删 .sh/.py 活引用 | ⚠️ .ai/README.md 4 处 + sensor-ledger 2 行（ITM-661/662）；scripts/*.cs 与 ci.yml 零残留 |
| .ai/README 计数 | ⚠️ 未反映 27 .cs 形态（ITM-661） |

### 2.3 抽样敌对复查（8 个高风险变更文件）

| 文件 | 结论 |
|------|------|
| PalOrmSagaStateStore（37+ 行） | ⚠️ ITM-660 注释过期（声称姊妹未收口，实际已收口） |
| IdempotencyProcessor / ProjectionProcessor（OCE 收口） | ✅ catch 无过滤 + 注释对齐 |
| DapperOutboxStore（maxRetryCount 守卫） | ✅ ThrowIfNegativeOrZero 在位 |
| EFCore OutboxDbContext + 3 派生类（override 架空收口） | ✅ MySql/PG 各 2 处 + Sqlite 1 处（QueryEligibleAsync 单入口共享=全覆盖） |
| RabbitMqBroker（CreateAsync confirms） | ✅ remarks 与实现一致 |
| DapperSagaStateStore（Materialize fail-fast） | ✅ 已收口（反而证明 ITM-660 的 PalORM 注释过期） |

### 2.4 证伪记录（4 项）

1. ci.yml 引用 MISSING 疑似 P0 → git 中间态瞬时误报（6/6 存在 + CI 背书）
2. 4 文件无 Error 流疑似死锁 → 2 处误报 + 2 处 bash 同语义降 P3
3. Dapper 姊妹缺口疑似 P2 → 已收口，真问题是反向过期注释
4. SqliteOutboxDbContext 1 处守卫疑似缺 → QueryEligibleAsync 单入口全覆盖

## 第三部分：收束判定

| 轴 | 状态 |
|----|:--:|
| 机械轴 | ✅（C# 形态全套 + CI 真跑三 job） |
| 静态轴 | ⚠️ 1 P2 + 4 P3（全为注释/文档面）→ 主线程顺手清（<30 分钟）即收束 |
| 实测轴 | ✅ CI dialect-probe 42 断言持续绿 |

**判定**：修复轮已并入收口——5 项全部清偿（ITM-660 注释回填 grep 零残留；661/662 文档同步；663 取舍注释；664 verify-ai 自测要求登记）。**收束达成（本地两轴）**；实测轴 CI 持续绿（run 34661346188 背书）。下轮全量地毯待子代理配额恢复（9-17）后补——本轮范围收缩已声明。

---

## 事后勘误 / 修复轮（2026-09-12 追加，正文结论不变）

**5 项全部清偿**（主线程直接清偿，<30 分钟——四轮最轻修复轮）：ITM-660 PalORM 注释回填（grep "未同步收口" 零残留）· ITM-661 README 3 处 + 计数口径 · ITM-662 sensor-ledger 2 行指向现行形态 · ITM-663 两处 stderr 取舍注释声明 · ITM-664 verify-ai 头注释登记自测要求（下次改 V 项前置门）。清单 v4 = 5/5 = 100%。

## 第四部分：附录

### 执行统计

| 指标 | 数值 |
|------|:----:|
| 实跑覆盖 | 27/27 工具（100%）+ 3 工具交叉验证 |
| 静态扫 | Process 32 处 / 根定位 17 文件 / selftest 4/27 |
| 抽样敌对 | 8/57 变更文件（14%，高风险优先） |
| 证伪 | 4 项 |

### 局限声明

- src/test 存量未全量重扫（子代理配额耗尽；上轮 v3 已地毯 + 本轮无 src 判定逻辑变更）——9-17 后补全量。
- 抽样 8/57 为高风险优先抽样，非随机——低风险文件（纯注释/纯测试数据）未覆盖。
- P3 批 4 条为文档面，未逐条独立探针。
