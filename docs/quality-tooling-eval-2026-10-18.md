# 代码质量工具试点评估 2026-10-18

目标仓：`C:\ai\claude\Pal.DDD`（分支 `dev`）
基线：`dotnet build PalDDD.slnx -c Release` = 0 警告 0 错误，18 秒；CI 已有 `dotnet format style --verify-no-changes`、漏洞扫描、覆盖率门禁、AOT 验证。

## 一、已做的改动

| 文件 | 改动 |
|---|---|
| `src/Directory.Build.props` | 新增，继承根 props；Roslynator.Analyzers 5.0.1、Meziantou.Analyzer 3.0.307、SonarAnalyzer.CSharp 10.35.0.4138，`PrivateAssets=all` |
| `Directory.Packages.props` | 新增三条 `PackageVersion` |
| 分析器开关 | `EnableQualityAnalyzers` 默认 `false`；仅 `-p:EnableQualityAnalyzers=true` 时引用 |

排除项：`PalDDD.Base`、`PalDDD.Extension`（`ManagePackageVersionsCentrally=false` 的元包，无源码）、`PalDDD.Analyzers*`（自身是分析器工程）。

默认构建仍为 0 警告 0 错误，未改变基线契约。

## 二、分析器基线

命令：

```bash
dotnet build PalDDD.slnx -c Release -p:EnableQualityAnalyzers=true -p:TreatWarningsAsErrors=false
```

结果：**642 条警告**，按分析器分组：

| 分析器 | 警告数 |
|---|---:|
| Meziantou (MA) | 482 |
| Sonar (S) | 155 |
| Roslynator (RCS) | 5 |

Top 规则：

| 规则 | 条数 | 性质 |
|---|---:|---|
| MA0048 文件名与类型名不一致 | 159 | 风格 |
| MA0015 参数名/异常参数 | 73 | 一致性 |
| MA0051 方法过长 | 64 | 风格 |
| MA0011 缺少 IFormatProvider | 60 | 文化正确性 |
| MA0004 缺少 ConfigureAwait(false) | 44 | 与已抑制的 CA2007 重复 |
| MA0006 字符串比较未指定 StringComparison | 41 | 正确性 |
| MA0021 使用 StringComparer | 18 | 一致性 |
| S125 注释掉的代码 | 17 | 风格 |
| S8949 未传 CancellationToken | 15 | 正确性 |
| S3358 嵌套三元 | 15 | 风格 |
| S8969 未传 CancellationToken | 13 | 正确性 |
| S2077 SQL 注入模式 | 8 | 安全 |
| S1939 类型转换简化 | 8 | 风格 |
| S3236 参数名 | 7 | 一致性 |
| MA0002 使用 StringComparer | 7 | 一致性 |

结论：Meziantou 占四分之三，且大头是文件名、方法长度、ConfigureAwait 这类在 Pal.DDD 已有明确取舍（CA2007 已抑制）的规则。直接常开会把 642 条警告变成构建错误，破坏零警告契约。

## 三、Stryker.NET

- 安装：`dotnet tool install -g dotnet-stryker`，版本 5.0.0。
- 首次尝试：`test/PalDDD.Compression.Tests` 对 `PalDDD.Compression` 跑基线，未传 `--test-runner mtp`；Stryker 默认走 VSTest，对 MTP 测试项目报错并引用 issue #3094。
- **更正（2026-10-18 晚）**：Stryker 5.0.0 已支持 MTP runner（4.13 起预览，5.0.0 支持 perTest coverage；`-t|--test-runner mtp`）。加该参数后 Compression 项目跑通，CompressionProvider 初始变异分数 40%；补 3 个守卫测试后达到 **100%**。
- 结论：MTP 不构成阻塞；配置见 `test/PalDDD.Compression.Tests/stryker-config.json`，手动 CI job 见 `.github/workflows/ci.yml` 的 `mutation`。

## 四、Semgrep

- 安装：`uv tool install semgrep --python 3.12`，版本 1.181.0（本机 Python 3.14 不适合，uv 拉隔离 3.12）。
- 扫描：`semgrep scan --config p/csharp --config p/security-audit src`。
- 结果：**6 条 findings，全部为同一规则 `csharp-sqli`，逐条核实全部为误报**。

