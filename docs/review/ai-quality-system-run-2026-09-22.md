# Pal.DDD AI 质量系统全面运行分析报告（2026-09-22）

> 运行方式：全量实跑机械门禁 + 对上一轮结论逐条反证复核（无缓存）
> 证据口径：每条发现附命令输出或 `file:line`；标注 [事实]（本次实跑可复现）· [推断]（逻辑推导未验证）· [猜测]（无依据）
> 本轮性质：**分析复核**，未改动任何业务源码、测试或门禁脚本

---

## 一、结论摘要

1. **机械层不是全绿，且当前处于"提交被阻断"状态**。`encoding-gate` 因一份**未跟踪**文档的裸 LF 而红；该门禁在 `.githooks/pre-commit:37-42` **无条件**执行，且枚举**工作树**而非暂存集，故本仓**任何提交都会被拦**。[事实]
2. **上一轮报告 11 项清单中，4 项定性有误**（§四）。其中 2 项恰好踩进本仓自己记录的陷阱：审计条目命中**代码内声明注释**（AGENTS.md §3，M3-3 同类失败模式）。
3. **上一轮漏掉一个结构性缺陷**：外部数据库配置路径在代码面已完全死透，而被跟踪的配置模板仍承诺它可用，形成三方不一致（代码 / 文档 / 注释）。[事实]
4. **多方言不对称已用数字钉死，但归因方向与上一轮相反**：同一台机器同一配置下，`Integration.Tests` 本地全绿（exit 0），`PalORM.Tests` 本地 46 红（exit 2）。谁该改向谁，需要按"守卫保护的危险是否真实存在"来判断，而非按"谁在跳过"。[事实 + 推断]
5. 门禁可信度矩阵、`verify-ai` 25 项、`guard.cs` 八道守卫、`tech-debt`、`refine-scan` 等实跑结果与上一轮一致，无新增退化。[事实]

---

## 二、复核环境与方法

| 项 | 实测值 |
|---|---|
| 平台 | Windows 10 x64 / .NET 11 / TUnit + MTP |
| Docker | **不存在**（`docker version` → `command not found`）[事实] |
| 本地测试配置 | 仓库根 `appsettings.test.local.json`，已被 `.gitignore:97` 忽略、**从未进过版本库**（`git log --all --diff-filter=A` 空）[事实] |
| 该配置内容 | PG/MySQL 指向外部实例且 `UseTestcontainers: false`（值不在此复现，见 §七 开放问题） |
| 工作树状态 | `?? docs/review/audit-2026-09-21-technical-audit.md`（未跟踪） |

**实跑命令与退出码（全部为本次实测）**

| 命令 | 退出码 | 关键输出 |
|---|---|---|
| `dotnet run scripts/encoding-gate.cs` | **1** | `FAIL E5` → `./docs/review/audit-2026-09-21-technical-audit.md` |
| `dotnet run scripts/gate.cs` | **1** | G22 FAIL（未跟踪 1）· G23 PASS · G24 WARN（2 处） |
| `dotnet run scripts/verify-ai.cs` | 0 | 25 PASS / 0 FAIL |
| `dotnet run scripts/gate-audit.cs -- --inventory` | 0 | 32 脚本 · 接线 16 · TOOL 16 · UNVERIFIED 0 · 缺口 0 · 未归类 0 |
| `dotnet run scripts/guard.cs` | 0 | 8 道守卫全 GREEN（23469ms） |
| `dotnet run scripts/tech-debt.cs` | 0 | 9 PASS / 2 ALLOW / 1 WARN / 0 FAIL |
| `dotnet run scripts/refine-scan.cs` | 0 | M2 3 / M3 92 / O1 8，三项均自带"高假阳性"标注 |
| `dotnet test test/PalDDD.PalORM.Tests/...csproj -c Release` | **2** | 总计 148 · **失败 46** · 成功 102 · 跳过 0 |
| `dotnet test test/PalDDD.Integration.Tests/...csproj -c Release -- --treenode-filter "/*/*/DialectProbeTests/*"` | 8 | 总计 12 · 失败 0 · 成功 0 · **跳过 12** |
| `dotnet test test/PalDDD.Integration.Tests/...csproj -c Release`（全量） | **0** | 总计 372 · 失败 0 · 成功 358 · 跳过 14 |

行尾字节级证据（E5 判定对象）：

