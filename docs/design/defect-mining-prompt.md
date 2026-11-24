# 刁钻缺陷挖掘提示词（v2，2026-11-24）

> 用途：已发布框架库的只读缺陷挖掘。目标是在基线全绿的前提下，找出今天就能复现的真实缺陷，并最大化死角覆盖。
> 本版基于 Pal.DDD R16 实跑固化：16 分片扫描、六视角交叉、七章输出，产出 6 条真缺陷（1 P2 + 5 P3）、2 条证伪、7 条死角。
> 与 `.ai/review/prompt.md` 的分工：review 审「承诺是否兑现、改动是否可合并」，本提示词挖「哪里会出错而现有防线看不见」。两者共用误判库与证据分级，但不共用轮次编号。

---

## 0. 总原则（违反即报告无效）

1. 缺陷的定义：产生错误行为、数据丢失、契约破坏、静默失效（组件死亡无信号），或让使用方写出错误代码。风格偏好与规模指标（行数、文件数、抽象层数）不构成缺陷。
2. 默认假设代码是对的。本仓有 40 份 ADR 与大量勘正注释。命中含「P定案、勘正、取舍、勿重开、有意为之、ADR-」字样的注释时，先引用注释原文，再论证其前提是否失效。无视注释直接上报视为无效条目。
3. 质量门槛：宁可 8 条真实，不要 40 条注水。允许结论是「零新缺陷 + re-affirmed N 项」。
4. 覆盖范围是默认项不是选项。时间不够时按优先级降级为抽样，但每个视角至少覆盖 1 片，且未读面必须写进第 7 章。
5. 假设必须证伪。任何「看起来有问题」先找反证命令，跑不出错误行为的降级为 [推断]，进死角，不进缺陷清单。

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

**1.5 是本版新增**。R16 曾花大量时间重跑「CTS Cancel 后 Dispose 破坏协作取消」与「解压炸弹防护缺失」两条，均被证伪。证伪台账把这类结论固定下来，避免下一轮重复。

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

**新增第 13 类：完成态词与实现证据对账**。 grep 注释中的「已、保证、立即、等待、串行、幂等、自动、协作收尾、闭环」，逐个找对应实现证据。R16 的 F2 就出自这里：注释写「协作收尾」，字段 `_fireTask` 全文只有声明、赋值、清空三处，没有任何 await。

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

片 1 与片 14 在 R16 完全未读，片 6 只读了拦截器同步桥。本版把这三片列为优先片，禁止再以时间不足跳过且不声明。

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

写不出具体代码位置的剧本进第 7 章死角，不进缺陷清单。

---

## 6. 证据验证与合成

- 每条发现必须有可执行验证命令，退出码 0 且输出非空。逐条执行，失败即丢弃并计数。无命令的 P0/P1 降 P2。
- 位置格式：路径:行号 + 符号名。行号不确定给唯一命中的原文片段。
- 证据等级：E1 运行复现且有错误状态；E2 运行观察符合或不符文档；E3 静态加现有测试门禁；E4 仅阅读。E4 不得进缺陷清单，E3 的 P0/P1 降 P2。
- 依赖行为断言给反编译或文档行号；配置组合断言给组合表；痕迹类给提交哈希与 grep 零命中输出；静默失效类给日志、指标、健康三面皆无的检索结果。
- 同根因合并。跨片复现 3 次以上的模式单列并建议机械化防线。
- S3 反向验证纪律：采纳一个修复因子前先移除一次，确认退化回基线；不退化即非必要，丢弃并标 [推断]。R16 的 `[NotInParallel]` 就是这样被丢弃的。
- Git Bash 纪律：管道尾用 `${PIPESTATUS[0]}`；GBK 输出 grep 加 -a 或尾接 `iconv -f UTF-8 -t GBK -c`；MTP 过滤用 `--treenode-filter` 不是 `--filter`。

---

## 7. 输出契约（七章，缺一不可）

1. 结论摘要：不超过 10 行，附 Top3。
2. 覆盖表：全部分片标读全、抽样、未读及原因；五视角各自覆盖声明；预扫描各类命中基线数。
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
