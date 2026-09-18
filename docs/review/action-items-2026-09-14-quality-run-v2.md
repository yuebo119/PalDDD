# Pal.DDD 修复清单 v2 — AI 质量系统全量运行（2026-09-14 次轮）

> 来源报告：[`docs/review/review-2026-09-14-quality-run-v2.md`](review-2026-09-14-quality-run-v2.md)
> 基线 commit：`b2ae2ec`（dev 分支）· 运行方式：22 组件 + 16 测试项目全真实跑，零缓存
> 编号衔接：首轮用到 ITM-680，本清单自 **ITM-681** 起。

---

## 总体进度

> 状态更新时间：2026-09-14（修复轮完成，3/3 闭环）

| 优先级 | 条目数 | 待处理 | 处理中 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|:------:|
| **P1** | 0 | 0 | 0 | 0 | — |
| **P2** | 1 | 0 | 0 | 1 | 100% |
| **P3** | 2 | 0 | 0 | 2 | 100% |
| **合计** | 3 | 0 | 0 | 3 | **100%** |

**次轮分析**：22 项运行 21 绿 / 1 环境红（PalORM 无 Docker，D1 在案）· 0 代码级缺陷 · 3 项新发现全部文档类。
**修复轮**：3/3 闭环——4 处文档修改（ITM-681 ×2 · ITM-682 · ITM-683）+ 门禁复验全绿。
**首轮 6 项（ITM-675~680）回归验证全部有效**（详见来源报告 §三）。

---

## 🟠 P2 — 计划修复（1 条）

