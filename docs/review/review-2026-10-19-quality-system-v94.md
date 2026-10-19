# Pal.DDD 评审报告（v94 · 质量系统优化轮）

> 报告编号：REVIEW-2026-10-19-v94
> 评审基准：commit `17ba422` · 系统优化档（本轮运行经验回灌）
> 触发：用户令「根据 AI 质量系统运行的经验，优化 AI 质量系统」
> 前序：v93 运行轮（1 P0 + 5 P3，报告 `review-2026-10-19-quality-system-v93.md`）

---

## 执行摘要

**结论**：把 v93 运行中暴露的五类系统缺口转成机械防线，全部落在现有门禁与测试内，未新增独立脚本。`gate-audit` 探针 41→42，17 个测试项目 1720→1724 用例，全部通过。

| 缺口 | 本轮载体 |
|------|---------|
| 评审债判定被否定短语误触发 | `review-freshness.cs` 的 `IsFullRoundType` + 3 条 selftest |
| CI 锁文件带入 Windows 专用依赖 | `config-policy` 检查 D + 4 条 selftest + 隔离探针 + pre-commit 触发 |
| 测试文件重写静默丢失旧断言 | `fix-protocol.md` 步骤 5 + `test-change-guard` 删除提示 + 2 条 selftest |
| 双栈门面复制无对等测试 | `SqliteFtsJsonParityTests` 4 件（Dapper/PalORM 同输入逐字比对） |
| Semgrep 注解计数硬编码 | CI 文案去计数 + 注解协议入 CI/质量工具文档 |

---

## 第一部分：评审基础

### 1.1 覆盖度

- 必须检查：`scripts/config-policy.cs`、`scripts/review-freshness.cs`、`scripts/test-change-guard.cs`、`scripts/hook-pre-commit.cs`、`scripts/gate-audit.cs`、`.github/workflows/ci.yml`、`AGENTS.md`、新增双栈对等测试、`.ai/review/fix-protocol.md`、`.ai/lessons.md`、`.ai/review/metrics.md`、`.ai/README.md`。
- 明确不检查：src/ 产品代码（本轮零产品变更）；PG/MySQL 真库实测（无 Docker）。
- 抽样策略：无范围外抽样。

### 1.2 基线

```
gate-audit：43 脚本 / 25 接线 / 42 探针（本轮新增 config-policy D 探针）
verify-ai：V1-V30 全通过
测试：16 个测试项目 1724 用例 = 1648 通过 + 76 跳过 + 0 失败
构建：Release 0 警告 0 错误；format-verify 0 差异；分析器 0 警告
```

---

## 第二部分：五项优化与证据

### 2.1 review-freshness：否定短语不得重置全量时钟

- 现象：v93 的 metrics 类型列写「非全量地毯」，旧的裸 `Contains("全量")` 把最近全量从 30 天误刷成 0 天，静默洗白 90 天评审债。
- 修复：`IsFullRoundType` 先把「非全量/非全仓/不是全量/未做全量/不做全量」归一化为维护轮再匹配。
- 锁定：selftest 新增「肯定形态识别」「非全量不算」「未做全量不算」三例；`--selftest` 14/14 通过。
- 文档：`metrics.md` 头部增类型列约定（维护轮写肯定描述）。

### 2.2 config-policy 检查 D：CI requirements 平台标记

- 现象：CI run 39220268700 的 semgrep job 在安装阶段失败，`ci/requirements-semgrep.txt` 由 Windows uv 生成，`pywin32==311` 无平台标记，Linux runner 报 `No matching distribution`。
- 修复：新增检查 D，扫 `ci/requirements-*.txt` 的 Windows 专用包名单（pywin32 / pywin32-ctypes / wincertstore / pyreadline3 / windows-curses），要求 `sys_platform == "win32"` 或 `platform_system == "Windows"` 标记；读取暂存优先，文件不可读 fail-closed。
- 接线：pre-commit 触发条件加入 `ci/requirements-`；`AGENTS.md` 门禁表 #9 同步；`gate-audit` gateForms 与隔离探针同步（42/42 通过）。
- 锁定：selftest 34/34（新增 4 例：带 marker 放行、platform_system 等价放行、pywin32 无 marker 必红、wincertstore 无 marker 必红）。

### 2.3 测试文件重写/删除的防线迁移

- 现象：v93 P0 的根因是 `SqliteDialectTests` 删除时未迁移 `Extract_KeyWithSingleQuote_IsEscaped`，行为仍被执行覆盖，断言级防线消失。
- 修复：`fix-protocol.md` 新增步骤 5，要求逐条处置旧断言（迁移/有意淘汰/确认重复覆盖）；`test-change-guard` 在暂存集含 test/**.cs 删除时列出旧测试方法名（WARN，不阻断），并跳过注释形态。
- 锁定：`test-change-guard --selftest` 9/9（新增 2 例：提取 `[Test]` 方法名、无 `[Test]` 返回空）。
- 知识层：`lessons.md` OPS-12 登记，carrier 指向 fix-protocol 步骤 5 与 test-change-guard。

### 2.4 双栈门面同输入逐字对等

- 背景：ADR-032 的 Dapper 与 PalORM 门面是两份独立源码，共享核心只保证生成逻辑一致，门面参数校验/便利方法仍可单侧演进。
- 修复：`test/PalDDD.Integration.Tests/SqliteFtsJsonParityTests.cs` 4 件测试，覆盖 JSON 片段矩阵（含 `'`/`"`/`\` 与注入形态）、FTS external-content/standalone 定义、触发器名、JOIN、重建/优化/删除语句、静态片段。
- 锁定：Integration 项目 424 用例（410 通过 + 14 跳过 + 0 失败）；`.ai/README.md` 机械防线表登记。

### 2.5 Semgrep 维护协议

- 现象：CI 错误文案写死「7 处 nosemgrep 注解」，每次增删注解都要改文案；新 finding 的处理顺序只散落在提交历史里。
- 修复：文案改为「现有 nosemgrep 注解」；`ci.yml` 的 scan step 注释写明「先改代码，确属受控拼接时加同形注解」；`docs/quality-tooling-eval-2026-10-18.md` 增维护注记，同时记录 requirements 平台标记约束。
- 影响：无代码行为变化。

---

## 第三部分：验证与收束

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | gate-audit 42/42 · verify-ai 30/30 · guard 8/8 · config-policy selftest 34/34 · review-freshness 14/14 · test-change-guard 9/9 |
| 静态轴 | ✅ | format-verify 0 差异 · 分析器 0 · tech-debt 0 失败 · encoding/secret/count-audit/doc-consistency 全过 |
| 实测轴 | ✅ | 16 项目 1724 用例 0 失败；新增双栈对等测试实跑通过 |

**局限**：config-policy D 只守已知 Windows 专用包名单，新增平台专用包需同步名单；`test-change-guard` 的删除提示是 WARN，不构成阻断，最终仍靠修复协议的人工判断。

**元审计自检**：覆盖度全勾销 ☑ · 每个优化有 selftest/探针/测试三类证据之一 ☑ · 无评分与预测性判断 ☑ · 文档计数与实测一致 ☑。