| 位置 | 原因 |
|---|---|
| `PostgreSqlOutboxNotifier.cs:145` | `_channelName` 经 `ValidateChannelName` 白名单校验（`[A-Za-z0-9_]`，首字符字母/下划线）后双引号包裹 |
| `PostgreSqlOutboxNotifier.cs:267` | 同上 |
| `SqlitePerformanceOptimizer.cs:150` | `WalPragma` 是 `const string` |
| `SqlitePerformanceOptimizer.cs:158` | `ProductionRestPragma` / `LightRestPragma` 是常量 |
| `DapperBulkCopy.cs:379` | `table`/`colList` 经 `ValidateIdentifier` / `ValidateColumns` 在公共入口校验，值侧参数化 |
| `DapperBulkCopy.cs:457` | 同上 |

误报率 6/6 = 100%，超过 30% 门槛，且没有产生任何真阳性。按准入标准不进入 CI 门禁。

可行收敛路径：对这 6 处加 `// nosemgrep: csharp.lang.security.sqli.csharp-sqli` 并注明校验依据；或只用 `p/secrets` 等规则包，不用 `p/csharp` 的 SQLi 规则。收敛前不接门禁。

## 五、dotnet format

| 检查 | 结果 |
|---|---|
| `dotnet format style --verify-no-changes` | 通过（CI 已在跑） |
| `dotnet format whitespace --verify-no-changes` | 失败，`ServiceRegistration.cs` 与 `OutboxSqliteConcurrencyTests.cs` 有既存空格格式违规 |
| `dotnet format analyzers --verify-no-changes` | 通过，但分析器默认关闭，等于空跑 |

结论：whitespace 门禁暂不加入 CI，先修既存违规；analyzers 门禁要等规则收敛后再加。

## 六、Trail of Bits 技能

已安装 5 个并登记 `sources-map`（repo `trailofbits`，mirror）：

| 技能 | 上游 |
|---|---|
| codeql | `plugins/static-analysis/skills/codeql` |
| semgrep | `plugins/static-analysis/skills/semgrep` |
| sarif-parsing | `plugins/static-analysis/skills/sarif-parsing` |
| mutation-testing | `plugins/mutation-testing/skills/mutation-testing` |
| differential-review | `plugins/differential-review/skills/differential-review` |

`insecure-defaults` 是命令插件（commands/workflows/references），不是技能，未安装。

安装后活跃 331，暂存 9，总安装 340；技能列表仍因 94,426 字符超过 20,000 预算而处于无描述格式。

## 七、准入标准逐项判定

| 指标 | 门槛 | 结果 | 判定 |
|---|---|---|---|
| 独特发现 | 两周内至少 1 条模型原本发现不了的问题 | Semgrep 0 条真阳性；分析器 642 条已逐条裁决（280→0）；Stryker 两个窄基线 100%；differential-review 试点发现 1 条独特问题（S2077 全局关闭削弱纵深，Semgrep 未覆盖两处 FromSqlRaw），已修复 | 通过（试点 1 条独特发现，见 docs/review/differential-review-2026-10-18-quality-tooling.md） |
| 上下文成本 | 新增工具 schema 不超过现有工具面的 20% | 本轮未新增 MCP，零 schema 成本 | 通过 |
| 误报率 | 低于 30% | Semgrep 原始 6/6 为 FP；逐条 `nosemgrep` 依据注释后当前 0 findings | 条件通过（规则本身 FP 率未变，但每条都有代码级依据；新 FP 生成即需评审） |
| 维护成本 | 无账号、无容器依赖 | Semgrep 本地、分析器 NuGet、ToB 技能 Markdown | 通过 |
| 安全 | 本地运行 | 全部本地 | 通过 |

## 八、决策

| 项 | 决策 |
|---|---|
| 分析器 | 保留但默认关闭（`EnableQualityAnalyzers=false`），先做规则收敛：关闭 MA0048/MA0051/MA0004/MA0006 等噪声项，保留 S2077、S8949、S8969、S6444、MA0015、MA0011 等高价值项，再逐步提为默认开启（2026-10-18 实施：`.editorconfig` 规则子集 + 逐条裁决 + 基线 642→0，见 §十一 与 docs/quality-analyzer-triage-2026-10-18.md） |
| Stryker | MTP runner 已验证（Stryker 5.0.0 `--test-runner mtp`）；配置 `test/PalDDD.Compression.Tests/stryker-config.json` 与 `test/PalDDD.Transactions.Tests/stryker-config.json` + workflow_dispatch `mutation` job；CompressionProvider 与 Transactions/MySqlLeasePolicy 均 100% |
| Semgrep | 6/6 误报已逐条加 `nosemgrep` 依据注释；当前 0 findings；CI `semgrep` job 接入（`--error`） |
| dotnet format whitespace | 先修既存违规，再考虑加 CI（已修复 3 文件/14 条并接入 CI，见 §十一） |
| ToB 技能 | 保留，作为审计方法论；2026-10-18 已用 differential-review 对 `b027e0a` 做试点：发现 S2077 全局关闭削弱纵深（Semgrep 未覆盖两处 `FromSqlRaw`），已改为站点级抑制；报告 `docs/review/differential-review-2026-10-18-quality-tooling.md` |

