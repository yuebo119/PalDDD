# Pal.DDD 修复清单 — AI 质量系统全量运行（2026-09-14）

> 来源报告：[`review-2026-09-14-quality-run.md`](review-2026-09-14-quality-run.md)
> 基线 commit：`e51d48b`（dev 分支）· 运行方式：16 门禁脚本 + 16 测试项目全真实跑，零缓存
> 编号衔接：最近在用编号为 ITM-674（open-items X1 基准），本清单自 **ITM-675** 起。

---

## 总体进度

> 状态更新时间：2026-09-14（修复轮完成，6/6 闭环）

| 优先级 | 条目数 | 待处理 | 处理中 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|:------:|
| **P1**（环境） | 1 | 0 | 0 | 1 | 100% |
| **P2** | 2 | 0 | 0 | 2 | 100% |
| **P3** | 3 | 0 | 0 | 3 | 100% |
| **合计** | 6 | 0 | 0 | 6 | **100%** |

**分析轮**：质量系统 19 项运行 17 绿 / 2 环境红、0 代码级缺陷；6 项发现全部成文。
**修复轮**：5 项修复执行 + 1 项环境自我消解（服务器恢复）；全部门禁复验通过（encoding-gate 5/5 · verify-conventions --quick · guard 8/8 GREEN · Messaging 集成 8/8）。

---

## 🟡 P1 — 环境（1 条，已闭环）

### [x] ITM-675 · 192.168.200.120 RabbitMQ 服务器 AMQP 层无响应 · 环境 ✅
- **维度**：环境（本机集成测试阻塞）
- **问题**：RabbitMQ 服务器 TCP 5672 端口可达（实测 12ms），但 AMQP 协议握手无响应（`connection.start` 帧 18.9s 超时）→ `Messaging.Integration.Tests` 5 个 Rabbit 测试各 ~19s 假失败。
- **证据**：① 隔离探针 TCP 探活 `connected=True` 12ms；② 失败堆栈 `connection.start was never received`；③ Kafka 轴协议级预检识别环境问题 → 3 测试正确跳过（对照）。
- **修复结果**：**服务器自行恢复**（2026-09-14 约 14 时，无需本仓动作）。修复轮复测 `Messaging.Integration.Tests` 8/8 通过（14.8s，Kafka/Rabbit 两轴全部真实执行）——5 失败 + 3 跳过全部消解。
- **验证**：`dotnet test test/PalDDD.Messaging.Integration.Tests/PalDDD.Messaging.Integration.Tests.csproj --no-build -c Debug` → 总计 8 / 失败 0 / 成功 8 ✅（修复轮实测）
- **收口**：`open-items-2026-09-14.md` D2 已更新为"状态不稳定，使用前先实测"并记录三次变动时间线。
- **状态**：✅ 已闭环（环境自愈）

---

## 🟠 P2 — 计划修复（2 条，已完成）

### [x] ITM-676 · RabbitMQ 预检升级为 AMQP 协议级探测（与 Kafka 轴对称） · 代码 ✅
- **维度**：可维护性 / 防线完整性
- **问题**：`BrokerIntegrationTests.cs` 的 Rabbit 预检只做 TCP 探活，对"TCP 通、AMQP 死"故障模式判 true → 与预检注释声称的"broker 不可达时显式 Skip 而非 19s×3 假失败"不符；Kafka 轴已是协议级（GetMetadata），两轴深度不对称。
- **修复**：预检改为 AMQP 协议级握手——`ConnectionFactory.CreateConnectionAsync(cts.Token)` + `RequestedConnectionTimeout=5s`（socket 层）+ CTS 5s（握手阶段）双层超时；成功后释放连接（`await using`，沿 703 行先例）。`RequestedConnectionTimeout` / `CreateConnectionAsync(CancellationToken)` / `IConnection.IsOpen` 三 API 均经 RabbitMQ.Client 7.2.2 XML 文档核证（非凭记忆）。
- **验证三重（S3 反向验证，`%TEMP%` 隔离探针）**：
  1. **缺陷复现**：本机假 TCP 服务器（接受连接、永不发 AMQP 帧）——旧逻辑 TCP 探活判 `true`（误判证据），新逻辑判 `false`（正确识别半坏，10s 含内部重试）。
  2. **不误伤**：新逻辑对真服务器判 `true`（25ms）。
  3. **端到端**：修复后集成测试 8/8 通过（健康环境真实执行，预检 true 路径）。
- **涉及**：`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs`（`InitializeAsync` 的 Rabbit 预检块）
- **状态**：✅ 已完成（2026-09-14 修复轮）

### [x] ITM-677 · open-items D2 环境声明更新（"当前就绪"已失实） · 文档 ✅
- **维度**：文档一致性 / 跨会话数据准确性
- **问题**：`open-items-2026-09-14.md:41`（D2）称 192.168.200.120"当前就绪，全部实测通过"；分析轮实测 RabbitMQ / Kafka 均不达标（ITM-675）——状态声明有效期短。
- **修复**：D2 改为"**状态不稳定，使用前先实测**"并记录 2026-09-14 三次变动时间线（早间通过 → 13 时半坏（ITM-675）→ 约 14 时恢复 8/8），保留"半坏特征 = TCP 开、协议无响应"的识别口径。
- **验证**：`grep -n "当前就绪" docs/review/open-items-2026-09-14.md` → 零残留 ✅
- **涉及**：`docs/review/open-items-2026-09-14.md`
- **状态**：✅ 已完成（2026-09-14 修复轮）

