# EPSL 地毯审计轮（2026-10-16 · 四阶段 SOP 完整轮）

> 触发登记 = 阶段边界（性能实施双轮 R1-R10 与 S0-S5+B4 于 2026-10-14~16 收官，80bff32 当日 19:21 落定 B1 裁决关闭修复轮）+ 用户显式指令「运行epsl系统」。
> 前置状态：dev 分支 80bff32，工作树清洁；AI 质量系统同日先行全量运行全绿（verify-ai 29/29、gate 2 PASS 1 WARN、test-gate 5 项、gate-audit 30 探针、全量 1619 测试 0 失败）。
> 承接：`epsl-carpet-2026-10-11.md`（上轮地毯 + 当晚修复轮 15 落地）与 `epsl-audit-2026-10-11.md`（接入轮）。

## 一、看点（量化结论）

1. **阶段 1 全绿**：`check-all.cs`（format + CA 分析 + 编译）0/0/0；`--smoke` 6/6 分配预算内（Success 0.000 / Failed 40.000 / FromValue 0.000 / RaiseEvent 128.001 / StartOutbox 0.002 / StartEventLog 0.000 B/op），与账本跨轮逐字节一致（第四次独立复现）；全量 1619 测试 0 失败（契约测试含 AllocationContractTests 在 Core.Tests 323/323 内）。门禁红项为零，阶段 1.5 归因不启动。
2. **表 1 机械核验 5/5 通过**：MemoryDiagnoser 11 文件、ManualConfig+InProcessEmitToolchain 8 处、MediumRun 6 处、真库车道 8 处、smoke 断言在档；ShortRunJob 13 处命中经逐处查证全部为「无 [ShortRunJob]」留档注释，属性残留为零（与台账「已全移除」一致）。
3. **地毯深读 223/223 全覆盖**：七分片逐文件读完 36645 行，与 review-scope full 档清单精确一致，零跳读零谎报；共产出 **26 条发现**（25 候选 + 1 附带文档缺陷），撞已定案清单（perf-ledger 四表 + ADR + 2026-10-16 三项用户裁决）计数 **0**。
4. **唯一高严重度 1 条**：`KafkaBroker.cs` 发布路径双 ToArray——S3 轮 A13「消双拷贝」净效果为零（133 行新增拷贝与 149 行遗留拷贝相抵），且 142-148 行注释「本处 ToArray 现为唯一一次托管拷贝」与同方法两个 ToArray 的事实不符。已落地优化的假闭环，只有深读能出。
5. **候选形态学**：26 条里 14 条属「不对称残余」（同文件/同族先例已做、漏一格：Kafka 遗留行、R9 驻留上限四池漏一、R3 惰性 List 两处漏格、A15 半场、A-1 一格、O27 idiom、B1 批量释放自身的 IN 列表无界），印证存量优化面收敛后剩余是结构性不对称。

## 二、四组表

### 表 1 · 阶段 1 闸口（本轮实测）

| 闸口 | 结果 |
| --- | --- |
| `check-all.cs`（dotnet format style + CA 分析 build + 编译 error 计数） | exit 0 / IDE 建议 0 / CA 错误 0 / 编译错误 0 |
| `--smoke`（分配预算断言，退出码执法） | exit 0 / 6/6 预算内 / 分配与账本跨轮一致 |
| 契约测试（AllocationContractTests 等，全量套件内） | 1619 测试 0 失败 76 跳过（跳过 = PG/MySQL 方言探针，无 Docker 按 T-17 自动跳过） |
| 表 1 机械核验（ledger 已用特性在位） | 5/5（ShortRunJob 13 处均为留档注释） |
| 门禁红项 | 无 → 阶段 1.5 自动归因不启动 |
| 基准矩阵 | 引用不重跑：80bff32 当日刚完成远程库四格实测入账 performance.md:615，24h 内无产品变更 |

### 表 2 · 候选总表（排序 = 严重度 × 影响面 × 可验证性 ÷ 成本；批定义见 SOP 阶段 3）

