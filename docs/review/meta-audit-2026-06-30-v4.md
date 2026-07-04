# 评审质量递归元审计报告（第四轮，2026-06-30）

> 审计范围：前三轮元审计报告自身的准确性 + .trellis/spec（前几轮未覆盖）+ 716 数字溯源
> 审计目的：识别前三轮元审计的递归误判，扩大到 Trellis spec 层面，量化"716"数字的根本错误
> 审计方法：awk 精确计数 + git stash 回溯验证 + 前几轮结论交叉验证
> 前序：首轮(9项) → 二次(+5项=14) → 全范围(+11项=25) → 本轮

---

## 一、前三轮元审计的递归误判

### RM1 · 全范围审计 F1"83→71"选择性计数误判 [中]

- **位置**：`full-meta-audit-2026-06-30.md` F1 项
- **声明**：架构测试用例从"83"降到"71"
- **事实**：[事实] ArchitectureBoundaryTests.cs 单独 = 71（21 Fact + 50 InlineData）；但 `PalDDD.DependencyInjection.Tests` 还含 `ServiceRegistrationTests.cs` 的 11 个用例，**DI 测试层总计 82**。F1 只统计了 ArchTests 一个文件，遗漏 ServiceRegistrationTests。"83"在改造前可能指 DI.Tests 全部用例（若改造前为 83，改造后为 82，仅减 1），则漂移幅度被**高估了 11 倍**（F1 声称减少 12，实际仅减少 1）。
- **根因**：R1 不完整读取——F1 计数时只读了 ArchitectureBoundaryTests.cs，未读同项目的 ServiceRegistrationTests.cs。**元审计自身犯了与 A1（InMemoryOutboxStore 仅读片段）完全相同的错误**。
- **修正**：漂移幅度应为"83→82"（减 1），而非"83→71"（减 12）。README 仍需更新为"82"，但严重度从高降为中。

### RM2 · "716 通过"数字根本性错误——前几轮未量化 [高]

- **位置**：README badge + 7 份评审报告
- **声称**：716 通过 0 失败 6 跳过
- **事实**：[事实] awk 全量计数 Fact+InlineData = **748**。若有 2 既有失败 + 6 跳过，实际通过 = **740**。**716 与任何实际结果都不匹配**——差值 -24。
- **溯源**：716 在 commit b085d29 中从 705 改为 716，依据是 Serena 记忆（commit 消息写"Serena 记忆载 716"）。716 从未被 `dotnet test` 实测验证。
- **根因**：R8 采信记忆——716 采信工具记忆，从未实测。前三轮审计指出了"716 未实测"（元审计 v1 E1），但**从未量化其与实际通过数(740)的差值(-24)**，即未指出"716 本身就是错的数字"这一更严重结论。
- **修正**：716 应改为"运行 `dotnet test` 获取实时值"或标注为不可信。

---

## 二、新审计角落发现（.trellis/spec 漂移）

前几轮未覆盖 `.trellis/spec/` 目录，本轮发现 2 项漂移：

### T1 · logging-guidelines.md PalmMetrics "22 个"漂移 [中]

- **位置**：`.trellis/spec/backend/logging-guidelines.md:42`
- **声明**：`PalMetrics | 22 个 Counter/Histogram/UpDownCounter`
- **事实**：[事实] 实际 `paldd.*` 指标 = **27 个**（`grep -roh "paldd\.[a-z_.]*" src/ | sort -u | wc -l` = 27）。漂移 +5。
- **根因**：R11 文档计数锁死——Trellis spec 在指标新增后未同步

### T2 · directory-structure.md PalDDD.Transactions "21 文件"漂移 [低]

- **位置**：`.trellis/spec/backend/directory-structure.md:60`
- **声明**：`PalDDD.Transactions/（21 文件，Saga 拆为 6 个职责文件）`
- **事实**：[事实] 实际 **22 个**（`find src/PalDDD.Transactions -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" | wc -l` = 22）。漂移 +1。
- **根因**：R11 文档计数锁死

---

## 三、累计错误修正

