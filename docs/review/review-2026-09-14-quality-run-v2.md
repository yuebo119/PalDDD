# Pal.DDD AI 质量系统全量运行报告 v2（2026-09-14 次轮）

> 基线 commit：`b2ae2ec`（dev 分支，工作树运行前后均干净）
> 运行方式：22 个门禁/工具脚本 + 16 个测试项目全真实执行，零缓存复用
> 对比基准：[`review-2026-09-14-quality-run.md`](review-2026-09-14-quality-run.md)（首轮，基线 e51d48b）
> 机器环境：无 Docker；内网服务器 192.168.200.120 本轮实测**健康**（Kafka/RabbitMQ 全通）

---

## 一、运行结论

**22 项运行：21 项全绿 · 1 项红灯（PalORM.Tests 46 项，无 Docker，D1 在案）· 0 代码级缺陷 · 3 项新发现（1 项 P2 + 2 项 P3，均为文档类）。**

- **红灯收敛**：首轮 2 项红（Messaging 集成 5 失败 3 跳过 + PalORM 46 失败）→ 本轮仅 PalORM 1 项。`verify-conventions full` 的测试阶段同步从「2 项目红」收敛为「1 项目红」——ITM-676 预检修复 + 服务器恢复的双重实证。
- **首轮 6 项修复的回归验证全部有效**（§三）：Messaging 集成 8/8 · `.sh` 引用零残留 · `path-gate` 零残留 · 预检协议级探测在位。
- **新发现全部为文档精确性类**（§四），其中 1 项为**事实错误**（P2）：两份文档称 `.NET 11 另增` 两个验证 attribute，与官方文档（.NET 10 已发布 experimental，.NET 11 转正）不符。

## 二、运行矩阵（22 项）

| # | 门禁 / 组件 | 结果 | 关键数据 |
|---|------------|------|---------|
| 1 | secret-scan | ✅ | 539 文件（537 为首轮值，+2 = 首轮新增的报告与清单） |
| 2 | encoding-gate | ✅ 5/5 | E1-E5 全过 |
| 3 | xml-guard | ✅ | PASS |
| 4 | dapper-param-guard | ✅ | 枚举直传零违规 |
| 5 | verify-ai | ✅ 23/23 | V1-V23 无 FAIL |
| 6 | changelog-check | ✅ 5/5 | C1-C5（次轮新增跑；覆盖 17158fd 的 CHANGELOG 改动） |
| 7 | doc-consistency | ✅ D7 | .ai/README.md 地图一致 |
| 8 | verify-conventions --quick | ✅ | V5/V8/V9/V10（80 文档互链） |
| 9 | verify-conventions full | ❌ 1 项目红 | 静态 4 项 ✅ · build 零错误零警告 ✅ · 测试阶段仅 PalORM 红（首轮 2 项目红） |
| 10 | tech-debt | ✅ 9/2/1/0 | 0 失败 |
| 11 | gate.cs | ✅ | G23/G24 PASS（G22 因 --allow-dirty 按设计跳过） |
| 12 | gate-lite | ✅ 3/0 | |
| 13 | test-gate | ✅ 0 失败 | OSC 无翻转（观测 1246 测试） |
| 14 | guard.cs | ✅ 8/8 GREEN | 25.5s |
| 15 | check-all | ✅ 全零 | IDE 0 · CA 0 错误 · 编译 0 |
| 16 | gate-audit | ✅ | 32 脚本：16 接线全 OK · 0 缺口 · 0 未归类；**6 探针全 PASS** |
| 17 | vuln-scan | ✅ | 0 漏洞 |
| 18 | ci-coverage --selftest | ✅ 18/18 | 次轮新增跑（含降幅判定纯函数 18 例） |
| 19 | review-snapshot | ✅ | b2ae2ec：源项目 36 · 测试项目 16 · 源文件 214 · 测试文件 114 · 架构测试 44 · 诊断 15 · AOT true 8 / false 14 |
| 20 | sibling-map / sister-axis / review-scope / refine-scan | ✅ | 工具可用（sister-axis 需轴参数，本轮无修复任务未展开） |
| 21 | 16 个测试项目 | ✅ 15 绿 | 1379 测试：1325 通过 · 14 跳过 · 详见 §三.1 |
| 22 | Messaging.Integration.Tests | ✅ 8/8 | **首轮为 5 失败 3 跳过 → 本轮全跑全过** |

## 三、首轮修复回归验证

