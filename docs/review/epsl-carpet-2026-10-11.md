# EPSL 地毯审计轮（2026-10-11 · 四阶段 SOP 完整轮）

> 承接同日接入审计轮（`epsl-audit-2026-10-11.md`）：本轮为用户二次指令后启动的**阶段 2/3 完整轮**（门禁先行 → 地毯深读 → 强制收敛），触发登记 = 阶段边界（优化实施轮 4e1af12 收尾）+ 用户显式指令。
> 前置状态变化：a380b2a（14:01:44）已接入 perf-budgets.md 与 AGENTS.md 性能节——**上轮报告「perf-budgets 不存在」的发现在其提交后被部分推翻**（时序竞争：上轮验证跑在 a380b2a 之前 3 分钟，结论以其时点为真，引用上轮报告须带此前提）。工作树另有三处未提交路径迁移修改（EPSL 文件 ZCodeProject → `~/.zcode/`），**存在活跃外部写入者：本轮全程只读、零提交**。

## 一、看点（量化结论）

1. **阶段 1 全绿**：Release 全解决方案 0 错误（12.62s）；`--smoke` 4/4 分配预算内且与账本跨轮逐字节一致（88 / 40,000,040 / 128.001 B/op，第三次独立复现）。
2. **地毯深读 222/222 全覆盖**：五分片（内核 34 / 分发消息 17 / 处理器族 77 / 持久化 60 / 序列化压缩宿主 34）逐文件读完，覆盖率与 count-audit 推导 [16] 的 src 源文件数 222 完全一致，零跳读零谎报。
3. **原始候选 18 条，收敛后 A 批 3 / B 批 7 / C 批 8**（含分片自证伪项）；头条五条候选经主 agent 独立行号复核（四红线第 4 条执行），全部属实。
4. **唯一高严重度候选 1 条**：Saga 超时扫描的逐 saga 租约释放（每 tick 批大小次往返 + EF DetectChanges 二次方形态）——机制跨层（处理器循环 × EF 跟踪语义），analyzer 与现有基准双双不可见。
5. 候选总体形态：**零条撞既有否决/勿重开案**（分片携否决清单前置过滤 + 主 agent 复核）；多数属「已落地优化的不对称残余」（同文件/同族先例已做、漏网一格）。

## 二、四组表

### 表 1 · 阶段 1 闸口（本轮实测）

| 闸口 | 结果 |
| --- | --- |
| `dotnet build PalDDD.slnx -c Release` | exit 0 / 0 错误 / 12.62s |
| `--smoke`（分配预算断言） | exit 0 / 4/4 预算内 / 分配逐字节 == 账本 |
| 基准矩阵 | 引用不重跑：12 车道 strict（10-10 晚）+ 三次增量真跑（10-11）均在 24h 内，代码面此后无产品变更 |

### 表 2 · 候选总表（排序 = 严重度 × 影响面 × 可验证性 ÷ 成本；批定义见 SOP 阶段 3）