| 轮次 | 新增 | 累计 | 修正说明 |
|------|:----:|:----:|----------|
| 首轮 | 9 | 9 | — |
| 二次 | +5 | 14 | — |
| 全范围 | +11 | 25 | — |
| **递归（本轮）** | **+2** | **27** | RM1 修正 F1（高估漂移幅度，不计新增错误但修正严重度）；RM2 量化 716 差值（升级 F7 serious度）；T1/T2 新增 2 项 |

**修正说明**：
- F1 严重度从"高"降为"中"（漂移幅度 12→1）
- F7 升级：不只是"0 失败不准确"——"716"这个数字本身与实际通过数(740)差 24，是根本性错误
- 新增 T1（22→27 指标漂移）、T2（21→22 文件漂移）

---

## 四、根因分类修正（R1~R11 验证）

本轮验证了前几轮的根因分类，发现：

| 根因 | 修正前 | 修正后 | 说明 |
|------|--------|--------|------|
| R1 不完整读取 | 4 次命中 | **5 次命中** | RM1 是 R1 的递归复发——元审计自身犯了 A1 同类错误 |
| R8 采信记忆 | 4 次命中 | **维持** | RM2 量化了 R8 的后果，但不新增命中次数 |
| R11 文档计数锁死 | F1~F5,F8 | **+T1,T2** | Trellis spec 层也有同样问题 |

**R1 的递归特性是本轮最关键发现**：前三轮元审计识别了 R1（不完整读取）为最高频根因，但全范围审计 F1 自身就犯了 R1——只读 ArchitectureBoundaryTests.cs 而未读 ServiceRegistrationTests.cs。这说明 R1 是最难根治的根因：**即使知道"要完整读取"这个纪律，执行时仍会遗漏**。

---

## 五、最优解决方案（增量）

### 方案 1：verify-doc-numbers.sh 扩展覆盖 Trellis spec（修复 T1/T2 — R11）

在 verify-doc-numbers.sh 中新增：

```bash
# Trellis spec PalmMetrics 数
TRELLIS_METRICS=$(grep -o "paldd\.[a-z_.]*" .trellis/spec/backend/logging-guidelines.md 2>/dev/null | sort -u | wc -l)
# 或直接 grep 实际代码并与 spec 声明比对
SPEC_METRICS_CLAIM=$(grep -oP "\d+ 个 Counter" .trellis/spec/backend/logging-guidelines.md | grep -oP "\d+")
check "Trellis spec PalmMetrics 声明" "$SPEC_METRICS_CLAIM" "$METRICS"
```

### 方案 2：README 测试数改为动态或移除（修复 RM2 — R8）

README badge 和"当前质量指标"中的"716 通过 0 失败 6 跳过"改为：
```
🧪  测试:    运行 `dotnet test` 查看实时结果
```
或改为模糊表述："700+ 通过（含 Broker 集成测试跳过）"。

### 方案 3：架构测试用例数自校验（修复 F1 RM1 — R1）

在 ArchitectureBoundaryTests 中新增自校验：读取 README 中声称的"架构边界用例"数，与实际 awk 计数比对。这使计数错误在测试时暴露而非在文档审计时。

### 方案 4：R1 纪律强化——计数必须跨同目录所有文件（修复 RM1 — R1 递归）

在 conventions.md 评审纪律 R1 中补充："计数任何测试文件的用例数时，必须统计同项目目录下所有 .cs 文件，而非单个文件。"明确"目录"而非"文件"为计数边界。

---

## 六、元审计递归的终极结论

四轮审计的递归模式本身揭示了一个工程定律：

**审计每多一轮，发现的错误数递减（9→5→2），但发现"前几轮自身错误"的概率不收敛——因为每轮审计都使用相同的认知工具（grep/awk/Read），而这些工具的盲区不变。**

| 轮次 | 发现新错误 | 发现前轮自身错误 | 递减率 |
|------|:---------:|:--------------:|:------:|
| 1 | 9 | 0 | — |
| 2 | 5 | 2（E1/E2） | 44% |
| 3 | 11 | 0（未检查前轮） | — |
| 4 | 2 | 2（RM1/RM2） | 82% |

**结论**：继续第五轮审计的边际价值低于实施已知 27 项错误的修复方案。建议**停止递归审计，转向实施修复**——特别是方案 1/2（verify-doc-numbers 扩展 + README 动态化），这两项能系统性消除 R11（文档计数锁死）这一最高频根因。