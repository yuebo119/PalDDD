# 决策论证：IdempotencyProcessor 前置读与投影检查点窗口化的裁决（B 批㉒/⑦(b)，全部不做）

> 编号：DECISION-2026-10-17-idempotency-preread-projection-windowing-adjudication
> 基线：commit `554ab7d`（EPSL B 批十二项落地 + 哈希勘误之后） · 日期：2026-10-17
> 性质：B 批两项移交项裁决（用户拍板「都不做」；无代码变更，本文件即裁决记录）
> 结构约束：本模板三段（消费状态/证据锚点表/开放决策点）为 V11 门禁必填段，
> `dotnet run scripts/verify-conventions.cs -- --quick` 机械校验段存在与锚点格式。

---

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-10-17 |
| 评审状态 | **已裁决**（2026-10-17）：两项经充分论证（语句数盈亏平衡 + 四栈形态分歧 + 契约表达力 + B5 互斥 + B1 先例同口径），用户拍板「都不做」；无代码变更，本文件即裁决记录 |
| 评审记录 | ㉒ 论证关键结论：①前置读在重复路径上是 1 条语句且 payload 已在手，删除后重复路径变为 INSERT 撞唯一索引（Dapper/PalORM 一次完整异常）加分类轻量读加二次 GetAsync 共 3 条语句加 1 次异常；按语句数盈亏平衡点在重复率三分之一，计入异常成本后本地库降至五分之一，而幂等组件的安装者恰是重复率高的人群。②四栈 TryStart 形态分歧（Dapper/PalORM insert-first、EFCore SELECT-first、InMemory 字典查找），前置读是唯一对四栈同形的快路径。③契约变更（TryStart 冲突返回既有记录）与本轮 B5 直接互斥：B5 刚把冲突读收窄为四列，契约变更要把 payload 放回冲突读，同一条路径两种设计只能选一个。④`IdempotencyRecord` 无属主信息、TryStartAsync 不接收 owner 身份，返回记录也分不清「我拿到租约」与「别人持着」，干净契约需加结果枚举或 out 参数，属四栈破坏性公共 API 变更。⑤opt-in 开关是无数据支撑的推测性 API 面，违反约定优于配置。⑦(b) 论证关键结论：①窗口化把崩溃重放面从 1 个位置扩到 N 个，契约文本（IProjectionHandler remarks）写的是「同一位置」，文本与事实的差即决策要消掉的东西。②收益真实但场景窄：远程 PG 百万位置 checkpoint 语句约 21 分钟降至约 1.3 秒，本地 SQLite 噪声级，且重建非热路径。③B1 先例（2026-10-16，同类重复窗口放大最高 99× 一票否决）口径是按最坏情况取默认值，同日自我推翻需更强理由。④风险比 B1 轻（契约已要求逐位置幂等，窗口化不创造新 bug 类，只放大既有违约的可见性），但方向是「风险可接受」而非「收益现在需要」，翻不动无消费方加特性级成本两条 |
| 裁决记录 | ㉒ IdempotencyProcessor 前置读 = 不做（不删除、不改契约、不加 opt-in 开关）；⑦(b) 投影检查点窗口化 = 现在不做（默认窗口 1 不变，不建批式能力接口）。两项均登记复活/立项触发条件于开放决策点表 |

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| 前置读现状：processor 入口先 GetAsync 全行读，命中且不可新建直接返回缓存结果（1 条语句） | `src/PalDDD.Idempotency/IdempotencyProcessor.cs:61` | ☒ |
| 删除前置读后的流程形态：TryStart 返回 null 后二次 GetAsync 取缓存结果 | `src/PalDDD.Idempotency/IdempotencyProcessor.cs:65` | ☒ |
| Dapper 栈 TryStart 为 insert-first：INSERT 撞唯一索引由驱动抛异常、catch 分类 | `src/PalDDD.Dapper/DapperIdempotencyStore.cs:122` | ☒ |
| EFCore 栈 TryStart 为 SELECT-first：编译委托查询后分支，无效分支 Detach 返回 null | `src/PalDDD.Idempotency.EFCore/IdempotencyDbContext.cs:99` | ☒ |
| InMemory 栈 TryStart 为字典查找：过期移除、Failed/过期 Processing 走后继实例复用 | `src/PalDDD.Idempotency/InMemoryIdempotencyStore.cs:55` | ☒ |
| 接口未钉住 null 语义：IIdempotencyStore.TryStartAsync 无 remarks，二义仅靠各实现注释约定 | `src/PalDDD.Idempotency/IIdempotencyStore.cs:18` | ☒ |
| B5 与本决策的互斥点：冲突读已收窄为四列（status/locked_until/expires_at/revision），前提是 payload 由调用方二次读 | `src/PalDDD.Dapper/DapperIdempotencyStore.cs:221` | ☒ |
| 投影逐位置租约现状：每事件 TryStart 加 MarkCompleted，B7(a) 后新位 2 语句 | `src/PalDDD.Projections/ProjectionProcessor.cs:73` | ☒ |
| 投影投递契约原文：at-least-once，重放单位为「同一位置」，handler 必须幂等（upsert 或按位置去重） | `src/PalDDD.Projections/IProjectionHandler.cs:17` | ☒ |
| 收益量级锚：远程库单语句往返 PG 630μs / MySQL 1730μs（本地基线锚），百万位置 2 语句每位置约 21 分钟纯 DB 时间 | `docs/performance.md:624` | ☒ |
| B1 同构先例：重复窗口放大最高 99× 一票否决，口径按最坏情况取默认值 | `docs/review/decision-2026-10-16-outbox-eventlog-defaults-adjudication.md:17` | ☒ |
| 本裁决的出处审计：B 批处置表（㉒/⑦(b) 两项移交项的论证与结论） | `docs/review/epsl-carpet-2026-10-16.md:122` | ☒ |

