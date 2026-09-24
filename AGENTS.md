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
> （仅在其未设置时写入，不覆盖开发者既有配置；非 git 环境跳过；CI 跳过——CI 不提交，且并发构建会争用 `.git/config` 锁）。故「clone 后构建一次」即获得全部本地守卫，
> 无需手动 `git config`。跳过单次提交：`git commit --no-verify`。

| # | 守卫 | 触发条件 | 拦截什么 |
|---|------|---------|---------|
| 1 | `secret-scan` | 每次 | 受跟踪文件里的高置信硬编码凭据 |
| 2 | `encoding-gate` | 每次 | E1 CRLF(.sh/.py) · E2 BOM(.cs) · E3 mojibake(.cs) · E4 .verified 非 LF · E5 源文件裸 LF(.md/.cs/.csproj/.targets 等,2026-09-14 增) |
| 3 | `verify-ai` | 检测到 `.ai/` 目录时每次 | V1–V25 系统一致性（账本/传感器台账/提示词/死引用/命令形态）。2026-09-22 T-35 增：`.ai` 裁决为独立 git 库 ⇒ 永不进 CI，此前无任何自动执行路径 |
| 4 | `guard.cs` | `.cs` 入暂存集 | 8 道守卫测试 + 守卫命名类（`*GateTests`/`*GuardTests`/`ArchitectureBoundaryTests`）注册完整性核查 |
| 5 | `test-change-guard` | `test/` 有修改或删除 | 只改测试不改 `src/`（改测试修绿签名）。豁免：`ALLOW_TEST_ONLY_CHANGE=1` |
| 6 | `xml-guard` | xml 系扩展名入暂存集 | XML 非良构（`.csproj/.props/.slnx/.targets/.xml`） |
| 7 | `verify-conventions --quick` | `.md` 入暂存集 | V5 TODO 扫描 · V8 `.pal/prompts/` 模板必填段 · V9 文档命令引用的脚本须存在 · V11 决策文档三必填段与锚点格式（2026-09-18 增） |
| 8 | `dapper-param-guard` | `src/PalDDD.Dapper*/` 入暂存集 | 匿名 Dapper 参数枚举直传（Dapper.AOT 拦截器直传驱动，PG 拒绝——CI #94 根因，2026-09-14 增） |

### CI（`.github/workflows/ci.yml`）

`build-and-test`（vuln-scan → restore → build → **format-verify**（`dotnet format style --verify-no-changes`，8e97e9c 增） → test → secret-scan → dapper-param-guard → doc-consistency → encoding-gate/tech-debt/test-gate 循环 → `gate.cs`（G22/G23/G24）→ `gate-audit --inventory` → gate-lite。**2026-09-22 T-02/T-04**：`.ai` 恒假分支已删，主仓门禁全部无条件执行；`verify-ai`/`template-gate` 因 `.ai` 独立裁决永不进 CI，改由 pre-commit 与 `.ai` 仓钩子承担）· `aot-verify`（PublishAot + 运行二进制）· **`coverage`（覆盖率阈值门禁 0.70，先 `dotnet tool restore` 取 reportgenerator）** · `dialect-probe`（Testcontainers PG/MySQL；job 内含 Path gate 路径过滤步骤，仅 Store/SQL/DDL/映射面变更触发——非独立 job）。

### 门禁可信度（改动或新增门禁后必跑）

```bash
dotnet run scripts/gate-audit.cs              # 静态矩阵 + 隔离式变异探针
dotnet run scripts/gate-audit.cs -- --inventory   # 仅矩阵（快）
```

矩阵五态：`OK`（接线且有自证）· `UNVERIFIED`（接线但无自证 = 退化无人知）· `TOOL`（未接线但按设计手工调用）· `UNWIRED-GATE`（应接线未接 = 真缺口）· `REVIEW`（未接线且未归类，须归入前两者之一）。
**新增脚本必须归入 TOOL 或 UNWIRED-GATE**——`REVIEW` 桶非空即表示有脚本未经分类。
**退出码（2026-09-22 T-03 增）**：`REVIEW` 桶非空 → 退出 1，已接 CI（矩阵自身的退化防线）。此前矩阵恒 `return 0`，退化只能靠人工读。`UNWIRED-GATE` 单列不阻断——待接门禁属人工裁决，登记 `intendedWire` 或改判 `TOOL`。
**G23 判定口径（2026-09-25 修订为发布语义）**：`gate.cs` 的 G23（公共 API 快照 ↔ CHANGELOG 同步，BINC-1 三真源）变更集按发布范围取——暂存集（pre-commit 语义）优先；否则 HEAD 可达的最近 `v*` tag 到 HEAD 之间的全部提交（`git describe --tags --abbrev=0 --match v*`，describe 而非全局 `git tag --sort`：只认 HEAD 可达的"最近"tag，旧分支上取全局最高版本会拿到不可达基线）；取不到 tag（孤儿/浅克隆/无 tag）回落 `HEAD~1..HEAD`，皆不可解析则显式 SKIP。判定为**逐提交同集耦合**：窗口内任一提交改了快照而未**同一提交**改 `CHANGELOG.md` 即 FAIL。修前口径（只看最后一次提交的合计）可被"补一个只改 CHANGELOG 的提交"无条件洗白——本仓实证：v3.0.0→v3.1.0 窗口 `11a4f2d` 只改快照、`b0feaa7` 只补 CHANGELOG，旧口径在补记提交上转绿。G22 仍按工作树判定、G24 同步扩到同一窗口（WARN 级，净差新增行）。
**接线判定口径（2026-09-22 T-36 修）**：按「可执行调用形态」匹配且要求名字边界——修前 `encoding-gate.cs` 会把 `gate` 误判为已接线（子串假阳性），即"检测假接线的工具自身有假接线"。
**探针隔离策略（2026-09-22 T-05 增）**：两种根解析策略决定门禁能否被隔离探针覆盖——**CWD 系**（`secret-scan`/`test-change-guard`/`verify-conventions`/`dapper-param-guard`）直接从隔离目录运行即可；**CallerFilePath 系**（`gate`/`tech-debt`/`doc-consistency`/`test-gate`）按**源文件位置**向上找仓库根，直接跑会扫到脚本所在的真实仓库、注入被完全忽略（实测：注入未跟踪文件与 TODO 注释后三门禁仍全绿）。此类探针须置 `CopyScriptIntoIsolation: true`，夹具会复制脚本进隔离目录并暂存（暂存是必需的：否则复制件自己就是"未跟踪 1"，会让 `gate` 的 G22 在干净输入下也变红，正向探针假绿）。
**已提交历史夹具（2026-09-25 增，服务 G23）**：判定对象是提交历史而非暂存集时，探针还须置 `CommitBeforeRun: true`——夹具先落基座提交（slnx + .gitignore + gate.cs 复制件），再把注入文件单独提交为最后一次提交。不先提交会让 `gate` 的「暂存优先」口径把复制件与未提交注入都当变更集，注入的提交路径反而不可见。

