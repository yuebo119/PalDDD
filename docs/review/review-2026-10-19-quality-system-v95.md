# Pal.DDD 评审报告（v95 · AI 质量系统全量运行轮）

> 报告编号：REVIEW-2026-10-19-v95
> 评审基准：commit `5207c69`（本轮修复提交紧随其后）
> 触发：用户令「全面运行当前系统的ai质量系统，并修复系统发现的问题」
> 前序：v94 质量系统优化轮（`review-2026-10-19-quality-system-v94.md`）

---

## 1. 执行摘要

**结论**：机械、静态、实测、发布、覆盖五轴全部跑通，无阻断失败。系统发现 1 项文档一致性缺陷（项目 EPSL 声明 v1.6 落后全局 v1.7），已在本轮修复并复验；变更面七流复审无 P0/P1/P2，2 项计数语义 P3 同轮收口；`license-policy` 在批量执行中出现一次零输出瞬时失败，直接复跑通过，归因运行环境而非产品缺陷。

| 轴 | 结果 |
|---|---|
| 机械轴 | Release 全量重建 0 警告 0 错误；format 0 项；第三方分析器 0 警告 |
| 静态轴 | verify-ai 30/30、gate-audit 42/42、guard 8/8、25 个脚本自测全过、Semgrep 29 规则 0 findings、漏洞 0 |
| 实测轴 | 16 个测试项目 1731 用例 = 1655 通过 + 76 跳过 + 0 失败；AgentSample 17 断言；AotSample 全过 |
| 发布轴 | PalOrmSample 与 AotSample 均 PublishAot 成功并运行通过 |
| 覆盖轴 | LOCAL 行覆盖率 70.75%（阈值 0.65）；CI 0.70 口径未在本机评估 |
| 月度轴 | Stryker 两个 curated 基线均 100%；EPSL quick 对账与 smoke 通过 |

---

## 2. 评审基础

### 2.1 覆盖度

- **必须检查**：HEAD 变更面 4 个生产文件全文（`PalDiagnostics.cs`、`DetachedStreamEndpoints.cs`、`EndpointExtensions.cs`、`StreamRunRegistry.cs`）、对应测试与文档、质量脚本与 CI 配置。
- **明确不检查**：src 全量地毯（本轮为变更面标准档，非全量档）；PG/MySQL 真库方言探针（本机无 Docker，由 CI `dialect-probe` 承担）；GitHub Actions 远端执行（本地命令逐项复现）。
- **抽样策略**：无范围外抽样。评审范围 = HEAD 变更面 + 质量系统全量运行结果。

### 2.2 基线

```
构建：Release no-incremental 0 警告 0 错误
format：0 项
分析器基线：0 == 0
gate-audit：43 脚本 / 25 接线 / 42 探针全过
verify-ai：V1-V30 全过
测试：16 项目 1731 用例 = 1655 通过 + 76 跳过 + 0 失败
覆盖率：LOCAL 70.75%（0.7074707470747075，阈值 0.65）
Semgrep：29 规则 / 284 文件 / 0 findings
变异：Compression 100.00% / Transactions 100.00%
```

---

## 3. 五轴运行结果

### 3.1 机械轴

| 检查 | 结果 | 证据 |
|---|---|---|
| 全量重建 | 通过 | `dotnet build PalDDD.slnx -c Release --no-incremental`，0/0 |
| IDE 风格 | 通过 | `check-all` 1/3 段，0 项 |
| CA 分析构建 | 通过 | `check-all` 2/3 段，0 错误 |
| 编译错误计数 | 通过 | `check-all` 3/3 段，0 项 |
| 第三方分析器基线 | 通过 | `quality-baseline`，0 条 == 基线 0 |

### 3.2 静态轴