## 开放决策点

| # | 决策点 | 状态 | 复活/立项触发条件 |
|---|--------|------|------|------|
| 1 | ㉒ IdempotencyProcessor 前置读（删除 / 契约变更 / opt-in 开关三形态） | **已裁决：不做**（2026-10-17） | 某消费方实测自己的重复率稳定低于两成，且该前置 SELECT 在其部署环境构成可测占比（建议门限 5%）。届时形状为 processor 的 opt-in 跳过开关（默认 false，不动四栈、不动 TryStart 契约、不动 B5） |
| 2 | ⑦(b) 投影检查点窗口化（批式能力接口） | **已裁决：现在不做**（2026-10-17） | 某消费方实测远程重放中 checkpoint 语句占可测占比超过三成，且重建窗口不可接受。届时按 ADR-027 与 B-1 能力接口先例立项：批式检查点能力接口、默认窗口 1、IProjectionHandler remarks 改写清楚「重放单位为批」、配崩溃重放锁定测试 |

---

## 正文：论证摘要

### ㉒ 为什么三项形态全不做

删除前置读在主场景是净亏。命中路径现状 1 条语句且 payload 已在手；删除后重复路径为 INSERT 尝试（Dapper/PalORM 上一次完整异常）加冲突分类轻量读加二次 GetAsync，共 3 条语句加 1 次异常。按语句数盈亏平衡点在重复率三分之一处；把异常成本计入（本地 SQLite 异常约 20μs 对语句约 10μs），平衡点降至五分之一；远程库上语句 630μs 压过异常，平衡点回落至三分之一附近。幂等组件的安装者恰是重复率高的那些（重试风暴、at-least-once 投递、防重复提交），默认值按契约使用场景取优化点，不按新键路径取。

契约变更（TryStart 冲突时返回既有记录）看似两全，实为四栈公共行为变更，且与 B5 互斥：B5 刚把冲突读收窄为四列，前提正是 payload 由调用方二次读；契约变更要把 payload 放回冲突读，B5 在该路径的收益当场作废。同一条路径两种设计只能选一个。且 `IdempotencyRecord` 无属主信息、TryStartAsync 不接收 owner 身份，返回记录也分不清「我拿到了租约」与「别人持着」，干净契约要加结果枚举或 out 参数，属破坏性公共 API 变更；EFCore 栈另有跟踪实体泄漏的修复史（v26/v38 两轮 Detach），把内部跟踪实体直接返回给调用方是重开那扇门。

opt-in 开关是无数据支撑的推测性 API 面：库内无热路径消费方，库外无人报告该 SELECT 构成问题。按约定优于配置，给核心处理器加一个要永远维护、测试、解释的第二种行为模式，代价先行、收益虚构。前置读亦非正确性依赖（删掉后 TryStart 返回 null 再 GetAsync，流程照样正确），纯性能结构，无实测数据不动。

### ⑦(b) 为什么现在不建窗口化

窗口化把租约粒度从 1 个位置扩到 N 个，MarkCompleted 每批一次；崩溃点在中间时整批重放，已投影未标记的前缀和从未尝试的后缀一起重来。契约理论上覆盖（逐位置幂等则逐批幂等），但契约文本写的是「同一位置」，实际保证变成「同一批 N 个位置」，这个文本与事实的差需要一次显式裁决，而本轮裁决的结论是现在不翻。

收益真实但场景窄：远程 PG 百万位置，checkpoint 语句约 200 万条、按 630μs 锚约 21 分钟纯 DB 时间，窗口 1000 后约 2000 条、1.3 秒；本地 SQLite 噪声级。重建不是热路径，现行设计已能扛一次性代价。真撞到的消费方再来时，需求形态可能不止一个窗口参数（批量检查点重写），现在照着猜着建，形状大概率不对。

风险侧公平地说比 B1 轻：契约已要求 handler 逐位置幂等，窗口化不创造新的 bug 类，只让「几乎幂等」的 handler 把 1 条重复副作用变成 N 条，这类 handler 今天已经坏了，只是坏得少一点。但这个反驳的方向是「风险可接受」，不是「收益现在就需要」，翻不动「无消费方 + 特性级成本」两条。B1 先例（同类重复窗口放大最高 99× 一票否决，口径按最坏情况取默认值）在前，同口径裁决。
