# Pal.DDD 评审报告

> 报告编号：REVIEW-2026-08-22-v3（第四十一轮）
> 评审基准：commit `0b1b60f` · 全仓档（src 4 片 + test/samples/bench 4 片 = 8 片并行地毯）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD33）
> 本轮双重身份：全量地毯轮 + **第四十轮修复轮（0b1b60f，20 文件）的强制验证轮**。

---

## 执行摘要

**评审结论**：0b1b60f 修复主体全部经敌对核通过（P0 消息修复四维成立、34 行 IsNotNull 删除零误删、Broker 锁结构闭环），但**验证轮抓出一例修复自身缺陷**——ITM-265 的终验传感器恒真（P2，验证轮价值的最直接体现）。新发现 P2×2 / P3×~12 / P4×~15；三轴中实测全绿、静态轴未收束（P2 新增）。

| 指标 | 本轮 | 上轮（40 轮） | 趋势 |
|------|:--:|:--:|:----:|
| P0 / P1 | 0 / 0 | 1 / 0 | ↓ |
| P2 / P3 | 2 / ~12 | 3 / ~45 | ↓ / ↓ |
| 逃逸 / 复发 | 0 / 0 | 0 / 0 | → |
| 证伪数 | 1（CS1998 疑点被直构建否定） | 1 | → |
| 修复轮自带缺陷率 | 1（终验恒真——验证轮当场抓出） | 0 | 验证轮检出力证明 |

### 与上轮对比

| 上轮发现 | 上轮 | 本轮 | 状态 |
|----------|:---:|:---:|:----:|
| F1 P0 连接串入异常消息 | P0 | — | ✅ 修复成立（片 1 四维敌对核：消息无敏感段/索引语义/释放路径/Suppress 合规） |
| F2 弱断言棘轮 186>173 | P2 | 152≤173 | ✅ 34 行删除零误删（片 5/6/7 逐点核验红路径保留；两类正确保留形态确认） |
| F3 Broker 轴对称 | P2 | — | ✅ 锁结构闭环（三 return 全在 try + finally 单点；30s 余量充分） |
| F4 AotSample no-op | P2 | P2×1 | 🟡 **主修复正确（pending[0]），但配套终验恒真**——ITM-268 返工 |
| ITM-266/267 三方一致 | P3 | P3×5 | 🟡 主体完成，勘正自身漏网 5 处（ITM-271） |

---

## 第一部分：评审基础

### 1.1 覆盖度账本

```
src 198 文件（4 片 49/49/50/50 逐行 EOF）+ test/samples/bench 100+ 文件（4 片全覆盖）。
七流覆盖度: 全部 100%（各片零发现流均附覆盖证据）。
三轴状态:
  机械轴: ✅ gate 22/22 · tech-debt 20/0/2allow · encoding 4/4 · verify-ai 21/21 · 棘轮 152≤173 · 文档 10/0
  实测轴: ✅ dialect-probe 40/40（真库 PG+MySQL）
  静态轴: ❌ P2×2 新发现
```

### 1.2 评审基线

```
Commit: 0b1b60f · 分支 dev · 双仓清洁 · 构建 Release 0/0
测试 1043 = 998 通过 + 45 fail-closed + 0 代码失败（与 40 轮终基线逐项一致）
boundary 38 用例 · PDDD 15 · catch(Exception) 71 / OCE 71
```

### 1.3 范围声明

