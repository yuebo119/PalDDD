# Pal.DDD 评审报告

> 报告编号：REVIEW-2026-08-22-v4（第四十二轮）
> 评审基准：commit `934c0c6` · 全仓档（8 片并行地毯）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD33）
> 本轮双重身份：全量地毯轮 + **第四十一轮修复轮（934c0c6，11 文件）的强制验证轮**。

---

## 执行摘要

**评审结论**：934c0c6 的**代码半面全部验证成立**（终验状态断言检出路径真实、Lease 样本语义正确、IsDBNull 三栈安全、五处勘正逐字对齐），但 **doc 半面发现一处事实错误**——MarkProcessed 前置条件声明把 Dapper 错归"拒绝"阵营（Dapper 实为 owner-null 放行，主线程 SQL 亲验铁证）。新发现 P2×1 / P3×~8 / P4×~25。

| 指标 | 本轮 | 上轮（41 轮） | 趋势 |
|------|:--:|:--:|:----:|
| P0 / P1 / P2 | 0 / 0 / 1 | 0 / 0 / 2 | ↓ |
| P3 / P4 | ~8 / ~25 | ~12 / ~15 | ↓ |
| 逃逸 / 复发 / 证伪 | 0 / 0 / 2（DATETIME(6) 精度❓双证伪 + PalORM 库 AsyncLocal 语境澄清） | 0 / 0 / 1 | 证伪治理生效 |
| 修复轮自带缺陷率 | 1（doc 半面——代码半面 0） | 1 | 模式见 §3.3 |

### 与上轮对比

| 上轮发现 | 上轮 | 本轮 | 状态 |
|----------|:---:|:---:|:----:|
| ITM-268 终验恒真 | P2 | — | ✅ 状态断言检出路径真实（片 8 引用语义+mutation 路径核；片 5 静态推演确证） |
| ITM-269 Lease 路径 + doc | P2 | P2×1 | 🟡 样本半面成立；**doc 半面 Dapper 分类错误** → ITM-272 |
| ITM-270 IsDBNull | P3 | — | ✅ 全链安全（片 2：default 判可抢占、不进 SQL、三栈对齐；传感器真红性确证） |
| ITM-271 五处勘正 | P3 | P3×1 | 🟡 五处逐字对齐通过；同提交第六处注释漏网 → ITM-273 |

---

## 第一部分：评审基础

### 1.1 覆盖度账本

```
src 198 文件（4 片 49/49/50/50 逐行 EOF）+ test/samples/bench 100+ 文件（4 片全覆盖）。
七流覆盖度: 全部 100%。三轴:
  机械轴: ✅ gate 22/22 · tech-debt 20/0/2allow · encoding 4/4 · verify-ai 21/21 · 棘轮 152≤173
  实测轴: ✅ dialect-probe 40/40（真库 PG+MySQL）
  静态轴: ❌ P2×1 新发现（doc 事实错误）
```

### 1.2 评审基线

```
Commit: 934c0c6 · 构建 0/0 · 测试 1044 = 999 通过 + 45 fail-closed + 0 代码失败（与 41 轮终基线逐项一致）
主线程亲验: 934c0c6 src diff 全量复核 + Dapper owner-null 放行 SQL 铁证 + DATETIME(6)/ffffff 对齐裁决
```

### 1.3 范围声明

