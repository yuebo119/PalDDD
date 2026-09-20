# 规则归属审计（2026-09-13）

> **审计对象**：全局 `~/.zcode/AGENTS.md`（545 行 / 54,376 字节，常驻上下文）
> 与本仓规则体系（`docs/conventions.md` + `.githooks/` + `scripts/*.cs` + `ci.yml`）。
> **审计问题**：每条规则该由谁承载？——文档？类型系统？还是脚本门禁？
>
> 方法来源：Harness Engineering 实践的三分类（可脚本化 / 类型化 / 人工判断），
> 结合本仓「验证验证者」判据：**若这条规则被静默忽略，是否有可观察输出不同？**
> 回答「否」= 该规则当前没有机械承载，只是概率引导。

---

## 一、结论

| 发现 | 证据 |
|------|------|
| 全局 AGENTS.md 按**重要度**分层（P0/P1/P2），未按**可执行性**分层，因此无法判断「哪些规则真在起作用」 | 545 行中「能用确定性手段强制的，不写入概率规则」为元规则，但主文件自身未按该元规则重排 |
| 元规则机制本身有效 | 详细参考下沉到 `references/`（19 文件 / 156KB），按需加载不常驻——这一条已落实 |
| 仓库侧已有大量规则完成下沉 | 28→32 个 `scripts/*.cs`；`.githooks/` 5 道提交守卫；`ci.yml` 10+ 门禁 |
| **但下沉覆盖率此前无度量**，且实测发现三类承载缺口 | 见 §二（本次实测） |
| 规则归属问题的**最短判据**是接线状态 | 「门禁写成脚本但不接线」= 该规则仍是概率引导（`ci-coverage.cs` 即此形态） |

---

## 二、本次实测发现的承载缺口（均为验证过的，非推测）