- 必须检查：src 198 逐行；test/samples/bench 全量；0b1b60f 20 文件 diff 敌对验证；机械防线。
- 明确不检查：`*.g.cs`、obj/bin 产物、nupkgs、research/。
- 抽样策略：片外反证性阅读按软预算（各片自报 0-5%）；docs/ 针对性核对 ⚠。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:0]     [P1:0]     [P2:1]
中等     [P1:0]     [P2:1]     [P3:~8]
难修复   [P2:0]     [P3:~4]    [评估:0]
```

### 2.2 发现清单

#### 🔴 P0 / 🟠 P1 — 无

#### 🟡 P2 — 计划修复

| ID | 发现 | 证据 | 已对照模式 | 定稿门 |
|----|------|------|:--:|:--:|
| F1 | AotSample 终验恒真：MarkProcessed 被拒（no-op）时 successor 持未过期租约 → QueryPending 租约过滤照样排除 → afterMark==0 两态皆绿——终验对其声称要防的自欺零检出力 | 片 8 三文件闭环推演（InMemoryOutboxStore.cs:138/141-146/236 + 样本 59-63） | PD29 命中（验证器自欺：名字声称≠断言） | 三问✅ |
| F2 | PalOrmSample 演示路径违反接口契约：GetPending（doc 明示"观测用不获租约"）→ MarkProcessed 当管线——仅 PalORM 放行（owner-null 分支），复制到 InMemory 静默 no-op 无任何报错 | OutboxStore.cs:21-24 doc vs PalOrmSample:39-44 vs InMemory/PalORM 两实现行为分叉 | PD12 部分适用（样本非 API）但复制即坏 | 三问✅ |

<details>
<summary><b>F1 · 终验恒真（R40 ITM-265 配套传感器的 mutation 推演）</b></summary>

**推演链**：临时改回 bug 形态（`MarkProcessed(outboxMsg)` 原始引用）→ InMemory `IsCurrentLeaseHolder` 引用守卫拒绝 → successor 仍 Pending + LockedUntil=now+2min 未过期 → `GetPendingMessagesAsync` 的 `(LockedUntil == null || LockedUntil <= now)` 过滤排除该行 → `afterMark.Count == 0` 成立 → Check OK → **bug 复活时终验仍绿**。

**有效形态**：断言 `pending[0].Status == OutboxStatus.Processed`——成功路径 MarkProcessed 原地改写入参状态；拒绝路径 no-op 状态仍 Pending（两态可分）。修复后必须做一次 mutation 自证。

**定稿门三问**：①误判库——PD29 第三形态（名字声称防自欺的验证器自身自欺）教科书命中。②反证——"正常路径必命中 1 条租约"成立（RetryCount/时间字段全 null），恒真只发生在 bug 复活分支——推演覆盖三文件闭环。③触发路径——"59 行改回传 outboxMsg → 终验仍 all checks passed"。

</details>

#### ⚪ P3 / 评估

~12 项（详见行动项与 P3 账本四十一轮段）。代表性：PalOrmProjectionCheckpointStore lease_until 无 IsDBNull（三十八轮只修 Dapper 姊妹——PD17）、5 处三方一致勘正漏网（含 R40 勘正自身漏网的 :79 与 AsyncLocal/Base64 措辞残留）、Dapper MarkProcessed 的 Status 不同步跨栈分叉、PalOrmOutboxStore 截断族、AotSample 防御顺序倒置与注释失实、ServiceRegistrationTests singleton 名实不符、Pooled 分配比较 tiered-JIT 假红窗、boundary obj 过滤不对称、mojibake 关键注释恢复。

### 2.3 观察项

| ID | 观察 | 说明 |
|----|------|------|
| O1 | CWT TryGet→执行的清理竞态窗口 | 需跨线程共享 Scoped Session 才可触发——违反文档契约的环境误用；P4 备注在案 |
| O2 | ITM-166 编号四处语境复用 | 文档追溯性受损；编号治理核对项 |
| O3 | CS1998 疑点被证伪 | async 无 await 方法直构建 0 警告（片 5 ❓ → 主线程直构建裁决非问题）——转误判库候选（"勿凭'必报 CS1998'推断测试项目告警面，直构建裁决"） |

---

## 第三部分：架构合规与收束判定

### 3.1 DDD/Clean Architecture 合规

六项原则全部 ✅（片 1-4 架构流零发现；片 8 样本聚合封装 P4 观察项维持）。

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | 六件套全绿（棘轮 152） |
| 静态轴 | ❌ | P2×2 新发现 |
| 实测轴 | ✅ | dialect-probe 40/40 |

**判定**：未收束 → 修复轮（ITM-268..271）→ 验证轮。趋势：R39 P1×4 → R40 P0×1+P2×3 → R41 P2×2（其中 1 项为上轮修复的传感器返工）——严重度单调收敛。

### 3.3 验证轮结论（本轮核心产出）

**0b1b60f 的 20 文件修复敌对核结果**：

| 上轮修复 | 验证证据 | 结果 |
|----------|----------|------|
| ITM-262 P0 消息修复 | 片 1 四维（无敏感段/index 值域/释放路径/Suppress）+ 片 5 传感器四变红维度核 | ✅ 成立 |
| ITM-263 棘轮 34 行删除 | 片 5（21 处）/片 6（4 处）/片 7（9 处）逐点核验 + 两类正确保留形态（唯一断言/as 转换） | ✅ 零误删 |
| ITM-264 Broker 三处 | 片 7 锁结构闭环核（三 return 全 try 内 + finally 单点 + 30s 余量三数量级） | ✅ 成立 |
| ITM-265 AotSample | 片 8：主修复（pending[0]）方向与执行正确 | 🟡 终验恒真 → ITM-268 |
| ITM-266/267 勘正 | 片 5：3 处漏网（含勘正清单自身遗漏） | 🟡 ITM-271 |

**修复轮自带缺陷率**：1（终验恒真）——与历轮 31%→8%→0→1 的波动一致，验证轮的必要性再次实证（本轮若无验证轮，恒真传感器将永久驻留）。

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖 | src 198 + test/samples/bench 100+（8 片全勾销） |
| 探针数 | 方言 40 断言 + 直构建裁决 1（CS1998 证伪）+ mutation 推演 1（F1 三文件闭环） |
| 证伪数 | 1（CS1998——agent ❓ 被主线程直接构建否定） |
| 下沉建议 | F1 → 样本验证器三问增补"终验必须做 mutation 自证"到修复门两问第①问的样本场景 |

### 4.2 自我局限声明

```
- F1 恒真结论基于推演（片 8 未实跑 mutation——修复时 ITM-268 已要求实跑自证）
- Dapper 版 MarkProcessed 的未租约直标行为（F2 的第三方实现）未亲核——行动项修复时一并
- docs/ 正文未逐字全读 ⚠
```

### 4.3 元审计自检

```
□ Z0 ✅ · □ S1 ✅（P2 含推演链+触发路径）· □ S2 ✅ · □ S3 ✅ · □ S4 ✅ · □ 可信度 ✅ · □ 一致性 ✅ · □ 对比 ✅
违反项: 无
```