```
f=docs/review/audit-2026-09-21-technical-audit.md
bytes=42644   CR=0   LF=554      # 纯裸 LF，仓规要求 CRLF
```

---

## 三、逐条复核结果

### 3.1 上一轮结论中**证实成立**的部分

| 上一轮断言 | 复核 | 证据 |
|---|---|---|
| E5 阻断，审计文档裸 LF | **成立** [事实] | 退出码 1 + `CR=0 LF=554` |
| `verify-ai` 25/25 全过 | 成立 [事实] | 输出 `通过：25 失败：0` |
| 门禁矩阵 0 缺口、12 探针 | 成立 [事实] | 32 脚本 16 接线 / 16 TOOL / 0 UNVERIFIED / 0 未归类 |
| `guard.cs` 八道全 GREEN | 成立 [事实] | 23469ms 全 GREEN |
| `tech-debt` 9/2/1/0 | 成立 [事实] | Obsolete WARN 6 处 |
| `refine-scan` 三个高假阳性簇 | 成立 [事实] | 工具**自身输出**即标注"高假阳性""命中数≠可改数" |
| PalORM 多方言 46 项红 | 成立 [事实] | 148/46/102/0，exit 2 |
| `fix-orchestrator` 引用已删脚本 | 成立 [事实] | `.ai/scripts/` 现存仅 `install-ai-system.sh` + `template-gate.sh` |

### 3.2 上一轮结论中**需要纠正**的部分

见 §四（6 处纠正）。

---

## 四、对上一轮报告的 6 处纠正

### C-1 `gate.cs` 结果记错：不是 0 FAIL，是 1 FAIL

上一轮写"`gate.cs` 1 PASS / 2 WARN / 0 FAIL，G22 跳过"。实测为 **1 PASS / 1 WARN / 1 FAIL**：

```
[0;31mFAIL[0m PDDD-G22: 外仓有未提交改动（未暂存 0 + 未跟踪 1，违规数：1）
[0;32mPASS[0m PDDD-G23: 公共 API 快照与 CHANGELOG 同步
[0;33mWARN[0m PDDD-G24: 新增 2 处 Path.GetFileName* 调用但未见分隔符归一化
```

G22 的违规数 1 正是那份未跟踪的审计文档本身。[事实]

### C-2 S2 的**机理**说错了

上一轮写"缺 Testcontainers 时硬失败"，把原因归给环境缺失。实测异常为：

```
InvalidOperationException: PostgreSQL 多方言 Fixture 禁止连接或清理外部数据库；必须启用 Testcontainers。
  在 MultiDialectFixture.EnsureTestcontainersRequired(...) 位于 MultiDialectFixture.cs:96
```

触发条件是**本地配置 `UseTestcontainers: false`**（即"配置指向外部库"），不是"Docker 不存在"。Docker 缺失是**另一个独立条件**，而 PalORM 侧对那个条件**根本没有守卫**（§五 N2）。[事实]

### C-3 S3 的"颜色码未展开"是**有意保真**，不是残渣

`scripts/fix-orchestrator.cs:15-19` 与 `:224` 有明确声明：

```
//   1) 结尾行输出字面 ${CYAN}/${NC}——原版第 102 行 printf 用单引号未展开变量
//      （笔误），本版保真不改，避免输出漂移；标题行的 ANSI 色码为真实输出。
```

按 AGENTS.md §3，命中声明注释的改动**必须先落决策文档回应该声明，禁止删声明来"通过"**。上一轮的 R-03"清洗颜色码"若执行，就是 M3-3 失败模式的复现（删掉声明而非回应声明）。**该子项撤销**；只有 `:118-119` 的已删脚本引用是真问题。[事实]

### C-4 H1（G24）是**假阳性**

G24 的 2 处命中定位在 `scripts/verify-ai.cs:851,854`：

```csharp
? Directory.GetFiles(Path.Combine(aiDir, "scripts")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
? Directory.GetFiles(Path.Combine(repoRoot, "scripts")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
```

G24 担心的是"Unix 上反斜杠不拆分"。但这里的输入由 `Directory.GetFiles` 产生，**天然使用本平台分隔符**，两侧平台都正确拆分。判定：无需归一化、无需豁免，**上一轮 R-04 撤销**（若要加强，应改门禁启发式使其识别 `Directory.GetFiles` 来源，而非改被测代码）。[事实 + 推断]

