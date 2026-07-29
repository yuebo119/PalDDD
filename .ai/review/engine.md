# Pal.DDD Review 检查引擎（review/engine v1.0）

> **review 系统的检查引擎**——入口与触发档位见 [`prompt.md`](prompt.md)；本文件只定义"怎么查"。
> **宗旨：质量为先**——默认执行方式是**地毯式逐行、逐文件阅读全部范围内代码**；探针、机械防线、档位分级都是在质量之上叠加的证据强化与流程编排，**不是阅读的替代品**。
> **检查依据**：[`docs/conventions.md`](../../docs/conventions.md)（14 章 + 附录）+ [`docs/architecture.md`](../../docs/architecture.md)（18 决策）+ [`ArchitectureBoundaryTests.cs`](../../test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs)（33 测试方法机械守护）

---

## 执行前强制项

1. `bash .ai/scripts/gate-check.sh` 全绿（门禁是前置，引擎不重复门禁已覆盖的检查）
2. 加载 [`known-false-positives.md`](known-false-positives.md)（误判知识库，只增不删）
3. `bash scripts/review-snapshot.sh`（项目根 DDD 原生）→ 锚定 commit + 基线数据
4. 声明范围：必须检查 / 明确不检查 / 抽样策略——三项缺一不可

---

## 地毯式逐行 × 探针并用（质量为先）

> 两者是**串联关系，不是二选一**：逐行负责"发现面"（不遗漏），探针负责"证据面"（不误判）。

1. **逐行是默认**：范围内每个手写 `.cs` 文件逐行读完，七流视角同时在场；范围由档位决定（diff 档=触及文件全文，全量/里程碑档=src/ 全部手写代码），范围内不抽样、不跳读。抽样只允许出现在"明确不检查"声明覆盖的范围外区域，且结论必须标 ⚠。
2. **探针是定稿要求**：逐行发现的每个 P0-P2 疑点必须转化为可执行探针证实或证伪（[推断]零容忍）。每流的标准探针形态见下。
3. **机械防线先跑**：ArchitectureBoundaryTests/PublicApiSnapshot/AotContract/PerformanceContract 测试与门禁在逐行**之前**执行——它们的失败直接给出逐行的重点区域；它们的通过**不豁免**对应区域的逐行。
4. 探针骨架用 `bash .ai/scripts/probe-template.sh <名称>` 生成，成本 ~30 秒。
5. **覆盖度可审计**：报告段 1 声明"逐行覆盖的文件清单与行数"；零发现的流必须附覆盖度证据。

---

## 七流定义（检查对象 = 手写代码；`*.g.cs` 仅验语义不评风格）

| 流 | 检查焦点 | Pal.DDD 专项 |
|------|---------|-----------|
| 架构流 | DDD 分层依赖方向 · 命名空间一致性 · 循环依赖 | 对照 docs/architecture.md 18 决策 + ArchitectureBoundaryTests 33 方法 |
| 安全流 | SQL 注入 · 输入校验 · 密钥泄露 | SqlTemplates 常量 + 标识符白名单（PostgreSqlAuditor ITM-批次3） |
| 资源流 | IDisposable/IAsyncDisposable · DbContext 释放 · Saga 租约锁释放 | BackgroundService→IServiceScopeFactory→scoped OutboxBatchProcessor 模式（ITM-026） |
| 并发流 | Lock/共享状态 · 竞态 · async void · Dispatcher.Freeze() | SagaState.CurrentState `\|` 字符校验（ITM-001）+ 租约锁（Outbox FOR UPDATE SKIP LOCKED） |
| 错误流 | catch(Exception) OCE 过滤 · 异常类型 · 取消语义 | OperationCanceledException 过滤（ITM-030 三处同型：PeriodicBackgroundProcessor/ExceptionMiddleware/HealthCheck） |
| AOT流 | IsAotCompatible 分层 · STJ 源生成 · 零反射 · DIM 桥接 | AOT 核心层（7 项目）true / 适配器层（14 项目）显式 false 分离 |
| 生成语义流 | GenerateId/GenerateEnum/MessageRegistry emit 与运行时假设一致性 | PublicApiSnapshot + MessageCatalog 键集完整 |

### 生成语义流机械化状态（检查前先跑，人工只做增量）