| # | 位置 | 机制（一句话） | 严重度 | 批 |
| --- | --- | --- | --- | --- |
| 1 | `Transactions/Saga/SagaProcessor.cs:231-244` × `SagaStateDbContext.cs:186-351` | 超时扫描非超时分支逐 saga SaveChanges：批大小 B 次 DB 往返 + 被跟踪实体 O(B²) DetectChanges（ValueComparer 快照 CloneDictionary 使 ReferenceEquals 恒假） | 高 | B（SQL 计数探针先行；0 行告警逐 saga 语义需联动裁决） |
| 2 | `Messaging/MessageConsumeContext.cs:58` | FromHeaders 在空判短路**前**建字典——无头消息（本仓默认发布路径产物）每条消费白分配一个 Dictionary | 中 | **A**（空头快路径 ~5 行；分配断言测试旧码必红） |
| 3 | `Transactions/Inbox/InboxProcessor.cs:95,134` | 消费两条热路径无条件插值 Information——日志关闭也挡不住每消息 ~150-250B；接口 IsEnabled 注释明示此用途 | 中 | **A**（IsEnabled 守卫两处） |
| 4 | `Compression/SystemCompressor.cs:86` | Brotli 解压每次 `new ArrayBufferWriter`——C6 池（`NativeCompressors.cs:22-37`，LZ4/ZStd）跨程序集漏网一格，16KB 负载 ~32KB/op 临时分配可消 | 中 | **A**（照抄 DecompressBufferPool 并上移公开；峰值驻留/非重入约束头注背书现成） |
| 5 | `Core.SourceGen/IdentityGenerator.cs:684-691` | Guid 腿 JSON 读路径 GetString+Parse 每读 ~90B；Ulid 腿（同函数 701-732）三腿零分配形态未镜像 | 中 | B（契约敏感区：ITM-629 多段/v34 转义腿/v35 异常契约勘正链密集；格式集 N/D/B/P 需保） |
| 6 | `Dapper/DapperSagaStateStore.cs:169-172` × `PalORM/Stores/PalOrmSagaStateStore.cs:213` | Save 的存在性预查拉全行并**完整反序列化** TState，结果只做 null 判断即丢弃 | 中 | B（UPDATE-first 方向；并发 INSERT catch 两栈已有） |
| 7 | `Dapper/DapperEventLog.cs:183-250` × `PalORM/Stores/PalOrmEventLog.cs:109-170` | EventLog k>1 逐事件 INSERT 往返；PG 多行 VALUES..RETURNING 可收 2 往返（AddBatch5 已批化 Outbox，EventLog 未批，同库不对称） | 中 | B（三方言门控流程照 T2.2 先例；MySQL/SQLite 自增空洞不建议） |
| 8 | `Projections/ProjectionProcessor.cs:73-109` | 回放每事件 3 语句（SELECT+INSERT+UPDATE）；幂等契约已声明（IProjectionHandler remarks），窗口化检查点可批但重放窗口 1→N | 中 | B（重建工作负载收益；语义变化需裁决） |
| 9 | `Messaging.Kafka/KafkaBroker.cs:132` + `Messaging.RabbitMQ/RabbitMqBroker.cs:239` | 空追踪上下文下每发布仍分配空 Headers/Dictionary（~70B/48B）；客户端 null 语义等价 | 中 | B（null 契约依赖客户端行为，需集成验证） |
| 10 | `Messaging/DomainEventDispatcher.cs:76-77` | 每次 DispatchAsync 整份 Queue 拷贝 + 去重 HashSet；「无再入队」不变量已文档化则可免 | 低 | B（不变量先行验证） |
| 11 | `Messaging.Kafka/KafkaBroker.cs:407` | 有头消费双重字典物化（ToHeaderMap + FromHeaders） | 低 | C |
| 12 | `Transactions/Saga/Saga.cs:330-335` + `SagaTimeoutDetector.cs:66-69` | ExecutedStepKeys List.Contains 线性扫（O(S²) 累计）+ 每 tick 子串分配 | 低 | C（保序 List + 伴随 HashSet 可解，绝对量小） |
| 13 | `Transactions.EFCore/InboxDbContext.cs:57-77` | EF 栈首投双往返（SELECT 探测 + INSERT）；方言 upsert 可折叠 | 低 | C（「重复路径优先」取舍在案，需实测频率） |
| 14 | `PalORM/Stores/PalOrmInboxStore.cs:93-109` | PalORM×MySQL 新消息 INSERT 后 id 回查往返（Dapper 姊妹单往返） | 低 | C |
| 15 | `Dapper/DapperBulkCopy.cs:238` | MySQL 大批量路径每次重解析整条连接串读一个布尔 | 低 | C |
| 16 | `Compression/SystemCompressor.cs:131,184` | GZip/Deflate 压缩输出 MemoryStream 无容量预设（同文件解压侧 v53 已做，压缩侧漏） | 低 | C（超配风险依赖负载压缩比） |
| 17 | `Analyzers/StrategicDddAnalyzer.SymbolHelpers.cs:351-374` | 分析器全名比对无缓存 + 同符号事实单轮重复求值 4 次（构建期/IDE 体感面，无运行时 SLO） | 低 | C |
| 18 | `Messaging.Kafka` 头还原中间形态 | （与 #11 同族，B4 原文并入 #11 计） | 低 | — |

### 表 3 · 分片自证伪/退回清单（退回也是产出，防下轮重扫）