### C-5 M2"控制台中文 mojibake"不成立

`guard.cs` 实跑输出中**中文完全正常**（"全部守卫 GREEN"），只有勾选符号在采集管道里退化为 `?`。属采集端字形降级，不是脚本编码缺陷。**上一轮 R-10 撤销**。[事实]

### C-6 "Obsolete 残留 6 处"计数不准

`tech-debt` 的 6 处里，只有 **3 处是真实的 `[Obsolete]` 特性**，另 3 处是注释里提到 `[Obsolete]`：

| 类型 | 位置 |
|---|---|
| 真特性 | `src/PalDDD.Core/Attributes.cs:93`、`:128` |
| 真特性 | `src/PalDDD.Transactions.EFCore/SqlServerOutboxDbContext.cs:11` |
| 注释提及 | `src/PalDDD.Analyzers.CodeFixes/MatchEventNameCodeFix.cs:47`、`test/PalDDD.Core.Tests/StrategicMetadataAttributeTests.cs:6`（及同段） |

三处真特性均带"精炼裁决 2026-08-26 … v3.0 移除"理由，属**有计划的债务**，WARN 为正确口径。[事实]

---

## 五、深层发现（本轮新增）

### N1 外部库配置路径**全链路死透**，但模板仍承诺可用（三方不一致）

这是上一轮完全漏掉的结构性缺陷。论证分四步：

**① 被跟踪的模板承诺该路径可用**（`appsettings.test.json:5`）：

```
"外部数据库必须设置 UseTestcontainers=false、使用 palddd_test_ 唯一名前缀，
 并设置 PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP=1。"
```

**② 该环境变量在代码中零引用**。两种独立方法一致（`git grep` 全仓跟踪文件 + 文件系统全类型 grep）：

```
$ git grep -n "PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP" -- .
appsettings.test.json:5:  "//": "...PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP=1。"
```

唯一出现处就是那句注释本身。[事实]

**③ 配套的连接串属性也零消费者**：

```
$ git grep -n "PostgreSqlConnectionString" -- .
test/PalDDD.Testing/TestEnvironment.cs:19:    public static string PostgreSqlConnectionString =>
```

`MySqlConnectionString` 同形。而 `PalDDD.Testing` 是 `<IsPackable>false</IsPackable>`（`test/PalDDD.Testing/PalDDD.Testing.csproj:4`），`docs/release.md:182` 明载"测试基础设施，仅项目内部用"，故**不存在外部消费者**，不能按"框架库 API 面向外部"保留。[事实]

**④ 该路径在运行时被硬拒**。`MultiDialectFixture.EnsureTestcontainersRequired`（`:92-99`）在 `UseTestcontainers == false` 时无条件抛异常。

**结论**：模板描述的路（`UseTestcontainers=false` + 专用 env var）在代码里**没有任何实现**，且被 fixture 主动拒绝。三方不一致成立。

**来源可追溯**：`git log -S` 显示该属性由 `a10cd51`（统一测试配置架构）引入，被 `8c9e26b`（ITM-208 返工）孤儿化，该提交主题即写明 **"Testcontainers-only"**；模板注释那句则由 `d162414`（ITM-208 原始提交）写入，此后未随返工更新。即：**返工只改了代码，没改文档**。[事实]

### N2 多方言策略不对称是**双向**的，且两套件都引用了同一条裁决

| 条件 | `PalORM.Tests` | `Integration.Tests` |
|---|---|---|
| 配置 `UseTestcontainers=false` | **抛异常**（`MultiDialectFixture.cs:96`） | **Skip**（`DialectProbeTests.cs:174-177`） |
| Testcontainers 开但 Docker 不可达 | **无守卫**（直接撞容器启动失败） | **Skip**（`InboxTimestampTokenPrecisionProbeTests.cs:93,125`） |

两边的注释**都引用同一条裁决**：

```
DialectProbeTests.cs:172  // 对齐 MultiDialectFixture "禁止连接或清理外部数据库"裁决：
                          // 配置指向外部连接串时 Skip 而非硬拒（探针的 PG 不可达跳过语义）
```

**量化后果**（同一台机器、同一配置）：

| 套件 | 总计 | 失败 | 成功 | 跳过 | 退出码 |
|---|---|---|---|---|---|
| `PalORM.Tests` | 148 | **46** | 102 | 0 | **2** |
| `Integration.Tests` | 372 | 0 | 358 | 14 | **0** |

