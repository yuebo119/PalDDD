# AI 质量系统全面审计（2026-10-18）

> 审计对象：Pal.DDD `.ai` 四系统（review/gate/refine/test）+ 主仓机械门禁 + 质量工具试点。
> 基线：`dev` 分支，审计时 HEAD `90aca94`（ADR-031 实施后）。
> 结论口径：机械层 A 级，知识层 C 级；本轮已按建议完成 V30 门禁与全部修复，验证证据见 §5。

---

## 1. 结论

门禁判定层可信：40 个脚本中 23 个接线门禁全部有自证，全量隔离变异探针 30/30 通过，`verify-ai` V1-V29 与自测全过，默认 Release 构建 0 警告 0 错误。问题集中在 `.ai` 知识层：活文档有 27 处计数/版本锚点过期（V30 首跑实测），另有 2 处活死路径；这些位置不在任何机械扫描面内，所以整套自检仍然全绿。

这不是门禁失效，而是覆盖面的错位：判定仪器检查文件存在、命令形态与少量计数，Agent 实际阅读 ADR 范围、测试规模、快照覆盖与门禁版本。上一轮 2026-09-21 审计的 F-09/G-08 已手工校准过同一批数字，26 天后再次漂移。按 `.ai/lessons.md` 的 REC-1，本轮把对账做成 V30 门禁，而不是再做一次手工勘正。

## 2. 审计基线（本轮实测）

| 检查 | 结果 |
|---|---|
| `dotnet run scripts/gate-audit.cs`（全量） | 30/30 探针通过；23 接线门禁 OK；0 UNVERIFIED/UNWIRED/REVIEW |
| `dotnet run scripts/verify-ai.cs` | V1-V29 全过（审计时） |
| `dotnet run scripts/verify-ai.cs -- --selftest` | 全部通过 |
| `dotnet run scripts/count-audit.cs` | 16 项推导 · 22 文件 · 78 处声明一致 |
| `dotnet run scripts/doc-consistency.cs` | D7/D13 通过 |
| `dotnet run scripts/license-policy.cs` | 162 包全过 |
| `dotnet build PalDDD.slnx -c Release` | 0 警告 0 错误 |

## 3. 发现

### P0 知识层锚点漂移（27 处）

| 事实 | 文档现值（审计时） | 实测真值 | 代表位置 |
|---|---|---|---|
| ADR 数 | 24 / 22 / 17 | **31** | `.ai/README.md:88,117,164`；`AGENTS.md:124`；`.ai/review/prompt.md:14,126,132`；`.ai/lessons.md:301` |
| 边界断言 | 89 | **99** | `.ai/README.md:88`；`.ai/gate/prompt.md:12`；`.ai/test/prompt.md:267` |
| 公共 API 快照 | 12 程序集，路径写 `Core.Tests` | **30 程序集**，实际 `Integration.Tests` | `.ai/README.md:89`；`.ai/gate/sensor-ledger.md:21`；`.ai/review/engine.md:158`；`.ai/test/prompt.md:268` |
| 根脚本 | 32 | **40**（审计时；修复轮新增 quality-baseline 后 41） | `.ai/README.md:129` |
| verify-ai 范围 | V1-V24 / V1-V25 / V1-V29 | **V1-V30**（本轮新增 V30） | `.ai/gate/prompt.md:41`；`.ai/gate/sensor-ledger.md:12`；`.ai/review/engine.md:33`；`.ai/review/review-charter-v2.md:179`；`AGENTS.md:38` |

活死路径：`.ai/README.md:89` 的快照测试路径；`.ai/review/action-items-p3-backlog.md:3` 指向已迁移的报告。历史引用（`metrics.md` 旧报告路径、`lessons.md` 案例引用）已加时点标记，不改写历史。

### 纠正：37 测试方法不是过期锚点