| # | 规则/意图 | 此前承载 | 实测状态 | 本次处置 |
|---|----------|---------|---------|---------|
| 1 | 「验证验证者」：门禁在信任前必须见过它拒绝坏输入 | 无（纯文档规则） | **5/28 脚本有 `--selftest`；0 个被实际注入过坏输入验证** | ✅ **已收口**：新增 `scripts/gate-audit.cs`（静态矩阵 + 隔离式变异探针）；并为全部接线门禁补 `--selftest`——`verify-conventions`(12) · `vuln-scan`(14) · `encoding-gate`(14) · `secret-scan`(16) · `gate-lite`(6) · `doc-consistency`(12) · `gate`(18) · `tech-debt`(17) · `ci-failed-tests`(22) · `guard`(14)，另修 `guard.cs` 漏登的第 8 道守卫。**`UNVERIFIED` 由 9 清零**（14 个接线门禁全部 OK），每项均经变异验证可红或开发中真实红过 |
| 2 | 「改测试修绿」应被拦截 | 无 | `.githooks/` 无任何测试文件守卫 | ✅ 新增 `scripts/test-change-guard.cs` + 接入 pre-commit |
| 3 | 构建输入必须可加载 | 仅靠 CI build 事后兜底 | `21549d3` 在 `.csproj` 注释写 `--` → MSB4025，**全仓构建失败且一路通过所有提交门禁** | ✅ 新增 `scripts/xml-guard.cs` + 接入 pre-commit；修复断构建 |
| 4 | 编码一致性（BOM/mojibake）全仓覆盖 | `encoding-gate` E2/E3 只扫 `src test` | E1 覆盖 6 目录而 E2/E3 仅 2 目录 → `scripts/ samples/ bench/` 下 **69 个 .cs 在盲区**（当前 0 违规，属潜在） | ✅ 抽 `CsScanRoots` 常量扩至 5 目录 + 加范围回归探针 |
| 5 | 覆盖率不低于阈值 | `ci-coverage.cs`（脚本就绪） | **未接入 CI**；且本机因 Docker 缺失无法产出全局数字（12/16 项目） | ✅ **已完成**（2026-09-14，`47c8c24`）：接 CI 独立 `coverage` job + 阈值 0.70（按本机并集实测校准）；详见 §四 |
| 6 | 单模块覆盖率降幅 ≤5% | 人工核对表格 | 无机械判定 | ✅ **已完成**（2026-09-14，`080871c`）：`coverage-baseline.json` + `ci-coverage.cs` Step 6（容差 5pp，自测 18/18）；详见 §四 |
| 7 | 审计文档的时间视角唯一 | `NAMING.md`（只规范命名） | `docs/` 下 **54 处**「本轮/上轮/下轮」表述，导致范围决策不可复现 | ✅ `NAMING.md` 新增规则 6/7 + 写法对照 |
| 8 | 审计文档命名规范被执行 | `NAMING.md` | **7 份文件违规**（禁词 `full`/`comprehensive`）；§六 清单所列文件已全部不存在 | ✅ 清单改为命令式；违规登记为显式债务（未改名，避免破坏引用） |
| 9 | `.pal/prompts/` 结构约束 | `conventions.md` 称「六段结构」，标注为**人工** | 实测 9 个模板段数为 **5/6/7 不等**：7 个为「角色/框架约束/必须遵守/禁止/输出格式」（其中 5 个追加示例段），`bounded-context` 以「项目引用指南」替代「输出格式」，`task-intake` 为验收断言门专用 7 段结构。README 自述「v54 勘正：各模板段数不一」——即该失实表述已被勘正过一次而 conventions 未同步 | ✅ **已从人工转机械**：`verify-conventions.cs` 新增 V8 断言 9 个模板的必填段齐全（只断必填、不限可选段与段序；未登记的新模板只 WARN 不 FAIL）；`conventions.md` 按实测改写 |
| 10 | Windows/Git Bash 下的 AOT 发布命令 | 无 | `conventions.md` 用 `/p:PublishAot=true`，而 MSYS 会把 `/p:` 路径转换为 `p:` → `MSB1008 只能指定一个项目`（本次实测踩中，发布静默失败）；`docs/testing.md`/`performance.md`/`release.md` 均用正确的 `-p:`，只有 conventions 是异类 | ✅ 改为 `-p:` 并写明原因 |
| 11 | `guard.cs` 是否会被「零测试假绿」击中 | 判定仅看 `p.ExitCode == 0`，不断言测试数 | **已检查并排除**（假设被实测证伪）：用一个不可能匹配的过滤器运行 → **exit 8**（MTP 对「匹配到 0 个测试」的退出码），非 0；对照组真实过滤器 → 总计 15 / exit 0。故测试类被改名后本门禁会报 RED 而非假 GREEN | ✅ 无需改动判定逻辑；已将「该保护依赖 MTP 退出码语义、换运行器须重测」写成 `guard.cs` 内的约束注释 |
| 12 | **守卫清单自身的完整性**（本地漏跑） | 无（`guard.cs` 硬编码 7 项，无完备性约束） | **实测缺口**：`CompressionGuardTests`（解压炸弹防护：输入上限/损坏输入/输出上限，16 测试）自 v2.1.0 起存在、`CompressionGuardTests.cs:23` 为 `public sealed class`，但 `guard.cs` 创建时（更晚的 v88/ITM-667）未纳入 → **本地 pre-commit 一直不跑这个安全守卫**，而 CI 的全量 `dotnet test` 会跑——正是 ITM-667 立命要消除的本地漏检窗口 | ✅ 二处修：① 登记第 8 道（实测 `CompressionGuard … GREEN`）；② 新增**注册完整性核查**——扫描 `test/` 下守卫命名类（`*GateTests`/`*GuardTests`/`ArchitectureBoundaryTests`）与本清单比对，未登记即红，有意只在 CI 跑者须登记进 `exemptGuardClasses` 并写理由。另补 `--selftest` 14 例（含「清单含 CompressionGuardTests」这条本次修复自身的回归守卫） |
| 13 | **本地防线的安装缺口**（2026-09-13 审计轮新增，整轮最严重的一处） | 无（`core.hooksPath` 是本地配置，既无安装脚本也无校验） | **实测缺口**：`.githooks/` 脚本已被跟踪（clone 即获得），但 `core.hooksPath` 是 **git 本地配置、不进版本库** → **新 clone 默认无钩子**，整套本地防线（5 道 pre-commit + pre-push）形同不存在。全仓仅在一处括号注里提过它，**无安装脚本 / 无 clone 安装步骤 / 无任何地方校验是否生效**（本机之所以有效，仅因该 clone 的 `.git/config` 恰好设过） | ✅ 新增根 `Directory.Build.targets` 的 `ConfigureGitHooks` 目标：首次构建时配置，把「记得手动 git config」变成「构建即生效」。约束：仅在未设置时写入（不覆盖既有自定义配置）· `.git` 缺失时跳过 · 失败不阻断构建 · 增量判定避免每次构建起 git 进程。实测三向验证（解除→构建→装回→二次跳过）；`AGENTS.md` 与 `docs/development.md` 同步文档化 |