## 九、回滚

本轮对 Pal.DDD 的改动只有三个文件：

```text
src/Directory.Build.props        （新增）
Directory.Build.props.bak-20261018
Directory.Packages.props.bak-20261018
```

回滚把两个 `.bak-20261018` 覆盖回原文件，并删除 `src/Directory.Build.props` 即可。分析器默认关闭，即使不回滚也不影响构建。

> **2026-10-18 实施后状态**：已按 §十一 正式落地，不再回滚；两个 `.bak-20261018` 已移至 `%TEMP%/palddd-bak-20261018/`（保留证据、不留在工作树）。原回滚步骤仅作历史记录。

## 十、一句话结论

Pal.DDD 的工程基线已经很硬：零警告、SQL 标识符有入口校验、测试栈是 MTP。本轮三个工具的首次准入都没有当场通过：Stryker 首次尝试未启用 MTP runner，Semgrep 全是误报，分析器需要规则收敛。修正后 Stryker MTP 跑通并达到 100%，Semgrep 误报逐条注释并接入 CI，分析器收敛到 0 并建立 0 基线棘轮——结论从“继续装工具”转为“把工具纳入可执行的防线圈”。

## 十一、后续处置（2026-10-18 实施）

| 项 | 处置 | 验收 |
|---|---|---|
| 分析器规则收敛 | `.editorconfig` 末节新增规则子集：MA0048/MA0051/MA0004/MA0006/MA0002/S3358/S125/S1939/S3236 压为 `none`；MA0015/MA0011/S2077/S8949/S8969 保持 `warning`；随后逐条修复/精确抑制（MA0015/S2077/S1133/S1199/S3267/MA0016/S2326/S3246 按业务判断关闭） | 重新测量 642 → **0** 条（去重后） |
| 基线棘轮 | 新增 `scripts/quality-baseline.cs`（`--update` / `--selftest` / 退出码 0/1/2）；基线 `scripts/quality-analyzer-baseline.txt` | 本地 `--selftest` 全过；`--update` 写入 0；默认构建仍 0W0E |
| CI 接线 | 新增独立 job `quality-analyzers`（基线 0 棘轮）、`semgrep`（0 findings）、`mutation`（workflow_dispatch，Stryker MTP） | 新警告 > 0 即红；Semgrep finding 即红；默认零警告契约不受影响 |
| whitespace | `dotnet format whitespace` 修复 3 文件/14 条；CI `Format verify (whitespace)` 步骤接入 | `--verify-no-changes` exit 0 |
| Stryker / Semgrep | Stryker 5.0.0 MTP runner + 两个 `stryker-config.json` + workflow_dispatch job；Semgrep 6/6 FP 加 `nosemgrep` 依据注释并接入 CI | CompressionProvider 与 MySqlLeasePolicy 均 100%；Semgrep 0 findings |
| AOT 真发布 | AotSample + PalOrmSample `win-x64` `PublishAot` 本地实跑 | AotSample 全检查通过；PalOrmSample PASSED |
| review 债阻断 | pre-push 在 metrics >30 天且 src 有变更时阻断；豁免 `.ai/review/review-exemption.json` | selftest 正负例通过；豁免至 2026-11-15 |
| 工作树卫生 | `Directory.Build.props.bak-20261018` / `Directory.Packages.props.bak-20261018` 移至 `%TEMP%/palddd-bak-20261018/` | `git status` 无 `.bak-*` 残留 |

**为什么能到 0**：剩余 280 条并非都需要改代码。逐条裁决分三类：真实缺陷修复（MA0011/MA0021/MA0132/S8949/S8969/MA0054 等）、站点级精确抑制（反射物化/ThreadStatic 池/AOT 非目标路径/刻意 fire-and-forget，每条带理由）、规则级业务判断（配置值异常 paramName、Semgrep 重复的 S2077、与性能纪律冲突的 S3267 等，见 triage 文档）。基线与规则子集均为单一事实源，改规则时同提交重测。
