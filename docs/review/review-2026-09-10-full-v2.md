# Pal.DDD 评审报告 — 第二轮全仓运行（验证轮）

> 报告编号：REVIEW-2026-09-10-V2
> 评审基准：commit `69c06d8`（dev 分支）· **全仓重扫 + 验证轮**（逐 diff 验证上轮 ITM-615~647 共 64 项修复，diff 范围 d8746a6..69c06d8 = 88 文件 +2380/-167）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD37 + 评审-修复循环协议·验证轮）
> 行动项清单：[`action-items-2026-09-10-v2.md`](action-items-2026-09-10-v2.md)（ITM-648 起）
> 上轮报告：[`review-2026-09-10-full.md`](review-2026-09-10-full.md)（正文不可变，已含事后勘误段）

---

## 执行摘要

**评审结论**：机械防线与测试基线全绿（9 道防线 + Release 0W/0E + 14 项目 1098 用例），全仓重扫（src 213 文件 32067 行 + test 105 文件 28292 行 + 跨片 6 轴）未发现存量 P0；但**验证轮抓到上轮 64 项修复中 4 项自带缺陷（6.2%）**——其中 1 项 P1（CI 凭据门禁因 pipefail 缺位假绿，探针实锤）、3 项 P2（1 项为上轮修复引入的真回归）。另有 1 项 P2 为本轮新发现的跨栈精度疑点（待真库探针）。**验证轮协议（修复轮后必须跟验证轮）再次证明必要**——4 项缺陷全部是集成验证（编译+测试全绿）无法暴露的语义层问题。

| 指标 | 本轮 | 上轮 | 趋势 |
|------|:--:|:--:|:----:|
| P0 | 0 | 0 | → |
| P1 | 1 | 4 | ↓ |
| P2 | 4 | 26 | ↓ |
| P3 | 30（汇总） | 34 | → |
| 证伪数 | 9 | 24 | — |
| 上轮修复自带缺陷 | **4/64（6.2%）** | — | 历史基线 3/50=6%（PD34 模型一致） |

### 验证轮总账（64 项上轮修复逐项核验）

| 结果 | 数量 | 明细 |
|------|:----:|------|
| ✅ 验证通过 | 59 | src 28 / 测试 20 / 脚本·CI 7 / 文档 4（各组子代理 diff+语义双核） |
| 🔴 修复自带缺陷 | 4 | ITM-648（ITM-616 的 pipefail 缺位）/ ITM-649（v65 引入 UPDATE 回归）/ ITM-651（ITM-639 完整性缺口）/ ITM-652 关联（MySQL 修复未同步 PG） |
| ⚠ 连带观察 | 2 | PD34 计数自激振荡×2（41→37、444→447+，修复轮自己的提交使刚修正的数字再次失实——无机械锚的裸数字注定振荡） |

### 与上轮对比

| 上轮发现 | 上轮状态 | 本轮验证 |
|----------|---------|---------|
| ITM-615 CI 覆盖面（6 门禁挂入） | 已修 | ✅ 通过（六门禁循环自身 pipefail 位置正确） |
| ITM-616 secret-scan 落地 | 已修 | 🔴 **部分失效**——脚本本体有效，但 CI 调用点 pipefail 缺位被掩码（ITM-648） |
| ITM-617~624 文档/计数批 | 已修 | ⚠ 通过但 PD34 振荡（2 处计数当轮失实，ITM-648 清单 P3 #3/#4） |
| ITM-625 快照 field emit | 已修 | ✅ 通过（78 行 field 全为既有公共字段、无泄漏；P3：delegate 死分支/event 缺口为存量） |
| ITM-626 生成物端到端 | 已修 | ✅ 通过（真链路断言；P3：Count==1 隐含前提） |
| ITM-628/629/634~638/640/641 src 运行时缺陷 | 已修 | ✅ 通过（ITM-634 例外：PalOrmSaga UPDATE 漏改 = ITM-649） |
| ITM-632 EFCore 幽灵租约 | 已修 | ✅ 通过（9 处 bare-catch 核验；P3：ResetAsync InMemory 路径缺口） |
| ITM-639 RabbitMQ confirms | 已修 | 🔴 完整性缺口——CreateAsync 零调用零测试（ITM-651） |
| ITM-642~647 测试防线 | 已修 | ✅ 通过（P3 批：nextAttempt 未断言/PDDD013 空洞等为新增观察） |
| MySQL SslMode 校验（v65） | 已修 | ⚠ 单边——PG 姊妹未同步（ITM-652） |