---

## 三、三分类归属表（规则 → 承载物的映射）

### 3.1 已正确下沉（类型系统 / 编译期）

| 规则 | 承载物 |
|------|--------|
| 零反射四条红线 | `ArchitectureBoundaryTests` 源码扫描 |
| AOT 属性组合 | csproj 属性 + `VerifyReferenceAotCompatibility` + CI `aot-verify` 真发布运行 |
| 禁止 `async void` / 强制 `ConfigureAwait(false)` | `SourceCodeGuardTests`（Roslyn 判定，守卫 3/4） |
| 中心包管理 | `Directory.Packages.props` + `CentralPackageTransitivePinningEnabled` |
| 警告即错误 | `TreatWarningsAsErrors=true` + `AnalysisLevel=latest-all`（本次三次拦下我自己的 CA1031） |

### 3.2 已正确下沉（脚本门禁）

| 规则 | 承载物 | 接线 |
|------|--------|:--:|
| 凭据不入库 | `secret-scan.cs` | ✅ pre-commit + CI |
| 编码一致性 | `encoding-gate.cs`（E1-E4，本次扩范围） | ✅ pre-commit + CI |
| 三方一致（计数/口径） | `doc-consistency.cs` | ✅ CI |
| 依赖漏洞 | `vuln-scan.cs` | ✅ CI |
| 守卫测试 | `guard.cs`（8 道 + 注册完整性核查） | ✅ pre-commit |
| 测试文件不得被单方面改动 | `test-change-guard.cs`（本次新增） | ✅ pre-commit |
| XML 良构性 | `xml-guard.cs`（本次新增） | ✅ pre-commit |
| 门禁可信度 | `gate-audit.cs`（本次新增） | ⚠️ 未接线（建议按需手跑或并入 CI 前段） |

### 3.3 应下沉但尚未下沉

| 规则 | 现状 | 可行路径 |
|------|------|---------|
| 「同类缺陷第二次出现即编码成检查」（自我退火） | 纯文档，无触发 | 缺陷台账 + 一条检查：同一 `ITM-` 前缀第二次出现在提交信息时提示补门禁；或并入 `gate-audit` 输出 |
| 覆盖率阈值与单模块降幅 | 见 §二 #5/#6 | 见 `docs/test-coverage-baseline.md` §门禁阈值的前置序列 |
| 审计文档命名 | 有规范无校验 | `xml-guard` 式小守卫：校验 `docs/review/` 新增文件名符合 `{type}-{date}[-{topic}][-v{n}]` 且不含禁词 |
| 审计文档时间视角 | 有规范无校验 | 同上守卫可加一条：新增文档不得含 `本轮\|上轮\|下一轮\|本次会话` |

### 3.4 不可机械化，必须保留为人工判断

- 需求清晰度判断（何时该走 `grill-me`）——依赖语境，无法脚本化。
- 方案取舍与架构裁决（如「Kiro『代码是一次性消耗品』 vs 本仓重构防护」的冲突）——需人裁决，**不属缺口**。
- 辩证决策流程的场景适配（A/B/C 分流）。
- 破坏性操作的人审授权（P0 #5）——注意：**「需要人审」本身要保持概率性**，一旦脚本化即失去守卫意义。

---

## 四、本次未完成项与阻塞（诚实登记）