| # | 位置 | 机制（一句话） | 严重度 | 批 |
| --- | --- | --- | --- | --- |
| 1 | `Messaging.Kafka/KafkaBroker.cs:133` + `:149` | 双 ToArray：A13 新增的 133 行拷贝与遗留的 149 行拷贝相抵，发布路径每消息仍两次全载荷拷贝，A13 净效果为零；142-148 注释「唯一一次托管拷贝」失实 | 高 | **A**（`Value = value` 一行 + 注释勘误；分配计量探针红证明） |
| 2 | `Dapper/DapperEventLog.cs:183-194`（BatchRowLimit MySQL 返 0） | MySQL k>1 追加未批化；登记延后的前提可证伪：innodb_autoinc_lock_mode=2 下已知行数的多行 VALUES 获连续自增块、多行 INSERT 的 LAST_INSERT_ID 返回首行值（官方文档两项，agent 附来源），多行 INSERT + 一次 LAST_INSERT_ID 即 2 往返，k≥3 净胜 | 中 | B（wslc MySQL 连续性实测 + **用户裁决注释级登记重开**） |
| 3 | `Dapper/DapperSagaStateStore.cs:124-134` × `PalORM/Stores/PalOrmSagaStateStore.cs:108-167` | Saga 租约两步（UPDATE + 回读 SELECT）未 RETURNING 化；Outbox PG/SQLite 与 EF 栈租约均已单语句，双栈不对称 | 中 | B（语句计数探针 + 三方言行为锁定；先例 B1/R7 在案） |
| 4 | `Dapper/DapperEventLog.cs:98-99` 门控 + `:160-164` 慢路径 | PG 单事件快路径只覆盖 Any 版本；Exact 才是事件溯源聚合保存 dominant 形态，仍付 MAX 预查 + INSERT 两往返（守卫谓词可原样下推同一 INSERT..SELECT..RETURNING） | 中 | B（PG 车道 A/B，Any 快路径 -15% 先例锚） |
| 5 | `Dapper/DapperIdempotencyStore.cs:132` × `PalORM/Stores/PalOrmIdempotencyStore.cs:132` | 冲突路径全行读含 response_payload 大列；Completed 情形读一次丢弃、调用方再读一次（双传），另三种情形纯浪费传输 | 中 | B（payload 尺寸敏感需实测；双栈同批处置防跨栈比值漂移） |
| 6 | `Transactions.EFCore/SagaStateDbContext.cs:383-389` | B1 批量释放的 IN 列表无界：TimeoutScanBatchSize 启动校验上界 10_000，单语句可携 1 万参数；SQLite 变量上限 999 下抛错并被 `SagaTimeoutProcessor.cs:288` catch 吞成日志，租约退化为 LeaseDuration 过期释放 | 中 | B（SQLite 变量上限实测；OutboxDbContext:218-220 的 400 行分块先例现成） |
| 7 | `Projections/ProjectionProcessor.cs:73-109` | 回放每事件 3 条语句（B7 移交项现状复核，状态「⏸ 移交」非否决）；两级方案：(a) TryStart 跳过前置 SELECT 直插，冲突才分类，新位常见路径 3→2 条零语义变化 | 中 | B（(a) 可升级 A 批；(b) 窗口化维持移交，崩溃窗口 1→N 需裁决） |
| 8 | `Compression/DecompressBufferPool.cs:20-28` | 缺驻留上限弃槽：R9 给四个同族 TLS 池加了 256KB 阈值弃槽，解压池只改了首轮 GetSpan 提示，Rent 无容量检查，超大载荷后按历史峰值驻留线程槽 | 中 | B（阈值按解压输出典型尺寸标定，可探针） |
| 9 | `CQRS/PipelineBehaviors.cs:41` + `:74` | ValidationBehavior._validators 静态类型是接口，MS DI 注入运行时形态为数组，foreach 走接口 GetEnumerator 每请求一笔 SZGenericArrayEnumerator（~32B，含零 validator 常见场景）；Dispatcher.cs:212-216 MaterializeBehaviors 先例现成 | 中 | B（先探针确认 net11 JIT 未对接口型数组 foreach 去虚拟化，增量 0 则候选死亡） |
| 10 | `Core.SourceGen/EnumGenerator.cs:468-511` + `IdentityGenerator.cs:450-500` | duplicateHints/Combine 的 O(N) 字符串 churn：任一候选值变化触发全量 N 个 IdentityHint + LocationIdentity 重算（A15 只把模板渲染迁出 Collect 回调，键构造留两节点） | 中 | B（SourceGen driver + GC 分配计数探针） |
| 11 | `PalORM/Stores/PalOrmEventLog.cs:95-102` | A11 登记阈值算术核点：单次 IN 回查（2 往返含预检共 3）即可达，登记按「位置回查需 2 次额外往返」计 k≥4，实为 k≥3 净胜，k=5 档多付 2~3 次 | 低~中 | B（**用户裁决注释级登记重开** + MySQL 真库 A/B；与 #2 同主题双栈） |
| 12 | `Transactions/Saga/Saga.cs:799` | Dynamic 车道 routeFailures 在 try 前无条件建空 List（~32B/步）；同文件 R3 刚把四车道共享骨架 failures 改惰性建，Dynamic 是漏格姊妹 | 低 | **A**（首个失败入 list 才建；同文件先例） |
| 13 | `Transactions/Saga/SagaTimeoutDetector.cs:52` | 每 saga 每 tick 建空 List，无超时经 :79 `hasTimeout ? list : []` 丢弃；30s 周期 × 批大小次白分配 | 低 | **A**（首个超时命中才建） |
| 14 | `Messaging/MessageConsumeContext.cs:61-76` | correlation-only 消费路径仍建空字典：A-1 补了「无头且无兜底 correlation」格，漏「无头、有 correlationId」格；本仓 RabbitMQ 写侧把 correlation 放 BasicProperties.CorrelationId 而非 header，主流形态每条白付空字典 + 四次 TryGetValue | 低 | **A**（headers is null 用 correlationId 直构上下文） |
| 15 | `EventLog/RecordedEvent.cs:42-43` | InMemory 追加每事件 payload/metadata 双拷（EventData ctor 防御拷 + RecordedEvent ctor 再拷）；EF 栈 StoredEvent.cs:45-46 零拷贝别名先例，同程序集别名契约更强 | 低 | **A**（别名共享 + 行为锁定；公开面仍只读） |
| 16 | `Analyzers/StrategicDddAnalyzer.Handlers.cs:72` | chainBoundedContext 已形参传入（:12-15）且 PDDD003 支路已用（:27），:72 又以逐字相同实参内联重算一遍基类链遍历（纯 CPU，零分配） | 低 | **A**（一行改用形参；等价性由现有行为测试全绿锁定） |
| 17 | `Core.SourceGen/EnumGenerator.cs:216-218` + `IdentityGenerator.cs:171-174` | partial 检查的捕获闭包：lambda 捕获 ct 参数，每次 transform 一个 display class + delegate（~48-64B）；同文件 :676 IsPartial(ct) 零分配参照现成且 :308 已改调，:216 漏改 | 低 | **A**（改调扩展方法） |
| 18 | `Analyzers.CodeFixes/AddBoundedContextPrefixCodeFix.cs:64` + `AddProjectionContextPrefixCodeFix.cs:136` | `StartsWith(prefix + ".")` 每次执行拼接中间串；分析器侧同形态已被 R10 拆成零分配 char 比对（SymbolHelpers.cs:473-477），fix 侧两处原样保留 | 低 | **A**（读码可证 + 现有 fix 行为测试锁定） |
| 19 | `Analyzers.CodeFixes/AddVersionSuffixCodeFix.cs:71-72` | 剥离循环每次 IsMatch + Replace 双次正则全扫（Replace 无匹配返回原串，可并一次判定）；双后缀输入放大为 4 次 | 低 | **A**（循环条件改用 Replace 返回值；行为测试锁定） |
| 20 | `Dapper/DapperBulkCopy.cs:431-495` | SQLite 批量插入逐条执行未多行 VALUES 化；同文件 MySQL 小批量（:353-400）与 EventLog SQLite（A12）双先例在案，1000 行批 = 999 次可省命令执行 | 低 | B（本地 SQLite 车道实测；量级或个位数百分比） |
| 21 | `Dapper/DapperIdempotencyStore.cs:148` | 过期回收 UPDATE 后回读 revision 可 RETURNING 化（PG/SQLite）；冷路径，PalORM 侧有「低频可接受」注释在案，双栈口径待齐 | 低 | B（冷路径；与 PalORM 注释并案裁决） |
| 22 | `Idempotency/IdempotencyProcessor.cs:61-65` | 前置 GetAsync 与 TryStart 内冲突读在 race/过期路径重复；前门命中路径单读不涉（本轮实测确认路径边界） | 低 | B（需实测命中率分布 + TryStartAsync 契约变更四栈联动） |
| 23 | `Transactions/Saga/InMemorySagaStateStore.cs:14` | 终态 Saga 永不移除：GetActive/Lease 每 tick 全量扫含终态（成本随历史终态数无界增长）+ 长跑原型内存无界；Outbox 侧 R1 终态清除先例在案，但 Saga 终态仍被 GetByIdAsync 公开返回（恢复链路/断言依赖），清除改可观测行为 | 低 | B（**用户裁决淘汰策略**：要清需连带公开面语义变更） |
| 24 | `DependencyInjection/ServiceRegistration.cs:271-289` + `:326-342` | 每 AddPalHandler 对 services 线性查重，N 个 handler 启动期累计 O(N²)；TryAddEnumerable 线性去重是 MS DI 语义非本仓代码 | 低 | 不做登记（一次性启动成本，N=1000 估算数十 ms 量级，典型应用低于此） |
| 25 | `Core.SourceGen/EnumGenerator.cs:635-636` + `IdentityGenerator.cs:943-944` | Equals 内 string[] 绑定 LINQ SequenceEqual，非空数组每次 Equals 分配 2 个枚举器（两数组共 4 个）；同方法 Fields 已手写 FieldsEqual 零分配；仅嵌套类型候选（空数组走 ICollection 快路径不分配） | 低 | B（仅嵌套类型；需实测或补 InternalsVisibleTo 直调） |
| 26 | `Serialization/IMessageSerializer.cs:33（附带·文档缺陷）` | 注释称 MemoryPackMessageSerializer「（原生 writer 重载）覆写为真零拷贝」，与 MemoryPackMessageSerializer.cs:87-91 明确不覆写声明（走 DIM 默认回退，ADR-029 延后段）直接矛盾，自定义序列化器作者会被误导 | 文档 | **A**（注释修正，零代码行为变化） |