| 检查项 | 守护机制 | 人工职责 |
|--------|---------|---------|
| 核心包公共 API 端到端可编译 | PublicApiSnapshotTests（12 程序集） | Snapshots/*.txt diff 审阅 |
| MessageCatalog 键集完整 | AotContractTests + MessageRegistryGeneratorTests | 键集变更审阅 |
| GenerateId/GenerateEnum emit 同步 | PublicApiSnapshot 基线（`PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1` 刷新） | diff 逐文件评审 |
| Dispatcher.Freeze() 后不可变 | PerformanceContract + XML doc 约束 | — |
| 诊断不退化（PDDD001-015） | StrategicDddAnalyzerTests 负向测试 | 新诊断补测试 |
| AOT 分层合规 | AotContractTests + ArchitectureBoundaryTests `InfrastructureAdapters_AreExplicitlyNonAot` | — |

---

## 并行地毯协议

> 全量/里程碑档的 src/ 行数较大。串行逐行是速度瓶颈；分片并行把墙钟时间除以片数，每片内部仍是完整地毯——总阅读量不变，质量不降。

1. `bash .ai/scripts/review-scope.sh [--diff] [--partitions N]` 生成**应读清单 + 按行数均衡分片 + 覆盖度账本模板**。
2. 每片派一个子代理，提示词自包含五要素：①片内文件清单（逐行读完）②七流问题卡全文 ③误判库速版 ④产出格式（疑点表）⑤范围与预算边界。
3. **子代理只产疑点不定级**——定级、探针、定稿门收敛到主线程统一执行。
4. 主线程在等待期间执行：机械防线全跑 + 跨片关注点（AOT 分层合规、MessageCatalog 键集、依赖方向、注册表）。
5. 会话中断纪律：子代理丢失按片重派；主线程增量成果随做随记。
6. 覆盖度账本逐文件勾销；未勾销文件出现在报告 = 报告视为草稿。

### 子代理范围与预算边界

1. **主职责是片内清单**：清单读完 = 交付条件达成。清单外的反证性阅读允许且鼓励，但属于"锦上添花"——反证做不完就把疑点标 ❓ 移交主线程。
2. **软预算**：片外阅读量以不超过片内清单行数的一半为参考线。接近参考线时优先收口输出。
3. **主线程催收时限**：任一分片耗时超过已交付分片平均耗时的 3 倍仍未完成，主线程 SendMessage 催收。
4. **等待姿势**：主线程等完成通知，不做轮询式阻塞等待；等待期即第 4 条的机械防线与跨片不变式窗口。

---

## 七流问题卡（逐行时"带着问题读"，发现率与精准度的主杠杆）

> 逐行不是漫读。每流一张卡：读到对应形态时逐问核对。卡内问题全部来自本项目真实 ITM。新根因类出现时向对应卡追加一问。

**架构流卡**：这个 Core 层文件引入了 App/Infra 命名空间吗（PDDD-G5）？这个 Domain/App 层含 DbContext/HttpClient 关键字吗（PDDD-G2/G3）？项目引用矩阵合规吗（boundary 7 大类）？

**安全流卡**：这个插值进 SQL 的值是编译期常量还是运行时可控（标识符必须走白名单 + QuoteIdentifier，PostgreSqlAuditor ITM-批次3）？异常消息/日志里有连接串或 PII 吗？JSON 路径值是否做了反斜杠转义（PostgreSqlJsonb ITM-033）？

**资源流卡**：这个 Create 后抛异常的路径，资源谁释放？Saga 租约的所有权转移点明确吗（leased_by/leased_until + idx_saga_lease）？CTS 有 using 吗？枚举器被放弃时 Outbox DbContext scope 会释放吗？Commit/Rollback 后是否清除了 DataSession 事务引用（PalORM UseTransaction(null) ITM-批次5）？

**并发流卡**：Dispatcher.Freeze() 后还有运行时 Add 吗（ITM-027）？两个 Volatile 字段的发布顺序读方真的依赖吗？SagaState.CurrentState 含 `|` 字符吗（ITM-001）？Saga 租约是否在所有路径（超时/非超时）都释放（ITM-031）？DataSession 是否被多 worker 共享（PalORM AsyncLocal 门禁）？**每个 if/else/switch 分支是否都释放了该路径持有的资源（SPD-3）**？**返回值是否被静默丢弃（SPD-2）**？

**错误流卡**：这个 catch(Exception) 的 OCE 过滤在哪（三种合规形态都查过了吗——`when(ex is not OCE)` / 前置 catch(OCE){throw;} / 后台处理器 [SuppressMessage] + 具体理由）？清理异常挂 Data 还是覆盖了主异常？这个取消与关停取消可区分吗（ITM-030）？DbUpdateException catch 是否区分了唯一约束冲突与其他错误（ITM-036）？

**AOT 流卡**：这个新 API 走反射/MakeGeneric/Expression.Compile 了吗（_expression.Compile() 实例调用也算，PDDD-G8 真实命中）？STJ 调用传 JsonTypeInfo 了吗？这个项目该是 AOT 核心层（true）还是适配器层（false）？这个 IsAotCompatible=true 的项目是否有 NoWarn IL3058 抑制（Dapper P0-5 同型）？注释声称的 AOT 状态与代码实际是否一致（PD14 + SPD-5）？源生成器改动后是否清了 obj/bin（铁律 #13）？

**生成语义流卡**：MessageCatalog 键集改动，三处（注册表/快照/启动期校验）同步了吗？GenerateId/GenerateEnum 改动后 PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS 走了吗？新增消息类型按 5 步流程（conventions §10.5）了吗？PDDD001-015 战略约束满足吗？

---

## 核心原则

```
0. 质量为先——一切优化（探针/防线/档位/并行）都在保持最高质量前提下进行
1. 先发现，后判断——禁止在信息不完整时下结论
2. 证据驱动——每项发现标注可信度（✅⚠❓）+ 至少 1 个信息源（conventions §13 R0）
3. 地毯式逐行是默认——范围内逐文件逐行读完；抽样仅限声明的范围外区域且结论标 ⚠
4. 实现质量 ≥ 架构设计——不因"架构清晰"而忽略"配置缺失"
5. 有害度驱动优先级——危害 × 复杂度，不是"容不容易修"（conventions §13）
6. 区分"没找到"和"不存在"——零发现必须附覆盖度证据
```

---

## 误判定稿门（三问齐备才定稿）

每条 P0-P2 候选定稿前过三问，任一不过即降级或转探针：

1. **误判库对照**：逐条对照模式 ID（通用 1-8 + DDD PD1-PD9），报告段 3 记"已对照模式"列。
2. **反证搜索**：花一次搜索找"这不是缺陷"的证据——前置 catch？幂等保护？ModuleInitializer 填充？文档声明的契约？调用方约束？（误判库 15 模式里 11 个的根因是没做这一步）
3. **可复现表述**：发现描述必须含触发路径（"调用 X 传 Y 则 Z"），写不出触发路径的降 P3 或标 ❓。

被探针证伪的候选计入 metrics"证伪数"列并评估转误判库新模式——证伪数下降才证明误判库在生效。

---

## 证据分级与验证要求

| 发现级别 | 最低验证要求 | 默认探针形态 |
|:--------:|-------------|------|
| P0/P1 | Read 源码 + 探针实证 | 按流选：DryRun SQL / Testcontainers 真库用例 / 编译探针 / AOT publish+run |
| P2 | Read 源码 + grep 交叉验证 | 允许 [推断] 定稿，行动项标"修复前先补探针" |
| P3 | 至少 1 个信息源（标注 ⚠） | — |

**[推断] 零容忍**（教训：ITM-317/318 挂账一轮才实测收口）：

1. P0/P1 发现禁止以 ⚠[推断] 状态定稿。定稿前必须转化为可执行探针之一：
   - **编译探针**：/tmp 最小工程复现目标 API → 编译通过/失败
   - **真库用例**：写入 Integration.Tests，Testcontainers CI（PG/MySQL/RabbitMQ/Kafka）执行；本机不可达标注"待 CI 证实"
   - **生成物断言**：SourceGen 类推断写入 PublicApiSnapshotTests 或 AotContractTests
2. 探针结果（证实/证伪）写入报告信息源列；证伪项转误判知识库候选模式。
3. P2/P3 允许 [推断] 定稿，行动项标注"修复前先补探针证实"。

---

## 影响判定（危害 × 复杂度矩阵，conventions §13）

| | 高危害 | 中危害 | 低危害 |
|------|:------:|:------:|:------:|
| **易修复**（< 1h） | P0 紧急 | P1 近期 | P2 |
| **中等**（1-4h） | P1 近期 | P2 | P3 |
| **难修复**（> 4h） | P2 | P3 | 评估（产出 ADR） |

---

## 发现下沉审查（收口时强制）

> 原则：评审证明会反复出现的发现类别，逐一变成机械防线；评审收缩到机器构不着的语义判断。
> 阶梯：提示词 → 脚本门禁（gate-check PDDD-G1..G22） → Roslyn 诊断（PDDD001-015） → 测试（ArchitectureBoundaryTests/PublicApiSnapshot/AotContract） → 类型系统。

每个 P0/P1 发现收口时必须回答：

| 问题 | 是 → 动作 |
|------|----------|
| 可被 grep/正则机械检测？ | 下沉为 PDDD-G{N} 门禁项 |
| 可在编译期由分析器捕获？ | 下沉为 PDDD0xx 诊断 + 负向测试 |
| 可被 ArchitectureBoundaryTests 捕获？ | 下沉为 boundary 测试方法 |
| 属于"所有 X 必须经过 Y"的架构不变式？ | 下沉为架构测试 |
| 都不是（需语义判断）？ | 保留为提示词检查项 + 误判库模式 |

P0/P1 收口的附加动作：该发现所属根因类若是新类，向对应**七流问题卡**追加一问。

下沉结果记录在行动项账本的「下沉审查」段。

---

## 质量指标（替代主观评分）

> 三层加权综合分与十维度小数分已废止——评分不可复现且与实现质量脱节。
> 每轮记录以下指标至 [`metrics.md`](metrics.md)：

| 指标 | 定义 | 数据源 |
|------|------|--------|
| 缺陷逃逸率 | 真库/CI/用户发现而深检未发现的缺陷数（每轮） | metrics.md 逃逸账本 |
| 复发率 | 同类 ITM（同根因分类）再次出现的数量 | 行动项账本 ID 对照 |
| 按流发现密度 | 每流每千行审查代码的 P0-P2 发现数 | perspective-stats.md |
| P0/P1 修复时延 | 报告定稿 → 修复提交的 commit 间隔 | git log |
| **证伪数** | 定稿前被探针推翻的候选发现数（误报治理指标） | 报告定稿门记录 |

里程碑档的趋势判断基于指标轮次对比，不再输出综合分。

---

## 历史教训速查卡（DDD 专项）

| 如果遇到… | 记住… |
|-----------------|--------|
| "grep 显示有 N 处 X" | grep 做定位，不做计数判断。逐行读后再下结论（误判库模式 2） |
| "这个分析器应该会在 X 时报错" | dotnet build 验证。不许猜分析器行为（PDDD001-015 触发条件） |
| "代码看起来有并发竞态" | 读完整 Lock 块。不许读半截下结论（ITM-027 Dispatcher.Freeze） |
| "这段代码缺异常过滤" | 检查上下文。可能有前置 catch(OCE){throw;} 或 when(ex is not OCE)（误判库模式 3） |
| "这个修复很简单，P2 吧" | 判断危害，不是复杂度。SQL 注入/Outbox 租约泄漏不是 P2 |
| "Core 层应该加个 Helper" | Core 只放领域抽象 + DIM 桥接。Helper 放对应层（conventions §4.10 禁 Helpers/） |
| "这个文件可以移到其他层" | 检查是否破坏 DDD 分层（ArchitectureBoundaryTests 强制） |
| "架构分层很清晰" | 检查配置完整度和资源释放。观感不等于实现质量 |
| "Broker 间代码重复" | 检查是否是 Broker 抽象刻意独立（误判库 PD2：InMemory/Kafka/RabbitMQ 相似 ≠ DRY） |
| "*.g.cs 文件有问题" | 排除源生成器生成文件。只评手写代码（误判库 PD1） |
| "InMemory Broker 测试过了" | InMemory 掩盖 Kafka/RabbitMQ 缺陷——Broker 敏感变更必须四实现验证（对称测试） |
| "测试里构造了这个异常" | 手工 `new XxxException(code)` 走不到驱动的真实错误码填充路径。断言驱动异常行为的探针必须真库触发 |
| "上一轮已经修过这类问题" | 区分补丁与根治：修复只覆盖已发现的触发形态 = 补丁；根治 = 让该类形态在结构上不可表达 |
| "_expression.Compile() 不是问题" | 是的——PDDD-G8 已发现 ISpecification.cs:218 真实违规（实例调用也算） |

---

## 子代理执行纪律

1. **探针先行，委托靠后**：能被一个探针直接证实或证伪的疑点，主线程立即做。
2. 子代理只委托**边界清晰的全量枚举面**（逐 catch 判定、逐文件逐行流），且提示词必须自包含（误判库摘要 + 判定规则内嵌）。
3. 主线程在等待子代理期间持续做增量探针，不空转；子代理丢失时按其范围重新派发。
4. **催收优于苦等**：子代理有进展但迟迟不收口，催收即可（"基于已读内容立即输出，未完成部分如实标注"），不必重派也不必等它自然结束。
5. 中断后恢复的子代理，其交付物按"覆盖度自报 + 主线程抽查复核"处理，并在报告局限声明中标注。
