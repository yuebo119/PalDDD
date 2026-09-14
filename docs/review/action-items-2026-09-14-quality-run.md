# Pal.DDD 修复清单 — AI 质量系统全量运行（2026-09-14）

> 来源报告：[`review-2026-09-14-quality-run.md`](review-2026-09-14-quality-run.md)
> 基线 commit：`e51d48b`（dev 分支）· 运行方式：16 门禁脚本 + 16 测试项目全真实跑，零缓存
> 编号衔接：最近在用编号为 ITM-674（open-items X1 基准），本清单自 **ITM-675** 起。

---

## 总体进度

> 状态更新时间：2026-09-14（分析轮产出，修复未开始）

| 优先级 | 条目数 | 待处理 | 处理中 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|:------:|
| **P1**（环境阻塞） | 1 | 1 | 0 | 0 | 0% |
| **P2** | 2 | 2 | 0 | 0 | 0% |
| **P3** | 3 | 3 | 0 | 0 | 0% |
| **合计** | 6 | 6 | 0 | 0 | **0%** |

**本轮是分析轮**：质量系统 19 项运行结果中 17 项全绿、0 代码级缺陷；6 项发现全部是环境/文档/门禁范围类，无生产代码修改项。

---

## 🟡 P1 — 环境阻塞（1 条）

### [ ] ITM-675 · 192.168.200.120 RabbitMQ 服务器 AMQP 层无响应 · 环境
- **维度**：环境（本机集成测试阻塞）
- **优先级**：P1 · 危害: 中（阻塞本地验证，不阻塞 CI——CI 用 Testcontainers）· 复杂度: 易（服务器侧操作）
- **问题**：RabbitMQ 服务器 TCP 5672 端口可达（实测 12ms），但 AMQP 协议握手无响应（`connection.start` 帧 18.9s 超时）→ `Messaging.Integration.Tests` 5 个 Rabbit 测试各 ~19s 假失败。
- **证据**：① 隔离探针 TCP 探活 `connected=True` 12ms；② 失败堆栈 `connection.start was never received`；③ Kafka 轴协议级预检识别环境问题 → 3 测试正确跳过（对照）。
- **建议**：服务器侧检查/重启 RabbitMQ 服务；若该模式反复出现（D2 已记录同型故障），考虑在服务器侧加服务健康检查。
- **验证**：重启后跑 `dotnet test test/PalDDD.Messaging.Integration.Tests/PalDDD.Messaging.Integration.Tests.csproj --no-build -c Debug` → 期望 8/8 通过（或 Rabbit 预检 Skip 转为执行）。
- **涉及**：服务器运维（仓库外）
- **状态**：⬜ 待处理（需用户侧操作）

---

## 🟠 P2 — 计划修复（2 条）

### [ ] ITM-676 · RabbitMQ 预检升级为 AMQP 协议级探测（与 Kafka 轴对称） · 代码
- **维度**：可维护性 / 防线完整性
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`BrokerIntegrationTests.cs:135-153` 的 Rabbit 预检只做 TCP 探活，对"TCP 通、AMQP 死"故障模式判 true → 与预检注释（`:136`）声称的"broker 不可达时显式 Skip 而非 19s×3 假失败"不符；Kafka 轴（`:112-133`）已是协议级（GetMetadata），两轴深度不对称。
- **证据**：本次实测（ITM-675 环境）Rabbit 预检 true 但 5 测试各 18.9s 假失败；D2 记录该故障模式两度出现——会重复造访。
- **建议**：预检改为 AMQP 协议级——`ConnectionFactory.CreateConnectionAsync()` 包 `.WaitAsync(5s)`（含凭据，探测即完整握手语义）；失败 → `RabbitAvailable = false` → 测试 Skip。改动集中在 `InitializeAsync` 的 Rabbit 预检块（`:137-153`）。
- **验证**：① 现状环境（AMQP 无响应）重跑 → Rabbit 测试 Skip 而非 19s×3 失败；② 正常环境（若可恢复）重跑 → 测试执行且通过（S3 反向验证：确认预检不误伤健康服务器）。
- **涉及**：`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs`
- **状态**：⬜ 待处理

### [ ] ITM-677 · open-items D2 环境声明更新（"当前就绪"已失实） · 文档
- **维度**：文档一致性 / 跨会话数据准确性
- **优先级**：P2 · 危害: 中（误导后续会话判断）· 复杂度: 易
- **问题**：`open-items-2026-09-14.md:41`（D2）称 192.168.200.120"当前就绪，全部实测通过"；本次实测 RabbitMQ / Kafka 均不达标（ITM-675）。
- **证据**：同 ITM-675；且 D2 自身记录该服务器"曾两度变动"——状态声明有效期短。
- **建议**：改为带时间戳的观测（"截至 2026-09-14 上午实测通过"）或直接改为"状态不稳定，使用前先实测"；并把 ITM-675 登记进 D 类。
- **验证**：`grep -n "当前就绪" docs/review/open-items-2026-09-14.md` → 零残留或改写为时间戳形态。
- **涉及**：`docs/review/open-items-2026-09-14.md`
- **状态**：⬜ 待处理

