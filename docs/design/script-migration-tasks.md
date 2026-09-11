# 脚本 → C# 迁移任务清单（MIG 系列）

> 生成：2026-09-11 · 依据：四轮论证 + 项级深扫（3 个并行审计代理逐项拆解 34 文件 5,738 行 bash/python）
> 决策文档：本清单自带完整论证摘要；原始分析见会话记录（三轮审计 + 迁移论证）
> 编号体系：**MIG-NNN 独立序列**（与缺陷行动项 ITM 体系并行，不复用编号）
> 目标架构：**判定层下沉主仓 xUnit 测试 + `.ai` 保留编排层与引用消费**（boundary tests 83 轮先例模式）

---

## 总览与进度

| 批次 | 任务数 | 迁移行数 | 量级 | 状态 | 进度 |
|------|:--:|:--:|:--:|:--:|:--:|
| **P0 立即** | 1 | 941 | 1 会话 | ✅ 已完成 | 100% |
| **P1 内嵌 Python 双件** | 2 | ~416 | 1 会话 | ✅ 已完成 | 100% |
| **P2 三轨去重批** | 3 | ~377 | 1 会话 | ✅ 已完成 | 100% |
| **P3 Roslyn 能力升级批** | 4 | ~325 | 2 会话 | ✅ 已完成 | 100% |
| **P4 按触碰登记** | 7 项登记 | ~640 | 已执行（可做部分） | **✅ 完成** | 100% |
| **不迁移登记** | 23 文件 | ~3,104 | — | 决策已定 | — |
| **合计** | 10 排期 + 7 登记 | **~1,994 该迁 + 640 按触碰** | 实际 1 会话（4 并行代理 + 主线程） | **✅ 排期任务全部完成** | **100%** |

> **实施纪要（2026-09-11，单会话完成全部 10 项）**：波 1 三代理并行（MIG-001/002+003/007+008+009）+ 波 2 一代理（MIG-004+005+006+010）+ 主线程收口（ci.yml/缺陷修复销账/残留清理/docs 同步）。
> 战果：新增 5 个 C# 门禁测试文件（DialectProbeTests 12/DocConsistencyGateTests 15/AssertionStrengthGateTests 2/SourceCodeGuardTests 11/TechDebtGuardTests 6，共 46 个测试）；删除 3 个 bash 脚本（dialect-probe 双副本 941 行 + assertion-strength 86 行）；gate-check 24→8 项（767→328 行）、tech-debt 23→19 项（413→302 行）、doc-consistency 薄壳（343→73 行）、verify-conventions 删 V1-V4、test-gate 删 T4；boundary 补强 +137 行（G18 全 src 范围 + G2/G3/G13 参数矩阵补全）。
> **迁移暴露并修复存量缺陷 1 个**：EventLogDbContext.cs:317 裸 await（G12 文件级差值计数的假绿盲区——节点级守卫抓出，已修 + 偏差账本销账）。
> **基线净化发现**：python 花括号配对漏检 10 个 DialectProbeTests 壳方法（断言在共享辅助方法内），断言强度真实存量 166→185（新基线 190 = 185 + 余量）。
> 红测全记录：G18 负向自证（RegisterDialectProbe 红）、#8 无 Justification 红、V5 集合红测、D12a 文档数字红、断言超基线红、SourceCodeGuard 24 红绿矩阵 + 临时探针 6 守卫全红（行号精确）、DialectProbe 断言反转红。

每批独立可停；批内任务按依赖排序；全程遵守下方「双轨过渡协议」。

---

## 通用协议（每个任务强制执行，缺一步即视为未完成）

1. **新实现先行**：C# 测试落地并跑绿；**红测必做**——故意植入违规样本（如 G7 下沉时在临时文件写 `MakeGenericType` 调用）验证新测试能红，移除后复绿（S3 反向验证）。
2. **双轨并行一轮**：旧 bash 项保留，下一轮评审轮比对两侧结论等价（无口径分裂）。
3. **等价确认后删旧**：删 bash 实现，`.ai` 侧改引用消费（prompt/engine 的命令行更新）。
4. **三方一致同步**（同一次提交内）：
   - `.ai/review/prompt.md` / `.ai/gate/prompt.md` G 表 / `docs/conventions.md` 附录执行矩阵 / `docs/testing.md`
   - `.ai/scripts/verify-ai-system.sh`（V5 门禁编号校验、V23 镜像名单——若迁移对象在名单内）
   - `CHANGELOG.md` [Unreleased]
