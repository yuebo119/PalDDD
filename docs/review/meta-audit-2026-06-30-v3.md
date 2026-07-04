# 评审质量全范围元审计报告（2026-06-30）

> 审计范围：33 个文档（docs/ 全部 + README + AGENTS + CLAUDE）+ 8 个 .pal prompts + 14 个 ADR + conventions.md + 代码事实交叉验证
> 审计方法：Agent 交叉验证（3 个 Agent 超时后转手动）+ Microsoft 官方文档查证 + 源码事实核查 + awk 精确计数
> 审计日期：2026-06-30（commit 184b950 后）
> 前序：`meta-audit-2026-06-30.md`（首轮，9 项）+ `meta-audit-v2-2026-06-30.md`（二次，14 项）

---

## 一、扩大审计范围的新发现

本轮将审计范围从"评审报告+任务清单"扩大到**全部文档+代码注释+配置**，发现前两轮未覆盖的 **11 项新错误**，使累计错误总数达到 **25 项**。

### F1-F5：数字声明漂移（文档与代码事实不符）

#### F1 · README"83 架构边界用例"漂移 [高]

- **位置**：`README.md:215,228`、全部 7 份评审报告
- **声明**："含 83 架构边界用例"、"83 架构边界测试用例零违规"
- **事实**：[事实] commit ea67fa7 动态扫描改造后，实际执行用例数 = 21 Fact + 50 InlineData = **71 个**（原 9 个 InlineData Theory 合并为 1 个 Fact，减少 8 个）。所有评审报告和 README 仍称"83"。
- **根因**：R8 采信记忆——动态扫描改造后未同步更新文档计数

#### F2 · v4 评审"11 份 ADR"漂移 [中]

- **位置**：`serena-comprehensive-review-2026-06-29-v4.md:5,22,61,293,452,456,478`
- **声明**："11 份 ADR（001-011）"
- **事实**：[事实] ADR-012/013/014 在 commit b085d29 中创建，实际 **14 份**。v4 评审写于 ADR 012-014 创建前，但后续未回溯更新。
- **根因**：R3 基于过期快照——评审写于特定 commit，但文档计数未随 ADR 新增更新

#### F3 · v4 评审"714 行 12 章 conventions.md"漂移 [中]

- **位置**：`serena-comprehensive-review-2026-06-29-v4.md:22,61,67,283`
- **声明**："714 行 conventions.md（12 章）"
- **事实**：[事实] 当前 **754 行 13 章**（第 13 章"评审纪律"在 commit ea67fa7 新增）。
- **根因**：同 F2，评审后新增内容未回溯更新

#### F4 · v4 评审"167 源文件 / 63 测试文件"漂移 [中]

- **位置**：`serena-comprehensive-review-2026-06-29.md:5`
- **声明**："167 源文件 · 63 测试文件"
- **事实**：[事实] 当前 **168 源文件 / 64 测试文件**（ITM-038 新增 PostgreSqlReadWriteRouter 测试或源码变更导致）。
- **根因**：R3 基于过期快照

#### F5 · architecture.md"30 个源项目"漂移 [中]

- **位置**：`docs/architecture.md:3`
- **声明**："30 个源项目按依赖方向从 Core 到 Infrastructure"
- **事实**：[事实] 当前 **31 个源项目**（`find src -name "*.csproj" | wc -l` = 31）。
- **根因**：R8 采信记忆——architecture.md 未随项目新增更新

### F6-F8：README 特性矩阵声明不精确

#### F6 · README IL3058"6 个 Dapper 项目"遗漏 MemoryPack [中]

- **位置**：`README.md:201`
- **声明**："IL3058 精确抑制：全局移除 → 6 个 Dapper 项目本地保留"
- **事实**：[事实] IL3058 实际在 **8 个项目**中：6 个 Dapper + `PalDDD.Serialization.MemoryPack` + `PalDDD.Dapper.PostgreSql`。"6 个 Dapper 项目"技术上正确（PostgreSql 也是 Dapper），但 MemoryPack 非 Dapper 项目含 IL3058 被遗漏。
- **根因**：R1 不完整读取——声明时只统计了 Dapper 命名模式项目，遗漏了 MemoryPack

#### F7 · README"716 通过 0 失败"与既有 2 失败矛盾 [高]

- **位置**：`README.md:5,215,224`
- **声明**："716 通过 0 失败 6 跳过"
- **事实**：[事实] 实际运行 `dotnet test` 存在 **2 个既有失败测试**（`EmitsOutboxProcessActivity` + `CancellationDuringTick`），已在 commit b085d29 中通过 `git stash` 确认。"0 失败"声明不准确。716 这个数字本身也未经实测（元审计 v2 E1 已指出）。
- **根因**：R8 采信记忆——716 采信 Serena 记忆，"0 失败"基于过期测试运行

