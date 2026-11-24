# 刁钻缺陷挖掘提示词（v3，2026-11-24）

> 用途：已发布框架库的只读缺陷挖掘。目标是在基线全绿的前提下，找出今天就能复现的真实缺陷，并最大化死角覆盖。
> 版本沿革：v2 基于 R16 实跑固化（16 分片、六视角、6 条真缺陷）；v3 基于 R16+R17 两轮实跑扩充——R17 产出 9 条真缺陷（3 P2 + 6 P3）、3 条证伪、10 条死角，其中 8 条已修复（`f6764bc`），并暴露两处方法学缺陷（行为探针假绿、语义克隆漏检），已写入本版。
> 与 `.ai/review/prompt.md` 的分工：review 审「承诺是否兑现、改动是否可合并」，本提示词挖「哪里会出错而现有防线看不见」。两者共用误判库与证据分级，但不共用轮次编号。

**v3 相对 v2 的三处关键升级**：
1. **V1 从「同族修复漏格」扩为「语义克隆部分同步」**（§4 V1）——R17 的 D1/D2/D8 全是此形态，且可从 `git log -S` 机械枚举，不只是靠记忆。
2. **新增 V16-V20 五个视角**（§4）——覆盖重构复制残留、承诺手段缺失、测试自身假绿、跨抽象层状态、配置/能力声明不对称，均来自 R17 真实命中或死角。
3. **证据纪律修订**（§6）——行为探针在 ThreadStatic/并行调度下会假绿（R17 D2 实证），源码级不变式不是退路而是并列的一等证据；并明确「语义相同≠实现相同，别用 A 的行为探针证明 B」。

---

## 0. 总原则（违反即报告无效）

1. 缺陷的定义：产生错误行为、数据丢失、契约破坏、静默失效（组件死亡无信号），或让使用方写出错误代码。风格偏好与规模指标（行数、文件数、抽象层数）不构成缺陷。
2. 默认假设代码是对的。本仓有 40 份 ADR 与大量勘正注释。命中含「P定案、勘正、取舍、勿重开、有意为之、ADR-」字样的注释时，先引用注释原文，再论证其前提是否失效。无视注释直接上报视为无效条目。
3. 质量门槛：宁可 8 条真实，不要 40 条注水。允许结论是「零新缺陷 + re-affirmed N 项」。
4. 覆盖范围是默认项不是选项。时间不够时按优先级降级为抽样，但每个视角至少覆盖 1 片，且未读面必须写进第 7 章。
5. 假设必须证伪。任何「看起来有问题」先找反证命令，跑不出错误行为的降级为 [推断]，进死角，不进缺陷清单。**证伪的平台陷阱**：行为探针不红不等于假设错，先排除探针自身失效（R17 D2：ThreadStatic 槽 + TUnit 调度让「bomb 后检查槽容量」恒绿，实际缺陷仍在）——见 §6「验证验证者」。
6. **语义克隆思维（v3 核心）**：本仓是持久化矩阵（4 栈 × 5 存储 + 2 broker），同一语义逻辑常有多份物理实现。任何一次修复、任何一处约定，都必须问「同一语义的其它物理位置同步了吗」。克隆关系不只看接口实现（`scripts/sibling-map.cs` 已枚举 27 族），更要看**跨接口、跨文件、跨方法**的语义等价物：一个方法内两处循环、两个文件里同名钩子、Dapper 版与 PalORM 版、对象路径与 raw 路径、注释承诺与实际落点。R17 的 8 条里有 6 条属此形态。

---

## 1. 事实底座（动手前必做，防重复上报）

