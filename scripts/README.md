# scripts/ 索引

> 42 个 file-based C# 脚本（零 package 依赖，`dotnet run scripts/<name>.cs` 运行）。
> **权威分类与可信度**：`dotnet run scripts/gate-audit.cs -- --inventory` 的矩阵（WIRED/SELFTEST/PROBED/退出码/五态判定）——本索引只给「分组 + 一句话职责」，用法与退出码语义以各脚本头部注释为准。新增脚本必须在 gate-audit 矩阵归入 TOOL 或 UNWIRED-GATE（REVIEW 桶非空即 CI 红）。

## 提交/推送守卫（.githooks/pre-commit、pre-push 自动调用）

| 脚本 | 职责 |
|------|------|
| `hook-pre-commit` / `hook-pre-push` | 钩子编排真身（12 道守卫序列 + 推送门禁；`.githooks/*` 是 3 行启动器） |
| `secret-scan` | 已跟踪文件的高置信硬编码凭据检测 |
| `encoding-gate` | E1 CRLF(.sh/.py) · E2 BOM(.cs) · E3 mojibake · E4 .verified LF · E5 源文件裸 LF |
| `verify-ai` | `.ai/` 独立仓 V1–V30 系统一致性（含 V30 知识层锚点对账） |
| `guard` | 8 道守卫测试 + 守卫命名类注册完整性（.cs 入暂存集触发） |
| `test-change-guard` | 只改 test/ 不改 src/ 的拦截（豁免 `ALLOW_TEST_ONLY_CHANGE=1`） |
| `xml-guard` | 暂存 XML 良构性 |
| `verify-conventions` | V5 TODO · V8 模板段 · V9 命令引用 · V10 文档互链 · V11 决策文档 · V12 私网 IP |
| `dapper-param-guard` | 匿名 Dapper 参数枚举直传（PG 拒绝形态） |
| `config-policy` | dependabot 词表 / Directory.Build Exec 副作用 / Actions 钉 SHA / CI requirements 平台标记 |
| `license-policy` | 依赖许可白名单（deny-by-default）+ 包级禁令 + gap 台账 |
| `count-audit` | README/docs 计数声明 ↔ 源码推导（16 项）对账 |
| `new-skip-guard` | test/** 新增 Skip 标注净增拦截（豁免须带到期日） |

## CI 门禁（ci.yml / mutation.yml / perf-nightly.yml 接线）

| 脚本 | 职责 |
|------|------|
| `gate` | G22 工作树 / G23 快照↔CHANGELOG 同提交耦合 / G24 路径归一 |
| `gate-lite` | 根目录快速门禁 |
| `tech-debt` | Obsolete/TODO/空 catch/超长行/测试数/版本统一等 15 检查 |
| `doc-consistency` | D7 .ai 文件地图（其余 D 族已下沉测试） |
| `test-gate` | T1–T12 测试规范 / 薄壳缺失 / CI job timeout |
| `ci-coverage` | 覆盖率阈值（CI 0.70 / LOCAL 0.65）与模块降幅门禁 |
| `ci-failed-tests` | CI 测试失败日志聚合 |
| `quality-baseline` | 构建警告计数 + 基线比较 |
| `vuln-scan` | `dotnet list package --vulnerable`（含 transitive） |
| `eps-run` | EPSL 基准编排（--quick/--full/--profile；perf-nightly 调用） |
| `perf-registry-check` | bench/bench-registry.json ↔ 车道实现双向对账 |
| `review-freshness` | review 债双层新鲜度与豁免（pre-push） |

## 发布流程（docs/release.md §十二）

| 脚本 | 职责 |
|------|------|
| `changelog-facts` | Phase 1 事实收集（9 段：快照 diff/诊断/ADR/依赖/面板） |
| `changelog-check` | Phase 4 结构门禁 C1–C10（[Unreleased] 首位/版本对账/分类顺序/日期权威性；release.yml pack 前阻断） |

## 审计/评审工具（按需人工运行，读输出）

| 脚本 | 职责 |
|------|------|
| `gate-audit` | 门禁可信度矩阵 + 隔离式变异探针（改动门禁后必跑） |
| `check-all` | 开发者全量自检（format+CA+编译） |
| `review-coverage-report` | 沉寂面报告（发布 tag 前定向抽查池） |
| `review-gate` / `review-scope` / `review-snapshot` / `sibling-map` / `sister-axis` | 评审轮次路由/范围/快照/姊妹文件映射/对称核查 |
| `fix-completeness` / `fix-orchestrator` | 修复提交前的姊妹轴覆盖验证 / 守卫失败修复指引 |
| `flaky-parse` | 抖动测试日志分析 |
| `refine-scan` | 精炼扫描 |
| `verify-action-items` | 校验指定 action-items 清单 |
| `osc-check` | OSC 翻转检测（test-gate 调用） |
| `probe-template` | 探针模板生成 |
| `template-gate` | .pal/prompts 模板编译探针（.ai 独立裁决，人工运行） |

维护纪律：新增脚本 → 脚本头部声明用法与退出码 → gate-audit 归类 → 本表同步一行。
