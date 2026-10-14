# ADR 028：投影增量回放续读 — 检查点高水位 + 可续读回放源双能力接口

> 状态：**已采纳（2026-10-14 全量重读性能实施轮 R6，P0-3）**
> 日期：2026-10-14
> 关联：ADR-027（同轮能力接口先例 IBatchMarkOutboxStore）、B-1（ISagaLeaseBatchStore
> 能力接口先例）、`docs/performance.md`（R6 入账段）

## 背景

全量重读性能审计（2026-10-14）确认：`ProjectionRebuilder.ReplayAsync` 的增量回放
**恒从 version 0 重读全流**（`EventLogReplaySource.ReadAsync(sourceName)` 无 fromVersion
下传），且每个事件伴随一次 checkpoint `TryStartAsync` SELECT。百万事件流的「增量」回放 =
百万次 payload 物化 + 百万次 checkpoint 探查 + 百万次 Detach——每次回放成本
O(全流长度)，事件量增长后随全流长度线性恶化。

## 决策

**双能力接口 + Rebuilder 自动检测**（非破坏，镜像 ADR-027/B-1 先例）：

1. **`IResumableProjectionCheckpointStore`**（`IProjectionCheckpointStore` 能力扩展）：
   `GetResumePositionAsync(projectionName, sourceName, ct)` 返回三态——
   ① 存在未完成位置（Failed/僵尸 Processing）→ **最小数值位置**（首个洞，回放从该位置
   重试，与 ReplayAsync 既有契约「仅处理缺失或失败的检查点」逐字一致）；
   ② 全部已完成 → (最大位置 + 1)；③ 无行（首跑/Reset 后）→ null（全量）。
   四栈实现：InMemory（遍历）/ Dapper（单语句方言 CAST 聚合，MySQL `SIGNED`）/
   EF（服务端聚合优先，SQLite 不翻译 `Convert.ToInt64(string)` 实测——回退最小投影
   客户端聚合：仅 Position/Status 两列轻量行）/ PalORM（raw command 同 Dapper SQL）。
2. **`IResumableEventReplaySource<TMessage>`**（回放源能力扩展）：`ReadAsync(sourceName,
   fromPosition, ct)` 重载，`EventLogReplaySource` 实现（下传 `ReadStreamAsync` fromVersion），
   全量读取收敛为 fromVersion=0 的同一实现源。
3. **Rebuilder 接线**：store 与回放源**双双**具备能力时取续读位置下推；任一侧不支持、
   续读不可解析、或非数值 Position → 全量路径（`RebuildAsync` 先 Reset 使续读返回 null，
   天然走全量，零特判）。

## 消费方指引（本决策的对外契约）

- **Position 数值契约**：管线内 Position 恒为数值串（EventLogReplaySource 产
  StreamVersion.ToString()，流版本 0..N 连续），顺序回放保证检查点行按位置前缀式产生
  （缺失行先于既有行不可达）。非数值 Position / 方言 CAST 失败 → 实现返回 null 回退
  全量回放（续读是优化面，语义兜底恒安全）；
- 手工篡改检查点行造成洞位低于水位时超出契约——完整重建用 `RebuildAsync`（先 Reset）；
- 自定义 `IProjectionCheckpointStore` / `IEventReplaySource<TMessage>` 实现不标注能力
  接口时行为不变（全量路径），无迁移成本；
- 收益：增量回放从 O(全流长度) 降为 O(增量)——已完成前缀零 payload 重读、零逐事件
  checkpoint 探查（EF 回退档为两列轻量行客户端聚合，仍免 payload 物化）。

## 被否决方案

- **直接在 `IProjectionCheckpointStore`/`IEventReplaySource` 加成员**：对自定义实现方
  是源破坏（接口成员新增），能力接口零破坏（ADR-027/B-1 同款裁决先例）；
- **最大完成位置语义（跳过洞）**：被否决——洞（Failed/僵尸）低于水位时被跳过，
  违反「仅处理缺失或失败的检查点」既有契约；最小未完成位置语义与之逐字一致；
- **EF 只做服务端聚合**：被否决——SQLite provider 不翻译 `Convert.ToInt64(string)`
  （本地实测 TRANSLATION-PROBE 判红），回退全量回放会使 SQLite 栈（测试/小型部署主力）
  零收益；最小投影客户端聚合保语义与主要收益（无 payload、无逐事件探查）。

## 验证（本决策的实现依托）

- InMemory 三态语义五件 + Rebuilder 续读集成两件（`RequestedFromPosition`/`OfferedCount`
  观测桩；**变异红证明在案**：注释 Rebuilder 续读分支 → OfferedCount=9/RequestedFrom
  断言判红，恢复绿）+ Dapper/EF SQLite 真库三态两件（方言 CAST 聚合 SQL 正确性）；
- 公共 API 快照 +9 行纯增量（两能力接口 + 四 store 方法 + 回放源重载），零删除。