| 项 | 证伪依据 |
| --- | --- |
| LZ4/ZStd `with` 表达式疑似每次分配 | DLL 字符串堆无 `<Clone>$` 有 `op_Equality` = record struct，栈上零分配 |
| MapCommand 端点 2.1KB 增量 | 成分全为 BCL（ReadFromJsonAsync/状态机框），无应用层可指认项 |
| DI 注册 O(N×M) descriptor 线性扫 | 启动期豁免条款 |
| RabbitMQ 每消息 linked-CTS | v25 P2-5 注释已声明为接受成本 |
| `WrittenSpan.ToArray()` 物化 | 接口契约必然 + P7 否决案范围 |
| 核心域内核 L1-L4 全扫（事件单链表/SmartEnum 无锁读/Spec 缓存 CAS 等） | 零候选，形态全部最优（详见分片 A 报告） |
| 声明注释排除（EventLogPositionReserver 每次分配器行 / InMemory 各取舍 / CloneForLease 浅拷贝等） | 代码内勘正/取舍声明在案，未触碰 |

### 表 4 · 深读执行台账

| 分片 | 文件 | 候选 | 备注 |
| --- | --- | --- | --- |
| A 内核/生成器/分析器 | 34/34 | 2 | 首派即返 |
| B CQRS/消息四包 | 17/17 | 5 | 首派模型请求失败，原样重派成功（无返回=失败纪律） |
| C 处理器族九包 | 77/77 | 5 | 同上重派成功；含已核实 T2.1 靶点现状（逐条 UPDATE 属实，非新发现） |
| D 持久化九包 | 60/60 | 4 | 同上重派成功 |
| E 序列化/压缩/宿主/DI | 34/34 | 2 | 首派即返 |
| **合计** | **222/222** | **18** | 头条五条主 agent 独立行号复核属实 |

## 三、行读法归因

- **候选形态学**：18 条里 9 条属「不对称残余」（同文件/同族先例已落地、漏一格：Brotli 池、GZip 容量、Guid 腿、Inbox 回查、EventLog 批化）——印证 EPSL §0「微调边际递减」与 Pal.ORM 四报告经验：存量优化面在收敛，剩余是结构性不对称而非疏忽。
- **唯一高严重度（#1）为何此前不可见**：三层原因叠加——EF 栈基准车道待上游 Provider（NU1107/NU1102）从未跑过；往返数是驱动 wire 行为源码不可见；二次方项在 ValueComparer 快照语义里（每跟踪实体每次 DetectChanges 全字典比较）。这是 SOP 保留深读价值的又一例证（VSB 同款：analyzer 抓不到的跨层形态）。
- **A 批三条为何当天可闭环**：均有确定性分配断言可写「旧码必红」测试（#2 字典构造、#3 插值守卫、#4 池化）；#4 稍大（池跨程序集上移公开，DecompressionGuard 单一事实源先例现成）。

## 四、口径注记

