# Pal.DDD 评审报告（第五十四轮 · 第八流首战轮）

> 报告编号：REVIEW-2026-09-19-FULL-V3
> 评审基准：commit `ccb605c` · 四片并行（变更面修复质量深审 / SourceGen+Analyzers 补深 / samples+bench+Hosting 补深 / **第八流跨栈行为对照首战**）+ 机械轴
> 方法：全部子代理强制 Read 全文；高危发现主线程逐项亲证（P1/P2-1/F1/F2 均复核确认）

---

## 执行摘要

| 指标 | 本轮 |
|------|:--:|
| P0 / P1 | 0 / **1**（已修） |
| P2 | 6（本轮修 2，余 4 登记） |
| P3 | 23（本轮修 2，余登记） |
| 修复引入缺陷率 | **2/6**（片1 实证——较历史 6% 回升，P1 与 P2-1 均为本会话修复自身引入） |
| 第八流首战 | **验证成功**：贡献 2×P2 + 4×P3 + 结构性规律，值得保留并提效 |

**本轮两大主题**：① 修复自身缺陷被自己的评审体系抓住（P1 = M3-9 的 skip-nuget 逃生门未接线；P2-1 = ITM-796 收口引入 SQLite 码 19 误判面）——修复质量深审的价值实证；② 第八流（跨栈行为对照）首战即中——F1（EF Inbox 抢占无 DB 守卫）是 53 轮以来首次暴露的语义级分叉，且全部行为类发现落在"EF 守卫在 C# 层 vs 姊妹在 SQL 层"这条结构性温床上。

## 发现与修复总表（带进度）

### 已修复（本轮清偿，提交见 git log）

| ID | 来源 | 严重性 | 发现 | 状态 |
|---|---|---|---|---|
| R54-P1-1 | 片1 | **P1** | release.yml skip-nuget 逃生门未接线到 push step——「勾选 skip-nuget + 有 key」时 35 包照推（不可逆公开副作用）；input 唯一有实际作用的场景完全失效 | ✅ push 条件补 `&& inputs.skip-nuget != true` |
| R54-P2-1 | 片1 | P2 | DapperSqlErrorClassifier 收口引入 SQLite 码 19 误判面：基码对全部 SQLITE_CONSTRAINT 家族恒 19（四码探针实证：NOT NULL=19/1299、UNIQUE=19/2067、CHECK=19/275、PK=19/1555）——NOT NULL/CHECK 违规被误判为唯一冲突，EventLog 误转并发异常掩盖真实数据错误（ITM-188/192 明令防的失败模式） | ✅ SQLite 分支去码判定，仅保留类型限定 message（与原副本等价） |
| F2 | 片4 | P3 | EF SagaStateStore 漏 ITM-163 null state 守卫（NRE vs 姊妹 ArgumentNull）+ PalORM :183 三方对齐注释失实 | ✅ 补 ThrowIfNull + 注释勘正 |
| 片1-P3-1 | 片1 | P3 | EndpointExtensions 收口注释「MapCommand/MapQuery 两处」失实（实为 MapCommand 两重载） | ✅ 勘正 |

### 登记待修（按优先级）

| ID | 来源 | 严重性 | 发现 | 建议窗口 |
|---|---|---|---|---|
| **F1** | 片4 | **P2（高优）** | EF Inbox 抢占路径无 DB 端 status 守卫：MarkProcessedAsync 不改 ProcessingStartedAt（:171-173）→ 抢占令牌在终态化后不失效 → 已 Processed 行被翻回 Processing + attempts+1 → handler 重复执行（破坏 InboxStore.cs:16「只处理一次」承诺）。姊妹栈 SQL WHERE status 守卫在同窗口拒绝。:164 注释自称"对齐 Dapper status 守卫"实为内存前置——**结构性温床首证** | M（ExecuteUpdate + status 守卫 + 并发测试） |
| F1b | 片4 | P3 | MarkProcessed/MarkFailed 内存态守卫（vs 姊妹 DB 态）——过期引用可把 Failed 行翻 Processed | 与 F1 同轮 |
| R54-P2-2 | 片1 | P2 | M1-1 fail-fast 的告警噪音：配置缺失永久错误 × 每 tick × 每条活跃 Saga 一条 Error，无熔断 | 声明或节流（决策补充） |
| 片3-P2-1 | 片3 | P2 | bench `--persist-medium` 双 job：类 [InProcess] attribute 与 ManualConfig 的 MediumRun 叠加（BDN 0.15.8 最小复现实锤）——medium 报告每基准两行、耗时双倍 | 移除类 attribute 或显式双分支 config |
| 片3-P2-2 | 片3 | P2 | DapperAotProbe 不在 slnx/CI/README（282 行实验探针裸奔主分支），三轮审计点名未修 | 纳入编译或移除 |
| F3 | 片4 | P3 | EF Saga SaveChangesAsync 对未跟踪新实体返回 1（谎报写入生效，违反接口契约 :44-46） | 行为决策 |
| F4 | 片4 | P2（文档） | Outbox 死信语义 usage.md 零覆盖（545 行零命中）——用户接入后消息失败 10 次静默停止投递无运维指引 | 文档批 |
| 其余 P3 | 各片 | P3 | 片1×6（注释失实×3/上界无实测/1022 备案/V12 边界）+ 片2×4（关键字标识符/Polyfills 注释/HasVersionSuffix 双副本/PDDD009 消息）+ 片3×5（Confirm 守卫/币种硬编码/探针转义/AwaitAndForget 命名/null→200）+ 片4×2（param-guard 弱一档/V12 空集放行） | 触碰时清偿 |

### 白名单新增（第八流产物，防重复报告）

1. v70 半租约态分叉（Outbox Mark* owner 非空/until null：EF 放行 vs 姊妹拒绝）——已声明
2. EventLog 元素 null 检查 2:1（EF 有守卫 vs Dapper/PalORM NRE）——PalORM 已声明，Dapper 待补声明（F7）

## 第八流首战有效性评估（片4 产出）

- **值得保留**：单轮 2×P2 + 4×P3，F1 为 53 轮首次暴露的语义级分叉
- **结构性规律**（写进视角定义）：三栈分叉温床 = "EF 守卫在 C# 内存层/并发令牌，Dapper/PalORM 在 SQL WHERE"——F1/F1b/F2/F3/F7 全部落此缝。提效法：逐方法列守卫集两栏比对（而非逐输入试探）
- 待维护者裁决：矩阵是否扩为四栈（InMemory）

## 正面确认

- 修复面等价性：M1-1/ITM-799/ITM-797/FanOut 契约/M1-5/M3-11/M0-1/V12 与原行为等价或纯收紧（片1 逐项核对）
- v2 自认浅区（SourceGen/Analyzers）经精读 0 P1/P2——已被 20+ 轮历史加固；v2 开放疑问证伪（CodeFixes 4/4 全有端到端测试）
- ExceptionMiddleware/N3 桥接/DI 注册面/基准口径四大复核点全部通过（片3）
- 机械轴 15/16 PASS（PalORM 为 D1 环境性在案）
