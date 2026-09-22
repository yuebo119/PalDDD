# 技术审计报告核验（2026-09-22）

> 对象：`docs/review/audit-2026-09-21-technical-audit.md`（554 行，25 条发现 + 优势清单 + 三里程碑任务表）
> 方法：只读取证。对审计的每条承重断言回原始 `file:line` 核验，不接受"审计说审计过"
> 标注：`[事实]` = 本次实跑/实读可复现；`[判断]` = 基于证据的评估；`[纠正]` = 审计表述有误
> 本轮性质：**核验**，未改动任何源码、测试、门禁或审计原文

---

## 一、结论

1. **审计质量高，承重断言基本全部成立**。核验的 4 条高危（O1/A1/A2/S1）与 12 条中低危发现，无一条被证伪；引用的行号绝大多数精确到行。[事实]
2. **审计有 1 处计数错误、2 处路径不精确、2 处表述过重**（§四）。均不影响其结论方向。
3. **审计漏掉 3 项**（§五）：v3.0 承诺面的扩散范围比它说的更广；**审计自己的报告文件正在阻断本仓全部提交**；以及一个能解释多数发现同源性的结构性观察（声明已存在，缺的是强制）。
4. **对"健康等级 B+"的独立判断：维持**，但建议在评分口径里显式扣除一项：报告交付物自身违反编码门禁且未被自查。[判断]

---

## 二、核验方法

对每条发现回到原始文件读取，不看审计的转述；对计数类断言用独立命令重算；对"缺失类"断言（无锁文件、无压测用例）用全仓搜索加第二方法确认。

**实跑基线**

| 命令 | 结果 |
|---|---|
| `dotnet build PalDDD.slnx -c Release` | **0 警告 0 错误**（14.88s）[事实] |
| `find src -name "*.csproj"` | **36**（非 37）[事实] |
| `find test -name "*.csproj"` | 17 = 16 测试 + 1 支持库 ✓ |
| `find src test -name "*.cs" \| xargs wc -l` | 68898 行 ≈ 69k ✓ |
| `ls docs/decisions/*.md \| wc -l` | 24 ✓ |
| `grep -c "^  [a-z-]*:$" ci.yml` | 4 job（build-and-test / aot-verify / coverage / dialect-probe）✓ |
| `find . -name packages.lock.json` | **零命中** ✓ |

---

## 三、逐条核验

### 3.1 高危四条：全部成立

**O1 CI 恒假分支** [事实] 完全成立。`ci.yml:167` 的 `if [ -d .ai/scripts ]` 为真分支内是 `verify-ai.cs`(:168)、`gate.cs`(:169)、`encoding-gate`/`tech-debt`/`test-gate` 循环(:179-188)、`template-gate.sh`(:189)；假分支只有 `gate-lite.cs`(:197)。`.gitignore:68` 确为 `/.ai`。分支外常跑的是 `secret-scan`(:142)、`dapper-param-guard`(:152)、`doc-consistency`(:161)。审计所述六门禁"从不在 CI 自动执行"成立。
补充：`ci.yml:132-138` 已用 F-04 勘正自陈此事为"已知且已声明的设计取舍"。审计把它标 [高] 是对的：声明不消除缺口。

**A1 Saga 反射** [事实] 完全成立，且声明齐备。`Saga.cs:631-646` 的 `ResolveChildSagaByType` 走 `GetMethod` + `MakeGenericMethod` + `Invoke`；`:651-657` 的 `CreateChildState` 走 `Activator.CreateInstance`。两处均带 `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]`（`:629-630`、`:649-650`）。`src/PalDDD.Transactions/PalDDD.Transactions.csproj:9` 确为 `<IsAotCompatible>false</IsAotCompatible>`，且上方注释说明是"显式声明不兼容 AOT 以与同等反射使用项目保持一致"。

**A2 三栈契约分叉** [事实] 成立，且为接口级自声明。`OutboxStore.cs:12-20` 的 Remarks 写明："v3.0 破坏性变更预告（ADR-020）…① 跨栈 fencing 契约统一：对未租约消息 MarkProcessed 的三栈分歧行为（PalORM/Dapper 放行 vs InMemory 拒绝）收敛为统一语义；② 异步化"。`MarkProcessed` 的 Remarks（`:82-92`）逐栈列出差异（含 ITM-269/ITM-272 勘正）。

**S1 发布供应链** [事实] 成立。`release.yml:33` 为 **job 级** `NUGET_API_KEY`（第 31 行注释自陈"step-level env does not cross steps"是踩坑后的选择）；全仓 `release.yml` 无 `id-token: write`（无 OIDC）、无 `dotnet nuget sign`（无签名）。`workflow_dispatch` 路径确跳过 CI 结论检查（`:46` 分支 + `:76` 注释"显式人工意图，可能是重跑/补发"），故"手动 dispatch 弱化 tag 即验证"成立，但属**已声明例外**。