### [x] ITM-681 · `.NET 11 另增` 两个验证 attribute——与官方文档不符（事实错误） · 文档 ✅
- **维度**：文档准确性（外部事实）
- **优先级**：P2 · 危害: 中（误导 .NET 10 目标用户的技术判断）· 复杂度: 易
- **问题**：`docs/usage.md:137` 与 `CHANGELOG.md:73` 称「.NET 11 **另增** `SkipValidationAttribute` / `ValidatableTypeAttribute`」。官方文档：这两个 attribute **在 .NET 10 已随 `Microsoft.Extensions.Validation` 包发布（experimental）**，.NET 11 起**不再标记 experimental**（转正）——是"转正"不是"新增"。
- **证据**：① [Validation in ASP.NET Core（.NET 10）](https://learn.microsoft.com/aspnet/core/fundamentals/validation?view=aspnetcore-10.0#experimental-api-in-apps-that-target-net-10)："published as experimental in .NET 10 … As of .NET 11, the attributes are no longer experimental"；② [.NET 11 发行说明](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-11?view=aspnetcore-10.0#minimal-apis) 专节 "Validation attributes are no longer experimental"。两处均于 2026-09-14 经 microsoft-docs 实查（非推断）。
- **背景**：该表述由 `17158fd` 引入；该提交其余事实（AddValidation / 未注册返回 200 而非 400 / 两类误用）经同批核实均准确，仅此一处转述偏差。
- **建议**：两处改为「（.NET 11 起这两个 attribute 不再标记 experimental——.NET 10 已随包发布）」。
- **验证**：`grep -n "另增" docs/usage.md CHANGELOG.md` → 零命中；改后 `verify-conventions --quick` 全绿。
- **修复**：两处均改为「两者 .NET 10 已随 `Microsoft.Extensions.Validation` 包发布，.NET 11 起不再标记 experimental」。
- **涉及**：`docs/usage.md`、`CHANGELOG.md`
- **状态**：✅ 已完成（2026-09-14 修复轮）

---

## 🟢 P3 — 低风险修正（2 条）

### [x] ITM-682 · rule-placement-audit §二 与 §四 对同一事项互斥 · 文档 ✅
- **维度**：文档内部一致性
- **优先级**：P3 · 危害: 低 · 复杂度: 易
- **问题**：`docs/review/rule-placement-audit-2026-09-13.md` §二 缺口表第 33-34 行 #5 写「**未接线**（见 §四）」、#6 写「未实现」；§四 第 92-93 行已标两行「✅ 已完成」（覆盖率门禁接线 `47c8c24` / 单模块降幅门禁 `080871c`）。同一文档两处互斥。
- **证据**：`b2ae2ec` commit message 声称「审计账本 §四 该行由未实现改为已完成」——与其 diff 一致（改的是 §四），但 §二 对应两行未同步；该提交自身注记的「动手前先查现状」教训恰是此矛盾会诱发的错误形态。
- **建议**：§二 #5/#6「本次处置」列同步为「✅ 已完成（47c8c24 / 080871c）」，保留指向 §四。
- **验证**：改后文档内 `grep -c "未接线\|未实现"` 的命中不再指向已完成的覆盖率门禁两行；doc-consistency/verify-conventions --quick 全绿。
- **修复**：§二 #5/#6「本次处置」列同步为「✅ 已完成（47c8c24 / 080871c），详见 §四」（"实测状态"列保留 09-13 时点记录不改）。
- **涉及**：`docs/review/rule-placement-audit-2026-09-13.md`
- **状态**：✅ 已完成（2026-09-14 修复轮）

### [x] ITM-683 · 覆盖率基线文档对 PalORM 的措辞与实现不符（附：守护空窗登记） · 文档 ✅
- **维度**：文档精确性 / 门禁覆盖面登记
- **优先级**：P3 · 危害: 低（过渡期状态，不产生假红）· 复杂度: 易
- **问题**：`docs/test-coverage-baseline.md`（第 80-81 行）称 PalORM「**其 line-rate 偏低，故基线偏保守**（CI 上更难触发降幅判定）」——暗示有低基线值受守护。实测：`coverage-baseline.json` 15 键**不含 PalORM**；`TestResults/` 无其 cobertura 产物；`UpdateBaseline` 只为有产物项目写键、`EnforceModuleDropLimit` 只遍历基线内键 → **PalORM 的覆盖率降幅当前完全不被检查**（非"宽松"而是"未纳入"）。
- **证据**：json 内容（15 键，逐键核对）· `find TestResults -name "coverage.*.cobertura.xml"`（15 个，无 PalORM）· `ci-coverage.cs` UpdateBaseline/EnforceModuleDropLimit 实现。
- **建议**：改为「`PalDDD.PalORM.Tests` 因本机无 Docker 无覆盖率产物、**未纳入基线**（其降幅当前不受 Step 6 检查），待 CI 首跑后按 `--update-baseline` 补齐」——把"守护空窗"显式写出，而非用"偏保守"模糊带过。
- **验证**：改后 `grep -n "偏保守" docs/test-coverage-baseline.md` 零命中或语境不再指 PalORM；`ci-coverage --selftest` 18/18 复跑仍过（纯文档改动，门禁行为不变）。
- **修复**：改为「因本机无 Docker 无 cobertura 产物、**未纳入基线**（`UpdateBaseline` 只为有产物的项目写键），其降幅当前不受 Step 6 检查……届时 PalORM 随产物齐全一并纳入」——守护空窗显式写出。
- **涉及**：`docs/test-coverage-baseline.md`
- **状态**：✅ 已完成（2026-09-14 修复轮）

---

## 附：首轮清单回归状态（ITM-675~680，全部有效）

| 项 | 次轮回归结果 |
|----|-------------|
| ITM-675 环境（服务器） | ✅ 2026-09-14 v2 轮健康：Messaging 8/8；D2 时间线记录已含三次变动 |
| ITM-676 AMQP 协议级预检 | ✅ 真实执行路径生效（8/8 全跑，非跳过） |
| ITM-677 D2 状态更新 | ✅ 在位 |
| ITM-678 AGENTS CI 段 | ✅ `path-gate` 零残留；与 ci.yml 4 job 一致 |
| ITM-679 .sh 引用修正 | ✅ 三文档零残留 |
| ITM-680 V9 边界登记 | ✅ V9 PASS；注释在位 |

**排除项（设计内行为，勿重提）**：PalORM 46 项（无 Docker，D1）· Integration 14 项跳过（外部连接串优雅降级）· `[Obsolete]` 6 处（B3 排队）· verify-conventions full 无 Docker 必红（testing.md 已标注前提）。

---

## 进度追踪

**首轮**（e51d48b）：19 项运行 · 6 项发现 → **6/6 闭环**（a524e85 + acfacfa + 94bb857）。
**次轮**（b2ae2ec）：22 项运行（21 绿 / 1 环境红）· 3 项发现 → **3/3 闭环**（2026-09-14 v2 轮提交）。
**复验**：encoding-gate 5/5 · verify-conventions --quick 全过 · changelog-check 5/5 · doc-consistency D7 · verify-action-items 0 缺失。