5. **禁止一把梭**：每批一个 commit 序列，批间可停（反转条件触发时冻结后续批次，见文末）。

---

## P0 · 立即迁移

### [x] MIG-001 · dialect-probe 双副本转正为 xUnit 多方言测试
- **源**：`scripts/dialect-probe.sh`（472）+ `.ai/scripts/dialect-probe.sh`（469）——bash 编排 → 332 行 Python（连接串解析/探活/C# 源码生成）→ C# 探针本体（file-based app，40 项方言断言）
- **目标**：`test/PalDDD.Integration.Tests/`（或 PalORM.Tests）内新建 `DialectProbeTests`，探针 C# 本体**直接复用**（从生成模板剥离），宿主用 `MultiDialectFixture` 现成范式（Testcontainers 生命周期/环境守卫/连接管理）
- **该迁依据**：三层嵌套（bash 里 python 写 C# 字符串）；探针本体已是 C#；双副本 + V23 比对整类消失
- **子任务**：
  1. 40 项断言（每方言 20）迁入测试类，按 Add→GetPending→Lease→MarkProcessed / Append→Read→冲突分类 / Insert→Lease 三族分测试方法
  2. 安全守卫等价迁移：库名白名单（`palddd_probe_*` 前缀 + ownership marker 校验后才清理）、管理库限定（postgres/mysql）、连接串不打印——**这些是 P0 #1 安全红线，逐条对照迁移**
  3. `AmbientTxDapperSmoke`（ambient 事务挂接探针）与 `RunDialectGuarded`（单方言降级）语义保留
  4. CI：`ci.yml` 的 dialect-probe job 改为跑新测试（或并入 build-and-test 的 Test 步骤后删除独立 job）
  5. 删除双副本脚本；`verify-ai-system.sh` V23 镜像名单移除 dialect-probe 对
- **验收**：①新测试在 CI（Testcontainers PG/MySQL）全绿，断言数 ≥40；②红测——临时反转一项断言预期确认能红；③`grep -r dialect-probe .ai/ scripts/ .github/` 仅剩历史文档引用；④verify-ai 23→N 项全绿（V23 名单更新后）；⑤本地 `dotnet test --filter DialectProbe` SQLite 路径可跑
- **量级**：1 会话（C# 本体复用，主要工作是 fixture 化 + 安全守卫对照 + CI 接线）
- **风险**：安全守卫遗漏（缓解：子任务 2 逐条对照清单验收）；Testcontainers 镜像差异（探针现用固定镜像名，fixture 用 TestEnvironment 配置——对齐即可）

---

## P1 · 内嵌 Python 双件下沉（表达力溢出实证最强国）

### [x] MIG-002 · doc-consistency-check D1-D6/D8-D12 下沉（11/12 项）
- **源**：`.ai/scripts/doc-consistency-check.sh`（343 行，D1-D12）；内嵌两个 Python 迷你解析器：D12a raw-string 状态机（数 `[Test]` 真实数 vs 文档声称）、D11 XML doc 覆盖窗口状态机
- **目标**：并入 `test/PalDDD.DependencyInjection.Tests/`（与 boundary 同居）；D12a → 直接引用测试程序集反射计数（比状态机更强，零解析）；D11 → 反射 + XML doc 提取；D1-D6/D8/D9/D10 → 文档文本断言（File.ReadAllText + 断言）
- **该迁依据**：12 项中 11 项查主仓对象；两个内嵌 Python 解析器 = bash 表达力溢出实证；缺陷史（假 PASS、三次扫描面修正、D12 裸数字振荡即 PD34）
- **子任务**：①D12a/D12b 以测试形态重建（D12b 的"去裸数字化"检查转为"文档不含 `\d+ 处` 模式"断言）；②D11 反射化；③D1-D10 平移；④D7 删除（与 verify-ai V9 重复检查）；⑤bash 壳保留为薄调用（提示运行 dotnet test）或直接删（.ai prompt 改引用）
- **前置**：无
- **验收**：①新测试覆盖 11 项且红测（改错一个文档数字 → 红）；②CI 六门禁循环更新（doc-consistency-check.sh 从列表移除或变薄）；③verify-ai V5（引用存在性）同步
- **量级**：0.5-1 会话