| 项 | 阻塞 | 解锁条件 |
|---|---|---|
| 覆盖率门禁接入 CI | 无（2026-09-14 已完成） | ✅ **已完成**：阻塞原是「本机无 Docker → 多方言测试失败 → 全局 line-rate 不可测」。**解法不是装 Docker，而是换测量口径**——Docker 只挡住 `PalDDD.PalORM.Tests` 一个项目，另有 4 个项目（Projections.EventLog/Repository.EFCore/Serialization/Transactions）此前因脚本在该项目处中断而**从未跑到**，经查它们均不依赖 Testcontainers，遂本地补跑并产出其 cobertura，再按 **(文件, 行号) 取并集**（等价 ReportGenerator 合并语义；各文件 `lines-valid` 1578–10371 不等，不能简单相加）算出 **16/16 项目 72.98%**（下界，PalORM 的 46 项未跑完）。阈值按项目原始原则「基线 − 3pp」由 0.65 校准为 **0.70**，`ci.yml` 新增独立 `coverage` job（并行、失败域隔离，避免主 job 关键路径翻倍），并要求先 `dotnet tool restore`——`reportgenerator` 是接线的隐藏前置，CI 此前从未还原过它。另记录复校准触发：首次 CI 运行给出含 Docker 的完整值后按其值重校准 |
| 单模块降幅门禁 | 无（2026-09-14 已完成，由并行工作提交 `080871c`「门禁:A6 单模块覆盖率降幅门禁落地」） | ✅ **已完成**：`coverage-baseline.json`（15 个测试项目的 line-rate 基线）+ `ci-coverage.cs` Step 6（容差 5pp 绝对降幅，epsilon 1e-9 吸收浮点噪声，等于容差放行）+ `--update-baseline` 校准入口 + 自测 18/18（含变异验证）+ 端到端 15/15 复算。**设计要点（与本文档原设想不同）**：基线键为**测试项目名**而非模块名——理由是同一测试项目跨时间可比（同一测试集插桩同一装配集），而项目间不可比（各次插桩 `lines-valid` 各异）；手写 flat-JSON 解析以规避 AOT 反射序列化禁用。本次仅补充基线值的来源与复校准说明，未改实现 |
| `AotSample` 纳入 CI AOT 矩阵 | 无（已完成） | ✅ **已完成**：本地 win-x64 等价形式 `dotnet publish -p:PublishAot=true` 实测通过（输出 `Generating native code`、产物仅 native exe 无托管 dll、实跑 exit 0 含 CQRS AOT 值类型管道检查），CI 已补 3 步（`aot-verify` job）。覆盖缺口：PalOrmSample 直接引用仅 PalORM.Sqlite，AotSample 引用 Core/Serialization/Transactions/CQRS/DI，二者不重叠 |
| ~~12 个 src 项目未声明 `IsAotCompatible`~~ **【本项已证伪，见 §七】** | 初审仅 grep 各 csproj 的显式声明，得出「24 声明 / 12 未声明」 | ❌ **假阳性**：根 `Directory.Build.props:46-48` 全局设 `IsAotCompatible=true` / `IsTrimmable=true` / `VerifyReferenceAotCompatibility=true`，未显式声明的项目**继承生效值 true**（`dotnet build -getProperty:IsAotCompatible` 对 `PalDDD.Core` 实测返回 `true`）。且该设计由 `ArchitectureBoundaryTests.CoreProjects_EnableAotReferenceVerification` 断言守护 | ✅ 无需动作；已改为「静态声明计数 ≠ 生效值」的教训（§七） |
| `docs/` 54 处会话相对表述 | 追溯改写成本高、收益低 | 归档整理时按 `NAMING.md` §七 对照表改写 |
| 7 份违规命名的评审文档 | 无（已完成） | ✅ **已裁决并执行，存量清零**：按规则 5 的**原意**（禁止自我评价，理由是「用版本号区分让读者判断」）分类处置——6 份的 `full` 标示**范围**（全仓轮 vs 局部 `*-bench`），可核验、不表达优劣 → 违反规则文字而非意图 → 细化规则（§四 接纳 `review-` 为现行类型前缀、规则 5 增设范围标记白名单、§七「review≠audit」作废）；1 份 `comprehensive-review-2026-09-13.md` 的 `comprehensive` 属自我评价且类型词不在最前 → 改名为 `review-2026-09-13-architecture.md`（全仓仅 1 处引用） |
| `guard.cs` 是否也挂 pre-push | 无（已裁决） | ✅ **裁决为不挂**：8 道守卫套件本身就在 CI 的 Test 步骤内，绕过提交门禁的改动仍会被 CI 拦住——pre-push 加挂只把检测提前、不改变「最终会被发现」，而代价是每次 push ~22s。裁决与复核条件已写入 `guard.cs` 头注释 |
| **文档引用已不存在的 `*.sh`（本次验证期新发现）** | 无（已完成） | ✅ **已完成并机制化**：修正 `conventions.md`/`release.md`/`testing.md`/`CHANGELOG.md` 全部命令形态断链，并由 `verify-conventions.cs` 新增的 **V9**「文档命令形态引用的脚本路径必须存在」机械拦截。**V9 精度经过一轮修整**：首版对真实仓库报 12 处，其中 7 处为误报（5 处来自 `docs/review/` 历史审计记录、2 处为我自己的占位文本 `scripts/xxx.cs`）→ 排除 `docs/review/` 并修正占位文本后归零。全程遵循「低精度门禁比无门禁更坏」（沿 `secret-scan` 设计纪律） |