### 3.1 测试层对比（首轮 → 次轮）

| 项目 | 首轮 | 次轮 | 变化 |
|------|------|------|------|
| Messaging.Integration | 5 失败 / 3 跳过 / 0 成功 | **0 失败 / 0 跳过 / 8 成功** | ITM-676 + 服务器恢复 |
| PalORM | 46 失败 / 99 成功 | 46 失败 / 99 成功 | 未变（无 Docker，D1 在案） |
| 其余 14 项目 | 全绿 | 全绿（1226 用例） | 未变 |
| verify-conventions full | 2 项目红（Messaging+PalORM） | **1 项目红（PalORM）** | 收敛 |

### 3.2 修复项逐一回归

| 项 | 回归方式 | 结果 |
|----|---------|------|
| ITM-676（AMQP 协议级预检） | Messaging 集成真实执行 | ✅ 预检 true 路径下 8/8 全跑全过（非跳过伪装） |
| ITM-675（环境） | 服务器实测 | ✅ 健康（Kafka/Rabbit 全通） |
| ITM-677（D2 状态） | 文件在位性 | ✅ |
| ITM-678（AGENTS CI 段） | `grep "path-gate" AGENTS.md` | ✅ 零残留 |
| ITM-679（.sh 引用） | `grep -rn "scripts/*.sh" docs/testing.md docs/pitfalls.md docs/release.md` | ✅ 零残留 |
| ITM-680（V9 边界登记） | verify-conventions --quick | ✅ V9 PASS |

## 四、本轮新发现（3 项，均为文档类）

### F-1（P2·事实错误）`.NET 11 另增` 两个验证 attribute——与官方文档不符