### 表 3 · 分片自证伪/退回清单（退回也是产出，防下轮重扫）

| 项 | 证伪/退回依据 |
| --- | --- |
| LZ4/ZStd `with` 表达式疑似每次分配 | DLL 字符串堆无 `<Clone>$` 有 op_Equality = record struct，栈上零分配 |
| PalORM EventLog MAX 预查 + INSERT 两往返 | 已登记为 T2.2 优化靶点（performance.md:475/677），非新发现 |
| EventLog/Outbox Ulid 26 字符存储读侧 TryParseUlid 每行成本 | B6 同族（2026-10-16 用户裁决落库形态不做），读侧解析是该形态后果，无独立机制 |
| Saga MySQL 租约无 SKIP LOCKED / 无版本探测 | ADR-017 裁决维持乐观并发；Dapper 栈同形态双栈对称，非不对称残余 |
| MySQL `SELECT VERSION()` 每 scope 一次探测往返 | ADR-025 明文接受（「探测成本 = 每 scope 一次额外往返」） |
| `MarkCompletedAsync` 的 responsePayload.ToArray | PalORM byte[] 参数绑定必需拷贝，待上游支持 ReadOnlyMemory 绑定，属上游事项 |
| InsertBatchWithReturning/MarkProcessedBatch 每批 StringBuilder + List + ToArray | 批粒度小额分配，无实证达到 5% 量级，按「无机制证据的微调猜想不报」丢弃 |
| EventStreamJsonLines WrittenSpan.ToArray | 2026-10-11 表 3 已自证伪：接口契约必然 + P7 否决案范围 |
| OutboxDomainEventInterceptor payload.ToArray | OutboxMessage.Payload byte[] 公共契约必然（与 ADR-029 对 Kafka 的排除同 rationale） |
| InMemoryProjectionCheckpointStore GetResume 全字典扫 | 每次回放一次，冷路径 |
| ProjectionCheckpointDbContext SQLite 回退档全行拉取 | ADR-028 已裁决的回退设计 |
| InMemoryInboxStore Processed 记录滞留 | 非缺陷：幂等去重必须记住 Processed（与 Outbox 终态可清除本质不同） |
| SagaState.CloneForLease 浅拷贝 | v26 P3 勘正在案的有意取舍（僵尸步骤必须可被后继者补偿），未触碰 |
| `Saga.cs:344` RecordExecutedStep 残余 Contains | A6 把 O(k³) 降为 O(k²) 后明确承认停在存在性判定；消除需改持久化形态，属决策项非优化项 |
| `Saga.cs:272` MakeKey 每事件字符串拼接 | 机制确定但业务事件频率下量级低于门禁 |
| `SagaStateDbContext.GetByIdAsync` 缺 AsNoTracking / `DefaultSagaManager._invalidated` 无界 | 前者运维路径，后者既有声明取舍（16B/项） |
| RabbitMQ `PooledBodyWriter` using 归还时点 | 已对 RabbitMQ.Client main 分支源码核实：body 在 BasicPublishAsync 返回前同步拷入帧缓冲，无悬垂引用 |
| `JsonLinesEventWriter/Reader` ToArray | src 内无调用点（仅注释提及），非热路径，返回契约必需 |
| `SymbolHelpers.IsDomainModelType` 与 `IsDomainEventType` 遍历重叠 | 同族但需重构非换参可修，收益 CPU 口径，按不报纪律丢弃 |
| S1 内核 L1-L4 全扫（事件管线/遥测守卫/SmartEnum/ValueObject 栈缓冲/UnitBox/DIM 桥/PipelineStateMachine/FrozenDictionary） | 零候选，形态全部最优（R1/R2 及历史轮已落实，机制与注释自洽） |