- 必须检查：src 198 逐行；test/samples/bench 全量；934c0c6 11 文件 diff 敌对验证；机械防线。
- 明确不检查：`*.g.cs`、obj/bin、nupkgs、research/。
- 抽样策略：片外反证性阅读按软预算（各片自报 0-2%）；docs/ 针对性核对 ⚠。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:0]     [P1:0]     [P2:1]
中等     [P1:0]     [P2:0]     [P3:~6]
难修复   [P2:0]     [P3:~2]    [评估:0]
```

### 2.2 发现清单

#### 🔴 P0 / 🟠 P1 — 无

#### 🟡 P2 — 计划修复

| ID | 发现 | 证据 | 已对照模式 | 定稿门 |
|----|------|------|:--:|:--:|
| F1 | `IPalOutboxStore.MarkProcessed` doc（R41 ITM-269 新增）声称"InMemory/Dapper 栈拒绝"——**Dapper 实为放行**：SqlTemplates 的 `(@owner IS NULL AND locked_by IS NULL)` 分支 + `owner=message.LockedBy` 使未租约消息直达放行 | 主线程 SQL 亲验 + 片 4 三栈对照（PalORM=放行/InMemory=拒绝/Dapper=放行） | 无 PD 豁免（公共契约 doc 事实错误） | 三问✅ |

<details>
<summary><b>F1 · 三栈真实行为对照表（R41 doc 修正依据）</b></summary>

| 栈 | 未租约消息（LockedBy=null）MarkProcessed | 内存同步 |
|----|------|------|
| PalORM | **放行**（owner-null 分支 `locked_by IS NULL` 命中即 UPDATE） | affected>0 才全套回写 |
| Dapper | **放行**（SQL 模板同款 owner-null 分支） | 无条件清 LockedBy/LockedUntil（ITM-130），不回写 Status |
| InMemory | **拒绝**（IsCurrentLeaseHolder 引用+LockedBy 守卫静默 no-op） | 零变异 |

R41 doc 错在把 Dapper 与 InMemory 并列——Dapper 与 PalORM 才是同阵营（fencing 强度差异：Dapper 无 affected 门控）。触发路径："读者信 doc 对 Dapper 栈直标未租约消息以为被拒——实际 DB 行被静默改 Processed"。

</details>

#### ⚪ P3 / 评估

~8 项（详见行动项与 P3 账本四十二轮段）。代表性：第六处三方一致漏网（AotSample 注释描述 PalOrmSample 修复前形态——同提交内改码未改注释）、PalOrmInboxStore null 静默姊妹缺口（R41 勘正直接暴露）、SagaManager 临时实例掩盖子 saga Interrupt 前提、Kafka SubscribeAsync 缺 _disposed 守卫、PalOrmSagaMultiDialectTests:115 失实注释（SagaId 实已恢复）、InfraBenchmarks Append 无界流增长、EventLog 分配测量跨线程漂移（已知）。

### 2.3 观察项

| ID | 观察 | 说明 |
|----|------|------|
| O1 | DATETIME(6) 精度❓双证伪 | DDL 微秒列 + 参数恰好 6 位小数格式——写入前已量化，token 恒同串。转误判库候选："勿凭'列精度低于 ticks'推断 token 失配——先核对参数量化格式" |
| O2 | PalORM 库内 AsyncLocal 语境 | PalOrmConcurrencyTests:13 的"AsyncLocal"指 PalORM.Core 库内部 EC 优化——与 PalDDD 侧 CWT 化是两回事，非勘正遗漏 |
| O3 | doc-claims 系统性弱点 | 四轮验证轮的修复缺陷全部集中在 doc/注释半面（R40 勘正漏网/R41 恒真+错分类/R42 同提交漏网）——代码半面零缺陷。下沉建议：修复门两问第②问增补"此事实性声明还有谁在陈述"（doc 事实点枚举） |

---

## 第三部分：架构合规与收束判定

### 3.1 DDD/Clean Architecture 合规

六项原则全部 ✅（8 片架构流零发现）。

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | 六件套全绿（棘轮 152） |
| 静态轴 | ❌ | P2×1（doc 事实错误） |
| 实测轴 | ✅ | dialect-probe 40/40 |

**判定**：未收束 → 修复轮（ITM-272..274，全部为 doc/注释/守卫级）→ 验证轮。趋势：P1×5 → P0×1+P2×3 → P2×2 → **P2×1（且为 doc 句子级）**——严重度与代码距离双降。

### 3.3 验证轮结论（本轮核心产出）

**934c0c6 的 11 文件修复敌对核结果**：

| 上轮修复 | 验证证据 | 结果 |
|----------|----------|------|
| ITM-268 终验状态断言 | 片 8：断言对象=租约 successor、引用守卫 sealed class 不退化、7 Check 全可红；片 5：修复前真红推演确证 | ✅ |
| ITM-269 样本 Lease 路径 | 片 8：参数合理性/终态断言与 affected>0 语义一致/计数数学/重复运行幂等（删库重建） | ✅ |
| ITM-269 doc 前置条件 | 片 4 + 主线程：Dapper 分类错误 | 🟡 ITM-272 |
| ITM-270 IsDBNull | 片 2 全链（default=可抢占保守语义、不进 SQL、三栈对齐）+ 片 5 传感器真红确证 + 主线程 Rehydrate/谓词亲验 | ✅ |
| ITM-271 五处勘正 | 片 3 语义级逐字对齐（EF Entry/Nullable.Value）；片 5 grep 零过时残留 | ✅（第六处漏网另立） |

**修复轮缺陷模式（四轮数据）**：代码半面 0 缺陷；doc/注释半面 4 轮各 1 例。验证轮对 doc 半面的检出依赖"行为对照表"手法（本轮 F1 即三栈对照表产物）——已沉淀为修复门增补建议（O3）。

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖 | src 198 + test/samples/bench 100+（8 片全勾销） |
| 证伪数 | 2（DATETIME 精度 ❓×2 → 主线程 DDL+参数格式裁决；PalORM AsyncLocal 语境澄清） |
| 下沉建议 | O3 修复门增补"doc 事实点枚举"；O1 误判库候选（token 精度勿凭 ticks 推断） |

### 4.2 自我局限声明

```
- InfraBenchmarks Append 漂移幅度 [推断]（未运行量化）
- mutation 自证为 commit message 声明的样本级证据（片 5 标注），本轮未重跑
- docs/ 正文未逐字全读 ⚠
```

### 4.3 元审计自检

```
□ Z0 ✅ · □ S1 ✅（P2 含 SQL 铁证+三栈对照表）· □ S2 ✅ · □ S3 ✅ · □ S4 ✅ · □ 可信度 ✅ · □ 一致性 ✅ · □ 对比 ✅
违反项: 无
```