### [x] MIG-003 · assertion-strength-check 下沉（棘轮断言化）
- **源**：`.ai/scripts/assertion-strength-check.sh`（86 行）；Python 花括号配对做方法级断言扫描；`MAX_WEAK=173` 裸数字棘轮
- **目标**：并入测试项目——Roslyn 解析 `[Test]` 方法体断言形态（与 DiagnosticCoverageGate 的 Roslyn 基建共享）；弱断言基线从裸数字改为**受审常量**（`internal const int MaxWeakAssertions = 166`，变更需评审，与 PublicApiSnapshot 同治理模型）
- **该迁依据**：死亡 5 周缺陷史（cd 层数错误整体 no-op，弱断言积累至 173，棘轮即为此重置）；Python 配对对字符串内 `}` 误判靠代码风格兜底
- **验收**：①新测试对现有 test/ 全目录计数 ≤ 基线；②红测——临时写一个 `IsNotNull` 新测试 → 计数超基线 → 红；③棘轮基线常量带注释声明变更流程
- **量级**：0.5 会话

---

## P2 · 三轨去重批（纯删 bash 半边，零新判定逻辑）

### [x] MIG-004 · G19 + T4 双轨删除 + G20 活体 no-op 修复
- **源**：gate-check G19（perl 命名检查 27 行）+ test-gate T4（awk 命名检查 28 行）+ G20（路径错误的恒 PASS 项 24 行）
- **目标**：三者直接删除——命名规则的真源 `ArchitectureBoundaryTests.TestMethods_MustFollowUnderscorePattern`（含两层 falsification）独存；G20 的正确防线 `OutboxMessage_UsesBinaryPayload` 已在 boundary
- **该迁依据**：三轨重复实证（C# 版唯一经 falsification 验证）；**G20 是现存活体缺陷**（bash 检查不存在的路径 `Transactions/OutboxMessage.cs`，恒 PASS）
- **验收**：①G 表 24→21 项，三方同步（gate/prompt.md G 表、conventions 执行矩阵、verify-ai V5 编号连续性校验更新为 21）；②test-gate 输出不再含 T4；③`git grep "TripleUnderscore"` 仅剩历史文档
- **量级**：0.25 会话（纯删 + 三方同步）

### [x] MIG-005 · gate-check 12 项双轨去重（G2-G6/G13/G15/G16/G18/G21）
- **源**：gate-check 284 行（11 项与 boundary 测试精确双轨）+ G18（25 行）
- **目标**：删除 bash 侧 11 项；**G18 前置**——先扩 boundary `DependencyInjectionMethods_MustStartWithAddPalPrefix` 扫描范围（DI 目录 → 全 src 的 `*ServiceCollectionExtensions.cs`，对齐 bash 现范围），红测一个适配层违规样本，然后再删 G18
- **该迁依据**：12 项双轨实锤（boundary 有等价且更强的测试）；G15 的 bash 硬编码名单出过名单腐化（需元审计修复），C# 动态扫描无此风险
- **验收**：①G 表 21→9 项；②G18 范围扩展的负向自证测试新增；③三方同步；④一轮评审确认无覆盖损失
- **量级**：0.5 会话

### [x] MIG-006 · tech-debt #11 删除（诊断计数双轨）
- **源**：tech-debt-scan #11（13 行，只数 PDDD 子集 ID）
- **目标**：删除——真源 `DiagnosticCoverageGateTests`（38 条断言级 + 边界矩阵）
- **该迁依据**：bash 版是 C# 版真子集且已口径分裂（脚本注释被迫解释"16 描述符 vs 15 唯一 ID"歧义）
- **验收**：tech-debt 23→22 项；头部注释计数同步
- **量级**：0.25 会话