| 步骤 | 内容 | 产物 |
|---|---|---|
| 1.1 基线三数 | `dotnet build` + `dotnet test`。记录通过、失败、跳过三数及跳过分布。跳过面板必须披露，区分结构性跳过与环境缺失 | 基线行，写入报告第 1 章 |
| 1.2 勿重开清单 | 读 `docs/decisions/` 全部 ADR（重点近 15 份）、`.ai/perf-ledger.md` 三表、`.ai/slop-register.md` 或等价文件 | re-affirmed 清单，明列哪些已裁决不修 |
| 1.3 代码内声明注释 | `grep -rn "P定案\|勘正\|取舍\|勿重开\|有意为之\|ADR-" src --include=*.cs`，记行号，阶段 4 逐条比对 | 声明位置表 |
| 1.4 历史修复回归底账 | `git log --oneline -90` 圈出所有「修复、勘正、收口、优化」提交。逐条问两件事：锁定测试能否真失败、修复是否覆盖同族其他位置 | 同族漏格候选池（本轮 F1 的出处） |
| 1.5 证伪台账 | 读上一轮的证伪记录（`.ai/state.md` 与最近审计报告）。已知被证伪的假设直接跳过，不重复跑 | 排除清单 |
| 1.6 覆盖基线 | 统计 src 项目数、.cs 文件数、总行数、最近 20 提交 | 覆盖统计行 |
| 1.7 修复簇枚举（v3） | 对 1.4 的**每个**修复提交跑 `git show --stat` + `git show <commit>` 读改了哪些文件，再用 `git log -S "<关键符号>" --oneline` 找该符号的全历史引入点，对每个引入点确认「同一修复是否覆盖」——产出「修复簇表」：修复点 / 语义等价位置 / 是否同步 | R17 的 D1（PalORM 修了 StopAsync 等待、Dapper 漏）、D2（SystemCompressor 接了 Release、Native 漏）、D8（Rabbit 修了健康位清除、Kafka 漏）全由此表命中 |
| 1.8 承诺-实现对账清单（v3） | `grep -rnE "当前不|当前未|目前不|尚未|待验证|待跟进|暂不|未实现|不覆写|未覆写|暂不支持|只进日志|协作收尾|自动|保证|闭环" src --include=*.cs` 收集所有「完成态/否定态承诺」，对每条找实现证据（字段引用点、await、logger 调用、Activity 发射）——「承诺有词、实现无据」即候选 | R17 D4（MemoryPack「不覆写」在 a2b6434 落地后三处文档未同步）、D6（「details in logs」零 logger sink）由此命中 |

**1.5 的来历**：R16 曾花大量时间重跑「CTS Cancel 后 Dispose 破坏协作取消」与「解压炸弹防护缺失」两条，均被证伪。证伪台账把这类结论固定下来，避免下一轮重复。

**1.7/1.8 的来历**：R17 的 8 条真缺陷中，6 条是「某处修了、语义等价处没修」，2 条是「承诺写在那、实现落点空」。这两步把过去靠「碰巧读到」的发现变成机械枚举——`git log -S` 回答「这个符号历史上在哪些文件出现过」，完成态词 grep 回答「哪些承诺从未被兑现」。

---

## 2. 确定性预扫描（召回交给脚本，判断交给模型）

每条 grep 跑一次落盘，命中即候选。已知零命中的类别不必重复展示。

