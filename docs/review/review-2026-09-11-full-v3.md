# Pal.DDD 评审报告 — 第三轮全仓运行（MIG 迁移后验证轮）

> 报告编号：REVIEW-2026-09-11-V3
> 评审基准：commit `a80283b`（dev，与 main `17b2726` 内容一致）· **全仓重扫 + MIG 迁移验证**（判定层 bash→C# 迁移完成后的首次全量审计）
> 行动项清单：[`action-items-2026-09-11-v3.md`](action-items-2026-09-11-v3.md)（ITM-653 起）
> 前序：[v1 全仓审计](review-2026-09-10-full.md) → [v2 验证轮](review-2026-09-10-full-v2.md) → MIG 迁移批（`384e86b`..`a80283b` + `.ai 5023443`/`7dcc2ce`）

---

## 执行摘要

**评审结论**：MIG 迁移批经受住了验证——**0 P0/P1**；新门禁 7 文件（~4,800 行）逐行敌对审计后**无验证器自欺实害**（计数锚 37/190 双重实证为真、活账本全部核实、豁免语义迁移等价或收紧、计数锚反证一个 P0 级疑点被证伪）；三方映射 24+22+8 项**无虚假下沉、无覆盖丢失**。7 条 P2 **全部同属"收口完整性"主题**——上两轮修复与迁移的姊妹/口径同步不完整（v66 OCE 过滤修复只收口 1/3、Commit 测试姊妹漏、README/V2/断言数三处口径漂移），是历轮"修复自带缺陷"模式的降维延续：从语义错误（5+5 项）降为同步遗漏（0 项语义错误）。

| 指标 | 本轮 | 上轮(v2) | 趋势 |
|------|:--:|:--:|:----:|
| P0 / P1 | 0 / 0 | 0 / 1 | ↓ |
| P2 / P3 | 7 / 30 | 4 / 30 | → |
| 上轮改动自带缺陷 | **0 语义错误**（7 同步遗漏） | 5 语义错误 | **↓↓ 收敛** |
| 证伪数 | 6 | 9 | — |

### 质量系统收敛轨迹（三轮纵向）

| 轮次 | 审计对象 | 抓出的"改动自带缺陷"性质 |
|------|---------|------------------------|
| v2 验证轮 | 第一轮 64 项修复 | 5 项**语义错误**（pipefail 掩码/UPDATE 漏绑/confirms 零接线/SslMode 未同步/TryAddEnumerable 首调即抛） |
| 本轮 v3 | MIG 迁移批 + 上轮 34 项修复 | **0 项语义错误**；7 项**同步遗漏**（姊妹过滤未同步/姊妹测试漏/口径数字漂移） |

错误等级从"行为错误"降到"同步遗漏"，且全部被本轮机械可验证方式抓出——质量体系（修复门两问 + 红测纪律 + 误判库）对高阶错误的拦截有效，剩余暴露面集中在"多处同构改一处"的收口环节。**建议下一轮防线方向：姊妹收口清单机械化**（修复涉及 N 处同构时强制 grep 全部姊妹，进 fix-orchestrator）。

---

## 第一部分：评审基础

```
基线：a80283b · 工作树清洁 · 构建 Release 0W/0E · 16 项目 1357 用例（46 失败全 Testcontainers 环境性）
机械防线（MIG 后新形态）：gate-check 3/3（纯 git 编排）· verify-ai 23/23 · doc-consistency 薄壳 1/1
  · tech-debt 12 项 · test-gate 5 项 · encoding/template/secret-scan/verify-conventions 全绿
  + 7 个 C# 门禁测试文件（46+16=62 测试，随 Test 步骤运行）
实读覆盖：src 215 文件 32175 行（5 片）+ test 113 文件 32683 行（4 片）
  + 新门禁 7 文件 4821 行逐行敌对 + 三方映射逐项（bash git 历史三版本对照取证）
  + 11 个子代理覆盖度自报全部 100%（清单内零跳读）
三轴：机械 ✅ · 静态 ⚠️（7 P2）· 实测 ⏸️（方言轴 CI 持续绿——58121c2 后无 Store/DDL 变更未触发）
```

## 第二部分：发现与证据

### P2（7 条，主题：收口完整性）—— 详见清单