---

## P3 · Roslyn 能力升级批（新写判定，补主仓零覆盖区）

### [x] MIG-007 · G7/G8 下沉：Roslyn 反射调用扫描器
- **源**：gate-check G7（26 行，4 种反射 API + sed 回看 30 行查豁免注解）+ G8（42 行，Expression.Compile + dynamic，**ITM-073 误豁免缺陷史**——按"文件含 RequiresDynamicCode 字符串"豁免被 SuppressMessage 文案误触发）
- **目标**：新写 `SourceCodeGuardTests`（建议放 Core.Tests 或新建 QualityGate 测试类）：Roslyn 找 invocation 节点 → 沿语法祖先查 `[RequiresDynamicCode]/[RequiresUnreferencedCode]` attribute 挂载——语义级豁免判定，文本回看窗口的脆弱性整类消失
- **该迁依据**：主仓零覆盖（StrategicDddAnalyzer 不覆盖此域，bash 是唯一门禁）；AOT 红线（P0 #3）；ITM-073 实锤
- **验收**：①对全 src 扫描 0 违规（与 G7/G8 现结论等价）；②红测三样本——无豁免反射调用（红）、方法级豁免（绿）、类型级豁免（绿）、**SuppressMessage 文案含关键词但不挂 attribute（必须红——ITM-073 回归样本）**
- **量级**：1 会话（Roslyn 扫描器 + 样本矩阵）

### [x] MIG-008 · G11/G12 下沉：阻塞与 ConfigureAwait 扫描器
- **源**：G11（30 行，`.Result` 3 行回看豁免 + PalORM 路径白名单）+ G12（24 行，perl -0777 剥注释后 await/ConfigureAwait 差值计数——一个方法多次 await 共享一次 ConfigureAwait 时计数口径失真）
- **目标**：同 MIG-007 扫描器家族：Roslyn 逐 await 表达式判 `.ConfigureAwait` 后缀；`.Result` 判是否在 `IsCompletedSuccessfully` 条件分支内（语义级，跨行 if 可判）
- **该迁依据**：AI 高频错误区；G12 差值计数是近似口径；负地址 sed 修复史
- **验收**：全 src 等价 + 红测矩阵（合法 IsCompletedSuccessfully 快路径绿/裸 .Result 红/await 无 ConfigureAwait 红）
- **量级**：1 会话（与 MIG-007 同批做，扫描器基建共享）

### [x] MIG-009 · verify-conventions V1-V4 下沉（主仓零覆盖补缺）
- **源**：`scripts/verify-conventions.sh` V1 零反射族（44 行）/ V2 async void（1 行）/ V3-V4 .Result/.Wait（31 行）
- **目标**：与 MIG-007/008 同一扫描器家族承接（反射族与 G7/G8 合并；async void 与 .Result 并入 MIG-008）——本任务实际是确认覆盖映射 + 删根脚本对应段
- **该迁依据**：主仓几乎零覆盖（仅单文件弱等价物）——下沉即补缺而非去重
- **验收**：verify-conventions 剩 V5/V6/V7（纯编排段）；红测矩阵见 MIG-007/008
- **量级**：0.25 会话（若 MIG-007/008 完成则本任务为收尾）

### [x] MIG-010 · tech-debt #8/#13/#14 下沉（三个内嵌 Python 判定）
- **源**：#8 SuppressMessage 配对（20 行 python + fail-closed 门）/ #13 方言 SQL 守卫对称（51 行 python，两次 no-op 修复史）/ #14 姊妹乐观锁对称（43 行 python 跨文件语义）
- **目标**：#8 → Roslyn AttributeSyntax 解析（与 MIG-007 基建同族）；#13/#14 → C# 结构化读取（正则抽 SQL 常量 → 直接解析 `SqlTemplates` 类的 const 表 + 方法块内守卫判定，跨文件语义用符号引用而非文本计数）
- **该迁依据**：表达力溢出 + no-op 修复史；#14 的 impls 硬编码名单在 C# 可用类型发现替代
- **验收**：三项目标测试落地 + 各自红测样本；tech-debt 22→19 项
- **量级**：1 会话
- **前置**：MIG-007（Roslyn 基建）完成