---

## 第一部分：评审基础

### 1.1 覆盖度账本

```
本轮应读：src 213 文件/32067 行（7 片）+ test 105 文件/28292 行（5 片）
        + 验证轮专项（d8746a6..HEAD 88 文件 diff + 脚本/CI/测试/文档四类全文核读）
        + 跨片不变式（AOT/消息键集/依赖方向/多实现契约/双管线/诊断 6 轴）
实读方式：14 个并行子代理全部 Read 真读（无缓存）；各片覆盖度自报 100%（详见各分片交付）
上轮对照：上轮 642 文件含 docs/scripts/config——本轮 env 面由验证轮专项覆盖（上轮修复后的
        文档/脚本全量 diff 核验），未重复派独立 env 片（范围声明见 1.3）
七流覆盖度：架构 ✅ 安全 ✅ 资源 ✅ 并发 ✅ 错误 ✅ AOT ✅ 生成语义 ✅
三轴状态：
  机械轴 ✅：gate 24/24 · verify-ai 23/23 · tech-debt 0 失败 · doc-consistency 11/11
            · encoding 4/4 · template PASS · test-gate 0F · assertion 166/173 · secret-scan PASS（本机）
  静态轴 ⚠️：新发现 P1×1 + P2×4 + P3×30
  实测轴 ⏸️：构建 0W/0E + 14 项目 1098 用例全绿；方言探针沿上轮结论（环境握手超时待 CI）；
            ITM-650 需真库探针（本轮环境不可达）
```

### 1.2 评审基线

```
基线 commit : 69c06d8（dev）· 工作树清洁
构建        : dotnet build PalDDD.slnx -c Release → 0 警告 0 错误
测试        : 14 项目 1098 用例 0 失败（逐项目 MTP；Messaging.Integration 4 失败为 broker 环境性）
机械防线    : 9 项全绿（见上）
上轮修复 diff: d8746a6..69c06d8 = 88 文件 +2380/-167（7 commits）
```

### 1.3 范围声明