① 本轮**零代码改动、零提交**：工作树有活跃外部写入者（三处未提交路径迁移修改）；SOP 阶段 3 双出口取「A 批就绪待开工 + B/C 批明确移交」出口。A 批三条 (#2/#3/#4) 待开工指令即进入「旧码红测试 → 修 → 三连绿 → S3」标准动线。
② 排名 #1 的批量化释放与 P0-FIX-3 声明注释无冲突（该注释裁决的是「必须释放」，非逐条粒度）；但 P3（八轮）的逐 saga 0 行告警语义需与批量化联动——列入 B 批裁决点。
③ 深读基于工作树现状（含 a380b2a 与未提交修改）；perf-budgets.md（44 行）已存在并作为分片预算上下文——上轮接入缺口的第 ① 项已闭合，AGENTS.md 文档地图三行亦已补（a380b2a）。
④ 上轮报告勘正声明：`epsl-audit-2026-10-11.md` 发现②的「perf-budgets.md 不存在」子项被 a380b2a 推翻（其余子项——perf-ledger、28 轮三不做无台账入口——仍然成立）；该文件正被外部写入者编辑（路径迁移），本轮不代改，留其收口。

## 六、修复轮结果（2026-10-11 晚，用户令「根据报告完整修复」）

**处置总表（18 项：15 落地 / 1 移交 / 3 不做）**：

| 候选 | 处置 | 验证 |
| --- | --- | --- |
| #2 FromHeaders 空头快路径（A1） | ✅ 落地 | 分配断言旧码红→绿（30/30） |
| #3 InboxProcessor 日志守卫（A2） | ✅ 落地 | 间谍测试禁用态红→绿 + 启用态对照（214/214） |
| #4 Brotli 解压池化（A3） | ✅ 落地 | 断言红→绿（33/33）+ 基准 45,185→16,440B（-64%） |
| #9 Kafka/Rabbit 空 Headers null 化（B5） | ✅ 落地 | 四件行为锁定（35/35；结构性红——旧码 private 不可达） |
| #10 Dispatcher Queue 拷贝移除（B6） | ✅ 落地 | 旧码 851B/迭代红→新码 579B 绿（35/35） |
| #1 Saga 批量化释放（B1） | ✅ 落地 | 四栈 SQLite 真库测试 + 处理器接线红证明 + 快照 +8 行评审 |
| #6 Save 存在性预查 UPDATE 先行（B3） | ✅ 落地 | 既有 Save 行为测试全绿（384 零失败） |
| #5 Guid JSON 三腿零分配（B2） | ✅ 落地 | 旧生成器红→新码 323/323（含 N/B/P 格式集锁定） |
| #7 EventLog k>1 PG 批量化（B4） | ✅ 落地 | PG 真库配对 A/B 2.77× + 探针跨距/重复分类断言（CI 双方言） |
| #11 Kafka 消费双字典（C） | ✅ 落地 | 单趟 FromKafkaHeaders（Messaging 35/35） |
| #14 PalORM×MySQL Inbox 回查（C） | ✅ 落地 | 单命令 LAST_INSERT_ID（CI 容器面验证） |
| #15 BulkCopy 连接串缓存（C） | ✅ 落地 | CWT 按连接实例（Integration 384 零失败） |
| #17 分析器重复求值（C） | ✅ 落地 | 零风险合并（Analyzers 47/47） |
| #8 投影窗口化检查点（B7） | ⏸ **移交** | 特性级变更：ProjectionProcessor 是逐消息 API，窗口化需重建驱动配合的批式 API + 检查点存储语义 + Rebuilder 改造 + 崩溃重放窗口语义（1→N）裁决——不属局部修复，独立立项（幂等契约 IProjectionHandler remarks 已为其留好前提） |
| #12 SagaState 线性扫（C） | ❌ 不做 | 伴随 HashSet 影子集与 CloneForLease 浅拷贝共享（v26/M3-3 勘正链领地）耦合序列化面；典型 10~50 步绝对量小，风险/收益比不支持 |
| #13 Inbox EF 首投双往返（C） | ❌ 不做 | 方言 upsert 需在 EF 包引入 raw SQL 方言面 + 首投/重投频率部署依赖 + 「重复路径优先」取舍在案；EF 栈车道待上游解除后一并重估 |
| #16 GZip/Deflate 压缩输出容量预设（C） | ❌ 不做 | 无双赢容量值：不可压缩最坏 ≈ input+64 预设、高压缩比场景预设 input.Length 反超配（MemoryStream 容量即分配）；替代形态（ArrayPool+精确回拷）引入额外拷贝 |

红证明台账：A1/A2/A3/B6/B1/B2 为「旧码红→新码绿」实测（stash 循环日志在案）；B5 为结构性红（旧码 private 不可达，T1.2 先例口径）；B3/B4 为行为等价变更（既有行为测试全绿 + B4 配对 A/B + 探针新断言）；C 批四项为行为等价（既有测试全绿）。

## 五、尾注

- 后续移交：A 批 3 条待开工指令；B 批 7 条按排序表逐项走深挖协议（探针前置→单变量→S3）；C 批 8 条登记不排期。perf-ledger 归集（上轮后续③）落成后，本表 2/表 3 应整体迁入「已充分利用特性表 / 明确不做表」两列。
- 数据源：五分片 agent 报告（会话内全文在案）；行号复核 sed 输出在案；`%TEMP%/epsl2-build.log`、`%TEMP%/epsl2-smoke.log`。
- 相关：`epsl-audit-2026-10-11.md`（接入轮）、`docs/performance.md`（账本）、`perf-budgets.md`（预算契约）。