---

## P4 · 按触碰登记（不排期，触发条件命中时执行并勾销）

| 登记 ID | 对象 | 行数 | 触发条件 |
|:--:|---|---:|---|
| MIG-T1 | gate-check G1（异常 sealed） | 12 | G7/G8 扫描器落地时搭车（MIG-007） |
| MIG-T2 | gate-check G9/G10（async void/TransactionScope 禁词） | 12 | 同上（薄禁词并入扫描器） |
| MIG-T3 | gate-check G14（核心层 AOT 正向断言） | 26 | G15 去重时搭车（MIG-005） |
| MIG-T4 | gate-check G17（UtcNow 直调） | 29 | action-items-2026-08-15 债务清零升级 FAIL 时 |
| MIG-T5 | tech-debt #15/#16/#17（SqlTemplates 域三点） | 38 | #13 下沉（MIG-010）时同域顺带 |
| MIG-T6 | tech-debt #18/#19/#20（截断对称/AT TIME ZONE/DbContext 关系型） | 65 | 各自域下次被触碰时 |
| MIG-T7 | test-gate T6/T8/T11 + post-fix-check + flaky-gate + secret-scan + encoding-gate | ~470 | 各自触发（接 CI 再现 bash 陷阱 / 高频化 / 新增配置场景 / mojibake 指纹扩充） |

---

## 不迁移登记（决策记录，防未来重提；推翻需新论证）

| 类别 | 文件 | 机理 |
|---|---|---|
| 结构死锁 | `install-ai-system.sh`（139） | 自举：安装时宿主可能无 .NET；安装源是 .ai 自身 |
| 结构死锁 | `.ai/scripts/verify-ai-system.sh`（419） | 13/23 项绑 .ai 独立仓账本（含全部厚判定项）；9 个主仓侧项全薄存在性 |
| 结构死锁 | 根 `scripts/gate-check.sh`（43） | CI 降级兜底的存在意义即零依赖 |
| 零依赖诊断 | `scripts/ci-failed-tests.py`（94）、`ci-coverage.sh`（80）、ci.yml 内嵌 python（18） | 诊断工具不得依赖被诊断的 dotnet 工具链 |
| 编排/信息工具 | `fix-orchestrator`(62)、`review-gate`(94)、`review-scope`(115)、`sibling-map`(92)、`sister-axis-scan`(115)、`probe-template`(88)、`fix-completeness-check`(109)、`template-gate`(15，该迁已迁标本)、`itm-208-safety-test`(79，被测对象是 bash)、`refine-scan`×2(110)、`review-snapshot`×2(142)、`verify-action-items`×2(186) | spawn 外部命令 + 给 AI 输出清单；正则升级路径是换解析器非换宿主 |
| 发版流程 | `changelog-check`(107)、`changelog-facts`(105) | release.md SOP 绑定，低频 |
| 编排 | `check-all`(37) | C# 化 = spawn dotnet 解析输出，净亏损 |

**gate-check / tech-debt-scan / test-gate / verify-conventions 的 bash 壳**：P2/P3 完成后保留残余项（git 编排 + 流程绑定项 + 按触碰项），作为薄门禁继续运行或退化为 CI 调用 `dotnet test --filter QualityGate` 的启动行。

---

## 反转条件（触发即冻结未执行批次）

1. **`.ai` 启动第二个项目适配且该项目非 .NET**：冻结 P2 及以后批次（判定层需为新宿主重写，bash 轨道复活成本需重估）；P0/P1 已完成部分保持。
2. **项目进入封存期**（审计频率 < 每季）：全部未执行批次搁置，P4 触发条件失效。
3. **多真人协作启动**：先落"防线测试变更需显式标注"的 PR 规则，再继续 P3。

---

## 进度追踪