| 检查 | 结果 | 说明 |
|---|---|---|
| verify-ai | 30/30 | V1-V30，0 警告 0 失败 |
| gate | 3/3 | G22/G23/G24 |
| gate-lite | 3/0 | G1-G3 |
| gate-audit | 42/42 | 隔离式变异探针全过 |
| guard | 8/8 | 8 道守卫 GREEN |
| verify-conventions | 通过 | `--quick` 静态检查 |
| tech-debt | 0 失败 | 9 通过 + 2 已知债 + 1 提示 |
| test-gate | 0 失败 | T4/T6/T8/T9/T11/T12 等 |
| encoding-gate | 0 失败 | E1-E5 |
| secret-scan | 通过 | 695 文件，0 高置信凭据 |
| count-audit | 通过 | 16 项推导 / 24 文件 / 81 处声明 |
| doc-consistency | 通过 | D7 + D13（跨仓契约 3） |
| config-policy | 通过 | A/B/C/D 四组 |
| license-policy | 通过 | 判定集 162 / 观察态 7 / 零消费 10 |
| xml-guard / dapper-param-guard | 通过 | 无待校验 XML；Dapper 枚举无直传 |
| template-gate | 通过 | 9 模板 / 13 代码块可编译 |
| changelog-check | 4/4 | C1-C4 |
| changelog-facts | 通过 | v4.0.0..HEAD 事实清单 |
| review-freshness | 通过 | 最近轮次 0 天；最近全量 30 天 |
| vuln-scan | 0 漏洞 | `dotnet list package --vulnerable` 输入 |
| Semgrep | 0 findings | p/csharp + p/security-audit，29 规则 |
| 门禁自测 | 25 脚本全过 | 含 ci-coverage 26/26、config-policy 34/34、license-policy 59/59、count-audit 47/47 |

### 3.3 实测轴

| 项目 | 总计 | 通过 | 跳过 | 失败 |
|---|---:|---:|---:|---:|
| Analyzers.Tests | 51 | 51 | 0 | 0 |
| CQRS.Tests | 44 | 44 | 0 | 0 |
| Compression.Tests | 46 | 46 | 0 | 0 |
| Core.Abstractions.Tests | 8 | 8 | 0 | 0 |
| Core.Tests | 323 | 323 | 0 | 0 |
| DependencyInjection.Tests | 178 | 178 | 0 | 0 |
| EventLog.Tests | 43 | 43 | 0 | 0 |
| Hosting.AspNetCore.Tests | 88 | 88 | 0 | 0 |
| Integration.Tests | 424 | 410 | 14 | 0 |
| Messaging.Integration.Tests | 18 | 10 | 8 | 0 |
| Messaging.Tests | 33 | 33 | 0 | 0 |
| PalORM.Tests | 168 | 114 | 54 | 0 |
| Projections.EventLog.Tests | 9 | 9 | 0 | 0 |
| Repository.EFCore.Tests | 15 | 15 | 0 | 0 |
| Serialization.Tests | 54 | 54 | 0 | 0 |
| Transactions.Tests | 229 | 229 | 0 | 0 |
| **合计** | **1731** | **1655** | **76** | **0** |

跳过 76 项均为环境性（Testcontainers 无 Docker / 本机外部实例未配置），与 v94 基线结构一致。AgentSample 17 条断言全过；AotSample 普通运行全过。

### 3.4 发布轴

| 样本 | PublishAot | 运行 |
|---|---|---|
| PalOrmSample | win-x64 成功 | 全部检查通过 |
| AotSample（含 §8 断线续跑） | win-x64 成功 | 全部检查通过，含 POST 启动、附着回放、幂等取消 |

### 3.5 覆盖轴

`ci-coverage.cs -- --local`：全局行覆盖率 **0.7074707470747075**，高于 LOCAL 阈值 0.65，单模块降幅门禁按 LOCAL 口径跳过。CI 0.70 口径与 PG/MySQL 车道由远端 job 评估。

### 3.6 月度与性能轴

- **Stryker**：Compression `100.00%`；Transactions `100.00%`；均 exit 0。
- **EPSL**：`eps-perf-check` 全局 v1.7 / 项目 v1.7，37 基准类 37/37 接入；`eps-run --quick` 的 `smoke` 车道通过。

---

## 4. 变更面七流复审