### 3.2 中低危：抽验 12 条，全部成立

| 审计项 | 核验结果 | 证据 |
|---|---|---|
| T1 薄模块覆盖率 | **逐位一致** [事实] | `coverage-baseline.json`：`Core.Abstractions.Tests 0.0132` / `Repository.EFCore.Tests 0.081` / `DependencyInjection.Tests 0.1511` / `Messaging.Tests 0.2179` / `Core.Tests 0.309` |
| T2 PalORM 缺基线 | 成立 [事实] | 该文件 15 键，无 `PalDDD.PalORM.Tests` |
| S2 NU19xx 全局静音 | 成立 [事实] | `Directory.Build.props:45` 含 `NU1900..NU1904;NU5104`，`:41-42` 为"已手动审计"理由 |
| S6 标识符裸拼 | **成立，且与 CA2100 理由直接冲突** [事实] | `DapperBulkCopy.cs:355` `$"INSERT INTO {table} ({colList}) VALUES ({placeholders})"`；只有值走 `@c`。而 `Directory.Build.props:28` 的 CA2100 理由写"无字符串拼接注入风险" |
| C4 分类器复制 | **成立，且比审计说的更多** [事实] | `bool IsUniqueConstraintViolation` 定义 **6** 处（DapperSqlErrorClassifier:22、EventLogDbContext:383、EventLogPositionReserver:296、IdempotencyDbContext:249、ProjectionCheckpointDbContext:353、InboxDbContext:277）+ PalORM 的 `IsUniqueKeyViolation`（SqlErrorClassifier.cs:25，**命名不同**）= 7 |
| C5 EventLog N+1 | 成立 [事实] | `DapperEventLog.cs:113-147` 循环内每事件一次 `QuerySingleAsync<long>` |
| A4 AddPalFullStack | 成立（自声明） [事实] | `ServiceRegistration.cs:70-76` 自陈"名字容易让人误以为一键到位"，并有测试锁定等价性 |
| A5 HandlerRegistrar 冻结 | 成立 [事实] | `ServiceRegistration.cs:35` 注册为 `IHostedService`；`:427-428` 注释自陈"仅在 IHost 宿主启动时执行"；`Freeze()` 在 `:452` |
| A7 InternalsVisibleTo | 成立 [事实] | src 内 10 处 |
| D1 无锁文件 | 成立 [事实] | 全仓 `packages.lock.json` 零命中（第二方法：`find` 加 `git grep`） |
| T3 无压测/E2E/性能门禁 | 成立 [事实] | `test/` 与 `bench/` 下 `*stress*`/`*load*`/`*perf*` 零命中（仅 bin 产物） |
| T4 墙钟等待 | 成立 [事实] | `test/` 内 `Task.Delay` 22 处 |
| P2 Expression.Compile | 成立 [事实] | `ISpecification.cs:48-56` 带 ITM-073 声明"NativeAOT 下不受支持" |
| C1 Obsolete 与 3.0 不符 | 成立 [事实] | 3 处真 `[Obsolete]` 写"v3.0 移除"；`Directory.Build.props:60` `VersionPrefix=3.0.0`；`git tag` 有 `v3.0.0` |
| L3 文档口径漂移 | 成立 [事实] | `README.en.md:914` "currently at version v2.2.0"；`docs/design/palorm-architecture.md:1273` "- **许可证**: MIT (与 PalDDD 一致)"（另 :1280、:1429）；`docs/architecture.md:115` "## 新增组件（v0.1.0）" |

**顺带解开了审计 L3 提到的"包计数口径混用"**：`src` 下 36 个 csproj，其中 1 个 `IsPackable=false`，故 **35 包 / 36 csproj** 两个数字都对，只是口径不同；"37 目录"来源不明（见 §四 C-1）。[事实]

---

## 四、审计需要修正的 5 处

### C-1 计数错误：src 项目是 36，不是 37

审计 §一写"37 个 src 项目依赖图无环"，§2.2 写"35 个自有 NuGet 包"。实测 `find src -name "*.csproj"` = **36**（§二表）。35 + 1 非打包项 = 36 自洽，"37"无对应口径。审计自己在 L3 指出仓库存在"35 包 / 36 csproj / 37 目录"的口径混用，而它的执行摘要恰好用了那个来历不明的 37。[事实]

### C-2 路径不精确：C6 的端点文件位置

审计写 `src/PalDDD.Hosting.AspNetCore/EndpointExtensions.cs`，实际路径是 `src/PalDDD.Hosting.AspNetCore/**AspNetCore/**EndpointExtensions.cs`（多一层子目录）。[事实]