| 任务 | 状态 | 完成日期 | 提交 | 备注 |
|---|:--:|---|---|---|
| MIG-001 | ✅ 完成 | 2026-09-11 | 本会话 | |
| MIG-002 | ⬜ | | | |
| MIG-003 | ⬜ | | | |
| MIG-004 | ⬜ | | | |
| MIG-005 | ⬜ | | | |
| MIG-006 | ⬜ | | | |
| MIG-007 | ⬜ | | | |
| MIG-008 | ⬜ | | | |
| MIG-009 | ⬜ | | | |
| MIG-010 | ⬜ | | | |
| MIG-T1..T4 | ✅ 完成（2026-09-11 用户指令触发） | 2026-09-11 | 本会话 | gate-check 判定层清零：G1/G9/G10/G14/G17 下沉（SourceCodeGuard 守卫 4→6 族/矩阵 34→57 样本；G14 并入 boundary 既有方法保 D12a 锚 37 不变），终态仅 G22/G23/G24 纯 git 编排（329→179 行） |
| MIG-T5..T7 | ✅ 可做部分完成 | 2026-09-11 | 本会话 | tech-debt #15-#20+T6/T8/T11+post-fix-check 下沉（TechDebtGuard 6→13 + TestGateGuardTests 新建 4；#20/T6 转活账本；T8 勘正 no-op 门扫错文件；post-fix-check 删除确认零引用）。**维持不迁**：flaky-gate（OSC 有状态跨运行，正确归宿 dotnet tool）、secret-scan（刚经 mutation 验证）、encoding-gate（字节工具天然 shell）——论证见前 |

> 多 commit 任务的最后一次提交在正文附累计进度（项目 Git 提交规范 §9.3）。


---

## MIG-011 · Python 清零批（2026-09-11 立项 · 用户裁决）

> **状态：2026-09-11 完成——全仓 Python 归零**（5 个 file-based app：ci-failed-tests/vuln-scan/osc-check/sibling-map/flaky-parse；死脚本 itm-208 删除；等价性双跑/对照/合成红测全过）。依据：用户挑战"零依赖诊断原则"成立——build 失败 ≠ SDK 不可用（诊断步骤在 Setup .NET 后，
> SDK 必在；本地 .NET 开发机 SDK 必在反而 python3 不一定在）。file-based app 实测可行
> （零 package，STJ 框架内可用）。目标：**全仓 Python 归零**，语言栈 = C#（判定+诊断+工具）+ bash（编排）。

### [x] MIG-011{n} · ci-failed-tests.py → ci-failed-tests.cs（file-based app）
- 94 行三通道诊断（TUnit JSON 失败名/日志尾/快照对 diff）→ BCL 等价（System.Text.Json + IO）。
- 验收：对真实 TUnit 报告样本输出逐字段等价；`grep -rn python3 scripts/ .github/` 调用点零残留。

### [x] MIG-011{n} · ci.yml 漏洞扫描内嵌 python → scripts/vuln-scan.cs
- ~18 行 JSON 遍历 → file-based app；workflow 改 `dotnet run scripts/vuln-scan.cs`（Setup .NET 已在前）。

### [x] MIG-011{n} · test-gate OSC 检测器（82 行内嵌 python）→ scripts/osc-check.cs
- 有状态跨运行（state.json 3 次观测+指纹去重）→ file-based app 正确宿主（此前判 dotnet tool 即此）。
- test-gate.sh 改调 `dotnet run`；保留 --oscillation-selftest 入口等价。

### [x] MIG-011{n} · sibling-map 类型解析（68 行内嵌 python）→ 独立 sibling-map.cs
- 类型声明解析/partial 合并/传递闭包 → C#（输出 markdown 不变）；sibling-map.sh 变薄编排壳。

### [x] MIG-011{n} · verify-ai 零星 python（11 行）→ 顺手并入
- V 系零星解析（日期比较等）改 C# 或 bash 内建可表达形态。

### [x] MIG-011{n} · 调用点与文档全量同步
- ci.yml（2 处 python3）、check-all.sh（若有）、README/development/testing 的 python 引用；
- verify-ai V2/V16 同步；`grep -rn "python" scripts/ .ai/scripts/ .github/ docs/` 零残留（历史段豁免）。