### 表 4 · 深读执行台账

| 分片 | 范围 | 文件 | 发现 | 撞已定案 | 备注 |
| --- | --- | --- | --- | --- | --- |
| S1 | Core + CQRS + DI + Hosting + Shared | 34/34 | 2 | 0 | 首派即返 |
| S2 | Transactions + Transactions.EFCore | 41/41 | 4 | 0 | 含 B1 批量释放自身的 IN 列表无界（已落地优化的残余） |
| S3 | Dapper 四包 | 36/36 | 6 | 0 | 候选 1 附官方文档两项来源（innodb-autoinc 模式 2 + information-functions） |
| S4 | PalORM 四包 | 21/21 | 3 | 0 | 8 项按既有裁决/登记丢弃并计数 |
| S5 | Messaging + Serialization + Compression | 37/37 | 3 + 1 附带 | 0 | 附带 = IMessageSerializer 注释失实（文档缺陷） |
| S6 | EventLog + Projections + Idempotency + Repository.EFCore | 39/39 | 3 | 0 | 含 B7 移交项现状复核（状态「⏸ 移交」非否决） |
| S7 | Analyzers + CodeFixes + Core.SourceGen | 15/15 | 6 | 0 | 编译期面口径 = 分析器回调分配，非请求路径 B/op |
| **合计** | **src 全量** | **223/223（36645 行）** | **26** | **0** | 头条候选主 agent 独立行号复核：#1 #2 #3 #5 #6 #9 #12 #13 #16 共 9 条属实 |