1. `async void`
2. `.Result` / `.Wait()` / `GetAwaiter().GetResult()`，区分文档化阻塞桥与请求路径
3. 反射： MakeGenericType / Activator.CreateInstance / Assembly.GetTypes / Type.GetType( ，对豁免面只查滥用
4. fire-and-forget： `_ = .*Async\(` / Task.Run
5. SQL 字符串字面量内嵌 `--`
6. 批量路径： multiple:\s*true / BatchSize / BatchMark / Aggregat
7. 成对 API 订阅完整性： `\+=\s*On[A-Z]` 与 `-=\s*On[A-Z]` 的配对； OnError / OnFault / OnTerminated / OnClosed / OnDisconnected 全部命中点的订阅与退订配对
8. CTS 生命周期： CancellationTokenSource 的 Dispose 点与 Token 读取点交集； CancelAsync 之后是否 Dispose；局部 cts 是否 using 或 linked 泄漏
9. 死配置： Options 类属性名在全仓的读取点计数（定义点之外零命中即候选，区分「供调用方配置」与「内部未生效」）
10. 无界增长： ConcurrentDictionary / Dictionary / List 字段的 Add 与索引赋值点，检查是否有 Remove 或上限
11. 异步形态： lock 内含 await（不可编译，查 Monitor 手写形态）、finally 内 await、Task.Run 闭包捕获大对象
12. 条件编译： #if 分支两侧对称性（一侧有守卫另一侧没有即形态缺口）

**第 13 类：完成态词与实现证据对账**。 grep 注释中的「已、保证、立即、等待、串行、幂等、自动、协作收尾、闭环」，逐个找对应实现证据。R16 的 F2 就出自这里：注释写「协作收尾」，字段 `_fireTask` 全文只有声明、赋值、清空三处，没有任何 await。v3 扩为双向——否定态承诺（「当前不覆写」「暂不支持」「待验证」「不实现」）同样要对账：语义一旦落地，这些词就从“诚实声明”变成“失实声明”（R17 D4）。

**第 14 类（v3）：游离 Task 的生命线**。 `grep -rnE "_ = Task\.Run|_ = .*Async\(" src --include=*.cs`，对每个命中问三件事：① 宿主 Stop/Dispose 时等不等它？② 它持有的资源（gate/CTS/连接/scope）谁释放？③ 它抛异常谁观察？R17 D1 命中：Dapper PG 通知器的游离 `Task.Run` 无 StopAsync 等待，宿主 Dispose 释放 gate 后批处理的 Release 靠 ODE 吞掉。对照 `BackgroundService.StopAsync` 只等 `ExecuteAsync` 的 BCL 语义。

**第 15 类（v3）：Helplist/池的成对操作**。 `grep -rn "Pool\|Rent\|Acquire\|Lease\|Subscribe\|Register\|\+=" src --include=*.cs`，对每个 Rent/Acquire/Subscribe 找对应的 Release/Return/Unsubscribe/−=，检查异常路径是否覆盖（`finally`）。R17 D2 命中：三个原生解压器只 Rent 不 Release，异常路径泄漏 64MB 槽。

**第 16 类（v3）：同一文本/模式的重复出现计数**。 `grep -rn "<同一告警文本>\|<同一事件ID>\|<同一常量>" src --include=*.cs | wc -l`——重构抽取后同一模式出现 2 次，要么是合理的双路径，要么是复制残留（R17 D3：Saga 超时告警在调用方与抽取方法各一次，N 步发 2N 条）。计数 >1 就逐个确认调用关系。

**第 17 类（v3）：三面观测的承诺与落点**。 `grep -rn "details in logs\|已记录\|记入日志\|上报指标\|健康" src --include=*.cs`，对每个「说要记/报/置位」的承诺，在**同一执行路径**上 grep logger/PalMetrics/健康位赋值。R17 D6 命中：响应写器把异常丢弃并写「details in logs」，但全文件零 logger 调用。

---

## 3. 覆盖矩阵（本版扩大）

### 3.1 代码分片（默认全做，每片读全约 17 文件、不超过 3300 行）

| 片 | 范围 |
|---|---|
| 1 | Core + Core.Abstractions + Core.SourceGen |
| 2 | CQRS + Messaging（确认契约、批量泵、延迟登记） |
| 3 | Messaging.Kafka |
| 4 | Messaging.RabbitMQ |
| 5 | Transactions（Outbox、Inbox、Saga、后台循环、Retention） |
| 6 | Transactions.EFCore + Repository.EFCore |
| 7 | Dapper + Dapper.Sqlite / PG / MySQL（三方言 SQL 矩阵） |
| 8 | PalORM + PalORM.Sqlite / PG / MySQL |
| 9 | Serialization（.Evolution）+ Compression（.Native） |
| 10 | EventLog + Projections（.EventLog / .EFCore） |
| 11 | Idempotency（.EFCore） |
| 12 | Hosting.AspNetCore 全族 |
| 13 | DependencyInjection + 诊断、健康、遥测 |
| 14 | Analyzers + Analyzers.CodeFixes |
| 15 | 跨栈矩阵专项（X2 填满 12 格） |
| 16 | 热点复验（近 90 天改动面 + 1.4 的历史修复点） |

片 1 与片 14 在 R16 完全未读，片 6 只读了拦截器同步桥。v2 把这三片列为优先片，禁止再以时间不足跳过且不声明。v3 追加**跨片对称专项**（不按项目分片，按语义轴切片，优先执行）：

| 轴 | 要对照什么 | R17 命中 |
|---|---|---|
| Dapper ↔ PalORM 同名组件 | 每个 `*Notifier`/`*Store`/`*Processor` 两栈对照生命周期、关停、健康位、重连 | D1（关停等待漏格） |
| 对象发布 ↔ raw 发布 | 同一 broker 的两条发布路径对照 span/计数/异常分类/声明位置 | D9（raw 无 span、声明在 try 外） |
| System 压缩 ↔ Native 压缩 | 每个操作（Compress/Decompress）两实现对照池成对、上限、异常形态 | D2（Native 漏 Release） |
| Kafka ↔ Rabbit 同语义钩子 | 健康位清除、批量 ack、OCE 分类、关停时序逐项对照 | D8（Kafka 漏清除） |
| 接口/ADR/实现/CHANGELOG 四方 | 公共契约的每个承诺在四处是否一致 | D4（文档三方失实） |
| 同方法内重复模式 | 抽取重构后同一文本/事件 ID 是否出现两次 | D3（告警双发） |

### 3.2 扩大后的新面（本版新增）

| 面 | 要找什么 | 为什么 |
|---|---|---|
| 测试基建自身 | 门禁脚本的假绿、棘轮基线是否可绕过、变异探针能否真红 | R16 的 mutation 基数问题就在这里，Stryker 分数建立在个位数基数上 |
| CI 工作流 | continue-on-error 掩盖失败、schedule 不进主路径、job 条件漂移 | 改动 workflow 后 config-policy 只查 SHA 钉固，不查语义 |
| 公共 API 快照 | 欲删的符号是否在快照内、是否只勘正不删除 | R16 的 F4/F5 因快照收录只能勘正，删除须走 Major |
| 文档承诺与 ADR 自洽 | ADR 内部互相矛盾的默认值、README 与代码行为分叉 | R16 发现 ADR-034 同文档一处写默认 false 一处写 true |
| 生成物与构建产物 | 陈旧 DLL 假绿、路径嵌入差异导致的错误对照 | R16 排查 Kafka 偶发时多次被构建产物误导 |
| 上游依赖行为 | 反编译验证本仓对每个上游包的假设 | R15/R16 多次靠反编译证伪或坐实 |

---

## 4. 刁钻视角（核心，按命中率排序）

每个视角给：判据、命令锚点、历史命中案例、反面证伪案例。

### V1 同族修复漏格（R16 F1，最高产）

判据：某个缺陷修了路径 A，同语义的路径 B 是否也修了。历史修复常常只覆盖一个消费者。
命令：`git show <修复commit>` 读改了哪些文件，再 `grep -rn <同判据> src` 列全部命中点，逐个确认。
命中案例：R14 修复在消费线程 `:637` 置 `_consumeFaultReason`，注释明写「健康检查据此转 Unhealthy」；worker 循环的两条终止 catch 同样终止消费，却没有置位。F1 即此漏格。
证伪案例：R16 曾怀疑 CTS Cancel 后 Dispose 会让在飞 token 抛 ODE，探针实测 Task.Delay 抛 OCE、Register 不抛，证伪。

### V2 注释与行为对账（R16 F2）

判据：注释里的完成态词没有对应实现证据。
命令：见 2.13，拿到命中行后读字段的全部引用点计数。
命中案例：StopAsync 注释「先取消 in-flight 批处理（协作收尾）」，但 `_fireTask` 无 await。
反面：R16 F4 的注释「PG 变体见 SagaLeaseActivePG」确实有引用点，但那个引用在死分支里，属可访问性缺陷而非注释失实。有引用不等于在用，要分清。

### V3 生命周期中途失败态（R16 F3）

判据：异步初始化、启动、构造函数在 await 之前赋值字段，中途抛异常后对象处于非法或不可恢复状态。
命令：grep 方法内「字段 = new ... 」出现在首个 await 之前的位置，逐个问「若 await 抛异常，字段保留什么状态，能否重试」。
命中案例：StartAsync 先置 `_listener` 再 await，首连失败后重试命中幂等守卫，实际从未启动。
刁钻点：对比上游契约。上游 listener 明写 StopAsync 后能再次启动，包装类比上游更严就是缺口。

### V4 死代码、死配置、死分支（R16 F4/F5）

判据：字段、常量、配置属性定义点之外零读取；switch 臂因前置 return 或枚举成员全集而不可达。
命令：对每个 public const 与 Options 属性做全仓读取点计数；对 switch 枚举分支核对枚举成员数与前置 return。
命中案例：DapperDbType 只有三个成员，PG 与 SQLite 已提前 return，switch 的 PG 臂与 `_` 臂均不可达，四个 SQL 常量零运行时消费者。MaxRetryDelay 唯一读取是启动校验。
约束：若符号在公共 API 快照内，只勘正不删除，删除须走 Major。这是版本契约，不是保守。

### V5 契约的首个违反者（R16 F6）

判据：注释声明「调用方必须 X」，框架自己是否第一个做反 X 的事。
命令：grep「调用方必须、调用方负责、需自行保证」，逐个查框架自身注册的后台服务与默认配置。
命中案例：SQLite 内存分支声称调用方必须串行访问，但 AddPalOutbox 自己注册了后台线程访问同一 Singleton 连接。
升级路径：把 [推断] 变 [事实] 要探针。R16 用 4 线程乘 500 次并发 SELECT 实测 18 次越界异常，异常形态不是 database is locked，排障会走错方向。

### V6 可观测三面皆空

判据：某条失败路径在日志、指标、健康检查三面都没有信号。
命令：对每条后台循环的 catch 块，分别 grep 同文件内是否有 logger 调用、PalMetrics 调用、健康标志位赋值。
命中案例：R16 F1 的 worker 只记 Error，不置健康位，/health 恒绿。
同族：R15 的 PgNotificationListener 三通道（Logger 属性、OnError 订阅、LastError 读取）全空，断线静默死亡。

### V7 配对 API 完整性

判据：订阅与退订、注册与注销、获取与释放、开始与完成、置位与清除必须成对。
命令：grep `+=` 与 `-=`、TryStart 与 MarkProcessed、Rent 与 Return、Cancel 与 Dispose 的配对。
命中案例：R15 的 OnError 零订阅；R16 的 gate Wait 与 Release 靠 finally 兜底，但 StopAsync 释放 gate 时未等 fire 任务。

### V8 上游行为假设（必须反编译，不靠注释）

判据：本仓注释声称的上游行为，是否与上游实现一致。
命令：`ilspycmd ~/.nuget/packages/<pkg>/<ver>/lib/<tfm>/<dll> -t <Type>`，输出为 ISO-8859 中文被剥，只 grep -a 取标识符，关键行号写进证据。
命中案例：R15 反编译坐实 PgNotificationListener 重连上限 5 次，XML 文档未披露；R16 反编译 MySqlConnection.OpenAsync 确认回调在 SetState 之后触发。
反面：MySQL 回调时序、Rabbit dispatch 串行前提曾被怀疑，反编译后证伪。

### V9 门控可信度三问

判据：执行过吗、拒绝过吗、覆盖了多少。三问缺一即门禁假绿候选。
命令：`dotnet run scripts/gate-audit.cs -- --inventory` 看矩阵分类，`dotnet run scripts/mutation-probe.cs` 看变异能否真红。
命中案例：mutation.yml 的 Stryker 分数建立在 152 到 12、2310 到 35 的基数上，break 阈值无区分度；R16 裁决把 mutation-replay 提为唯一权威门禁。
刁钻点：一个能红、报错精确到行的门禁，可能对它本该抓的形态完全失明。R16 前的 V25 就是这种，补上裸文件名形态后当场抓出 46 处。

### V10 排序与优先级分叉

判据：同语义的多个实现，排序键或优先级口径不一致。
命令：把同族 SQL 或配置分支并排列出，逐列比较 ORDER BY、阈值、默认值。
命中案例：不可达的 Saga 到期租约用 ORDER BY created_at，在用的三个变体用 next_check_at, created_at。若有人把在用分支改回非 RETURNING 形态，公平性静默变化且无测试会红。

### V11 异常吞掉的边界

判据：catch 块里不 log、不重试、不传播、不降级，四者皆无即候选。
命令：grep catch 块，逐个检查块内是否有四类动作之一。注意 finally 内的空 catch 是否有意设计，R16 的 MessageBatchPump 就有明确声明。
命中案例：PeriodicBackgroundProcessor 的 OnTickFailed 自身抛异常时 catch 空吞，与「基类保证循环不中断」的契约相反，但已有注释声明，属 re-affirmed 而非新缺陷。

### V12 时间源与编码混用

判据：墙钟与单调时钟混用；同一时间值在三方言用不同编码；`DateTime` 与 `DateTimeOffset` 混传。
命令：grep Stopwatch.GetTimestamp、GetUtcNow、UtcDateTime、ToString("O")，逐个确认用途。
已知约束：ADR-027 明令批量标记的 until 不得简化为 UtcDateTime，三方言编码敏感。命中此类先看有无 ADR 声明。

### V13 批量与单条路径行为对等

判据：批路径与单条路径在失败处理、计数、事件 ID、日志上是否一致。
命令：找到成对方法，逐项对照。
已知约束：R9 修过批量 Inbox 标记失败记 evt=0 而单条记 1004 的分叉，本轮应对照复核。

### V14 状态机终态与永久空洞

判据：终态能否被写回；跳号是否被代码假设为不存在；并发两写者同格竞争结果。
命令：列全部状态枚举与写入点，逐格问触发条件、失败补偿、并发竞争。
已知约束：SagaStatus 的 4 是永久空洞，DeadLettered 已删，编号不得复用。终态不可写回有 architecture test 守护，命中先查守护再报。

### V15 版本约束与快照边界

判据：要删的符号是否在公共 API 快照内；Obsolete 标记与实际状态是否自相矛盾。
命令：`grep -n <符号> test/PalDDD.Integration.Tests/Snapshots/core-packages-public-api.txt`。
已知：快照不记 Obsolete 但记 public const 值。R16 的 F4/F5 因此只勘正不删除。

### V16 语义克隆的部分同步（v3 新增，R17 最高产）

判据：某个语义在多个物理位置实现，修复/约定只覆盖其一。与 V1 的区别——V1 看「同一次修复漏了同族文件」，V16 看**任意粒度的语义等价物**：同方法内两处循环、同文件两路径、跨文件同名钩子、跨栈同名组件、注释承诺与实现落点。
命令：`git log -S "<关键符号>" --oneline -- src`（找该符号全历史引入点）+ `grep -rn "<同语义模式>" src --include=*.cs`（列全部物理位置）+ 逐点确认「同一修复/约定是否到位」。
命中案例：R17 D1（PalORM StopAsync 等待已修、Dapper 同组件漏）、D2（SystemCompressor Release 已接、Native 三器漏）、D8（Rabbit 健康位清除已修、Kafka 漏）、D4（类注释已勘正、接口/ADR/performance 三处漏）。
反面：R17 D9 的判据「对象路径有 span、raw 无 span」是真的不对称；但 perf-ledger 里大量「A 有 B 无」是**显式取舍**（如 Kafka 不接 bodyOwner 因 byte[] 契约），命中先 grep 取舍声明（呼应 §0.2）。

### V17 承诺的手段缺失（v3 新增）

判据：注释/文档声称采取了某手段（「只进日志」「已上报指标」「协作收尾」「自动重连」「fail-fast」），但执行路径上该手段的载体（logger 调用 / Counter.Add / await / 重试循环 / throw）根本不存在。与 V2 的区别——V2 查「字段有没有读方」，V17 查「承诺的动作有没有执行点」，不限于字段。
命令：见 §2 第 13/17 类；拿到承诺行后，在同一方法/同一执行路径内 grep 对应动作动词。
命中案例：R17 D6「details in logs」承诺——响应写器写固定文案，但 `WriteHealthResponseAsync` 零 logger 调用，异常对象无任何出口。
反面：R16 F2 已在 PalORM 侧补了 await，v3 再扫到「协作收尾」时先确认该侧是否已修，勿把已修的同款再报（呼应 §1.5 证伪台账）。

### V18 测试基建自身的假绿（v3 新增，元层面）

判据：一个测试/门禁「通过了」，但它证明的不是它声称的命题。三类形态：① 行为探针受环境影响恒绿（ThreadStatic 槽、并行调度、JIT 消除、共享全局态）；② 断言写得太弱（只验 len/IsNotNull，不验内容）；③ 门禁接线了但从不执行（CI job 条件恒假、pre-push 降级路径不含它）。
命令：对每个新写的行为探针做**反向验证**——人为制造缺陷态，确认探针转红（R17 D2 的反面教训：bomb 后查 TLS 槽容量的探针在缺陷仍在时恒绿，改源码级不变式后才红→绿→变异红）；对门禁跑 `dotnet run scripts/gate-audit.cs`（矩阵 + 隔离探针）。
命中案例：R16/R17 的 mutation 基数问题（Stryker 分数建立在个位数基数上，break 阈值无区分度）；R17 D2 行为探针假绿。
反面：不要因此全面否定行为测试——能在缺陷态转红的行为测试优于源码级；源码级是**并列的一等证据**，不是行为测试失败后的退路（见 §6 证据纪律）。

### V19 跨抽象层的状态归属（v3 新增）

判据：状态（健康位、租约、offset、run 归属、CTS、gate）写在 A 层，读在 B 层，中间经过线程/异步边界——写者死后读者是否还能看到正确状态？清除动作是否覆盖所有恢复路径？
命令：对每个 `volatile`/`Interlocked`/`static` 状态字段 grep 全部写入点，按「置位 / 清除 / 读取」三分类，检查每条恢复路径（重连、重订阅、重启、重新 attach）都有清除；跨线程读取确认有内存屏障（Volatile/Interlocked/lock/await 的 happens-before）。
命中案例：R17 D8（Kafka `_consumeFaultReason` 置位 3 处、清除 0 处，重订阅恢复后健康位粘滞）；R16 F1（worker 死而不置位）。
反面：R15 已确认 PalORM PG 通知器的 `_fireTask` 竞态不成立——上游 `StopAsync` 会 `await runTask`，`OnNotificationReceived` 在 runTask 上同步分发并写字段，构成 happens-before 屏障。遇到「竞态」先反编译上游确认时序，别只凭代码直觉（呼应 V8）。

### V20 配置与能力声明的不对称（v3 新增）

判据：能力开关/配置属性在一处定义并声明「默认 X」，但另一处（自检函数、授权判定、文档）按不同默认值解读；或能力声明与实际行为分叉（声明支持 raw 发布但 raw 路径缺对象路径的观测能力）。
命令：`grep -rnE "默认|Supports[A-Z]|Can[A-Z].*=>.*null|Enable[A-Z]" src --include=*.cs`，对每个默认值/能力判定找其**所有**消费点，逐点确认口径一致；对照 `docs/decisions/` 与 README 的声明。
命中案例：R17 D9 的死角——`CanAggregateBatchAck(..., null)` 按「默认 1」放行，但 `CreateChannelOptions(consumerDispatchConcurrency: null)` 继承工厂值，工厂值可写且 >1 时授权门失配（本仓未暴露该配置，停死角）。ADR-034 `EnableDueTimeFilter` 正文 false / 实施附记 true 的分叉属同类（R16 已识别，勿重报）。
反面：`SupportsRawPublish => true` 若 raw 路径本就只做最简发布、不承诺 span/计数，则不构成缺陷——先找契约文档（呼应 §0.2）。

---

## 5. 刁钻剧本（破坏者视角，每类至少推演一条）

| 类别 | 剧本 | 落点要求 |
|---|---|---|
| 网络 | 100ms 闪断；半开连接无 FIN/RST；NAT 静默掐断；重连上限耗尽后的可观测面 | 写出组件名与行号 |
| 进程 | SIGTERM 时序：先停消费者还是先停生产者；线程池饥饿下的 sync-over-async；重启恢复时租约悬挂与半批写入 | 同上 |
| 时钟 | 墙钟回拨；单调与墙钟混用；三方言时间编码差异 | 同上 |
| 资源 | 磁盘满；连接池耗尽；ArrayPool 大对象弃槽；队列积压 10 万条下的无界结构 | 同上 |
| 数据边界 | 空批、单条、满批、超批；重复 ID；NULL 与三方言 NULL 比较；大小写折叠；整数溢出；emoji 四字节 | 同上 |
| 多实例 | 双 worker 抢租约的 fencing 谓词完整性；双 NOTIFY 唤醒；Saga 僵尸执行补偿 | 同上 |
| 关停/恢复（v3） | SIGTERM 后游离 Task 的终态落库；重连/重订阅后健康位与租约状态清零；宿主 Dispose 与 StopAsync 的先后顺序 | 同上（R17 D1/D8） |
| 克隆漂移（v3） | 同语义两实现的操作成对性（Rent/Release、Subscribe/Unsubscribe、置位/清除）；抽取重构后同一告警/事件 ID 是否双发 | 同上（R17 D2/D3） |
| 观测断链（v3） | 承诺「只进日志/已上报」时异常对象从产生到落点的全链路；三面（日志/指标/健康）同时为空的失败路径 | 同上（R17 D6） |

写不出具体代码位置的剧本进第 7 章死角，不进缺陷清单。

---

## 6. 证据验证与合成

- 每条发现必须有可执行验证命令，退出码 0 且输出非空。逐条执行，失败即丢弃并计数。无命令的 P0/P1 降 P2。
- 位置格式：路径:行号 + 符号名。行号不确定给唯一命中的原文片段。
- 证据等级：E1 运行复现且有错误状态；E2 运行观察符合或不符文档；E3 静态加现有测试门禁；E4 仅阅读。E4 不得进缺陷清单，E3 的 P0/P1 降 P2。
- **源码级不变式是一等证据（v3）**：E3 的源码级断言（「字段 X 的方法体内必须有 await」「Rent 与 Release 计数必须相等」「SubscribeAsync 的清零必须在 SetConsumeTask 之后」）与行为测试并列，不是行为测试失败后的退路。本仓 R16 F1/F2、R17 D2/D8/D9 均以源码级锁定并变异红。选择判据：行为能在缺陷态稳定转红就用行为；受线程调度/TLS/真外部依赖影响则用源码级，并在报告注明取舍（§7 缺陷清单的「证据等级」字段）。
- **验证验证者（v3 强化）**：一个「通过」的探针若从未在缺陷态转红，它什么也没证明。每个新探针必须做反向验证——人为把产品代码改成缺陷态（修复前形态），跑探针确认转红，再恢复。R17 D2 的教训：bomb 后查 ThreadStatic 槽容量的行为探针在缺陷仍在时恒绿（TUnit 调度使基线/Decompress/after 不共享槽），误判为「无缺陷」；改源码级不变式后红→绿→变异红三步齐全。**反向验证失败时，先怀疑探针而非缺陷**。
- 依赖行为断言给反编译或文档行号；配置组合断言给组合表；痕迹类给提交哈希与 grep 零命中输出；静默失效类给日志、指标、健康三面皆无的检索结果。
- 同根因合并。跨片复现 3 次以上的模式单列并建议机械化防线。
- S3 反向验证纪律：采纳一个修复因子前先移除一次，确认退化回基线；不退化即非必要，丢弃并标 [推断]。R16 的 `[NotInParallel]` 就是这样被丢弃的。
- Git Bash 纪律：管道尾用 `${PIPESTATUS[0]}`；GBK 输出 grep 加 -a 或尾接 `iconv -f UTF-8 -t GBK -c`；MTP 过滤用 `--treenode-filter` 不是 `--filter`（类名须精确：嵌套类用实际顶层类名，`--filter` 报 exit 5）。新建 .md/.cs 用 Write 工具默认是裸 LF，须转 CRLF 才能过 encoding-gate E5。
- **范围扩张的收敛纪律（v3）**：扩视角/扩分片是为了召回，不是为了产数量。每个新视角命中后仍须过 §0 五条总原则（尤其「假设必须证伪」与「默认假设代码是对的」）。宁可 8 条真实，不要 40 条注水——这条不因范围扩大而放宽。

---

## 7. 输出契约（七章，缺一不可）

1. 结论摘要：不超过 10 行，附 Top3。
2. 覆盖表：全部分片标读全、抽样、未读及原因；**v3 的 20 个视角（V1-V20）各自覆盖声明，未适用者注明原因**；**6 条跨片对称轴（§3.1）逐轴覆盖声明**；预扫描 17 类命中基线数。
3. 缺陷清单：每条七字段，位置、原文不超过 15 行、触发条件、后果、修法骨架、影响面含调用方数、证据等级。
4. 严重度：P0 数据错误或违红线；P1 契约破坏、静默失效、量级恶化；P2 明确维护成本或使用方易踩；P3 表达一致性。
5. 与已知项对账：新发现、re-affirmed（须本轮实测复验，引用旧结论不算）、与 ADR 冲突（写编号与前提失效条件）、本轮证伪（防止下轮重复）。
6. 未评审与不确定项。
7. 死角清单：探索过但证据不足的角度，逐条写「查了什么、缺什么证据能升级」。本轮零发现时本章是主要产出。

---

## 8. 反模式（写了即报告无效）

- 已勘正取舍再上报。
- 无法核验的 API、包、版本引用。
- 对未读文件做行为断言。
- 提出违反 AOT 或零反射红线的建议（EF、Kafka、Rabbit、MemoryPack 项目是显式豁免面）。
- 用「建议进一步深入分析」收尾。
- 为凑数把 P3 写成 P2。
- 静态阅读超时不转探针。
- 把 [推断] 当 [事实] 写进报告或记忆库。
- **用未做反向验证的「绿灯」探针证明无缺陷**（v3：R17 D2 的 TLS 槽探针假绿）；探针不红时先怀疑探针，别急着下结论。
- **用「A 有 B 无」直接当缺陷**（v3：先查取舍声明与 perf-ledger 的 ❌/⛔ 行；显式取舍不是缺陷）。
- **把源码级不变式当成「行为测试的安慰替身」**（v3：它是并列一等证据，应在报告注明为何选它——线程归属/外部依赖/不可控竞态）。
- **重复上报已修的同族**（v3：先跑 §1.7 修复簇表与 §1.5 证伪台账；R16 修的 PalORM 等待、R17 修的 Kafka 清除都已在案，勿再报）。