复审对象为 HEAD `5207c69` 的 4 个生产文件全文，加对应测试与文档。

| 流 | 结论 |
|---|---|
| 架构流 | Core 与 Hosting 依赖方向未变；`ReadJsonBodyOrWriteErrorAsync` private→internal 仅同程序集可见，无公共面扩张 |
| 安全流 | Activity tag 仅类型名，失败状态仅异常类型名；无请求内容、prompt、PII 或凭据进入遥测；POST 反序列化走源生成 JsonTypeInfo |
| 资源流 | Activity 在嵌套 finally 释放；run 自有 scope 的终态释放责任未变；取消令牌与附着释放路径未改 |
| 并发流 | 单写者泵模型未变；发现 `StreamAttached` 在注册表锁内递增，已移至锁外（见 P3-1） |
| 错误流 | OCE 取消语义、失败状态与错误帧路径未改；新增计数器不改变异常分类 |
| AOT 流 | 新增代码为静态字段、Counter、Activity 与普通泛型，无反射；AotSample §8 覆盖断线续跑端点 |
| 生成语义流 | 无生成器改动；公共 API 快照 +3 行（两个计数器字段 + 一个 POST 重载），计数门禁一致 |

---

## 5. 发现与修复

| ID | 级别 | 发现 | 证据 | 处置 |
|---|:--:|---|---|---|
| QS-95-1 | P2 | 项目 EPSL 声明 v1.6，全局已 v1.7，`eps-run --quick` 对账失败 | `eps-perf-check` 报「项目 EPSL 版本落后：项目 v1.6 vs 全局 v1.7」 | 修订 `AGENTS.md`、`perf-budgets.md`、`.ai/perf-ledger.md`、`docs/performance.md`、`bench/results/README.md`；复验 PASS |
| QS-95-2 | P3 | `StreamAttached` 在 `_gate` 锁内递增，MeterListener 用户回调会在注册表锁内执行 | 代码复审（`StreamRunRegistry.TryBeginAttach`） | 计数移到锁外；Hosting 88/88 复跑通过 |
| QS-95-3 | P3 | `StreamGap` 在帧写入前递增，写入抛异常时计数与事实不符 | 代码复审（`StreamRunRegistry.WriteGapFrame`） | 改为 `WriteRawFrame` 成功后递增；Hosting 88/88 复跑通过 |
| QS-95-4 | 非缺陷 | `license-policy` 批量执行一次 exit 1 且零输出 | 直接复跑通过（判定集 162）；同批次其他脚本正常 | 记为 `dotnet run` 瞬时运行异常，不立产品缺陷 |

修复后复验：Hosting 面板 88/88；`eps-perf-check` PASS；`eps-run --quick` 2 PASS；build 0/0。

---

## 6. 残余与边界

- PG/MySQL 方言探针：本机无 Docker，54 + 8 + 14 项环境性跳过；CI `dialect-probe` 覆盖。
- CI 远端未执行：本轮为本地全量复现；推送后由 `build-and-test`、`aot-verify`、`semgrep`、`coverage` 等 job 背书。
- 全量 src 地毯未做：本轮为变更面标准档，最近全量轮次仍为 2026-09-19（30 天，未超 90 天阈值）。
- 本轮无 P0/P1，P0/P1 修复时延不适用。

---

## 7. 指标

| 指标 | 值 |
|---|---|
| P0 / P1 / P2 / P3 | 0 / 0 / 1 / 2 |
| 逃逸 | 0 |
| 复发 | 0 |
| 证伪 | 1（license-policy 瞬时失败非产品缺陷） |
| P0/P1 修复时延 | 不适用 |
| 测试总量 | 1731（+7，来自 2026-10-19 断线续跑补强轮新增测试） |

---

## 8. 收束

五轴全绿，1 项 P2 文档一致性缺陷与 2 项 P3 计数语义同轮修复；月度变异双基线 100%；AOT 双样例发布运行通过。本轮账本同步：`.ai/review/metrics.md` v95 行、`.ai/perf-ledger.md` EPSL v1.7 版本对齐。
