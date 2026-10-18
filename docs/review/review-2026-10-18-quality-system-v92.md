# Review v92：质量系统维护轮（2026-10-18）

> 轮次类型：维护轮（质量系统审计 + 工具链收敛 + review 闸门），不是全量 src 地毯。
> 起点：v91（2026-09-19）。本报告为 v92 归档件，满足 V24「v92 起报告必须落库」。
> 关联：`docs/review/ai-quality-system-audit-2026-10-18.md`、`docs/review/differential-review-2026-10-18-quality-tooling.md`、`docs/quality-analyzer-triage-2026-10-18.md`。

## 一、范围

- 质量系统知识层：`.ai` 四系统 prompt/ledger、V30 锚点对账、count-audit 的 AGENTS.md 扫描、secret-scan、契约 v3。
- 质量工具链：第三方分析器基线（642→0）、Semgrep（6/6 FP 注解）、Stryker MTP 双基线、AOT 本地实证。
- review 闸门：`review-freshness` 抽取、pre-push 阻断、豁免到期处置。
- 未重读面：产品 src 的完整语义地毯沿用 v88-v91 与既有测试；本轮的 src 变更由差分评审、分析器、全量测试与 AOT 覆盖。

## 二、发现与处置

| # | 发现 | 处置 |
|---|---|---|
| F1 | S2077 曾被规则级关闭，Semgrep 未覆盖两处 EF Core `FromSqlRaw`，形成 SQL 注入检测盲区 | 恢复 `S2077=warning`，8 个已验证站点逐处 `#pragma` 说明依据（差分评审独特发现） |
| F2 | MA0015 全局关闭，公共/领域面参数名检查缺失 | 按路径重新启用；18 条逐条修 `nameof`/异常参数或站点级抑制，分析器回到 0 |
| F3 | 新控制只有 selftest，无隔离探针 | `quality-baseline --from-log` + `review-freshness` TOOL；新增 8 条探针（quality-baseline 2 / review-freshness 3 / xml-guard / vuln-scan / test-change-guard），gate-audit 39/39，20 门禁有探针【时点：v92 落账后双层扩展 + 第三轮 config-policy C 探针，现 41/41】 |
| F4 | mutation job `break=0`、仅手动、无 artifact | 拆独立 `mutation.yml`（月度+手动）、`break=95`、JSON/HTML artifact；Stryker 进 `dotnet-tools.json` |
| F5 | Semgrep 规则包 registry 漂移、无 SARIF | CI 加 29 规则 canary（变化即失败要求重审注解）、SARIF artifact、pip 缓存 |
| F6 | review 债靠豁免延后，闸门未实弹 | 本 v92 维护轮落账；移除 `review-exemption.json`，`review-freshness` 在 metrics 新鲜时放行 |
| F7 | `.ai` 传感器台账缺新质量传感器 | 新增分析器基线/Semgrep/mutation/review 债四行（见下） |
| F8 | `.ai` 备份手动、钩子激活手动 | 新增 `scripts/backup-bundle.cs`；README 给出首次 clone 激活命令 |

## 三、验证

| 检查 | 结果 |
|---|---|
| `gate-audit` 全量 | 39/39 探针通过；20 个门禁有自证；0 UNVERIFIED/UNWIRED/REVIEW；剩余 5 个为 meta/orchestrator/diagnostic |
| `verify-ai` | 30/30（V30 锚点对账、V24 报告归档、V26 版本对账） |
| `count-audit` | 16 项推导 · 24 文件 · 80 处声明一致 |
| 分析器（启用） | 0 警告；基线 0；MA0015 公共/领域面 18 条已修/精确抑制 |
| Semgrep | 0 findings；29 规则 canary；SARIF artifact；uv 哈希锁 |
| Stryker | CompressionProvider 100%、Transactions/MySqlLeasePolicy 100%（break 95） |
| 测试 | 16 面板全过；Transactions 229/229、Compression 46/46、DI 177/177 |
| AOT | AotSample 全检查通过；PalOrmSample PASSED（win-x64） |
| review-freshness | 双层：任一维护轮 >30 天阻断 / 全量或里程碑 >90 天阻断 / 有效豁免放行（探针四条 + selftest） |

## 四、结论

质量报告中的遗留项已全部闭环。review 闸门为**双层**：任一轮 30 天 + 全量/里程碑 90 天。本 v92 维护轮只重置“任一轮”时钟（metrics 最后一轮 2026-10-18），**不重置全量时钟**——最近全量仍为 v91（2026-09-19），下一次全量评审 due 2026-12-18。这样维护轮不能洗白全量评审债；到期后 pre-push 会阻断，直到全量轮或有意续期的豁免。后续正式语义 review 仍按里程碑触发。
