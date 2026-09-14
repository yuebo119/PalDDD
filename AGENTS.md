# Pal.DDD 项目级规则（agent 操作层）

> **规则优先级**：全局 `~/.zcode/AGENTS.md` > 本文件 > 任务上下文。
> **本文件的定位**：只写「在本仓库操作必须知道、而全局文件与 `docs/` 里没有」的东西。
> 编码规范（AOT / 注释 / 命名 / 工程结构 / 测试）的权威出处是
> [docs/conventions.md](docs/conventions.md)（1039 行）——**本文件不复制它，只指路**。
> 保持精简：目标 ≤ 150 行。规则膨胀即失效（写进脚本的规则才是硬规则）。

---

## 1. 三条硬红线（违反即不可合并）

| 红线 | 具体要求 | 机械防线 |
|------|---------|---------|
| **AOT 硬约束** | `IsAotCompatible`/`IsTrimmable`/`VerifyReferenceAotCompatibility`/`JsonSerializerIsReflectionEnabledByDefault=false`；不支持 AOT 的项目（EF Core / Kafka / RabbitMQ / MemoryPack）显式覆盖为 `false` | `ArchitectureBoundaryTests` 断言覆盖；CI `aot-verify` job 真发布 AOT 并运行二进制 |
| **零反射** | 生产代码零容忍：`MakeGenericType` / `Activator.CreateInstance` / `Assembly.GetTypes()` / `Type.GetType(string)` | `ArchitectureBoundaryTests` 源码扫描 |
| **三方一致** | 改公共 API / 签名 / 行为 / 计数 / 配置 / 命令，必须**同一次提交**同步 代码 + 文档（README、docs/**）+ 注释 | `scripts/doc-consistency.cs`（CI 跑）；计数锚见 `docs/review/`（PD34 去数字化口径） |

> `IsAotCompatible=true` + 0 警告 ≠ 运行时安全。声称 AOT 兼容前必须有 `PublishAot` + 运行实测。

---

## 2. 门禁体系

### 提交时（`.githooks/pre-commit`，`core.hooksPath=.githooks`）

> **钩子安装是自动的**：`core.hooksPath` 是 git 本地配置（不进版本库），新 clone 默认无钩子。
> 根 `Directory.Build.targets` 的 `ConfigureGitHooks` 目标在**首次构建时**为本 clone 配置它
> （仅在其未设置时写入，不覆盖开发者既有配置；非 git 环境跳过）。故「clone 后构建一次」即获得全部本地守卫，
> 无需手动 `git config`。跳过单次提交：`git commit --no-verify`。

| # | 守卫 | 触发条件 | 拦截什么 |
|---|------|---------|---------|
| 1 | `secret-scan` | 每次 | 受跟踪文件里的高置信硬编码凭据 |
| 2 | `encoding-gate` | 每次 | E1 CRLF(.sh/.py) · E2 BOM(.cs) · E3 mojibake(.cs) · E4 .verified 非 LF |
| 3 | `guard.cs` | `.cs` 入暂存集 | 8 道守卫测试 + 守卫命名类（`*GateTests`/`*GuardTests`/`ArchitectureBoundaryTests`）注册完整性核查 |
| 4 | `test-change-guard` | `test/` 有修改或删除 | 只改测试不改 `src/`（改测试修绿签名）。豁免：`ALLOW_TEST_ONLY_CHANGE=1` |
| 5 | `xml-guard` | xml 系扩展名入暂存集 | XML 非良构（`.csproj/.props/.slnx/.targets/.xml`） |
| 6 | `verify-conventions --quick` | `.md` 入暂存集 | V5 TODO 扫描 · V8 `.pal/prompts/` 模板必填段 · V9 文档命令引用的脚本须存在 |

### CI（`.github/workflows/ci.yml`）

`build-and-test`（vuln-scan → restore → build → test → secret-scan → verify-ai → gate → encoding/doc-consistency/tech-debt/test-gate → gate-lite）· `aot-verify`（PublishAot + 运行二进制）· **`coverage`（覆盖率阈值门禁 0.70，先 `dotnet tool restore` 取 reportgenerator）** · `path-gate` · `dialect-probe`（Testcontainers PG/MySQL）。

### 门禁可信度（改动或新增门禁后必跑）

```bash
dotnet run scripts/gate-audit.cs              # 静态矩阵 + 隔离式变异探针
dotnet run scripts/gate-audit.cs -- --inventory   # 仅矩阵（快）
```

矩阵五态：`OK`（接线且有自证）· `UNVERIFIED`（接线但无自证 = 退化无人知）· `TOOL`（未接线但按设计手工调用）· `UNWIRED-GATE`（应接线未接 = 真缺口）· `REVIEW`（未接线且未归类，须归入前两者之一）。
**新增脚本必须归入 TOOL 或 UNWIRED-GATE**——`REVIEW` 桶非空即表示有脚本未经分类。

### 新增/修改门禁的规程

1. 写 `scripts/<name>.cs`（file-based app，零 package 依赖），声明 `退出码` 语义。
2. 带 `--selftest`：判定逻辑抽成纯函数，自测含**正例与负例**（防「判定恒真」的假绿）。
3. 用变异验证自测**能红**：翻转判定 → 跑 `--selftest` → 确认 FAIL → 恢复。
4. 向 `scripts/gate-audit.cs` 追加隔离式探针（注入坏输入 → 断言非零退出），并登记 `probedGates`。
5. 接入 `.githooks/pre-commit`（带触发条件，避免无谓耗时）与/或 `ci.yml`。
6. 同提交更新 `docs/conventions.md` 或本文件的门禁表。

---

## 3. 已知陷阱（均为本仓实证，非推测）

| 陷阱 | 现象 | 规避 |
|------|------|------|
| **XML 注释含 `--`** | `21549d3` 在 `.csproj` 注释写 `--verify-persist` → MSB4025，**全仓构建失败** | 注释里不写 `--`；`xml-guard` 已守卫 |
| **`git add -A` + `dotnet run` 产物** | file-based app 的 runfile 产物落在 CWD 相对 `dotnet/`，`-A` 会暂存它们（实测被扫文件数 2 → 32） | 隔离脚本用**显式 `git add <file>`**；`.gitignore` 加 `dotnet/` |
| **`cmd \| tail` / `tee` 掩码退出码** | GitHub bash 默认无 pipefail，管道退出码取末段 → 门禁假绿（本仓已发生 5 次，最近 ITM-648） | 取 `${PIPESTATUS[0]}`；CI 里 `set -o pipefail` 必须在**首个管道之前** |
| **「退出码 0」≠ 任务成功** | 我曾以 `\| tail` 运行覆盖率脚本，脚本实际构建失败却报 exit 0 | 判定门禁结果时读**输出内容**，不只看退出码 |
| **覆盖率门禁形态** | 脚本 `scripts/ci-coverage.cs` 已接入 CI（独立 coverage job）+ 阈值 0.70（2026-09-14 实测校准）；合并 glob 曾因 MTP 双层落点缺陷卡死（`277bc34` 修复为递归 glob） | 状态与阈值见 [docs/test-coverage-baseline.md](docs/test-coverage-baseline.md) §门禁阈值 |
| **Agent 脚本默认 C#，禁止 python**（2026-09-14 用户裁决） | Agent 曾用系统 python 重写源文件 → CRLF 写成纯 LF（两轮三犯：`4a64fba` 修 1543 处 .cs、`277bc34` 修 255 处）；本仓已全 C# 化（0 个 .py），工具链不得再引入 Python 面。**Write 工具新建文件默认 LF**——新建后须跑行尾修复（`dotnet run %TEMP%/fix-eol.cs <path>` 或字节级校验） | **默认 `dotnet run <file>.cs`**（file-based app，与 scripts/ 同标准）；临时脚本放 `%TEMP%`（不继承根 props 的 TreatWarningsAsErrors）；机械编辑优先 **Edit/Write 工具**；**任何写文件后做字节级验证**（CRLF 计数 == LF 计数，见 `.ai` OPS-9） |

---

## 4. 编号体系（提交信息与文档引用）

| 前缀 | 含义 | 示例 |
|------|------|------|
| `ITM-<n>` | 待办/缺陷项编号 | `ITM-671`（pre-commit hook） |
| `MIG-<n>` | 迁移批次（bash/Python → C# file-based app） | `MIG-012`（27 个 app 替代 25 个 .sh） |
| `ADR-<n>` | 架构决策记录，落 `docs/decisions/` | `ADR-020`、`ADR-022` |
| `PD<n>` | 实践纪律条目（多出现在口径讨论中） | `PD29`、`PD34` |

提交信息格式（中文）：`类型：描述`，类型含 `修复/测试/文档/构建/决策/实验/审计/升级/卫生/合并/调查/勘正`。

---

## 5. 文档地图（先读这里，再动手）

| 想知道什么 | 读 |
|-----------|-----|
| 编码/命名/注释/工程结构规范 | `docs/conventions.md` |
| 架构与分层 | `docs/architecture.md` |
| AOT 现状与约束 | `docs/aot.md`、`docs/design/` |
| 用法与教程 | `docs/usage.md`、`docs/tutorial.md` |
| 测试策略与基线 | `docs/testing.md`、`docs/test-coverage-baseline.md` |
| 已定的架构决策（含被否决方案） | `docs/decisions/`（22 份 ADR） |
| 历轮评审与清单 | `docs/review/`（含 `NAMING.md` 命名约定与索引） |
| 迁移与发布 | `docs/migration/`、`docs/release.md` |
| 已知坑 | `docs/pitfalls.md` |
| 门禁可信度 | `dotnet run scripts/gate-audit.cs` 的矩阵输出 |

---

## 6. 本文件的维护

- **规则要下沉**：本文件里的规则一旦可脚本化，就改成脚本 + 在 §2 表格登记，本文件只留一行指针。
- **不写已有出处的规则**：`docs/conventions.md` 覆盖的内容不在此重复（复制必然漂移）。
- **改动本文件须同步**：§2 门禁表随 `.githooks/` 与 `ci.yml` 变更同提交更新。