**辩证处理（哪个方向对）**

- **我的判断**：Integration 侧的 Skip 是更合适的默认，理由是 PalORM 侧 fail-closed 所保护的危险**在当前代码下不可能发生**。守卫声称防的是"连接或清理外部数据库"，但连接串属性零消费者（N1③），测试**根本无法**连到外部库；即守卫在防一条不存在的路。
- **自我反驳（三条）**：① 若日后恢复外部库路径，守卫立刻重新有意义，删守卫会留下真实的数据破坏面；② Skip 是**静默不执行**，按本仓"静默控制问句"（若它是无声 no-op，可观察输出会不同吗）口径，Skip 属需要 owner 与执行兜底的观察态；③ 46 项硬红有**可读性优势**：开发者立刻知道要开 Testcontainers，而 Skip 只体现在"已跳过: 14"一行里。
- **反驳的回应**：②③ 不构成保留 throw 的理由，因为**两侧都有 CI 兜底**：`ci.yml` 主循环逐项目跑全部测试项目（含 `PalORM.Tests`），`dialect-probe` job 单独跑探针，CI 里 Testcontainers 可用（`ci.yml:104` 注释"CI 环境：Testcontainers 自动启动 PG/MySQL/Kafka/RabbitMQ"）。即 Skip 只发生在本地，CI 保证执行。①是**真约束**，但它的正确表达不是"保留无条件 throw"，而是"两条路都统一为 skip，并在恢复外部库路径时同时恢复守卫与文档"。
- **条件边界**：若组织决定外部库路径永不恢复（现状即如此），则统一为 Skip 无任何安全代价；若计划恢复该路径，则必须先实现 `PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP` 的读取逻辑与文档，再决定守卫形态。**因为守卫保护的危险当前不可达，所以建议统一向 Skip 收敛；因为恢复路径是可能的未来动作，所以同一提交必须把模板注释与死属性一并收口。**

### N3 E5 阻断的**完整机理**（上一轮只说了现象）

1. `encoding-gate.cs:152` 注释自陈这是"**工作树漂移防线**"，判定 `HasBareLf => bareLf > 0`（`:222`），即纯 LF 或混合行尾均命中；
2. 它枚举的是**工作树**（`CsScanRoots` 之外的扩展名清单），**不限于 git 跟踪文件**；
3. `.githooks/pre-commit:37-42` 对 `encoding-gate` **无触发条件**，每次提交都跑。

三点相乘的后果：**一份未跟踪的草稿文档，可以阻断本仓的全部提交**。这是 fail-closed 的正确行为（工作树漂移本来就该拦），但它把"我不打算提交的文件"也纳入射程，失败信息指向的文件与提交内容无关，**可读性代价**值得记一笔。[事实]

根因链：该文档由 Write 工具新建（默认 LF），未跟进行尾修复。AGENTS.md §3 已记录该陷阱。

### N4 V25 的覆盖面解释了 F-07 的漏网

`verify-ai` 的 V25 描述为"**.ai 文档**命令形态与引用完好（18 个文档）"，其真源是两个 `scripts` 目录的现存文件名集，扫描面是 `.ai/` 下非 history 的 `.md`。因此 `scripts/fix-orchestrator.cs` 的**输出文案**里那两条已删脚本引用，**结构上就在 V25 射程之外**。[事实]

这不是 V25 的缺陷（它的射程被明确定义），而是说明"命令形态"这类失实有**第三个面**：文档、`.ai/` 文档之外的**脚本自身输出**。上一轮把这条记在 F-07 名下，归因不准；它是独立缺口。

---

## 六、修复清单（修正版）

相对上一轮：**撤销 3 项**（原 R-04 G24 假阳性、原 R-10 中文 mojibake、原 R-03 的颜色码子项），**新增 3 项**（R-12 死配置路径收口、R-13 恢复路径守卫、R-14 V25 射程外的脚本输出面）。

