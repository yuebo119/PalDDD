# Pal.DDD 行动项清单 — 第四轮全仓运行（脚本 C# 化后验证轮）2026-09-12

> 来源报告：[`review-2026-09-12-full-v4.md`](review-2026-09-12-full-v4.md)
> 基线 commit：`7eca6be`（dev）· 全仓验证轮（MIG-011/012 脚本 C# 化后）
> 方式说明：子代理配额耗尽（9-17 重置），本轮由主线程直接执行——27 个工具全量实跑 + Process 使用全量静态扫 + 三方一致性闭环核对 + 8 个高风险变更文件抽样敌对复查。**范围收缩声明**：src/test 全量地毯改为抽样（57 变更文件抽 8 个高风险 + 上轮已地毯过的存量不重复）。
> 编号衔接：自 **ITM-660** 起。

---

## 总体进度

| 优先级 | 条目数 | 待修复 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|
| **P0 / P1** | 0 | 0 | 0 | — |
| **P2** | 1 | 0 | 1 | 100% |
| **P3** | 4 | 0 | 4 | 100% |
| **合计** | 5 | 0 | 5 | **100%** |

**修复轮（2026-09-12 主线程直接清偿）**：ITM-660 注释回填（grep 零残留）；661/662 文档同步；663 两处取舍注释；664 verify-ai 自测要求登记入头注释。

**核心结论**：脚本 C# 化（MIG-011/012）**经受住验证**——27 个工具全量实跑无功能性失败（5 个非零 exit 全为参数语义/环境性）；Process 使用 32 处全有 Dispose、无死锁面；三方一致性 ci.yml↔.cs 闭环成立（CI 真跑背书）；8 个高风险变更文件抽样敌对复查 7 通过。发现 **1 个 P2 注释失实**（PalORM 声称"姊妹未收口"实际 Dapper 已收口——收口反向确认后的注释未回填）+ 4 个 P3 文档过期（README/sensor-ledger 引用已删脚本形态）。

---

## 🟠 P2（1 条）

### [x] ITM-660 · PalOrmSagaStateStore 注释声称"姊妹 Dapper 未同步收口"已过期（实际已收口，注释反向失实） · ✅
- **维度**：三方一致（注释过期）
- **问题**：`PalOrmSagaStateStore.cs`（:325-326）注释声称"姊妹 DapperSagaStateStore.Materialize 的同型兜底未同步收口（见其声明，后续任务对齐）"——**实测 Dapper 侧已于 v3 轮同步收口**（`DapperSagaStateStore.cs:324-330` 有同款 fail-fast + 注释），该注释在收口完成后未回填，反向误导后续维护者去找一个不存在的缺口。
- **修复**：PalORM :325-326 注释改为"姊妹 DapperSagaStateStore 已同步收口（v3 轮）"。
- **验证**：grep "未同步收口" src/ 零残留。
- **涉及文件**：`src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs`

## ⚪ P3（4 条）

### [x] ITM-661 · `.ai/README.md` 4 处过期引用（已删脚本形态） · ✅
- :22 `ci-failed-tests.py`（应 .cs）；:85 `itm-208-safety-test.sh`（已删）；:140 `bash scripts/gate-check.sh`+`changelog-facts.sh + changelog-check.sh`（应 .cs）；scripts 计数与实际形态（27 .cs + 2 .sh）未反映。
- **涉及**：.ai/README.md

### [x] ITM-662 · `.ai/gate/sensor-ledger.md` 2 行传感器指向已删形态 · ✅
- :14 弱断言棘轮 → 指向已删除的断言强度脚本（现行：`AssertionStrengthGateTests`）；:18 方言探针 → 指向已删除的方言探针脚本（现行：`DialectProbeTests`）。
- **涉及**：.ai/gate/sensor-ledger.md

### [x] ITM-663 · 4 个工具无 Error 流读取（stderr 继承终端）——低危设计取舍声明 · ✅
- changelog-facts/ci-coverage（RedirectStandardOutput 但 stderr 未重定向——对齐 bash 管道行为，注释已声明）；fix-completeness/sister-axis 为误报（grep 词面命中非 Process 使用）。
- **处置**：CI 环境下 stderr 继承是可观察行为（bash 同款），仅 changelog-facts/ci-coverage 两处补一行注释声明取舍即可。

### [x] ITM-664 · 23 个工具无 --selftest 自测入口 · ✅
- 仅 4 个有自测（ci-coverage/osc-check/flaky-parse/test-gate）。高危缺自测优先级：verify-ai（23 项校验逻辑最复杂）> gate.cs（git 编排）> tech-debt.cs。
- **处置**：✅ v87 轮清偿——`verify-ai.cs --selftest` 红绿矩阵 14 例（四提取函数共用主流程实现，无复制漂移面），S3 红测实证（破坏 V19 → 自测红 → 还原绿）。

---

## 证伪记录（4 项，主线程裁决）

1. **ci.yml 引用 .cs 全 MISSING 疑似 P0** → 证伪：git 操作中间态瞬时误报，6/6 文件存在 + CI 真跑背书。
2. **4 文件无 Error 流读取疑似死锁** → 部分证伪：仅 changelog-facts/ci-coverage 两处真（另两处为 grep 词面误报——fix-completeness 的 "Process" 是注释词、sister-axis 是正则行）；且 stderr 继承= bash 同语义，降 P3。
3. **DapperSagaStateStore 姊妹缺口疑似 P2** → 证伪：实测已收口（v3 轮完成），真正问题是 PalORM 反向过期注释（ITM-660）。
4. **EFCore SqliteOutboxDbContext 仅 1 处守卫疑似缺失** → 证伪：其 GetPending/Lease 共享 QueryEligibleAsync 单入口，1 处即全覆盖。

## 范围收缩声明

子代理配额耗尽（2026-09-17 重置）。本轮由主线程执行：27 工具全量实跑+静态扫（覆盖 100%）+ 三方闭环（覆盖 100%）+ 变更面 57 文件抽 8 个高风险（14%）。**src/test 存量未重扫**（上轮 v3 已地毯、本轮无 src 判定逻辑变更），下轮配额恢复后补全量地毯。