---

## 🟢 P3 — 低风险修正（3 条）

### [ ] ITM-678 · AGENTS.md `path-gate` 表述与 CI 实际结构不符 · 文档
- **维度**：文档一致性（三方一致红线）
- **优先级**：P3 · 危害: 低 · 复杂度: 易
- **问题**：`AGENTS.md:44` 把 `path-gate` 列为 CI job；实际 ci.yml 仅 4 job（build-and-test / aot-verify / coverage / dialect-probe），"Path gate"是 dialect-probe job 内的路径过滤步骤（`ci.yml:246-260`，`id: paths`）。`git log --all -S "path-gate"` 全历史仅命中 `14cc980`（写 AGENTS.md 的提交），ci.yml 从未有该 job。
- **建议**：改为"`dialect-probe`（Testcontainers PG/MySQL；内含 Path gate 路径过滤步骤）"。
- **验证**：修改后 `grep -n "path-gate" AGENTS.md` 零命中；`grep -c "timeout-minutes" .github/workflows/ci.yml` = 4（job 数一致）。
- **涉及**：`AGENTS.md`
- **状态**：⬜ 待处理

### [ ] ITM-679 · 3 个文档共 4 处引用已删除的 .sh 路径 · 文档
- **维度**：文档一致性（V9 类断链）
- **优先级**：P3 · 危害: 低 · 复杂度: 易
- **问题**：主仓 `scripts/` 已 0 个 .sh（MIG-012 全 C# 化），活引用未同步：
  - `docs/testing.md:475`：`scripts/verify-conventions.sh` → `.cs`
  - `docs/pitfalls.md:139`（SE2）：`scripts/secret-scan.sh` → `.cs`
  - `docs/release.md:642`：`scripts/changelog-facts.sh`、`scripts/changelog-check.sh` → `.cs` ×2
- **证据**：`ls scripts/*.sh` = 0；上述 grep 命中为反引号/表格形态（详见 ITM-680）。
- **建议**：逐处改 `.cs`；`docs/design/`、`docs/review/` 中的 .sh 提及属历史叙述，不动。
- **验证**：`grep -rn "scripts/[a-z0-9-]*\.sh" docs/ README.md README.en.md` 排除 `已删除|迁移|design/|review/` 后应零命中。
- **涉及**：`docs/testing.md`、`docs/pitfalls.md`、`docs/release.md`
- **状态**：⬜ 待处理

### [ ] ITM-680 · V9 守护盲区裁决：非命令形态路径引用是否纳入 · 门禁
- **维度**：门禁覆盖面
- **优先级**：P3 · 危害: 低 · 复杂度: 中（需防误报设计）· **待裁决项**
- **问题**：`verify-conventions.cs:161-181`（V9）只抽"命令形态"（`bash X.sh` / `dotnet run X.cs`）；反引号/表格形态（`| scripts/x.sh |`）不在范围——ITM-679 的 3 处失实即该盲区产物，且 `--quick` 每次提交都跑 V9 却测不到。
- **选项**：① 扩展 V9 覆盖反引号路径形态（`IsHistoricalMention` 兜底扩关键词，接受一定误报率）；② 维持现状（命令形态是"会执行的"，优先级本就更高；反引号形态靠人工/评审轮发现）。
- **建议**：倾向 ②——V9 头注释已声明范围为命令形态是刻意决策；若选 ① 需先做误报率实测（全仓扫一遍统计真/假阳性）。
- **验证**：若选 ①——新增自测用例（正/负例）+ 全仓实跑误报清单评审；若选 ②——在 V9 注释中登记"反引号形态不覆盖"的已知边界。
- **涉及**：`scripts/verify-conventions.cs`
- **状态**：⬜ 待裁决

---

## 附：本轮排除项（已核实为设计内行为，勿重提）

| 项 | 结论 |
|----|------|
| PalORM.Tests 46 项本机失败 | D1 在案：无 Docker + Fixture fail-closed 设计（防误连外部库）；CI 容器环境全跑 |
| Integration.Tests 14 项跳过 | 设计内优雅降级："当前配置指向外部连接串，跳过"；与 PalORM 的 fail-closed 是不对称但有意为之 |
| `[Obsolete]` 6 处残留 | tech-debt WARN 在案：均有 v3.0 移除计划（B3 排队） |
| verify-conventions full 本机必红 | 该模式定位为开发者全量自检，无 Docker 机器不可完成；非 CI 步骤。可选：在 testing.md 标注前提（未单列条目，归入文档整理） |

---

## 进度追踪

**本轮（分析轮）**：质量系统 19 项运行完毕（17 绿 / 2 环境红）· 6 项发现全部成文（本清单）· 0 生产代码修改。
**下一步**：① 用户处理 ITM-675（服务器侧）；② P2/P3 文档修正可直接执行（5 项中 4 项为秒级改动）；③ ITM-676 代码改动建议在 ITM-675 恢复前后各验证一次（覆盖 Skip 与执行两条路径）。