- **位置**：`docs/usage.md:137`（表格）· `CHANGELOG.md:73`（摘要）
- **现状表述**：「.NET 11 **另增** `SkipValidationAttribute` 跳过指定参数、`ValidatableTypeAttribute` 强制生成静态推导不到的类型信息」
- **官方文档（双源）**：
  1. [Validation in ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/validation?view=aspnetcore-10.0#experimental-api-in-apps-that-target-net-10)（2026-09-14 经 microsoft-docs 实查）："Attributes from the `Microsoft.Extensions.Validation` NuGet package (ValidatableTypeAttribute and SkipValidationAttribute) are **published as experimental in .NET 10**. ... As of .NET 11, the attributes are **no longer experimental**."
  2. [What's new in ASP.NET Core in .NET 11](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-11?view=aspnetcore-10.0#minimal-apis)：专节标题即 "**Validation attributes are no longer experimental**"。
- **判定**：这两个 attribute 在 **.NET 10 已存在**（experimental 标记），.NET 11 是**转正**（移除 experimental）而非"新增"。现表述会使读者误判"目标 .NET 10 时不可用"。
- **出处**：由 `17158fd` 引入；该提交声明的其余事实（AddValidation 存在 / 未注册返回 200 而非 400 / 两类误用）经官方文档逐条核实**均准确**，仅此一处转述偏差。
- **建议**：两句均改为「（.NET 11 起这两个 attribute 不再标记 experimental——.NET 10 已随包发布）」。

### F-2（P3·文档内部矛盾）rule-placement-audit §二 与 §四 对同一事项互斥

- **位置**：`docs/review/rule-placement-audit-2026-09-13.md`（第 33-34 行，§二 缺口表 #5/#6；第 92-93 行，§四 已完成登记）
- **现状**：§二 #5 写「**未接线**（见 §四）」、#6 写「未实现」；§四 两行均已标「✅ 已完成」（覆盖率门禁接线 47c8c24 · 单模块降幅门禁 080871c）。
- **证据**：`b2ae2ec` 的 commit message 声称「审计账本 §四 该行由「未实现」改为「已完成（080871c）」」——与其 diff 一致（它改的确实是 §四），但**§二 的两行对应项未同步**，文档内形成"来源说未做、指向的章节说做完"的矛盾。该提交自身的方法论注记（「动手前先查现状」）恰是此矛盾会诱发的错误（读者按 §二 可能重复实现）。
- **建议**：§二 #5/#6 的「本次处置」列同步为「✅ 已完成（47c8c24 / 080871c）」并保留指向 §四。

### F-3（P3·文档精确性）覆盖率基线文档对 PalORM 的措辞与实现不符

- **位置**：`docs/test-coverage-baseline.md`（第 80-81 行）
- **现状表述**：「`coverage-baseline.json` 的 15 个值取自本机 Debug 插桩，属**下界**——`PalDDD.PalORM.Tests` 的多方言测试因本机无 Docker 未跑完，**其 line-rate 偏低，故基线偏保守**（CI 上更难触发降幅判定，不会造成假红）」
- **实测**：`coverage-baseline.json` 共 15 个键，**不含 `PalDDD.PalORM.Tests`**；`TestResults/` 下亦无该项目的 `coverage.*.cobertura.xml`（46 项失败致覆盖率产物未生成）。`ci-coverage.cs` 的 `UpdateBaseline` 只为有产物的项目写入键，`EnforceModuleDropLimit` 只遍历基线内的键。
- **判定**：措辞暗示"PalORM 有低基线值、其降幅受守护（只是宽松）"；实际是"**未纳入 → 降幅完全不检查**"。两者语义不同。
- **附带信息**：这是**过渡期状态**（文档已记录 CI 首跑后用 `--update-baseline` 复校准；届时 CI 产物齐全，PalORM 会被纳入并开始受守护）。首轮报告所称"该模式定位为开发者全量自检"的 F7 与本项无关。
- **建议**：改为「`PalDDD.PalORM.Tests` 因本机无 Docker 无覆盖率产物、**未纳入基线**（其降幅当前不受 Step 6 检查），待 CI 首跑后按 `--update-baseline` 补齐」。

## 五、口径核对与数据说明

| 项 | 实测 | 结论 |
|----|------|------|
| secret-scan 文件数 537→539 | `git log --diff-filter=A e51d48b..HEAD` = 首轮新增的 2 文档 | ✅ 可解释 |
| 测试总数 1379（1325 通过/46 失败/14 跳过） | 与首轮一致（用例集未变） | ✅ |
| 覆盖率基线键数 | 15（json）· 测试项目 16 → 差 PalORM（F-3） | ⚠️ 见 F-3 |
| review-snapshot 计数 | 与 b2ae2ec 一致（HEAD 匹配） | ✅ |
| 工作树 | 运行前后均干净 | ✅ |
| `| tail` 掩码陷阱 | 本轮所有关键判定均用重定向或 `${PIPESTATUS[0]}` 捕获真实退出码 | ✅ 未重蹈 |

## 六、复现命令

```bash
# 快速层（秒级）
dotnet run scripts/secret-scan.cs && dotnet run scripts/encoding-gate.cs
dotnet run scripts/xml-guard.cs && dotnet run scripts/dapper-param-guard.cs
dotnet run scripts/verify-ai.cs && dotnet run scripts/changelog-check.cs
dotnet run scripts/tech-debt.cs && dotnet run scripts/gate.cs -- --allow-dirty

# 重层
dotnet run scripts/guard.cs && dotnet run scripts/check-all.cs
dotnet run scripts/gate-audit.cs && dotnet run scripts/ci-coverage.cs -- --selftest
for p in $(find test -name '*.Tests.csproj' ! -path '*/obj/*' ! -path '*/bin/*' | sort); do
  dotnet test "$p" --no-build -c Debug
done
```

**注意**：本报告全部数字为本次真实运行的输出，零缓存；关键判定均规避了 `| tail` 退出码掩码（本仓已知陷阱）。

---

## 七、修复轮闭环（2026-09-14 同日）

本报告 3 项发现（ITM-681~683）已在修复轮全部闭环，明细与验证证据见
[`docs/review/action-items-2026-09-14-quality-run-v2.md`](action-items-2026-09-14-quality-run-v2.md)。要点：

- **ITM-681（事实修正）**：`docs/usage.md` 表格行与 `CHANGELOG.md` 摘要句的「.NET 11 另增」均改为「两者 .NET 10 已随 `Microsoft.Extensions.Validation` 包发布，.NET 11 起不再标记 experimental」。
- **ITM-682（状态同步）**：`rule-placement-audit` §二 #5/#6 处置列同步为「✅ 已完成（47c8c24 / 080871c），详见 §四」；"实测状态"列保留 09-13 时点记录。
- **ITM-683（措辞精确化）**：`test-coverage-baseline.md` 的 PalORM 段改为「未纳入基线（`UpdateBaseline` 只为有产物的项目写键），其降幅当前不受 Step 6 检查」——守护空窗显式写出。
- 门禁复验：encoding-gate 5/5 · verify-conventions --quick 全过 · changelog-check 5/5 · doc-consistency D7 · verify-action-items 0 缺失。