## 三、行读法归因

- **唯一高严重度为何此前不可见**：`KafkaBroker.cs` 双 ToArray 是「已落地优化的假闭环」——A13 提交信息与注释都声称消除一次拷贝，但遗留的 149 行 `Value = value.ToArray()` 把净效果归零；`byte[].ToArray()` 绑定 MemoryExtensions/LINQ 两路径都拷贝，无任何 IDE/CA 诊断覆盖「对已为数组的值再 ToArray」，且同方法注释的自我否证（「唯一一次」对两个调用点）只有逐行读能发现。这是 SOP 保留深读价值的又一例证。
- **A 批 10 项为何当天可闭环**：#1/#26 为一行加注释（红证明走分配计量探针：#1 修复后期望单上下文/单拷贝；#26 零行为变化仅注释）；#12/#13 同文件 R3 惰性建 List 先例逐字同构；#14 是 A-1 快路径漏格的直接补全；#15 有 EF 栈零拷贝别名先例；#16-#19 为换用现成零分配参照或并一次正则，等价性均由现有行为测试锁定。
- **两条注释级登记重开为何必须用户裁决**：#2 与 #11 撞的是代码注释里的「登记延后」（非 perf-ledger/decisions 正式条目），agent 分别以 MySQL 官方文档两项与往返计数算术挑战其前提；按 SOP 2.3「撞既有裁决即退回并计数」与用户裁决类同权重纪律，不默认重开，呈报裁决。

## 四、口径注记