| ID | 任务 | 位置 | 验收 | 量 | 风险 |
|---|---|---|---|---|---|
| R-01 | 修 E5 行尾（**当前阻断全部提交**） | `docs/review/audit-2026-09-21-technical-audit.md` | `encoding-gate` E1–E5 全 PASS | S | 低 |
| R-02 | 统一多方言策略向 Skip 收敛 + 补 Docker 不可达守卫 | `MultiDialectFixture.cs:92-99` | 无 Docker 且无外部库配置时两套件行为一致；`PalORM.Tests` 本地 exit 0 | M | 中 |
| R-03 | 清洗 `fix-orchestrator` 死脚本命令（**保留 `${CYAN}` 保真**，或先落决策文档再改） | `scripts/fix-orchestrator.cs:118-119` | 输出命令全部可执行；`${CYAN}` 声明被回应而非删除 | S | 低 |
| R-12 | 收口死配置路径：删 `PostgreSqlConnectionString`/`MySqlConnectionString` 或实现外部库路径；同步改模板注释 | `TestEnvironment.cs:19,30`、`appsettings.test.json:5` | 模板承诺与代码实现一致；`git grep` 三方零残留 | M | 中 |
| R-13 | 若保留外部库路径：实现 `PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP` 读取 + 守卫 + 文档 | `TestEnvironment.cs`、`MultiDialectFixture.cs` | env var 有读取点与测试；守卫在开启时生效 | M | 中 |
| R-05 | 把"测试环境策略"纳入姊妹轴 B | `.ai/review/sibling-map.md` | 轴 B 含该孪生对 | S | 低 |
| R-07 | 为 `doc-consistency`/`gate`/`tech-debt`/`test-gate`/`test-change-guard`/`verify-ai` 补隔离变异探针 | `scripts/gate-audit.cs` | PROBED 列由 `-` 变 `yes`；坏输入必红 | M | 中 |
| R-09 | `ci-coverage` 本地/CI 语义文档化（Docker 前置） | `docs/test-coverage-baseline.md` | 文档与脚本前置一致；V14 同步 | S | 低 |
| R-14 | 把"脚本自身输出的命令形态"纳入校验面（V25 射程外缺口） | `scripts/verify-ai.cs` 或新门禁 | 改坏 `fix-orchestrator` 输出行 → 门禁红 | M | 中 |
| R-08 | `refine-scan` 三簇逐条核实落记录 | `src/` 命中点 | 每条有诊断三步骤结论 | M | 低 |
| R-11 | 三处真 `[Obsolete]` 保持 v3.0 移除计划可追溯 | `Attributes.cs` 等 | 有对应 ITM/决策文档 | S | 低 |

**建议顺序**：R-01（解阻断）→ R-12/R-13（同一提交，三方一致）→ R-02 → R-03 → R-07/R-14 → 其余。

---

## 七、开放问题（需裁决）

1. **外部库路径的去留**：现状是"代码只支持 Testcontainers，模板却说支持外部库"。是**删死属性 + 改模板**（承认只支持 Testcontainers），还是**补实现**（恢复外部库路径）？这决定 R-12 与 R-13 哪个成立。
2. **本地配置的意图**：仓库根 `appsettings.test.local.json` 指向外部实例且 `UseTestcontainers: false`，与"Testcontainers-only"的代码现状冲突。若该配置已无用途，清掉它可让 `PalORM.Tests` 与 `Integration.Tests` 的本地行为都回到"跳过"路径。
3. **`ci-coverage` 定位**：本地门禁还是 CI-only？若 CI-only，则"本地红"不是缺陷，R-09 只需文档化。
4. **`fix-orchestrator` 是否升门禁**：若它继续作为修复轮入口，应纳入 R-14 的同类校验。

---

## 八、复核方法与局限

**已做**：全量实跑 10 个门禁脚本 + 3 次真实测试运行；对每条关键断言使用两种独立方法（`git grep` 全仓跟踪文件 + 文件系统全类型 grep）；对"死代码"判定执行本仓 5 项交叉验证（性质/文档/roadmap/git 历史/测试）；对守卫改动方向做了辩证处理（判断 → 3 条自我反驳 → 条件边界）。

**未做（局限）**：
- 未跑 `ci-coverage.cs` 全链路（其测试步骤必然因 R-02 中断，实跑成本高且结论已由分项测试确定）[推断]
- 未验证 `.ai/` 独立仓的 git 状态（本轮未进入该仓）
- `verify-action-items`/`sister-axis`/`fix-completeness`/`osc-check`/`flaky-parse` 按设计需参数，本轮只验用法面
- 未复核上一轮报告中未列入本报告的其余条目（如 review-scope 219 文件清单）