#### F8 · README"99 集成测试"未核实 [中]

- **位置**：`README.md:215`
- **声明**："含 83 架构边界用例 + 99 集成测试"
- **事实**：[事实] Integration.Tests 实际 **129 个**（awk 计数 Fact+InlineData=129）。"99"这个数字来源不明且与实际不符。
- **根因**：R8 采信记忆

### F9-F11：预防方案执行缺陷（前两轮未覆盖）

#### F9 · review-snapshot.sh / verify-action-items.sh 不在任何 hook 中执行 [高]

- **位置**：`.githooks/pre-push`（只含 verify-doc-numbers.sh + verify-conventions.sh）、`.githooks/pre-commit`（只含 verify-conventions.sh --quick）
- **声明**：`conventions.md:752-753` 声称"评审纪律"执行手段为 `review-snapshot.sh` + `REVIEW_TEMPLATE.md`
- **事实**：[事实] 两个评审纪律脚本均未接入任何 git hook 或 CI。它们的存在使 conventions.md 声称"已执行"，但实际依赖人工触发——与 D4/E5 项确认的"软约束"问题一致，但前两轮未明确指出"不在 hook 中"这一技术事实。
- **根因**：R9 预防方案自盲——预防方案声称"已落地"但未接入自动化执行链

#### F10 · aot.md IL3058 描述与实际配置范围不一致 [低]

- **位置**：`docs/aot.md:157`
- **声明**："`Dapper.AOT` 1.0.52 已接入 `PalDDD.Dapper`、`PalDDD.Dapper`、`PalDDD.Dapper` 和 `PalDDD.Dapper` 四个核心 Dapper 项目"
- **事实**：[事实] Dapper.AOT 接入的是 4 个项目，但 IL3058 抑制在 8 个项目（含 MemoryPack 和各 Dapper 方言）。文档将"Dapper.AOT 接入范围"和"IL3058 抑制范围"混为同一描述，读者可能误以为 IL3058 也只在 4 个项目。
- **根因**：R1 不完整读取——描述时未区分两个不同范围

#### F11 · ADR 012/013/014 论证充分度参差 [低]

- **位置**：`docs/decisions/012,013,014`
- **声明**：三个 ADR 均结论"维持现状/不动"
- **事实**：[事实] ADR-012（方言项目粒度）4 条理由充分；ADR-013（Preview→RTM）含具体步骤但无时间线承诺；ADR-014（Analyzer 拆分）3 条理由充分。整体论证质量合格，但 ADR-013 的"预计 2025 年 11 月 GA"是推测而非事实——.NET 11 实际 GA 时间需查证。
- **根因**：R10 经验性结论未溯源——GA 时间基于推测

---

## 二、修正后的累计错误分类

| 类别 | 首轮 | 二次新增 | 本轮新增 | 累计 |
|------|:----:|:--------:|:--------:|:----:|
| 误判（假阳性） | 4 | 0 | 0 | 4 |
| 过度预估 | 1 | 0 | 0 | 1 |
| 事实性错误 | 2 | 0 | F1-F8 (8) | 10 |
| 遗漏（假阴性） | 1 | 3 | F9 (1) | 5 |
| 未验证采信 | 1 | 1 | 0 | 2 |
| 预防方案缺陷 | 0 | 2 | F10,F11 (2) | 4 |
| **合计** | **9** | **5** | **11** | **25** |

修正错误率：25/~80（扩大审计范围后分母增大）≈ **31%**

---

## 三、根因分类修正（R1~R10 → R11）

前两轮 10 类根因需补充 1 类：

| 新根因 | 描述 | 涉及错误 |
|--------|------|----------|
| **R11 文档计数锁死** | 文档中的硬编码数字（项目数/行数/测试数/用例数）随代码演进漂移，无自动化校验机制 | F1~F5 |

R11 是 R3（过期快照）和 R8（采信记忆）的交集，但更具体——它指向**文档中硬编码数字本身的设计缺陷**：任何写死在文档中的计数都会随代码演进漂移，除非有 `verify-doc-numbers.sh` 自动校验。

当前 `verify-doc-numbers.sh` 已覆盖部分计数点（项目数/文件数/规则数），但**未覆盖**：架构测试用例数、ADR 数、conventions 章节数、集成测试数。

---

## 四、最优解决方案