① **A 批十项已闭环**（用户令「按报告修复」，2026-10-16 当晚四提交 `2e16620`/`62b417e`/`1822c84`/`38d5a21`，修复列表见第五节）；B 批 15 项维持移交待实测/裁决。审计轮出口形态 = A 批闭环 + B/C 批明确移交。
② 深读基于工作树 80bff32（当日 19:21 提交）；smoke 与契约测试为本轮 Release 实测，非沿用上轮结论。
③ 76 个跳过测试 = PG/MySQL 方言探针（Testcontainers 路径，本机无 Docker，T-17 统一跳过策略），非缺陷。
④ 分片 agent 论断按继承论断处理：头条 9 条已独立复核属实；其余条目行号锚点由分片报告提供，实施前按 A 批纪律逐条复验。
⑤ 与 2026-10-11 轮的关系：该轮 18 项当晚修复 15 落地 1 移交 3 不做；本轮 26 项中 14 项是不对称残余（含对已落地修复的残余：#1 A13、#6 B1、#8 R9、#12 R3、#14 A-1、#10 A15、#16 O27 同族），无一条与该轮已处置项重复提案。

## 五、修复列表（用户令「按报告修复」后 A 批十项当日闭环）

| 提交号 | 修复内容（A 批项） | 验证 |
| --- | --- | --- |
| `2e16620` | ① KafkaBroker 双 ToArray 收单（`Value = value` + 注释勘误）+ ② MessageConsumeContext correlation-only 免空字典（缓存只读空视图单例） | 分配断言旧码红→新码绿（Kafka 2264B/迭代；correlation ~136B vs 预算 70B）+ 新增 `KafkaPublishAllocationTests` 两件（IProducer 假件 + 内部构造 seam，另锁 Key/Value/Headers 行为契约） |
| `62b417e` | ③ SagaTimeoutDetector 常态路径三笔分配全消（下标循环 + 惰性 List + Array.Empty 单例；含 `[]` 集合表达式与接口枚举器两笔同路径同族分配）+ ④ Saga Dynamic 车道 routeFailures 惰性建 | ③ 零分配断言（旧码 96B/次红，阳性对照自证仪器）；④ 对照组增量设计（Dynamic 减普通路径，吸收 Debug/Release 基线，旧码 +32B/迭代红）；`SagaAllocationTests` 三件 |
| `1822c84` | ⑤ RecordedEvent payload/metadata 别名 EventData 构造期副本（StoredEvent 同构先例） | stash 旧码 2998B/事件红→新码 1974B 绿（差 = 一个 1KB 载荷拷贝）；`EventLogAllocationTests` 一件 |
| `38d5a21` | ⑥ PDDD004 形参重用 + `HasAttributeAlongBaseChain` 死代码清除；⑦ 生成器 partial 检查闭包消除两处；⑧⑨ CodeFix 消中间串/双次正则两处；⑩ IMessageSerializer 注释勘误 | ⑥⑦⑧⑨ 行为等价：Analyzers.Tests 51/51 + Core.Tests 323/323（双后缀 x.v1.v2 回归测试在案）；⑩ 纯注释零行为变化 |

**收尾纪律记录**：A 批两处新测试初版用了 `IsNotNull` 弱断言，被 `AssertionStrengthGateTests` 棘轮当场拦下（基线 200 上限）——按「新增测试必须行为断言」纪律改为直取属性（null 时 NRE 即红），未抬基线。修复轮另实证：`SagaTimeoutDetector` 无超时路径的分配面比审计发现更大（三笔而非一笔：空 List + `[]` 集合表达式每次求值新建空数组 + 接口枚举器），三笔同路径同族一并收单。

## 六、尾注

- 移交项：A 批 10 项（#1 #12 #13 #14 #15 #16 #17 #18 #19 #26）**已闭环**（第五节修复列表）；B 批 15 项（#2-#11 #20-#23 #25）按排序表逐项走深挖协议，其中 #2/#11 待用户裁决注释级登记重开、#7 维持 B7 移交格局（(a) 方案可先行）、#23 待淘汰策略裁决；不做批 1 项（#24，数据在表 2）。
- 归集：本表 2/表 3 应整体迁入 `.ai/perf-ledger.md` 表 4 候选排序表（本轮已同步更新）与表 2 明确不做表（#24 新增一行）。
- 数据源：七分片 agent 报告（会话内全文在案）；头条 9 条复核 sed/Read 输出在案；`--smoke`、`check-all.cs`、测试循环输出在案（tmp 日志）。
- 相关：`epsl-carpet-2026-10-11.md`（上轮）、`perf-budgets.md`（预算契约）、`.ai/perf-ledger.md`（性能台账）、`docs/performance.md`（实测账本）。