### C-3 S7 表述过重：库不是"强制开启 LOCAL INFILE"，而是"拒绝在未显式开启时工作"

审计 S7 标题为"MySQL Bulk 路径强制 `AllowLoadLocalInfile=True`"。实读 `DapperBulkCopy.cs:222-231`：代码**检测**连接串是否含该选项，缺失则抛异常，并明确声明"**不自动开启**（该选项涉及服务端 local_infile 权限面，须由调用方显式决策）"、"本库不自动开启该选项——它扩大服务端可访问的文件面，须由调用方显式决策"。[事实]

即：**该路径的设计已经比审计描述的更保守**（显式 opt-in + 说明理由），残余风险（Bulk 路径要求调用方开启该协议）真实存在，但"强制"一词会被读成"库替调用方打开了它"，方向相反。建议改写为"Bulk 路径要求调用方显式开启 LOCAL INFILE"，严重性 [中] 可下调至 [低]。[判断]

### C-4 release.md 状态头的偏差比审计说的轻

审计 L3 写"`docs/release.md:6` 状态头仍以 '2.2.0 已发布' 开篇"。实读第 6 行，它**先写了当前状态** `VersionPrefix=3.0.0 / VersionSuffix=`（空），随后列发布史（2.2.0 / 2.1.0 / 2.0.0 / 1.1.0）。真正的问题是**发布史缺 3.0.0 记录**（而 `v3.0.0` tag 已存在），不是"开篇写 2.2.0"。[事实]

### C-5 A1 的行号范围偏窄（不影响结论）

审计引 `Saga.cs:641-653`。实际反射面是 `:631-657`（含 `:634` 的 `GetMethod`、`:641` 的 `MakeGenericMethod`、`:653` 的 `Activator.CreateInstance`）。审计抓住了两个关键行，但起点漏了 `GetMethod`。[事实]

---

## 五、审计漏掉的 3 项

### N-1 v3.0 承诺面的扩散范围比 C1 说的广

审计 C1 只把"v3.0 承诺与 3.0.0 已发布不符"记在 3 处 `[Obsolete]` 上。实测**同一失效模式至少还有两处接口级承诺**：

| 位置 | 承诺内容 | 现实 |
|---|---|---|
| `OutboxStore.cs:13-20` | "v3.0 破坏性变更预告（ADR-020）：① 跨栈 fencing 统一 ② 异步化" | `VersionPrefix=3.0.0`，tag `v3.0.0` 已存在，两项均未执行 |
| `ServiceRegistration.cs:70-76` | "更名属破坏性变更，**随 v3.0 契约窗口处理**" | 同上 |

这不是 3 处文案笔误，而是**一个版本窗口的系统性欠账**：三处 `[Obsolete]` + 两处接口 Remarks 共 5 个"v3.0 窗口"承诺全部过期。[事实]

**建议**：C1 升格为独立主题"v3.0 窗口承诺清算"，清单为 5 项而非 3 项；处理方式二选一（兑现或改期到 4.0），但必须**同一提交内全清**，否则下次审计会以同样方式重发（PD34 振荡）。[判断]

### N-2 审计自己的报告文件正在阻断本仓全部提交

`docs/review/audit-2026-09-21-technical-audit.md` 是**未跟踪**文件（`git status` 显示 `??`），且为**纯裸 LF**（`CR=0 LF=554`）。而：

1. `encoding-gate.cs:152` 自陈是"工作树漂移防线"，枚举**工作树**而非暂存集，判定 `HasBareLf => bareLf > 0`（`:222`）；
2. `.githooks/pre-commit:37-42` 对 `encoding-gate` **无触发条件**，每次提交都跑。

两者相乘：**该文件使本仓任何提交都被 pre-commit 拦截**，即使它与提交内容无关。实测 `dotnet run scripts/encoding-gate.cs` 退出码 1，唯一 E5 命中即此文件。[事实]

讽刺点在于：审计报告在 S4 正确指出"本地未跟踪配置含口令但已 ignore"，却没有自查它自己的交付物是否触发编码门禁。[判断]

**修法**（一行）：`dotnet run %TEMP%/fix-eol.cs docs/review/audit-2026-09-21-technical-audit.md`。

### N-3 一个能解释多数发现同源性的结构性观察

核验过程中反复出现同一形态：**发现的问题在代码里早已有高质量声明**。