审计初稿用 `grep -c "[Test]"` 得到 44，据此判断“37 已过期”。V30 实现时用注释/raw string 感知的计数器复算，真实方法数为 **37**：44 中有 4 处是注释、3 处是 `ArchitectureBoundaryTests` 自身的坏样本字符串。文档的 37 一直正确，只有 89 断言点过期。这是“文本 grep 假阳性”的又一实证，已固化在 V30 的 `CountTestMethods` 与 ST-V30c 自测中。

### P1 `.ai` 运营韧性

`.ai` 无 remote、无 `.github`，140 个跟踪文件单副本；pre-commit 手动激活，原先只跑 `verify-ai`，`--no-verify` 可绕过。本轮补 secret-scan（`--root` 模式）并在 README 写入 bundle 备份命令；remote 仍需用户提供。

### P1 review 系统活性与台账分裂

`metrics.md` 最后一轮 v91（2026-09-19），10 月无正式 review 轮；EPSL 审计落在 `.ai/perf-ledger.md` 与 `docs/review/epsl-*`，不在 review 台账。本轮增加 pre-push 新鲜度告警（信息性不阻断）与 `metrics.md` 跨系统审计索引，并明确 EPSL/特性审计不计入 review 轮次。

### P2 质量工具试点

工作树有未提交的分析器试验与两个 `.bak` 备份；whitespace 实测 3 文件/14 条（评估文档写 2 文件）；Stryker 被 MTP 阻塞；Semgrep 6/6 误报。本轮完成规则子集、基线棘轮、whitespace 门禁与备份清理。

## 4. 修复清单

| 项 | 落点 |
|---|---|
| V30 锚点对账门禁 | `scripts/verify-ai.cs`（运行时推导 ADR/方法数/断言数/快照程序集与路径/根脚本数/V 上限；历史时点豁免；ST-V30a/b/c） |
| V30 隔离探针 | `scripts/gate-audit.cs`（注入陈旧 ADR 声明必红）+ `gateForms` 声明 |
| 活文档锚点修正 | `.ai/README.md`、`lessons.md`、`gate/prompt.md`、`gate/sensor-ledger.md`、`review/prompt.md`、`review/engine.md`、`review/review-charter-v2.md`、`test/prompt.md`、`AGENTS.md` |
| 历史时点标记 | `lessons-learned-v51.md` 头部、`lessons.md` 案例路径、`metrics.md` 历史路径说明 |
| count-audit 纳入 AGENTS.md | `scripts/count-audit.cs`（扫描面 + 3 条夹具；ADR/src-files 两条假阳性正则修正；selftest 47 例） |
| `.ai` 提交 secret-scan | `scripts/secret-scan.cs --root`、`.ai/scripts/hook-pre-commit.cs`、`.ai/README.md` |
| review 新鲜度 + 索引 | `scripts/hook-pre-push.cs`（纯函数 + 4 条 selftest）、`.ai/review/metrics.md`、`.ai/review/prompt.md` |
| 质量工具收敛 | `.editorconfig` 规则子集、`scripts/quality-baseline.cs`、`scripts/quality-analyzer-baseline.txt`、`.github/workflows/ci.yml`（whitespace 步骤 + quality-analyzers job）、whitespace 修复 3 文件 |
| 工作树卫生 | 两个 `.bak-20261018` 移至 `%TEMP%/palddd-bak-20261018/` |
| 跨仓契约 v3 | `.ai/README.md` 契约版本 3；`scripts/doc-consistency.cs` D13 期望 3 |

## 5. 验证证据（本轮实测）

> **时点说明**：本节为审计时读数（2026-10-18 白天，quality-baseline 642→280）；最终关闭读数为分析器 0、基线 0、Semgrep 0、Stryker 100%/100%，见 §7。