### 新增/修改门禁的规程

1. 写 `scripts/<name>.cs`（file-based app，零 package 依赖），声明 `退出码` 语义。
2. 带 `--selftest`：判定逻辑抽成纯函数，自测含**正例与负例**（防「判定恒真」的假绿）。
3. 用变异验证自测**能红**：翻转判定 → 跑 `--selftest` → 确认 FAIL → 恢复。
4. 向 `scripts/gate-audit.cs` 追加隔离式探针（注入坏输入 → 断言非零退出），登记 `probedGates`，**并在 `gateForms` 声明本门禁覆盖哪些形态**（T-34，2026-09-22 增；未声明即矩阵 ERROR 退出 2）。形态声明的价值是同时声明**不覆盖什么**——V25 实证：一个已接线、能红、报错精确到行的门禁，对它本该抓的形态（裸文件名）完全失明，T-26 补上后当场抓出 46 处。探针的隔离方式取决于门禁的根解析策略（见下「探针隔离策略」）。
5. 接入 `.githooks/pre-commit`（带触发条件，避免无谓耗时）与/或 `ci.yml`。
6. 同提交更新 `docs/conventions.md` 或本文件的门禁表。
7. **对真实仓库跑一次变异**（2026-09-21 实践新增，第 3 步只验自测、本步验门禁）：把仓库里某个真实数据改坏 → 跑门禁 → 确认 FAIL 且报错精确 → 还原。
   实证：V25 的 selftest 全过，但对真实 `.ai/` 首跑即抓 **32 处**失实——selftest 用注入样本，真实数据的形态复杂度（一行多引用、跨文件计数、历史标记混排）只有真跑才暴露。**两步不可互相替代**。

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
| **审计/扫描条目命中代码内声明注释**（2026-09-19 增，M3-3 实证） | 审计 2026-09-17 的 M3-3 评「风险: 低」并实施——但它命中的 `SagaState.CloneForLease` 有 v26 P3 勘正声明（浅拷贝共享是有意取舍：僵尸执行过的步骤必须能被后继者补偿），实施时**删掉了声明而非回应声明**，失败模式被静默翻转（并发写可抛 → 漏补偿无兜底），且 `init`→`set` 公共 API 破坏对快照不可见。2026-09-19 已回滚（含勘正注释） | 改动前 grep 目标代码的声明注释（「P定案 / 勘正 / ADR / 取舍 / 联动约束」字样）；命中即**先落 `docs/review/decision-*.md`**（V11 门禁管其结构）回应该声明的理由，同提交再改代码；**禁止删除声明注释来"通过"**。机械层（staged diff 删除含声明字样行时要求决策文档在暂存集）为待办 |

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
| 已定的架构决策（含被否决方案） | `docs/decisions/`（24 份 ADR，2026-09-19 ADR-024 增） |
| 历轮评审与清单 | `docs/review/`（含 `NAMING.md` 命名约定与索引） |
| 迁移与发布 | `docs/migration/`、`docs/release.md` |
| 已知坑 | `docs/pitfalls.md` |
| 门禁可信度 | `dotnet run scripts/gate-audit.cs` 的矩阵输出 |

---

## 6. 本文件的维护

- **规则要下沉**：本文件里的规则一旦可脚本化，就改成脚本 + 在 §2 表格登记，本文件只留一行指针。
- **不写已有出处的规则**：`docs/conventions.md` 覆盖的内容不在此重复（复制必然漂移）。
- **改动本文件须同步**：§2 门禁表随 `.githooks/` 与 `ci.yml` 变更同提交更新。