| 审计发现 | 代码内已有声明的位置 |
|---|---|
| O1 CI 恒假分支 | `ci.yml:132-138`（F-04 勘正） |
| A1 Saga 反射 | `Saga.cs:629-630,649-650`（两个 Requires* 特性） |
| A2 三栈分叉 | `OutboxStore.cs:13-20`（ADR-020 预告） |
| A4 名不副实 | `ServiceRegistration.cs:70-76`（自陈 + 测试锁定） |
| A5 IHostedService 依赖 | `ServiceRegistration.cs:427-428`（自陈） |
| C1 Obsolete 过期 | `tech-debt.cs` WARN 口径 |
| C3 同步包装异步 | PalORM DI 工厂注释"仅 Scoped 解析/非热路径" |
| S7 LOCAL INFILE | `DapperBulkCopy.cs:222-231`（拒绝 + 理由） |
| T3 无压测 | `docs/testing.md` 金字塔自陈 |
| P2 Expression.Compile | `ISpecification.cs:49-53`（ITM-073） |

即：该仓库的问题**主要不是"不知道"，而是"声明了但没有转成机械强制"**。这解释了为什么 2026-09-20 审计的 22 项修复"大部分落地"之后，本轮仍能查出 25 条：声明面在增长，强制面没同步。[判断]

**推论（对修复优先级的直接后果）**：最高杠杆的动作不是逐条修 25 个发现，而是**建立"声明 → 强制"的转化管线**。审计的 M0-1（把已有 selftest 门禁接入 CI）正是这条管线，应排在所有条目之前。这也解释了本仓为什么已经有 `gate-audit.cs` 的"五态矩阵"（OK/UNVERIFIED/TOOL/UNWIRED-GATE/REVIEW）：**该机制是对的，只是没接 CI**（见 O2）。

---

## 六、对审计"健康等级 B+"的独立判断

**维持 B+**，理由与审计一致：4 条高危中无一条是"功能坏了"或"数据会丢"，全部是**防守面与叙事面**的缺口（CI 覆盖、AOT 例外面、供应链、覆盖率口径）。代码质量层（零警告实测、分层无环、138 个门禁/守卫脚本、AOT 实跑进 CI、公共 API 快照、坏样本红测）确实高于同规模开源库。

**但建议在评分口径里显式扣一项**：报告交付物自身违反编码门禁且未被自查（N-2）。这类"审计者不在自己的射程内"的盲区，与审计自己在 §2.5 第 2 条点出的"诚实声明替代修复"是同一个病的两种表现。[判断]

**审计自身可信度评级**：断言准确率高（抽验 16 条全部成立），行号引用精度高（唯一失准是 C-6 的起点与 C-2 的目录层级），偏差集中在**计数**（C-1）与**强度用词**（C-3、C-4）。可作为后续工作的可靠输入。

---

## 七、优先级建议（与审计里程碑表对照）

审计的顺序（M0 安全网 → M1 关键修复 → M2 高杠杆 → M3 润色）方向正确。基于本轮核验，建议三处调整：

| 调整 | 理由 |
|---|---|
| **在 M0-1 之前插入"解 E5 阻断"** | 当前任何提交都被拦，其余任务无法落地（N-2）。一行命令 |
| **M1-4 升格为"v3.0 窗口承诺清算"，清单 3 项 → 5 项** | N-1：遗漏的 `OutboxStore.cs` 与 `ServiceRegistration.cs` 两处接口级承诺与 Obsolete 同源同窗 |
| **M0-2（PalORM 入覆盖率基线）标注前置依赖** | 实测该仓 `PalDDD.PalORM.Tests` 本地 46 项红（配置指向外部库 + fixture 拒绝外部库），本地生成 cobertura 不可行；需先解决环境策略或只在 CI 更新基线 |

审计 M0-1 的实现草图（仿 `doc-consistency` 的 `set -o pipefail` + tee + `ci-failed-tests` 注解、更新 `gate-audit` 矩阵、对真实仓库跑变异）与本仓 AGENTS.md §2 的"新增/修改门禁规程"第 7 步（对真实仓库跑一次变异，而非只跑 selftest）一致，可直接执行。[判断]

---

## 八、核验局限

- 未复核审计的浅审区域（§七自陈：三方言 SQL 全文、SourceGen/Analyzers 38 诊断逐条、Kafka/Rabbit 运行时语义、NuGet 上游元数据）
- 未实测 `dotnet restore --locked-mode`、包签名与 OIDC 的可行性（属方案评估，非核验）
- `S3`（PalORM nuspec 为 `AGPL-3.0-only`）未回上游包元数据核实，仅确认仓内 `Directory.Build.props:63` 为 `AGPL-3.0-or-later` 与 `palorm-architecture.md` 写 MIT 的矛盾 [事实]；"or-later 对 only 组件承诺过宽"的法律后果未评估 [判断]
- 未验证"依赖图无环"（需图分析工具或逐 csproj 遍历 ProjectReference），本轮以审计的既有守卫（`ArchitectureBoundaryTests`）作为交叉证据