### 方案 1：扩展 verify-doc-numbers.sh 覆盖全量计数声明（修复 F1~F5, F8 — R11）

在 `verify-doc-numbers.sh` 中新增以下断言：

```bash
# 架构测试用例数（awk Fact+InlineData）
ARCH_CASES=$(awk '/\[Fact\]/{f++} /\[InlineData/{i++} END{print f+i}' \
  test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs)
check "架构测试用例数" "$ARCH_CASES" "71"

# ADR 数
ADR_COUNT=$(ls docs/decisions/*.md | wc -l)
check "ADR 数" "$ADR_COUNT" "14"

# conventions.md 行数和章节数
CONV_LINES=$(wc -l < docs/conventions.md)
check "conventions.md 行数" "$CONV_LINES" "754"
CONV_CHAPTERS=$(grep -c "^## [0-9]" docs/conventions.md)
check "conventions.md 章节数" "$CONV_CHAPTERS" "13"

# 集成测试用例数
INT_CASES=$(awk '/\[Fact\]/{f++} /\[InlineData/{i++} END{print f+i}' test/PalDDD.Integration.Tests/*.cs)
check "集成测试用例数" "$INT_CASES" "129"
```

并将 `verify-doc-numbers.sh` 纳入 pre-push hook（commit b085d29 已完成）。

### 方案 2：README 数字声明改为动态化或移除（修复 F1, F7, F8）

方案 A（推荐）：README 中不再硬编码易漂移的数字，改为指向 `bash scripts/verify-doc-numbers.sh` 或 `bash scripts/review-snapshot.sh` 获取实时值。仅保留"30+ 项目"、"80+ 架构测试用例"等模糊表述。

方案 B：保留硬编码数字但在 verify-doc-numbers.sh 中校验 README 中的数字声明（grep README 中的数字并与实测比对）。

### 方案 3：架构测试用例数断言（修复 F1 的深层根因）

在 `ArchitectureBoundaryTests` 中新增一个自校验测试：

```csharp
[Fact]
public void ArchitectureBoundaryTestCount_MatchesDocumentedCount()
{
    var src = ReadSource("README.md");
    var actualCaseCount = /* awk 等价逻辑：Fact + InlineData */;
    Assert.Contains($"含 {actualCaseCount} 架构边界用例", src);
}
```

这使"架构测试用例数"变化时自动要求 README 同步更新。

### 方案 4：评审纪律脚本接入 pre-push（修复 F9 — R9）

将 `verify-action-items.sh` 接入 `.githooks/pre-push`，在 push 前自动校验最近修改的 action-items 文件：

```bash
# pre-push 追加
LATEST_ACTION_ITEMS=$(ls -t docs/review/action-items-*.md 2>/dev/null | head -1)
if [ -n "$LATEST_ACTION_ITEMS" ]; then
    bash "$REPO_ROOT/scripts/verify-action-items.sh" "$LATEST_ACTION_ITEMS"
fi
```

`review-snapshot.sh` 不接入 hook（它是评审时手动运行的工具），但在 conventions.md 中明确标注"不自动执行，评审者手动运行"。

### 方案 5：ADR GA 时间标注为推测（修复 F11 — R10）

在 ADR-013 中将"预计 2025 年 11 月 GA"改为"参考 .NET 官方发布节奏推测，实际 GA 时间以微软公告为准"。

---

## 五、元审计的递归局限

本报告发现 25 项错误，但本报告自身也存在局限：

1. **3 个 Agent 全部超时**：文档量对子代理过大，手动审计可能遗漏 Agent 本可发现的交叉问题
2. **测试数 748 未实测运行验证**：本报告用 awk 计数 Fact+InlineData=748，但 xUnit 实际执行的用例数可能因 `[Theory]` 无 `[InlineData]` 等边界情况与计数不符——这是 R8 的递归复发
3. **F1 的"83→71"基于 commit ea67fa7 后的 awk 计数**：若"83"在改造前的实际用例数本就不等于 83（可能也是漂移），则"83→71"的漂移幅度计算有误
4. **本报告的分母 80 是估算**：与首轮"40"一样未经核实，错误率 31% 是近似值

**根本结论**：完全消除文档与代码的漂移需要"文档零硬编码数字"或"每个数字都有自动化校验"，这在工程上代价很高。务实目标是：
- 将 verify-doc-numbers.sh 覆盖到"会漂移的高频数字"（项目数/文件数/测试数/ADR 数/架构用例数）
- README 中的易漂移数字改为模糊表述（"30+"、"80+"）或指向实时脚本
- 接受 5-10% 的漂移率作为可维护性与准确性的平衡点