---

## 五、验证记录（本次实际跑过的）

| 验证 | 命令 | 结果 |
|------|------|:--:|
| 门禁审计自测（含变异能红） | `dotnet run scripts/gate-audit.cs -- --selftest` | 5/5；变异后 3/5 红 ✅ |
| 门禁审计探针 | `dotnet run scripts/gate-audit.cs` | 4/4 PASS ✅ |
| 测试文件守卫自测 | `dotnet run scripts/test-change-guard.cs -- --selftest` | 7/7 ✅ |
| 测试文件守卫端到端（隔离仓库） | 只改测试 / 改测试+改源 / 显式豁免 | 1 / 0 / 0 ✅ |
| XML 守卫自测 | `dotnet run scripts/xml-guard.cs -- --selftest` | 5/5 ✅ |
| XML 全仓扫描 | `dotnet run scripts/xml-guard.cs -- --all` | 89 文件全良构 ✅（修复前 1 失败） |
| 编码门禁（扩范围后） | `dotnet run scripts/encoding-gate.cs` | E1-E4 全 PASS ✅ |
| 文档一致性 | `dotnet run scripts/doc-consistency.cs` | PASS ✅ |
| 变更日志结构 | `dotnet run scripts/changelog-check.cs` | 5/5 PASS ✅ |
| 覆盖率 | `dotnet run scripts/ci-coverage.cs` | ❌ 12/16 项目后中断（Docker 缺失）——阈值不可校准，已记录 |
| AOT 发布与实跑 | `dotnet publish samples/PalDDD.AotSample -c Release -r win-x64 --self-contained -p:PublishAot=true` | ✅ `Generating native code`；产物仅 native exe 无托管 dll；运行 exit 0 含 CQRS 值类型管道检查 |
| `verify-conventions` 自测（含变异能红） | `dotnet run scripts/verify-conventions.cs -- --selftest` | 12/12；变异后 8/12 红 ✅ |
| V8/V9 真实仓库判定 | `dotnet run scripts/verify-conventions.cs -- --quick` | V5/V8/V9 全 PASS ✅ |
| CA1508 假阳性证明 | `dotnet run scripts/verify-conventions.cs -- --build` | 输出「验证通过（--build 模式）」→ `fail == 0` 在该行可达，规则误报成立 ✅ |
| pre-commit 实际拦截（首次观察） | 本会话提交 `14cc980` 前的首次 commit 尝试 | `secret-scan` 拦截 `scripts/gate-audit.cs:138`（探针需注入可匹配密钥的假值），证明提交时守卫非 no-op ✅ |

---

## 六、给全局 `AGENTS.md` 的一条建议（供裁决，未实施）

全局文件当前 545 行、按重要度分层。若要让它自我一致于自身的元规则
（「能用确定性手段强制的，不写入概率规则」），最小改动是：

1. 对每条规则标注承载物：`[脚本: xxx.cs]` / `[类型: csproj 属性]` / `[人工]`；
2. 把 `[脚本]` 类规则压缩为一句话 + 指针，正文移交 `references/`；
3. 保留全部 `[人工]` 类规则在主文件（它们必须常驻，否则形同不存在）。

预期效果：主文件显著变短，且「哪些规则真在起作用」从无法判断变为一眼可查。
**此为判断项，需你裁决后执行——本次未改动全局文件。**

---

## 七、本次审计自身的更正（反向失实与自指陷阱）