| 检查 | 结果 |
|---|---|
| `verify-ai` | 30/30 通过（V30 首跑 27 处红 → 修正后绿） |
| `verify-ai --selftest` | 全部通过（含 ST-V30a/b/c） |
| `gate-audit` 全量 | **31/31 探针通过**；quality-baseline 矩阵 `OK`（接线 + 自证） |
| `count-audit` | 16 项推导 · 23 文件 · 79 处声明一致；selftest 47/47 |
| `doc-consistency` | D7/D13 通过（契约 3） |
| `dotnet format whitespace --verify-no-changes` | exit 0（修复前 3 文件/14 条） |
| `quality-baseline` | 642 → **280**（规则子集后）；棘轮 280 == 280 |
| `encoding-gate` | 0 失败 |
| `secret-scan` | 主仓 662 文件通过；`.ai` 134 文件通过 |
| `license-policy` | 162 包通过 |
| `dotnet build -c Release` | 0 警告 0 错误 |

## 6. 遗留项关闭（2026-10-18 晚）

1. `.ai` 无 remote：按用户裁决保持本地库，不添加 remote；新增 `scripts/backup-bundle.cs`（默认 ~/Backups），README 保留 bundle/恢复演练说明。
2. Stryker：MTP runner 已验证（Stryker 5.0.0 `--test-runner mtp`），两个 `stryker-config.json`（break 95）+ 独立月度/手动 `mutation.yml` + JSON/HTML artifact；CompressionProvider 100%、Transactions/MySqlLeasePolicy 100%（229/229）。
3. Semgrep：6/6 误报逐条 `nosemgrep` 依据；当前 0 findings；CI `semgrep` job 含 29 规则 canary、SARIF artifact、`uv` 哈希锁。
4. 分析器：642 → 280（规则子集）→ **0**；MA0015 按路径重启用公共/领域面（18 条修/精确抑制），S2077 站点级抑制；基线 0 棘轮。
5. review：pre-push 调用 `scripts/review-freshness.cs`（stale 无豁免阻断）；**v92 维护轮 2026-10-18 落账，豁免文件已移除**，metrics 恢复新鲜。
6. `.ai` 不入主仓 CI 的裁决不变；守卫仍依赖每 clone 手动 `git config core.hooksPath .githooks`（README 已写明）。
7. 新控制探针：quality-baseline（`--from-log` 正反例）、review-freshness（stale/豁免/fresh 三例）、xml-guard、vuln-scan、test-change-guard；gate-audit **39/39**，20 个门禁有探针。剩余 5 个为 meta/orchestrator/diagnostic（guard、hook-pre-commit、hook-pre-push、ci-failed-tests、gate-audit 自身），其组件或纯函数已覆盖。
8. `.ai` 传感器台账新增分析器基线/Semgrep/mutation/review 债四行；README v2.5 与评估文档历史态/最终态已标时点；工具版本以 manifest/CI 为单一来源。

## 7. 关闭验证（2026-10-18 晚）

| 检查 | 结果 |
|---|---|
| `gate-audit` 全量 | **39/39 探针**；20 门禁有探针；0 UNVERIFIED/UNWIRED/REVIEW |
| `verify-ai` / `count-audit` | 30/30；16 项推导 · 24 文件 · 80 处声明一致 |
| 分析器构建（`EnableQualityAnalyzers=true`） | **0 警告**（含 MA0015 路径级重启用） |
| `scripts/quality-baseline.cs` | 基线 0，棘轮 0 == 0 |
| 默认 Release 构建 | 0 警告 0 错误 |
| 16 个测试项目 | 全部通过；Transactions 229/229、Compression 46/46、DI 177/177 |
| Stryker（CompressionProvider / MySqlLeasePolicy） | **100% / 100%**（MTP runner，break 95） |
| Semgrep（p/csharp + p/security-audit） | **0 findings**；29 规则 canary；SARIF artifact |
| AOT 真发布（win-x64） | AotSample 全检查通过；PalOrmSample PASSED |
| review v92 | `docs/review/review-2026-10-18-quality-system-v92.md`；metrics 最后一轮 2026-10-18；豁免文件移除 |
| differential-review 试点 | 1 条独特发现（S2077 纵深缺口）已修复；报告 `docs/review/differential-review-2026-10-18-quality-tooling.md` |
| pre-push selftest / `.ai` 钩子 | 通过 / 通过 |
