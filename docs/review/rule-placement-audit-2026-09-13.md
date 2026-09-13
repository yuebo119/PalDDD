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
| 1 | 「验证验证者」：门禁在信任前必须见过它拒绝坏输入 | 无（纯文档规则） | **5/28 脚本有 `--selftest`；0 个被实际注入过坏输入验证** | ✅ 新增 `scripts/gate-audit.cs`：静态矩阵 + 隔离式变异探针，4 项探针 |
| 2 | 「改测试修绿」应被拦截 | 无 | `.githooks/` 无任何测试文件守卫 | ✅ 新增 `scripts/test-change-guard.cs` + 接入 pre-commit |
| 3 | 构建输入必须可加载 | 仅靠 CI build 事后兜底 | `21549d3` 在 `.csproj` 注释写 `--` → MSB4025，**全仓构建失败且一路通过所有提交门禁** | ✅ 新增 `scripts/xml-guard.cs` + 接入 pre-commit；修复断构建 |
| 4 | 编码一致性（BOM/mojibake）全仓覆盖 | `encoding-gate` E2/E3 只扫 `src test` | E1 覆盖 6 目录而 E2/E3 仅 2 目录 → `scripts/ samples/ bench/` 下 **69 个 .cs 在盲区**（当前 0 违规，属潜在） | ✅ 抽 `CsScanRoots` 常量扩至 5 目录 + 加范围回归探针 |
| 5 | 覆盖率不低于阈值 | `ci-coverage.cs`（脚本就绪） | **未接入 CI**；且本机因 Docker 缺失无法产出全局数字（12/16 项目） | ⚠️ 记录阻塞与前置，**未接线**（见 §四） |
| 6 | 单模块覆盖率降幅 ≤5% | 人工核对表格 | 无机械判定 | ⚠️ 已记录可行路径（逐项目 cobertura 已产出），未实现 |
| 7 | 审计文档的时间视角唯一 | `NAMING.md`（只规范命名） | `docs/` 下 **54 处**「本轮/上轮/下轮」表述，导致范围决策不可复现 | ✅ `NAMING.md` 新增规则 6/7 + 写法对照 |
| 8 | 审计文档命名规范被执行 | `NAMING.md` | **7 份文件违规**（禁词 `full`/`comprehensive`）；§六 清单所列文件已全部不存在 | ✅ 清单改为命令式；违规登记为显式债务（未改名，避免破坏引用） |
| 9 | `.pal/prompts/` 结构约束 | `conventions.md` 称「六段结构」，标注为**人工** | 实测 9 个模板段数为 **5/6/7 不等**：7 个为「角色/框架约束/必须遵守/禁止/输出格式」（其中 5 个追加示例段），`bounded-context` 以「项目引用指南」替代「输出格式」，`task-intake` 为验收断言门专用 7 段结构。README 自述「v54 勘正：各模板段数不一」——即该失实表述已被勘正过一次而 conventions 未同步 | ✅ 按实测改写并标注为「人工（机械化候选）」 |
| 10 | Windows/Git Bash 下的 AOT 发布命令 | 无 | `conventions.md` 用 `/p:PublishAot=true`，而 MSYS 会把 `/p:` 路径转换为 `p:` → `MSB1008 只能指定一个项目`（本次实测踩中，发布静默失败）；`docs/testing.md`/`performance.md`/`release.md` 均用正确的 `-p:`，只有 conventions 是异类 | ✅ 改为 `-p:` 并写明原因 |

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
| 守卫测试 | `guard.cs`（7 道） | ✅ pre-commit |
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
| 覆盖率门禁接入 CI | 阈值未校准：本机 Docker 不可用 → 多方言测试失败 → 全局 line-rate 不可测 | 在具备 Docker 的环境跑一次 `dotnet run scripts/ci-coverage.cs` 取真实值 → 校准阈值 → 接线 → 同步三处文档 |
| 单模块降幅门禁 | 依赖上一项（同一脚本） | 同上 |
| `AotSample` 纳入 CI AOT 矩阵 | 无（已完成） | ✅ **已完成**：本地 win-x64 等价形式 `dotnet publish -p:PublishAot=true` 实测通过（输出 `Generating native code`、产物仅 native exe 无托管 dll、实跑 exit 0 含 CQRS AOT 值类型管道检查），CI 已补 3 步（`aot-verify` job）。覆盖缺口：PalOrmSample 直接引用仅 PalORM.Sqlite，AotSample 引用 Core/Serialization/Transactions/CQRS/DI，二者不重叠 |
| ~~12 个 src 项目未声明 `IsAotCompatible`~~ **【本项已证伪，见 §七】** | 初审仅 grep 各 csproj 的显式声明，得出「24 声明 / 12 未声明」 | ❌ **假阳性**：根 `Directory.Build.props:46-48` 全局设 `IsAotCompatible=true` / `IsTrimmable=true` / `VerifyReferenceAotCompatibility=true`，未显式声明的项目**继承生效值 true**（`dotnet build -getProperty:IsAotCompatible` 对 `PalDDD.Core` 实测返回 `true`）。且该设计由 `ArchitectureBoundaryTests.CoreProjects_EnableAotReferenceVerification` 断言守护 | ✅ 无需动作；已改为「静态声明计数 ≠ 生效值」的教训（§七） |
| `docs/` 54 处会话相对表述 | 追溯改写成本高、收益低 | 归档整理时按 `NAMING.md` §七 对照表改写 |
| 7 份违规命名的评审文档 | 改名会破坏既有交叉引用，属判断问题 | 维护者裁决；改名需 `grep -rn "<旧名>" docs/ README.md` 同步引用 |
| **文档引用已不存在的 `*.sh`（本次验证期新发现）** | MIG-011/012 迁移了脚本但未全量收口文档。实测 `docs/` 中 48 行引用不存在的 `.sh`：其中 **8 行是可执行命令**（`bash scripts/xxx.sh`）、2 行历史提及（正确保留）、其余为「机制名指代」（如 pitfalls 表格里描述当前守卫）。本次已修 5 处主干（`conventions.md` ×2、`release.md` ×3、`testing.md` ×2），**未全量收口** | 剩余可执行命令集中在 `release.md:310/663/684`、`conventions.md:928/961/1037/1038`、`testing.md:475`、`pitfalls.md:139`。**建议机制化而非手改**：加一条「文档引用的脚本路径必须存在」的检查（同 `xml-guard` 形态），因为它已二次回归（`review-2026-09-11-v3.md` 曾以「MIG 迁移的姊妹同步不完整」为 P2 主题收口过一轮） |

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

## 七、本次审计自身的两次更正（反向失实教训）

审计者也会失实。两次都在提交前被自查拦下，记录方法而非仅记录结论：

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