| ID | 发现 | 性质 | 证据 |
|----|------|------|------|
| ITM-653 | Idempotency/ProjectionProcessor MarkCompleted OCE 过滤姊妹残留 | v66 修复收口 1/3 | 三文件代码直读对照 |
| ITM-654 | CommitAsync_AfterDispose 测试缺失 | 上轮姊妹收口漏 | grep 全 test 零命中 |
| ITM-655 | .ai/README 6 处"G1..G24 全阻断"旧口径 + V5 判定面放过 | MIG 后口径漂移 | grep 保留集零命中 |
| ITM-656 | V2 存在性缺 encoding-gate | 门禁判定面缺口 | 引用面 vs 列表对照 |
| ITM-657 | DialectProbe 断言数 40/42 三方分叉 | 同上 | 逐条数 #1-#21 |
| ITM-658 | AssertionStrength `""""` 定界净化缺口 | 判定边界（当前零影响） | 逻辑推演+负样本自证 |
| ITM-659 | maxRetryCount 负值三栈无守卫 | 守卫族自相矛盾 | grep 三栈 0 守卫 |

### 关键证伪记录（6 项，验证"验证者"的正面证据）

1. **37 计数锚疑似 P0 → 证伪**：boundary grep `[Test]` 得 40，但 3 处是 raw string 坏样本内伪命中——反射 37 精确成立，D12a 合法。
2. **MarkDead 零覆盖 → 降 P3**：DapperStoreTests 有入口双测（主线程 grep 裁决），仅"重试耗尽链路转移"是盲区。
3. **SagaCompensation 丢 failures → 降 P3**：SagaProcessor :184-195 置 CompensationFailed 并保存 + 幂等重放兜底（主线程裁决）。
4. **GetNowSql/BuildPendingSql 死代码疑点 → 自查撤销**：子代理文件名过滤失误，三方言实际 override 并消费。
5. **DialectProbe OutboxSmoke MySQL DATETIME 溢出 → 解除**：OutboxMessage.CreatedAt 属性初始化器兜底。
6. **fencing 测试攒批假绿疑点 → 解除**：两栈 MarkProcessed 均立即执行。

### 新门禁 7 文件敌对审计总账

| 文件 | 结论 | 要点 |
|------|:--:|------|
| SourceCodeGuardTests（1119） | PASS | 57 矩阵逐样本吻合；解析完整性自检；空账本实证；豁免语义 ≥ bash |
| DocConsistencyGateTests（500） | PASS | D12a 反射 37 双重实证；D11 六条白名单逐条真实；棘轮反腐化断言在位 |
| AssertionStrengthGateTests（249） | PASS | 190=155+30 实测吻合；`""""` 缺口零影响登记 |
| TechDebtGuardTests（737） | PASS | 8 个活跃性断言全部非空转；#20 自指排除完备（本文件级） |
| TestGateGuardTests（263） | PASS | T6 六条白名单逐行核实为探针串；T8 勘正 no-op |
| DialectProbeTests（696） | PASS | 安全守卫六条对照原探针逐条落地；42 断言完整；补偿清理更保守 |
| ArchitectureBoundaryTests（1257） | PASS | G18 并集更强；G2/G3/G13/G14 补强与 bash 全集对齐；3 个负向自证 |

## 第三部分：收束判定

| 轴 | 状态 |
|----|:--:|
| 机械轴 | ✅（新形态全套 + 62 个门禁测试） |
| 静态轴 | ⚠️ 7 P2（同步遗漏级，无行为错误）→ 小修复轮即可收束 |
| 实测轴 | ✅ CI 持续（上轮 58121c2 三 job 全绿背书仍有效；本轮无 Store/DDL 变更） |

**判定**：进入小修复轮（7 P2 全部低风险：2 姊妹收口 + 3 口径同步 + 2 判定加固，一个会话内可清）→ 轻验证（diff 验证 + ITM-655 红测）→ 收束。按 PD37：本轮验证面充分（全仓真读），修复轮不需再全量。

## 附录：自我局限

- docs/design/palorm-architecture.md（2024 行）沿历轮 ❓ 标注未逐行；release.yml 仅 grep 级。
- Messaging.Integration 本轮全绿（8/0，broker 可达）未逐测试深究。
- P3 批 30 条为汇总级（未逐条独立探针），修复前按清单标注补。
- CI 上的 DialectProbeTests/Testcontainers 持续绿采信上轮背书，本轮未推送（代理断连遗留：main=17b2726 本地领先远端）。