审计者也会失实。以下都在提交前被自查或门禁拦下，记录方法而非仅记录结论：

### 更正 1：`IsAotCompatible` 的「未声明」是假阳性

- **误判**：grep 各 `*.csproj` 的显式声明 → 「24 声明 / 12 未声明」，把后者写成缺口。
- **实际**：根 `Directory.Build.props:46-48` 全局设 `IsAotCompatible=true` /
  `IsTrimmable=true` / `VerifyReferenceAotCompatibility=true`；未显式声明的项目
  **继承生效值 true**。用 `dotnet build <proj> -getProperty:IsAotCompatible`
  对 `PalDDD.Core` 实测返回 `true`。该设计另有
  `ArchitectureBoundaryTests.CoreProjects_EnableAotReferenceVerification` 守护。
- **教训**：**静态声明计数 ≠ 生效值**。凡 MSBuild 属性，计数前必须用
  `-getProperty:` 求值，或至少先查根/目录级 props 的全局设置。与
  「负向声明不作数」同源：单一检索方法无法区分「真的没有」与「检索面不对」。

### 更正 2：AOT 验证的第一次「成功」是命令错误的产物

- **误判**：第一次 `dotnet publish ... /p:PublishAot=true` 报 exit 0（实为 `| tail`
  掩码退出码），且目录中存在可运行的 `PalDDD.AotSample.exe`，一度倾向认定已通过。
- **实际**：Git Bash 的 MSYS 把 `/p:` 路径转换为 `p:` → `MSB1008 只能指定一个项目`，
  **发布根本没执行**；那个 exe 是此前（19:00）遗留的产物，非本次构建。
- **更正后结论**：改用 `-p:PublishAot=true` 重跑 → 输出 `Generating native code`、
  产物仅 native exe（4.4MB，无托管 dll）、实跑 exit 0 且含 CQRS AOT 值类型管道检查
  → **AOT 确认通过**，AotSample 遂得以接入 CI。
- **教训**：① 判门禁结果读输出内容，不只看退出码，更不可用管道吞掉退出码；
  ② 目录里存在产物 ≠ 本次命令产出了它——用时间戳/清理后再跑核实归属。

### 更正 3：自指陷阱在同一会话内发生两次（可复现模式，非偶然）

- **现象**：给「检测 X 的门禁」写自测时，测试样本本身形似 X，被该门禁检出。
  ① `gate-audit.cs` 的探针需注入可匹配密钥的假值，字面写入后被 `secret-scan`
  拦下（pre-commit 拦截提交，命中 `gate-audit.cs:138`）；
  ② `secret-scan.cs` 自己的自测样本（AWS 密钥前缀、PEM 私钥块头、连接串内嵌密码
  三类样本）字面写入后被 **secret-scan 自己** 检出——真实运行从
  `PASS 534 文件` 变成 `8 处命中`，且 8 处全部落在 `scripts/secret-scan.cs`。
- **修法**：所有权重样本改为**运行期拼装**（`Join("AK","IA","IOSFODNN7EXAMPLE")`、
  `ConnLine(host, pwd)` 破坏 `Host=`/`Password=` 的同行相邻性、PEM 拆三段），
  使源码文本不构成任何可匹配形态。与 `encoding-gate` 的 E3 指纹 `\uXXXX` 转义
  同源——该门禁的文件头注释早已记录此陷阱，属已知模式。
- **一般化规则（新增）**：**任何「检测 X 的门禁」的自测，其病态样本一律运行期拼装，
  不得字面写入被测门禁的扫描面内。** 三类典型：凭据扫描器（密钥形态）、
  编码门禁（指纹字符）、任何模式匹配型门禁（正则本体）。
  **扫描面包含文档**——本条规则自身在写作时就第三次踩中：本文档记录「不要字面写
  密钥形态」的那一句里引了 PEM 块头字面量，被 `secret-scan` 拦下（`docs/*.md`
  在其扩展名列表内）。故写文档时同样只描述形态、不引字面量。
- **为什么没造成损失**：两次都是**门禁大声拦住**（pre-commit RED / 真实运行报 SUSPECT），
  不是静默失效。反过来说明这套防线在「检测器误伤自己」这个方向上是有牙齿的——
  记录本条的价值在于让下一个人少走一次弯路，而非修补漏洞。