---

## 🟢 P3 — 低风险修正（3 条，已完成）

### [x] ITM-678 · AGENTS.md CI 段与实际结构校准 · 文档 ✅
- **维度**：文档一致性（三方一致红线）
- **问题**：原 CI 段把 `path-gate` 列为独立 job（实际是 dialect-probe job 内的路径过滤步骤，`ci.yml:246-260`）；且漏记 dapper-param-guard（CI 实际有）与 template-gate（实际有）、把 gate-lite 写成主链一环（实际是"无 .ai 时"降级路径）。
- **修复**：改为"`build-and-test`（vuln-scan → restore → build → test → secret-scan → **dapper-param-guard** → verify-ai → gate → encoding/doc-consistency/tech-debt/test-gate → **template-gate**；无 `.ai` 时降级为 gate-lite）· ... · `dialect-probe`（Testcontainers PG/MySQL；**job 内含 Path gate 路径过滤步骤**，仅 Store/SQL/DDL/映射面变更触发——非独立 job）"。
- **验证**：`grep -n "path-gate" AGENTS.md` 零命中；与 ci.yml 4 job 逐一对照 ✅（修复轮实测）
- **涉及**：`AGENTS.md`
- **状态**：✅ 已完成（2026-09-14 修复轮）

### [x] ITM-679 · 3 个文档共 4 处引用已删除的 .sh 路径 · 文档 ✅
- **维度**：文档一致性（V9 类断链）
- **问题**：主仓 `scripts/` 已 0 个 .sh（MIG-012 全 C# 化），活引用未同步。
- **修复**（4 处）：
  - `docs/testing.md:475`：`scripts/verify-conventions.sh` → `.cs`
  - `docs/pitfalls.md:139`（SE2）：`scripts/secret-scan.sh` → `.cs`（并补记 pre-commit 也为该防线触发点）
  - `docs/release.md:642`：`scripts/changelog-facts.sh`、`scripts/changelog-check.sh` → `.cs` ×2
- **验证**：`grep -rn "scripts/[a-z0-9-]*\.sh" docs/ README.md README.en.md` 排除 `已删除|迁移|design/|review/` → 零命中 ✅（修复轮实测）
- **涉及**：`docs/testing.md`、`docs/pitfalls.md`、`docs/release.md`
- **状态**：✅ 已完成（2026-09-14 修复轮）

### [x] ITM-680 · V9 守护盲区裁决：非命令形态路径引用维持现状 + 登记边界 · 门禁 ✅
- **维度**：门禁覆盖面
- **问题**：`verify-conventions.cs` V9 只抽"命令形态"（`bash X.sh` / `dotnet run X.cs`）；反引号/表格形态（`| scripts/x.sh |`）不在范围——ITM-679 的 3 处失实即该盲区产物。
- **裁决（方案②）**：维持现状（命令形态是"会执行的"，优先级本就更高；扩展反引号形态含历史叙述会推高误报率）。**已在 V9 块头注释登记该已知边界**（含 ITM-679 实例与"若扩展需先做全仓误报率实测"的前置条件）。
- **验证**：`dotnet run scripts/verify-conventions.cs -- --quick` 全过（V9 PASS）✅（修复轮实测）
- **涉及**：`scripts/verify-conventions.cs`（V9 注释）
- **状态**：✅ 已完成（2026-09-14 修复轮；边界已文档化，防再提）

---

## 附：本轮排除项（已核实为设计内行为，勿重提）

| 项 | 结论 |
|----|------|
| PalORM.Tests 46 项本机失败 | D1 在案：无 Docker + Fixture fail-closed 设计（防误连外部库）；CI 容器环境全跑 |
| Integration.Tests 14 项跳过 | 设计内优雅降级："当前配置指向外部连接串，跳过"；与 PalORM 的 fail-closed 是不对称但有意为之 |
| `[Obsolete]` 6 处残留 | tech-debt WARN 在案：均有 v3.0 移除计划（B3 排队） |
| verify-conventions full 本机必红 | 该模式定位为开发者全量自检，无 Docker 机器不可完成；非 CI 步骤（未单列条目） |

---

## 进度追踪

**分析轮**：质量系统 19 项运行完毕（17 绿 / 2 环境红）· 6 项发现全部成文 · 0 生产代码修改。
**修复轮（2026-09-14）**：6/6 全部闭环——ITM-675 环境自愈复测 8/8 · ITM-676 代码修复 + 反向验证三重 · ITM-677/678/679 文档修正 · ITM-680 边界登记。门禁复验：encoding-gate 5/5 · verify-conventions --quick 全过 · guard 8/8 GREEN · check-all 全零 · Messaging 集成 8/8。
**遗留（移交）**：无本清单内遗留项；ITM-675 的服务器稳定性观测归 `open-items` D2 持续跟踪。