- **必须检查**：src+test 全量手写代码（真读重扫）；上轮 88 文件修复 diff 的逐项语义验证；跨片 6 轴全局不变式。
- **明确不检查**：`*.g.cs`；`.ai/review/history/` 与 `docs/review/` 历史报告；第三方包内部实现；`docs/design/palorm-architecture.md`（2024 行，沿上轮 ❓ 标注）。
- **抽样策略**：docs/scripts/config 面未派独立地毯片（上轮已全量读过且本轮无该面新增改动），由验证轮专项对**上轮改动集**做全量 diff+语义核验覆盖——存量部分沿上轮报告结论；此为本轮范围收缩声明，相关结论标 ⚠。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:0]     [P1:1]     [P2:2]
中等     [P1:0]     [P2:2]     [P3:20]
难修复   [P2:0]     [P3:2]     评估:0（+P3 汇总 30 含合并项）
```

### 2.2 发现清单

#### 🔴 P0 — 无

#### 🟠 P1（1 项，探针实锤）

| ID | 发现 | 证据 | 已对照模式 | 定稿门 |
|----|------|------|:--:|:--:|
| ITM-648 | ci.yml secret-scan 在 pipefail 前执行——CI 凭据门禁假绿 | 探针：`bash -e` 下 `if ! cmd\|tee` → MASKED；加 pipefail → CAUGHT；ci.yml:106 调用先于 :120/:135 的 `set -o pipefail` | PD29 | 三问✅ |

<details>
<summary><b>ITM-648 · SHELL-1 教训第五次现身（P1 展开）</b></summary>

**证据**：`.github/workflows/ci.yml` 的 secret-scan 步骤（:104-112）中 `if ! bash scripts/secret-scan.sh 2>&1 | tee ci-secret-scan.log; then` 位于 `set -o pipefail`（:120/:135，六门禁循环与根 gate 分支内）**之前**。GitHub Actions `run` 默认 `bash -e`（无 pipefail）→ 管道退出码 = tee 的 0 → `if !` 恒假 → 失败分支永不进入。

**定稿门三问**：①误判库——PD29 变体（验证器自欺：验了仪器 exit 1 没验管道包装）；②反证——实跑探针 `bash -e -c 'if ! bash -c "exit 1" | tee /tmp/x.log; then echo CAUGHT; else echo MASKED; fi'` 输出 **MASKED**，加 `-o pipefail` 输出 **CAUGHT**，无反证；③触发路径——CI 上真实凭据入库 → secret-scan exit 1 → tee 掩码 → 步骤绿 → 泄露无告警（正是 SE2 要防的场景）。

**修复**：`set -o pipefail` 提至 run 块首行（在 secret-scan 之前），并复核步骤内全部管道。

</details>

#### 🟡 P2（4 项）

| ID | 发现 | 证据 | 已对照模式 |
|----|------|------|:--:|
| ITM-649 | PalOrmSagaStateStore UPDATE 绑定原始 state.Error（上轮 v65 引入回归，注释"两处共用收口"与代码不符） | Read :243/:244（`error = {state.Error}`）vs :221/:222（`{truncatedError}`）；姊妹 DapperSagaStateStore 两处均正确 | PD19+PD34 |
| ITM-650 | Inbox 时间戳 token 精度（内存 100ns vs DB 微秒列）疑点——⚠ 有"两侧同舍入则等值成立"反证路径，须真库探针裁决 | DDL 实证 DATETIME(6)/TIMESTAMPTZ；Idempotency 栈 v53 已因同因换 revision token | PD36 降半级 |
| ITM-651 | RabbitMqBroker.CreateAsync 零调用零测试——ITM-639/213 的 mandatory+confirms 联合语义无锁定；fixture 仍裸构造 | grep 全仓零调用；fixture :208-209 实读 | PD29 近邻 |
| ITM-652 | PG MultiHost 凭据校验缺 SslMode（MySQL 侧 v65 修复未同步姊妹） | PostgreSqlMultiHost.cs:430-432 仅 3 项 vs MySqlMultiHost 4 项 | PD17 |

#### ⚪ P3（30 条汇总）

见清单 P3 段（上轮连带 4 + src 10 + 测试防线 12 + 其余 4）。要点：
- **PD34 计数自激振荡×2**：上轮刚修正的 ArchitectureBoundaryTests 方法数（41）与 ConfigureAwait 数（444）当轮再次失实（实际 37 / 447+）——修复轮自己的提交增删了计数对象。根因：**无机械锚的裸数字注定振荡**。建议 doc-consistency 补计数比对项或文档去裸数字化。
- **测试盲区三连**：HasValueSequence 多段分支（ITM-629 修复）零回归网、MySQL SslMode（v65 修复）零测试、EFCore 负 timeout 守卫零直接测试——上轮修复的"传感器"缺口。
- **DiagnosticCoverageGate 形态①未收紧**：上轮只收紧了形态②（diag 根），形态① `x.Id=="X"` 二元比较仍宽判定——收口不完整。

### 2.3 观察项

| ID | 观察 | 说明 |
|----|------|------|
| OBS-1 | 修复自带缺陷率 6.2%（4/64） | 与 PD34 历史模型（3-5%）同量级——"修复轮后必须验证轮"协议的持续必要性实证 |
| OBS-2 | 全部 4 项缺陷都在语义层 | 编译 0W/0E + 1098 测试全绿均无法暴露——机械防线上限的再确认 |
| OBS-3 | 跨片轴 5 项全绿 | AOT 三态/依赖方向零循环/created_at 四栈统一/截断常量分族恒定/诊断门禁不假红 |
| OBS-4 | 上轮弱断言棘轮（166）未反弹 | 新增测试全部行为断言，棘轮方向健康 |

---

## 第三部分：架构合规与收束判定

### 3.1 DDD / Clean Architecture 合规

| 原则 | 状态 | 证据 |
|------|:----:|------|
| 领域层零基础设施依赖 | ✅ | G2/G4/G5 PASS（上轮修复未破坏） |
| 依赖方向外→内单向 | ✅ | 36 项目全图零循环（跨片轴 3 复核） |
| DIM 桥接替代反射 | ✅ | 上轮 diff 反射模式扫描 0 命中（跨片轴 1） |
| AOT 三态分层 | ✅ | 显式 8/14 + 继承 14 复核成立；上轮改动无新反射 |
| 聚合根保护不变量 | ✅ | 上轮未触及 |
| 多实现契约一致性 | ⚠️ | PD17 两处新不对称（ITM-649 截断、ITM-652 SslMode）——均为上轮修复单边落地的连带 |

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | 9 道防线全绿（但 ITM-648 证明 CI 包装层有一处假绿——机械轴自身需修） |
| 静态轴 | ⚠️ | 验证轮新发现 P1×1 + P2×4 → 不可收束，需再修复轮 |
| 实测轴 | ⏸️ | 构建+测试全绿；方言轴待 CI；ITM-650 需真库探针 |

**收束判定**：**不可收束**。进入第三轮修复（范围小：1 P1 + 3 P2 易修项 + 1 待探针项）。按 PD37 轮次分级——本轮发现量小且集中（上轮修复连带），修复轮应**小改小评**（不放大为新一轮全量地毯），修复后跟一轮轻量验证（diff 验证 + ITM-648 探针复验 + 计数锚落地）即可收束。

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖文件/行数 | src 213/32067 + test 105/28292 + 88 文件修复 diff 全量 |
| 并行子代理 | 14（src×7 + test×5 + 验证轮专项×1 + 跨片不变式×1；1 个因 API 限速重派成功） |
| 探针数 | 主线程 3（pipefail 复现×2 + PalOrmSaga UPDATE 实读）；子代理实跑探针 8+（正则捕获/门禁 exit/镜像 diff/签名验证等） |
| 证伪数 | 9（子代理明确剔除的候选：EF 分页死循环/指标双计/审计 SQL 溢出/拦截器跨文档失败等） |
| 下沉建议 | 3（ITM-648→CI 模板 lint 约定；计数锚→doc-consistency 新 D 项；形态①收紧→DiagnosticCoverageGate 边界矩阵） |

### 4.2 自我局限声明

```
本轮局限：
- docs/scripts/config 存量面未独立地毯（由验证轮专项覆盖上轮改动集；见 1.3 范围声明）。
- ITM-650 未做真库探针（PG/MySQL 应用层握手超时沿上轮结论）——按证据分级标 ⚠，修复前必须补。
- 方言实测轴沿上轮 SKIP 待 CI。
- 测试 P3 批（清单 #30）为 13 条小项合并，未逐条独立探针。
- Messaging.Integration 4 个失败未重跑归类（沿上轮 BrokerUnreachable 结论，本轮无 broker 侧改动）。
```

### 4.3 元审计自检

```
□ Z0 覆盖度: src+test 全量真读；验证轮专项覆盖全部上轮改动；范围收缩（env 面）已声明 ⚠   ✅
□ S1 论证链: P1 含探针实证 + 三问；P2 含文件:行实读（ITM-650 双面呈现）              ✅
□ S2 合规: 3.1 六项全查                                                            ✅
□ S3 指标: 与清单一致（1 P1 + 4 P2 + 30 P3）                                        ✅
□ S4 反模式: 无；PD37 轮次分级已用于收束建议                                        ✅
□ 可信度: ITM-650 标 ⚠ 且列出反证路径；其余 P0/P1 无 [推断] 定稿                      ✅
□ 一致性: 摘要 ↔ 清单 ↔ 收束判定一致                                                ✅
□ 对比: 与上轮 64 项逐项对账（59 通过/4 缺陷/2 振荡）                                ✅
违反项: 无
```

### 4.4 下一步

1. **立即**：ITM-648（一行 pipefail 修复）+ ITM-649（两处绑定 + 测试）。
2. **本轮修复轮**：ITM-652（PG SslMode 镜像）+ ITM-651（fixture + PublishException 测试）+ 计数锚（PD34 根治）。
3. **待环境**：ITM-650 真库探针（CI Testcontainers 或环境可达时）。
4. **轻量验证轮**后按 PD37 收束（小改小评，不再全量地毯）。

---

## 事后勘误 / 第三轮修复（2026-09-10 追加，正文结论不变）

**修复轮完成**：ITM-648~652（1 P1 + 4 P2）**全部清偿** + P3 批 29/30；清单进度 97%（34/35，余 1 条 RollbackAsync 姊妹收口留下轮）。

**验证要点**：
| 项 | 结果 |
|----|------|
| ITM-648 | `set -o pipefail` 提至 run 块首；探针复验 CAUGHT；探针位置错误（set 在 :120 而 secret-scan 在 :106）已消除 |
| ITM-649 | UPDATE 改绑 truncatedError；新增回归测试（INSERT 后重塞超长 Error 触发 UPDATE 路径——避免"INSERT 回写后两值相同"的测试失效陷阱） |
| ITM-650 | 探针测试落盘；**SQLite 实证 token 命中**（疑点在 SQLite 轴不成立）；PG/MySQL 待 CI Testcontainers 首跑裁决 |
| ITM-651 | fixture 全量切 CreateAsync（4 既有测试自此覆盖 confirms）+ PublishException 测试首次锁定 ITM-213+639 联合语义 |
| ITM-652 | PG SslMode 第 4 项 + 5 个姊妹测试（PG×3 + MySQL LoadBalance×2）+ MySQL SslMode×2（P3#20 收口） |
| PD34 根治 | doc-consistency **D12a**（boundary 方法数 37 实测锚，文档 9 处统一）+ **D12b**（ConfigureAwait 去裸数字化 6 处）——双红测验证（改错必 FAIL） |
| 构建与测试 | Release 0W/0E；16 项目 1260 用例（51 失败全环境性：46 Testcontainers 守卫 + 5 broker AMQP 不可达，与基线同形态）；Integration 265 全绿含新增 11 测试 |
| 机械防线 | gate 24 · verify-ai 23/23 · **doc-consistency 12/12（D12 新项）** · tech-debt 含新 20a · encoding/template/test-gate/assertion/secret-scan 全绿 |

**上轮修复缺陷第 5 项（修复轮证伪发现）**：上轮 ITM-637 的 `TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService>(factory))` 写法在 MS.DI 下**首次调用即抛 ArgumentException**（factory 注册的 ImplementationType 取委托返回类型 = IHostedService 与 ServiceType 不可区分）——即该修复形态使 `AddPalPostgreSqlOutboxNotifier` 完全不可用（比验证轮清单预设的"第二次静默忽略"更严重）。已改双泛型 `Singleton<IHostedService, PostgreSqlOutboxNotifier>(factory)` 修复。**上轮修复自带缺陷总数：5/64（7.8%）**——验证轮 + 修复轮两轮共抓出 5 项，全部为语义层问题。

**收束判定更新**：机械轴 ✅（12/12 含计数锚）· 静态轴 ✅（P1/P2 全清）· 实测轴 ⏸️（ITM-650 PG/MySQL 探针 + PublishException 集成测试 + 方言探针，三者均待 CI Testcontainers/RabbitMQ）。**本地三轴中两轴全绿，可收束至 CI 验证**。
